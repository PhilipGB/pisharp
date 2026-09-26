using System.Text.Json;
using Microsoft.Extensions.AI;
using PiSharp.Core;
using PiSharp.Runtime.Tools;

namespace PiSharp.Runtime.Sessions;

/// <summary>Application-owned conversation. MAF sessions are rebuilt from the selected branch.</summary>
public sealed class ConversationSession
{
    public const int FormatVersion = 2;
    private static readonly JsonSerializerOptions BashExecutionJsonOptions = new(JsonSerializerDefaults.Web);
    public string Id { get; }
    public string WorkingDirectory { get; }
    public string Model { get; private set; }
    public string? Provider { get; private set; }
    public string? Endpoint { get; private set; }
    public string? Name { get; private set; }
    internal JsonElement? PiJsonlHeader { get; }
    public ConversationTree Tree { get; }

    public ConversationSession(string workingDirectory, string model, string? endpoint, string? provider = null)
        : this(Guid.NewGuid().ToString("N"), Path.GetFullPath(workingDirectory), model, endpoint, provider, null, new ConversationTree()) { }

    private ConversationSession(string id, string cwd, string model, string? endpoint, string? provider, string? name,
        ConversationTree tree, JsonElement? piJsonlHeader = null)
    {
        Id = id;
        WorkingDirectory = cwd;
        Model = model;
        Provider = provider;
        Endpoint = endpoint;
        Name = name;
        Tree = tree;
        PiJsonlHeader = piJsonlHeader?.Clone();
    }

    internal static ConversationSession FromPiJsonl(string id, string cwd, string model, string? provider, string? name,
        ConversationTree tree, JsonElement header) =>
        new(id, cwd, model, null, provider, name, tree, header);

    internal static ConversationNode ImportedPiMessage(string id, string? parentId, DateTimeOffset timestamp,
        ChatMessage message, JsonElement originalEntry)
    {
        var errors = message.Contents.Select((content, index) => new { content, index })
            .Where(item => item.content is FunctionResultContent { Exception: not null } or FunctionCallContent { Exception: not null })
            .Select(item => new ToolError(item.index, item.content is FunctionResultContent ? "result" : "call",
                item.content is FunctionResultContent result ? result.Exception!.Message : ((FunctionCallContent)item.content).Exception!.Message))
            .ToArray();
        return new(id, parentId, "chat", JsonSerializer.SerializeToElement(new ChatRecord(
            JsonSerializer.SerializeToElement(message, AIJsonUtilities.DefaultOptions), errors, originalEntry.Clone())), timestamp);
    }

    internal static JsonElement? PiEntryFromChatPayload(JsonElement payload) =>
        payload.ValueKind == JsonValueKind.Object && payload.TryGetProperty("PiEntry", out var entry) &&
        entry.ValueKind == JsonValueKind.Object ? entry.Clone() : null;

    internal static JsonElement? PiEntryFromBashPayload(JsonElement payload) =>
        payload.ValueKind == JsonValueKind.Object && payload.TryGetProperty("piOriginalEntry", out var entry) &&
        entry.ValueKind == JsonValueKind.Object ? entry.Clone() : null;

    public void Rename(string? name) => Name = name;
    public void AppendThinkingLevelChange(string level)
    {
        if (string.IsNullOrWhiteSpace(level)) throw new ArgumentException("Thinking level cannot be empty.", nameof(level));
        Tree.Append("thinking_level_change", JsonSerializer.SerializeToElement(new { thinkingLevel = level }));
    }

    internal void AppendContextOmission(string targetId)
    {
        if (string.IsNullOrWhiteSpace(targetId)) throw new ArgumentException("A context-edit target is required.", nameof(targetId));
        Tree.Append("context_edit", JsonSerializer.SerializeToElement(new { targetId, replacement = (string?)null }));
    }

    public void SelectModel(string model, string? endpoint, string? provider = null)
    {
        if (string.IsNullOrWhiteSpace(model)) throw new ArgumentException("Model ID cannot be empty.", nameof(model));
        Model = model;
        Provider = provider;
        Endpoint = endpoint;
        Tree.Append("model_change", JsonSerializer.SerializeToElement(new { model, provider, endpoint }));
    }

    /// <summary>Rollback a model change that failed before becoming authoritative.</summary>
    public void RevertModel(string model, string? endpoint, string? previousHead, string? provider = null)
    {
        Model = model;
        Provider = provider;
        Endpoint = endpoint;
        Tree.Select(previousHead);
    }

