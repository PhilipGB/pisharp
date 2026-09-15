using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using PiSharp.Core;

namespace PiSharp.Cli;

internal sealed class SessionController
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        TypeInfoResolver = new DefaultJsonTypeInfoResolver(),
    };

    private readonly AIAgent _agent;
    private readonly SessionStore? _store;
    private readonly string _model;
    private readonly PiSessionChatHistoryProvider _sessionHistory;

    private SessionController(
        AIAgent agent,
        string model,
        SessionStore? store,
        SessionDocument? document,
        AgentSession session,
        string? activeTurnId,
        string? activeEntryId,
        PiSessionChatHistoryProvider sessionHistory)
    {
        _agent = agent;
        _model = model;
        _store = store;
        Document = document;
        Session = session;
        ActiveTurnId = activeTurnId;
        ActiveEntryId = activeEntryId;
        _sessionHistory = sessionHistory;
        _sessionHistory.SetActiveDocument(document, activeEntryId);
    }

    public AgentSession Session { get; private set; }

    public SessionDocument? Document { get; private set; }

    public string? ActiveTurnId { get; private set; }

    public string? ActiveEntryId { get; private set; }

    public bool IsPersistent => _store is not null;

    public static async Task<SessionController> CreateAsync(
        AgentBootstrap bootstrap,
        CliOptions options,
        CancellationToken cancellationToken)
    {
        var agent = bootstrap.Agent;
        if (options.NoSession)
        {
            return new SessionController(
                agent,
                options.Model,
                null,
                null,
                await agent.CreateSessionAsync(cancellationToken),
                null,
                null,
                bootstrap.SessionHistory);
        }

        var store = new SessionStore(options.WorkingDirectory, options.SessionDirectory);
        SessionDocument? document = null;

        if (options.SessionSelector is not null)
        {
            document = await store.ResolveAsync(options.SessionSelector, cancellationToken);
        }
        else if (options.ContinueSession)
        {
            document = await store.ContinueAsync(cancellationToken)
                ?? throw new FileNotFoundException("No previous PiSharp session exists for this workspace.");
        }
        else if (options.ResumeSession)
        {
            document = await PickSessionAsync(store, cancellationToken)
                ?? throw new OperationCanceledException("Session selection cancelled.");
        }

        if (document is null)
        {
            document = await store.CreatePiAsync(cancellationToken);
        }

        EnsureWorkspaceMatches(document, options.WorkingDirectory);
        var active = document.LatestTurn;
        var session = await RestoreSessionAsync(agent, active, cancellationToken);

        var controller = new SessionController(
            agent,
            options.Model,
            store,
            document,
            session,
            active?.Id,
            document.IsPiV3 ? document.LatestEntryId : active?.Id,
            bootstrap.SessionHistory);
        if (!string.IsNullOrWhiteSpace(options.SessionName) && controller.IsPersistent)
        {
            await controller.SetNameAsync(options.SessionName, cancellationToken);
        }
        return controller;
    }

    public async Task PersistTurnAsync(
        string userMessage,
        string assistantMessage,
        CancellationToken cancellationToken,
        IReadOnlyList<ToolExecutionRecord>? toolExecutions = null)
    {
        if (_store is null || Document is null)
        {
            return;
        }

        if (Document.IsPiV3)
        {
            return;
        }

        var state = await _agent.SerializeSessionAsync(Session, JsonOptions, cancellationToken);
        var turn = SessionTurn.Create(ActiveTurnId, userMessage, assistantMessage, state);
        await _store.AppendTurnAsync(Document, turn, cancellationToken);
        ActiveTurnId = turn.Id;
        ActiveEntryId = turn.Id;
        _sessionHistory.SetActiveDocument(Document, ActiveEntryId);
    }

    public async Task PersistUserMessageAsync(
        string message,
        IReadOnlyList<AIContent>? contents,
        CancellationToken cancellationToken)
    {
        EnsurePersistent();
        if (!Document!.IsPiV3)
        {
            return;
        }

        var entry = new MessageEntry(
            Guid.NewGuid().ToString("N"),
            ActiveEntryId,
            DateTimeOffset.UtcNow,
            CreateUserMessage(message, contents));
        await _store!.AppendEntriesAsync(Document, [entry], cancellationToken);
        ActiveEntryId = entry.Id;
        _sessionHistory.SetActiveDocument(Document, ActiveEntryId);
    }

    public async Task PersistAssistantMessagesAsync(
        IReadOnlyList<JsonElement> messages,
        CancellationToken cancellationToken)
    {
        EnsurePersistent();
        if (!Document!.IsPiV3 || messages.Count == 0)
        {
            return;
        }

        var entries = new List<SessionEntry>(messages.Count);
        var parentId = ActiveEntryId;
        foreach (var message in messages)
        {
            var entry = new MessageEntry(
                Guid.NewGuid().ToString("N"),
                parentId,
                GetMessageTimestamp(message) ?? DateTimeOffset.UtcNow,
                message.Clone());
            entries.Add(entry);
            parentId = entry.Id;
            if (IsRole(message, "assistant"))
            {
                ActiveTurnId = entry.Id;
            }
        }
        await _store!.AppendEntriesAsync(Document, entries, cancellationToken);
        ActiveEntryId = parentId;
        _sessionHistory.SetActiveDocument(Document, ActiveEntryId);
    }

    public async Task PersistAgentStateCacheAsync(CancellationToken cancellationToken)
    {
        EnsurePersistent();
        if (!Document!.IsPiV3 || ActiveEntryId is null)
        {
            return;
        }

        var state = await _agent.SerializeSessionAsync(Session, JsonOptions, cancellationToken);
        var cache = new CustomEntry(
            Guid.NewGuid().ToString("N"),
            ActiveEntryId,
            DateTimeOffset.UtcNow,
            SessionEntryTypes.AgentStateCache,
            state.Clone());
        await _store!.AppendEntriesAsync(Document, [cache], cancellationToken);
        ActiveEntryId = cache.Id;
        _sessionHistory.SetActiveDocument(Document, ActiveEntryId);
    }

    public async Task CheckoutAsync(string turnSelector, CancellationToken cancellationToken)
    {
        EnsurePersistent();
        if (turnSelector.Equals("root", StringComparison.OrdinalIgnoreCase))
        {
            Session = await _agent.CreateSessionAsync(cancellationToken);
            ActiveTurnId = null;
            ActiveEntryId = null;
            _sessionHistory.SetActiveDocument(Document, ActiveEntryId);
            return;
        }

        var turn = Document!.ResolveTurn(turnSelector);
        Session = await RestoreSessionAsync(_agent, turn, cancellationToken);
        ActiveTurnId = turn.Id;
        ActiveEntryId = Document.IsPiV3 ? Document.GetTurnLeafEntryId(turn.Id) : turn.Id;
        _sessionHistory.SetActiveDocument(Document, ActiveEntryId);
    }

    public async Task NewAsync(CancellationToken cancellationToken)
    {
        EnsurePersistent();
        Document = await _store!.CreatePiAsync(cancellationToken);
        Session = await _agent.CreateSessionAsync(cancellationToken);
        ActiveTurnId = null;
        ActiveEntryId = null;
        _sessionHistory.SetActiveDocument(Document, ActiveEntryId);
    }

    public async Task ForkAsync(string? turnSelector, CancellationToken cancellationToken)
    {
        EnsurePersistent();
        var selectedId = turnSelector is null
            ? ActiveEntryId
            : Document!.ResolveTurn(turnSelector).Id;

        Document = Document!.IsPiV3
            ? await _store!.ForkPiAsync(Document, selectedId, cancellationToken)
            : await _store!.ForkAsync(Document, selectedId, _model, cancellationToken);
        ActiveTurnId = Document.LatestTurn?.Id;
        ActiveEntryId = Document.IsPiV3 ? Document.LatestEntryId : ActiveTurnId;
        _sessionHistory.SetActiveDocument(Document, ActiveEntryId);
        Session = await RestoreSessionAsync(_agent, Document.LatestTurn, cancellationToken);
    }

    private static JsonElement CreateUserMessage(string message, IReadOnlyList<AIContent>? contents)
    {
        var imageContents = contents?.OfType<DataContent>()
            .Select(content => (object)new
            {
                type = "image",
                data = content.Base64Data.ToString(),
                mimeType = content.MediaType,
            })
            .ToArray() ?? [];
        object contentValue = imageContents.Length == 0
            ? message
            : new object[] { new { type = "text", text = message } }
                .Concat(imageContents)
                .ToArray();
        return JsonSerializer.SerializeToElement(
            new
            {
                role = "user",
                content = contentValue,
                timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            },
            JsonOptions);
    }

    private static DateTimeOffset? GetMessageTimestamp(JsonElement message)
    {
        if (!message.TryGetProperty("timestamp", out var timestamp) ||
            timestamp.ValueKind != JsonValueKind.Number ||
            !timestamp.TryGetInt64(out var milliseconds))
        {
            return null;
        }
        return DateTimeOffset.FromUnixTimeMilliseconds(milliseconds);
    }

    private static bool IsRole(JsonElement message, string role) =>
        message.ValueKind == JsonValueKind.Object &&
        message.TryGetProperty("role", out var roleValue) &&
        string.Equals(roleValue.GetString(), role, StringComparison.Ordinal);

    public async Task<bool> ResumeInteractiveAsync(CancellationToken cancellationToken)
    {
        EnsurePersistent();
        var selected = await PickSessionAsync(_store!, cancellationToken);
        if (selected is null)
        {
            return false;
        }

        Document = selected;
        ActiveTurnId = selected.LatestTurn?.Id;
        ActiveEntryId = selected.IsPiV3 ? selected.LatestEntryId : ActiveTurnId;
        _sessionHistory.SetActiveDocument(Document, ActiveEntryId);
        Session = await RestoreSessionAsync(_agent, selected.LatestTurn, cancellationToken);
        return true;
    }

    private static async Task<AgentSession> RestoreSessionAsync(
        AIAgent agent,
        SessionTurn? turn,
        CancellationToken cancellationToken)
    {
        if (turn is null || turn.AgentState.ValueKind != JsonValueKind.Object ||
            !turn.AgentState.EnumerateObject().Any())
        {
            return await agent.CreateSessionAsync(cancellationToken);
        }
        return await agent.DeserializeSessionAsync(turn.AgentState, JsonOptions, cancellationToken);
    }

    public SessionStatistics GetStatistics() => Document?.GetStatistics() ??
        new SessionStatistics(string.Empty, null, string.Empty, 0, 0, 0, 0, 0);

    public async Task SetNameAsync(string name, CancellationToken cancellationToken)
    {
        EnsurePersistent();
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (!Document!.IsPiV3)
        {
            throw new InvalidOperationException("Session naming requires a Pi v3 session.");
        }

        var entry = new SessionInfoEntry(
            Guid.NewGuid().ToString("N"),
            ActiveEntryId,
            DateTimeOffset.UtcNow,
            name.Trim());
        await _store!.AppendEntriesAsync(Document, [entry], cancellationToken);
        ActiveEntryId = entry.Id;
        _sessionHistory.SetActiveDocument(Document, ActiveEntryId);
    }

    public string FormatSessionInfo()
    {
        if (Document is null)
        {
            return "Session: ephemeral (--no-session)";
        }

        var name = string.IsNullOrWhiteSpace(Document.Name) ? string.Empty : $" | name: {Document.Name}";
        return $"Session: {Document.Header.SessionId}{name} | turns: {Document.Turns.Count} | active: {Short(ActiveEntryId) ?? "root"}\n{Document.FilePath}";
    }

    public string FormatTree()
    {
        if (Document is null)
        {
            return "Session tree is unavailable in --no-session mode.";
        }

        if (Document.Turns.Count == 0)
        {
            return "(empty session)";
        }

        var children = Document.Turns
            .GroupBy(turn => turn.ParentId ?? string.Empty, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.ToList(), StringComparer.OrdinalIgnoreCase);
        var lines = new List<string>();
        AppendChildren(string.Empty, string.Empty, true, children, lines);
        return string.Join(Environment.NewLine, lines);
    }

    private void AppendChildren(
        string parentId,
        string prefix,
        bool isRoot,
        IReadOnlyDictionary<string, List<SessionTurn>> children,
        List<string> lines)
    {
        if (!children.TryGetValue(parentId, out var nodes))
        {
            return;
        }

        for (var i = 0; i < nodes.Count; i++)
        {
            var node = nodes[i];
            var last = i == nodes.Count - 1;
            var connector = isRoot ? string.Empty : last ? "└─ " : "├─ ";
            var marker = string.Equals(node.Id, ActiveTurnId, StringComparison.OrdinalIgnoreCase) ? "*" : " ";
            var summary = node.UserMessage.ReplaceLineEndings(" ");
            if (summary.Length > 70)
            {
                summary = summary[..67] + "...";
            }

            lines.Add($"{prefix}{connector}{marker} {Short(node.Id)}  {summary}");
            var childPrefix = isRoot ? string.Empty : prefix + (last ? "   " : "│  ");
            AppendChildren(node.Id, childPrefix, false, children, lines);
        }
    }

    private static async Task<SessionDocument?> PickSessionAsync(
        SessionStore store,
        CancellationToken cancellationToken)
    {
        var sessions = await store.ListAsync(cancellationToken);
        if (sessions.Count == 0)
        {
            Console.WriteLine("No saved sessions for this workspace.");
            return null;
        }

        Console.WriteLine("Saved sessions:");
        for (var i = 0; i < sessions.Count; i++)
        {
            var session = sessions[i];
            var latest = session.LatestTurn;
            var summary = latest?.UserMessage.ReplaceLineEndings(" ") ?? "(empty)";
            if (summary.Length > 60)
            {
                summary = summary[..57] + "...";
            }

            Console.WriteLine($"  {i + 1,2}. {session.Header.SessionId[..8]}  {session.Turns.Count,3} turns  {summary}");
        }

        Console.Write("Select session (blank to cancel): ");
        var input = Console.ReadLine();
        if (string.IsNullOrWhiteSpace(input))
        {
            return null;
        }

        if (!int.TryParse(input, out var selected) || selected < 1 || selected > sessions.Count)
        {
            throw new ArgumentException("Invalid session selection.");
        }

        return sessions[selected - 1];
    }

    private static void EnsureWorkspaceMatches(SessionDocument document, string workspace)
    {
        var expected = Path.TrimEndingDirectorySeparator(Path.GetFullPath(workspace));
        var actual = Path.TrimEndingDirectorySeparator(Path.GetFullPath(document.Header.WorkingDirectory));
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (!string.Equals(expected, actual, comparison))
        {
            throw new InvalidOperationException($"Session belongs to a different workspace: {actual}");
        }
    }

    private void EnsurePersistent()
    {
        if (_store is null || Document is null)
        {
            throw new InvalidOperationException("This command is unavailable in --no-session mode.");
        }
    }

    private static string? Short(string? id) => id is null ? null : id[..Math.Min(8, id.Length)];
}
