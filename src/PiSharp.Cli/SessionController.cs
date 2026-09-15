using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Microsoft.Agents.AI;
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

    private SessionController(
        AIAgent agent,
        string model,
        SessionStore? store,
        SessionDocument? document,
        AgentSession session,
        string? activeTurnId)
    {
        _agent = agent;
        _model = model;
        _store = store;
        Document = document;
        Session = session;
        ActiveTurnId = activeTurnId;
    }

    public AgentSession Session { get; private set; }

    public SessionDocument? Document { get; private set; }

    public string? ActiveTurnId { get; private set; }

    public bool IsPersistent => _store is not null;

    public static async Task<SessionController> CreateAsync(
        AIAgent agent,
        CliOptions options,
        CancellationToken cancellationToken)
    {
        if (options.NoSession)
        {
            return new SessionController(
                agent,
                options.Model,
                null,
                null,
                await agent.CreateSessionAsync(cancellationToken),
                null);
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
        var session = active is null
            ? await agent.CreateSessionAsync(cancellationToken)
            : await agent.DeserializeSessionAsync(active.AgentState, JsonOptions, cancellationToken);

        return new SessionController(agent, options.Model, store, document, session, active?.Id);
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

        var state = await _agent.SerializeSessionAsync(Session, JsonOptions, cancellationToken);
        if (Document.IsPiV3)
        {
            await PersistPiTurnAsync(userMessage, assistantMessage, state, toolExecutions ?? [], cancellationToken);
            return;
        }

        var turn = SessionTurn.Create(ActiveTurnId, userMessage, assistantMessage, state);
        await _store.AppendTurnAsync(Document, turn, cancellationToken);
        ActiveTurnId = turn.Id;
    }

    public async Task CheckoutAsync(string turnSelector, CancellationToken cancellationToken)
    {
        EnsurePersistent();
        if (turnSelector.Equals("root", StringComparison.OrdinalIgnoreCase))
        {
            Session = await _agent.CreateSessionAsync(cancellationToken);
            ActiveTurnId = null;
            return;
        }

        var turn = Document!.ResolveTurn(turnSelector);
        Session = await _agent.DeserializeSessionAsync(turn.AgentState, JsonOptions, cancellationToken);
        ActiveTurnId = turn.Id;
    }

    public async Task NewAsync(CancellationToken cancellationToken)
    {
        EnsurePersistent();
        Document = await _store!.CreatePiAsync(cancellationToken);
        Session = await _agent.CreateSessionAsync(cancellationToken);
        ActiveTurnId = null;
    }

    public async Task ForkAsync(string? turnSelector, CancellationToken cancellationToken)
    {
        EnsurePersistent();
        var selectedId = turnSelector is null
            ? ActiveTurnId
            : Document!.ResolveTurn(turnSelector).Id;

        Document = Document!.IsPiV3
            ? await _store!.ForkPiAsync(Document, selectedId, cancellationToken)
            : await _store!.ForkAsync(Document, selectedId, _model, cancellationToken);
        ActiveTurnId = Document.LatestTurn?.Id;
        Session = ActiveTurnId is null
            ? await _agent.CreateSessionAsync(cancellationToken)
            : await _agent.DeserializeSessionAsync(Document.LatestTurn!.AgentState, JsonOptions, cancellationToken);
    }

    private async Task PersistPiTurnAsync(
        string userMessage,
        string assistantMessage,
        JsonElement state,
        IReadOnlyList<ToolExecutionRecord> toolExecutions,
        CancellationToken cancellationToken)
    {
        var userId = Guid.NewGuid().ToString("N");
        var assistantId = Guid.NewGuid().ToString("N");
        var timestamp = DateTimeOffset.UtcNow;
        var user = new MessageEntry(
            userId,
            ActiveTurnId,
            timestamp,
            JsonSerializer.SerializeToElement(new { role = "user", content = userMessage }, JsonOptions));
        var assistantContent = new List<object>();
        if (!string.IsNullOrEmpty(assistantMessage))
        {
            assistantContent.Add(new { type = "text", text = assistantMessage });
        }
        assistantContent.AddRange(toolExecutions.Select(tool => new
        {
            type = "toolCall",
            id = tool.CallId,
            name = tool.Name,
            arguments = tool.Arguments,
        }));
        var assistant = new MessageEntry(
            assistantId,
            userId,
            timestamp,
            JsonSerializer.SerializeToElement(
                new { role = "assistant", content = assistantContent },
                JsonOptions));
        var entries = new List<SessionEntry> { user, assistant };
        var parentId = assistantId;
        foreach (var tool in toolExecutions)
        {
            var resultText = tool.Result?.ToString() ?? string.Empty;
            var toolResult = new MessageEntry(
                Guid.NewGuid().ToString("N"),
                parentId,
                timestamp,
                JsonSerializer.SerializeToElement(
                    new
                    {
                        role = "toolResult",
                        toolCallId = tool.CallId,
                        toolName = tool.Name,
                        content = new[] { new { type = "text", text = resultText } },
                        isError = tool.IsError,
                    },
                    JsonOptions));
            entries.Add(toolResult);
            parentId = toolResult.Id;
        }
        entries.Add(new CustomEntry(
            Guid.NewGuid().ToString("N"),
            assistantId,
            timestamp,
            SessionEntryTypes.AgentStateCache,
            state.Clone()));
        await _store!.AppendEntriesAsync(Document!, entries, cancellationToken);
        ActiveTurnId = assistantId;
    }

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
        Session = ActiveTurnId is null
            ? await _agent.CreateSessionAsync(cancellationToken)
            : await _agent.DeserializeSessionAsync(selected.LatestTurn!.AgentState, JsonOptions, cancellationToken);
        return true;
    }

    public string FormatSessionInfo()
    {
        if (Document is null)
        {
            return "Session: ephemeral (--no-session)";
        }

        return $"Session: {Document.Header.SessionId} | turns: {Document.Turns.Count} | active: {Short(ActiveTurnId) ?? "root"}\n{Document.FilePath}";
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