    // M.E.AI deliberately does not serialize function exceptions. Capture failures explicitly
    // so a failed invocation cannot become successful after restoring a branch.
    public void Append(ChatMessage message)
    {
        var errors = message.Contents.Select((content, index) => new { content, index })
            .Where(item => item.content is FunctionResultContent { Exception: not null } or FunctionCallContent { Exception: not null })
            .Select(item => new ToolError(item.index, item.content is FunctionResultContent ? "result" : "call",
                item.content is FunctionResultContent result ? result.Exception!.Message : ((FunctionCallContent)item.content).Exception!.Message))
            .ToArray();
        Tree.Append("chat", JsonSerializer.SerializeToElement(new ChatRecord(
            JsonSerializer.SerializeToElement(message, AIJsonUtilities.DefaultOptions), errors)));
    }

    public void AppendBashExecution(BashExecutionRecord execution)
    {
        ArgumentNullException.ThrowIfNull(execution);
        if (execution.Command is null || execution.Output is null)
            throw new InvalidDataException("A Bash execution requires a command and output.");
        Tree.Append("bash_execution", JsonSerializer.SerializeToElement(execution, BashExecutionJsonOptions));
    }

    public List<ChatMessage> ActiveMessages() => Tree.ActivePath()
        .Select(ContextMessageForNode)
        .Where(message => message is not null)
        .Cast<ChatMessage>()
        .ToList();

    /// <summary>Persist provider billing metadata without adding it to model context.</summary>
    public void AppendUsage(UsageRecord usage)
    {
        ValidateUsage(usage, Tree.HeadId ?? "new entry");
        Tree.Append("usage", JsonSerializer.SerializeToElement(usage));
    }

    public IReadOnlyList<UsageRecord> ActiveUsage() => Tree.ActivePath()
        .Where(node => node.Type == "usage")
        .Select(node => node.Payload.Deserialize<UsageRecord>() ??
            throw new InvalidDataException($"Invalid usage record at {node.Id}."))
        .ToArray();

    /// <summary>Record an in-flight-only summary so later budget checks never mistake its lower provider usage for raw context.</summary>
    public void MarkInFlightProjection() => Tree.Append("context_projection", JsonSerializer.SerializeToElement(new { }));

    /// <summary>Latest provider-reported context after the current compaction boundary.</summary>
    public long? LatestContextUsageTokens()
    {
        var path = Tree.ActivePath();
        var compactAt = path.ToList().FindLastIndex(node => node.Type == "compaction");
        if (path.Skip(compactAt + 1).Any(node => node.Type == "context_projection")) return null;
        return path.Skip(compactAt + 1).Where(node => node.Type == "usage")
            .Select(node => node.Payload.Deserialize<UsageRecord>() ??
                throw new InvalidDataException($"Invalid usage record at {node.Id}."))
            .LastOrDefault(usage => usage.Source == "model")?.TotalTokens;
    }

    /// <summary>Model input for the selected path; raw chat entries remain available to history and export.</summary>
    public List<ChatMessage> ContextMessages()
    {
        var path = Tree.ActivePath();
        var compactAt = path.ToList().FindLastIndex(node => node.Type == "compaction");
        var from = 0;
        var context = new List<ChatMessage>();
        if (compactAt >= 0)
        {
            var compact = path[compactAt].Payload;
            var kept = compact.GetProperty("firstKeptEntryId").GetString();
            from = path.ToList().FindIndex(node => node.Id == kept);
            var piBoundary = PiJsonlSessionInterchange.OriginalEntry(path[compactAt]) is not null;
            if (from < 0 || from >= compactAt || !piBoundary && ContextMessageForNode(path[from])?.Role != ChatRole.User)
                throw new InvalidDataException("Invalid compaction boundary.");
            if (PiJsonlSessionInterchange.CompactionSystemMessage(path[compactAt]) is { } systemMessage)
                context.Add(systemMessage);
            context.Add(new ChatMessage(ChatRole.User,
                "[Summary of earlier conversation; original turns remain in session history.]\n" + compact.GetProperty("summary").GetString()));
        }
        var edits = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var node in path.Skip(from))
            if (PiJsonlSessionInterchange.TryGetContextEdit(node, out var targetId, out var replacement))
                edits[targetId] = replacement;
        foreach (var node in path.Skip(from))
        {
            var message = ContextMessageForNode(node);
            if (message is null) continue;
            if (edits.TryGetValue(node.Id, out var replacement))
                message = PiJsonlSessionInterchange.ApplyContextEdit(node, message, replacement);
            if (message is not null) context.Add(message);
        }
        return context;
    }

    /// <summary>Keep the latest whole user turn; never split a tool call/result group.</summary>
    public CompactionPlan? PrepareCompaction(int? keepRecentTokens = null)
    {
        var path = Tree.ActivePath();
        var compactAt = path.ToList().FindLastIndex(node => node.Type == "compaction");
        var first = compactAt < 0 ? 0 : path.ToList().FindIndex(node =>
            node.Id == path[compactAt].Payload.GetProperty("firstKeptEntryId").GetString());
        if (keepRecentTokens < 0) throw new ArgumentOutOfRangeException(nameof(keepRecentTokens));
        // Select a user-turn boundary backwards; every tool call and result in a turn stays together.
        var latestUser = path.ToList().FindLastIndex(node => ContextMessageForNode(node)?.Role == ChatRole.User);
        if (latestUser <= first) return null;
        var boundary = latestUser;
        if (keepRecentTokens is int minimum)
        {
            long estimated = 0;
            var turns = path.Skip(first).Select((node, index) => (node, index: index + first))
                .Where(item => ContextMessageForNode(item.node)?.Role == ChatRole.User)
                .Select(item => item.index).ToArray();
            for (var i = turns.Length - 1; i >= 0; i--)
            {
                var start = turns[i];
                var end = i + 1 < turns.Length ? turns[i + 1] : path.Count;
                foreach (var message in path.Skip(start).Take(end - start).Select(ContextMessageForNode)
                             .Where(message => message is not null).Cast<ChatMessage>())
                    estimated += AutoCompactionPolicy.Estimate([message], "") - 512;
                boundary = start;
                if (estimated >= minimum) break;
            }
        }
        if (boundary <= first) return null;
        var context = ContextMessages();
        var keptCount = path.Skip(boundary).Count(node => ContextMessageForNode(node) is not null);
        return new CompactionPlan(path[boundary].Id, context.Take(context.Count - keptCount).ToArray());
    }

    public void AppendCompaction(CompactionPlan plan, string summary, int? keepRecentTokens = null)
    {
        if (string.IsNullOrWhiteSpace(summary) || summary.Length > 64 * 1024)
            throw new InvalidDataException("Compaction summary must be nonempty and at most 64KB.");
        if (PrepareCompaction(keepRecentTokens)?.FirstKeptEntryId != plan.FirstKeptEntryId)
            throw new InvalidOperationException("Conversation changed during compaction.");
        Tree.Append("compaction", JsonSerializer.SerializeToElement(new { summary, firstKeptEntryId = plan.FirstKeptEntryId }));
    }

    internal static ChatMessage RestoreEntry(ConversationNode entry) => entry.Type == "chat"
        ? Restore(entry.Payload, entry.Id) : throw new ArgumentException("Not a chat entry.", nameof(entry));

    internal static ChatMessage BashExecutionContextMessage(BashExecutionRecord execution)
    {
        var text = $"Ran `{execution.Command}`\n" +
            (execution.Output.Length == 0 ? "(no output)" : $"```\n{execution.Output}\n```");
        if (execution.Cancelled) text += "\n\n(command cancelled)";
        else if (execution.ExitCode is { } exitCode && exitCode != 0) text += $"\n\nCommand exited with code {exitCode}";
        if (execution.Truncated && execution.FullOutputPath is { } fullOutputPath)
            text += $"\n\n[Output truncated. Full output: {fullOutputPath}]";
        return new ChatMessage(ChatRole.User, text);
    }

    private static ChatMessage? ContextMessageForNode(ConversationNode node)
    {
        if (node.Type == "chat") return Restore(node.Payload, node.Id);
        if (node.Type is "custom_message" or "branch_summary")
            return PiJsonlSessionInterchange.ImportedContextMessage(node);
        if (node.Type != "bash_execution") return null;
        var execution = RestoreBashExecution(node.Payload, node.Id);
        return execution.ExcludeFromContext ? null : BashExecutionContextMessage(execution);
    }

    private static BashExecutionRecord RestoreBashExecution(JsonElement payload, string id)
    {
        if (!payload.TryGetProperty("command", out var command) || command.ValueKind != JsonValueKind.String ||
            command.GetString() is null ||
            !payload.TryGetProperty("output", out var output) || output.ValueKind != JsonValueKind.String ||
            !payload.TryGetProperty("exitCode", out var exitCode) ||
            (exitCode.ValueKind is not (JsonValueKind.Null or JsonValueKind.Number) ||
                exitCode.ValueKind == JsonValueKind.Number && !exitCode.TryGetInt32(out _)) ||
            !payload.TryGetProperty("cancelled", out var cancelled) || cancelled.ValueKind is not (JsonValueKind.True or JsonValueKind.False) ||
            !payload.TryGetProperty("truncated", out var truncated) || truncated.ValueKind is not (JsonValueKind.True or JsonValueKind.False) ||
            !payload.TryGetProperty("fullOutputPath", out var fullOutputPath) ||
                (fullOutputPath.ValueKind is not (JsonValueKind.Null or JsonValueKind.String)) ||
            !payload.TryGetProperty("excludeFromContext", out var excludeFromContext) ||
                excludeFromContext.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            throw new InvalidDataException($"Invalid Bash execution at {id}.");
        var execution = payload.Deserialize<BashExecutionRecord>(BashExecutionJsonOptions);
        if (execution is null)
            throw new InvalidDataException($"Invalid Bash execution at {id}.");
        return execution;
    }

    private static ChatMessage Restore(JsonElement payload, string id)
    {
        var record = payload.Deserialize<ChatRecord>() ?? throw new InvalidDataException($"Missing message at {id}.");
        if (record.Message.ValueKind != JsonValueKind.Object || record.Errors is null)
            throw new InvalidDataException($"Incomplete chat record at {id}.");
        var message = record.Message.Deserialize<ChatMessage>(AIJsonUtilities.DefaultOptions)
            ?? throw new InvalidDataException($"Missing message at {id}.");
        foreach (var error in record.Errors)
        {
            if (error.Index < 0 || error.Index >= message.Contents.Count) throw new InvalidDataException($"Invalid tool failure at {id}.");
            var exception = new ToolFailureException(error.Message);
            if (error.Type == "result" && message.Contents[error.Index] is FunctionResultContent result) result.Exception = exception;
            else if (error.Type == "call" && message.Contents[error.Index] is FunctionCallContent call) call.Exception = exception;
            else throw new InvalidDataException($"Invalid tool failure kind at {id}.");
        }
        return message;
    }

    /// <summary>Close a crash-interrupted run without guessing which side effects happened.
    /// No tool is invoked during recovery; the next model turn sees an explicit warning.</summary>
    public bool RecoverIncomplete()
    {
        var path = Tree.ActivePath();
        var start = path.LastOrDefault(node => node.Type == "run_started");
        if (start is null) return false;
        var runId = start.Payload.GetProperty("runId").GetString();
        if (path.Any(node => node.Type == "run_recovered" && node.Payload.GetProperty("runId").GetString() == runId) ||
            path.Any(node => node.Type == "run_finished" && node.Payload.GetProperty("runId").GetString() == runId &&
                node.Payload.GetProperty("completed").GetBoolean())) return false;
        var prompt = start.Payload.GetProperty("prompt").GetString();
        var intents = path.SkipWhile(node => node.Id != start.Id).Where(node => node.Type == "tool_intent" &&
            node.Payload.GetProperty("runId").GetString() == runId).ToArray();
        var outcomes = path.Where(node => (node.Type is "tool_outcome" or "tool_skipped") &&
            node.Payload.GetProperty("runId").GetString() == runId)
            .Select(node => node.Payload.GetProperty("operationId").GetString()).ToHashSet();
        var unknown = intents.Where(node => !outcomes.Contains(node.Payload.GetProperty("operationId").GetString()))
            .Select(node => node.Payload.GetProperty("name").GetString()).ToArray();
        Tree.Append("run_recovered", JsonSerializer.SerializeToElement(new { runId, unknownOperations = unknown }));
        var warning = unknown.Length == 0 ? "No tool outcome is unknown."
            : $"Outcome UNKNOWN for {string.Join(", ", unknown)}; a side effect may already have happened. Inspect before repeating any operation.";
        var progress = path.LastOrDefault(node => node.Type == "assistant_progress" &&
            node.Payload.GetProperty("runId").GetString() == runId)?.Payload.GetProperty("text").GetString();
        var fragment = string.IsNullOrEmpty(progress) ? "" : $" Last checkpointed assistant text: {progress[..Math.Min(progress.Length, 2048)]}.";
        Append(new ChatMessage(ChatRole.User,
            $"[Recovery notice: the previous request '{prompt}' was interrupted. {warning}{fragment} Uncheckpointed output may be missing. Do not automatically repeat it.]"));
        return true;
    }

    public IReadOnlyList<(string Id, string Text)> ForkableUserMessages()
    {
        var path = Tree.ActivePath();
        var start = path.ToList().FindLastIndex(node => node.Type == "model_change") + 1;
        return path.Skip(start).Where(node => node.Type == "chat")
            .Select(node => (node.Id, Message: RestoreEntry(node)))
            .Where(item => item.Message.Role == ChatRole.User && item.Message.Contents.All(content => content is TextContent) &&
                !string.IsNullOrWhiteSpace(item.Message.Text))
            .Select(item => (item.Id, item.Message.Text))
            .ToArray();
    }

    public IReadOnlyList<(string Id, string Text)> UserMessagesForForking() => Tree.Entries
        .Where(node => node.Type == "chat")
        .Select(node => (node.Id, Message: RestoreEntry(node)))
        .Where(item => item.Message.Role == ChatRole.User && !string.IsNullOrWhiteSpace(item.Message.Text))
        .Select(item => (item.Id, item.Message.Text))
        .ToArray();

    /// <summary>Create a separate session ending immediately before a selected user message.
    /// Return its editable prompt without silently sending it to the provider.</summary>
    public (ConversationSession Session, string Prompt) ForkAtUser(string id)
    {
        var entry = Tree.ActivePath().FirstOrDefault(node => node.Id == id)
            ?? throw new ArgumentException("Select a user message on the active branch.", nameof(id));
        if (entry.Type != "chat") throw new ArgumentException("Select a user message.", nameof(id));
        var message = RestoreEntry(entry);
        if (message.Role != ChatRole.User || message.Contents.Any(content => content is not TextContent) ||
            string.IsNullOrWhiteSpace(message.Text))
            throw new ArgumentException("Select a text-only user message.", nameof(id));
        if (Tree.ActivePath().SkipWhile(node => node.Id != id).Any(node => node.Type == "model_change"))
            throw new InvalidOperationException("Forking across a model change requires per-branch provider selection.");
        var previous = Tree.ClonePath(entry.ParentId);
        return (new ConversationSession(Guid.NewGuid().ToString("N"), WorkingDirectory, Model, Endpoint, Provider, Name, previous), message.Text);
    }

    public ConversationSession Fork() => new(Guid.NewGuid().ToString("N"), WorkingDirectory, Model, Endpoint, Provider, Name, Tree.CloneActivePath());

    public string ToJson()
    {
        var document = new Document(FormatVersion, Id, WorkingDirectory, Model, Endpoint, Name, Tree.HeadId,
            Tree.Entries.ToArray(), Provider, PiJsonlHeader);
        return JsonSerializer.Serialize(document);
    }

    public static ConversationSession Parse(string json)
    {
        var document = JsonSerializer.Deserialize<Document>(json) ?? throw new InvalidDataException("Empty session.");
        if (document.Version is not (1 or FormatVersion) || string.IsNullOrWhiteSpace(document.Id) ||
            string.IsNullOrWhiteSpace(document.WorkingDirectory) || string.IsNullOrWhiteSpace(document.Model) || document.Entries is null)
            throw new InvalidDataException("Unsupported or incomplete session document.");
        if (document.Entries.Any(entry => entry.Type == "chat" && entry.Payload.ValueKind != JsonValueKind.Object))
            throw new InvalidDataException("Session contains an invalid chat entry.");
        if (document.Entries.Any(entry => entry.Type == "bash_execution" && entry.Payload.ValueKind != JsonValueKind.Object))
            throw new InvalidDataException("Session contains an invalid Bash execution entry.");
        if (document.Version == 1 && document.Entries.Any(entry => entry.Type == "chat" &&
            (entry.Payload.Deserialize<ChatMessage>(AIJsonUtilities.DefaultOptions)?.Contents.Any(content =>
                content is FunctionCallContent or FunctionResultContent) ?? false)))
            throw new InvalidDataException("v1 tool turns did not persist failure state; refusing an unsafe import.");
        var entries = document.Version == 1
            ? document.Entries.Select(entry => entry.Type == "chat"
                ? entry with { Payload = JsonSerializer.SerializeToElement(new ChatRecord(entry.Payload, [])) }
                : entry).ToArray()
            : document.Entries;
        foreach (var entry in entries) ValidateCheckpoint(entry);
        // Validate every branch, not merely the currently selected path.
        foreach (var entry in entries.Where(entry => entry.Type == "chat")) _ = Restore(entry.Payload, entry.Id);
        foreach (var entry in entries.Where(entry => entry.Type == "bash_execution"))
            _ = RestoreBashExecution(entry.Payload, entry.Id);
        foreach (var entry in entries.Where(entry => entry.Type == "usage"))
            ValidateUsage(entry.Payload.Deserialize<UsageRecord>() ??
                throw new InvalidDataException($"Invalid usage record at {entry.Id}."), entry.Id);
        var tree = new ConversationTree(entries);
        foreach (var node in entries.Where(entry => entry.Type == "compaction"))
        {
            if (node.Payload.ValueKind != JsonValueKind.Object ||
                !node.Payload.TryGetProperty("summary", out var summary) || summary.ValueKind != JsonValueKind.String ||
                string.IsNullOrWhiteSpace(summary.GetString()) || summary.GetString()!.Length > 64 * 1024 ||
                !node.Payload.TryGetProperty("firstKeptEntryId", out var kept) || kept.ValueKind != JsonValueKind.String)
                throw new InvalidDataException($"Invalid compaction at {node.Id}.");
            tree.Select(node.ParentId);
            var path = tree.ActivePath();
            var boundary = path.ToList().FindIndex(entry => entry.Id == kept.GetString());
            var previous = path.ToList().FindLastIndex(entry => entry.Type == "compaction");
            if (boundary <= previous || boundary >= path.Count ||
                PiJsonlSessionInterchange.OriginalEntry(node) is null && ContextMessageForNode(path[boundary])?.Role != ChatRole.User)
                throw new InvalidDataException($"Invalid compaction boundary at {node.Id}.");
        }
        tree.Select(document.HeadId); // An explicit null selection is distinct from the last appended entry.
        return new ConversationSession(document.Id, document.WorkingDirectory, document.Model, document.Endpoint,
            document.Provider, document.Name, tree, document.PiHeader);
    }

    private static void ValidateUsage(UsageRecord usage, string id)
    {
        if (string.IsNullOrWhiteSpace(usage.Model) || string.IsNullOrWhiteSpace(usage.Source) ||
            usage.InputTokens < 0 || usage.OutputTokens < 0 || usage.CachedInputTokens < 0 ||
            usage.ReasoningTokens < 0 || usage.TotalTokens < 0 || usage.Cost < 0)
            throw new InvalidDataException($"Invalid usage record at {id}.");
    }

    private static void ValidateCheckpoint(ConversationNode node)
    {
        if (node.Type is not ("run_started" or "run_finished" or "run_recovered" or "tool_intent" or "tool_outcome" or "tool_skipped" or "assistant_progress")) return;
        var value = node.Payload;
        static bool HasString(JsonElement element, string key) => element.TryGetProperty(key, out var field) &&
            field.ValueKind == JsonValueKind.String && !string.IsNullOrEmpty(field.GetString());
        if (value.ValueKind != JsonValueKind.Object || !HasString(value, "runId") ||
            ((node.Type is "tool_intent" or "tool_outcome" or "tool_skipped") && !HasString(value, "operationId")) ||
            (node.Type == "run_started" && !HasString(value, "prompt")) ||
            (node.Type == "tool_intent" && (!HasString(value, "name") ||
                !value.TryGetProperty("arguments", out var arguments) || arguments.ValueKind != JsonValueKind.Object)) ||
            (node.Type == "assistant_progress" && !HasString(value, "text")) ||
            (node.Type == "run_finished" && (!value.TryGetProperty("completed", out var completed) ||
                completed.ValueKind is not (JsonValueKind.True or JsonValueKind.False))) ||
            (node.Type == "run_recovered" && (!value.TryGetProperty("unknownOperations", out var unknown) ||
                unknown.ValueKind != JsonValueKind.Array)))
            throw new InvalidDataException($"Invalid checkpoint {node.Type} at {node.Id}.");
    }

    public sealed record CompactionPlan(string FirstKeptEntryId, IReadOnlyList<ChatMessage> MessagesToSummarize);

    private sealed record ChatRecord(JsonElement Message, ToolError[]? Errors, JsonElement? PiEntry = null);
    private sealed record ToolError(int Index, string Type, string Message);
    private sealed record Document(int Version, string Id, string WorkingDirectory, string Model, string? Endpoint,
        string? Name, string? HeadId, ConversationNode[] Entries, string? Provider = null, JsonElement? PiHeader = null);
}
