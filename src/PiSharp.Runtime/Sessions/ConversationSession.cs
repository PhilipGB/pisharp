using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Microsoft.Extensions.AI;
using PiSharp.Core;
using PiSharp.Runtime.Extensions;
using PiSharp.Runtime.Tools;

namespace PiSharp.Runtime.Sessions;

/// <summary>Application-owned conversation. MAF sessions are rebuilt from the selected branch.</summary>
public sealed class ConversationSession
{
    public const int FormatVersion = 2;
    private static readonly JsonSerializerOptions BashExecutionJsonOptions = new(JsonSerializerDefaults.Web);
    private readonly object _metadataGate = new();
    private string _model;
    private string? _provider;
    private string? _endpoint;
    public string Id { get; }
    public string WorkingDirectory { get; }
    public string Model { get { lock (_metadataGate) return _model; } }
    public string? Provider { get { lock (_metadataGate) return _provider; } }
    public string? Endpoint { get { lock (_metadataGate) return _endpoint; } }
    public string? Name { get; private set; }
    public string? ParentSessionPath { get; }
    internal JsonElement? PiJsonlHeader { get; }
    public ConversationTree Tree { get; }

    public ConversationSession(string workingDirectory, string model, string? endpoint, string? provider = null,
        string? parentSessionPath = null)
        : this(Guid.NewGuid().ToString("N"), Path.GetFullPath(workingDirectory), model, endpoint, provider, null,
            new ConversationTree(), parentSessionPath: parentSessionPath)
    { }

    private ConversationSession(string id, string cwd, string model, string? endpoint, string? provider, string? name,
        ConversationTree tree, JsonElement? piJsonlHeader = null, string? parentSessionPath = null)
    {
        Id = id;
        WorkingDirectory = cwd;
        _model = model;
        _provider = provider;
        _endpoint = endpoint;
        Name = name;
        Tree = tree;
        PiJsonlHeader = piJsonlHeader?.Clone();
        ParentSessionPath = parentSessionPath;
    }

    internal static ConversationSession FromPiJsonl(string id, string cwd, string model, string? provider, string? name,
        ConversationTree tree, JsonElement header) =>
        new(id, cwd, model, null, provider, name, tree, header,
            header.TryGetProperty("parentSession", out var parent) && parent.ValueKind == JsonValueKind.String
                ? parent.GetString() : null);

    internal static ConversationNode ImportedPiMessage(string id, string? parentId, DateTimeOffset timestamp,
        ChatMessage message, JsonElement originalEntry)
    {
        var errors = message.Contents.Select((content, index) => new { content, index })
            .Where(item => item.content is FunctionResultContent { Exception: not null } or FunctionCallContent { Exception: not null })
            .Select(item => new ToolError(item.index, item.content is FunctionResultContent ? "result" : "call",
                item.content is FunctionResultContent result ? result.Exception!.Message : ((FunctionCallContent)item.content).Exception!.Message))
            .ToArray();
        return new(id, parentId, "chat", JsonSerializer.SerializeToElement(new ChatRecord(
            JsonSerializer.SerializeToElement(message, AIJsonUtilities.DefaultOptions), errors, originalEntry.Clone(),
            PiJsonlSessionInterchange.NestedToolCallsFromEntry(originalEntry))), timestamp);
    }

    internal static JsonElement? PiEntryFromChatPayload(JsonElement payload) =>
        payload.ValueKind == JsonValueKind.Object && payload.TryGetProperty("PiEntry", out var entry) &&
        entry.ValueKind == JsonValueKind.Object ? entry.Clone() : null;

    internal static JsonElement? PiEntryFromBashPayload(JsonElement payload) =>
        payload.ValueKind == JsonValueKind.Object && payload.TryGetProperty("piOriginalEntry", out var entry) &&
        entry.ValueKind == JsonValueKind.Object ? entry.Clone() : null;

    public void Rename(string? name) => Name = name;

    public ConversationSession Snapshot()
    {
        lock (_metadataGate)
            return new ConversationSession(Id, WorkingDirectory, _model, _endpoint, _provider, Name,
                Tree.Clone(), PiJsonlHeader, ParentSessionPath);
    }

    public SessionNameChange BeginSessionNameChange(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        var sanitizedName = Regex.Replace(name, "[\\r\\n]+", " ").Trim();
        var previousName = Name;
        var previousHeadId = Tree.HeadId;
        var entry = Tree.Append("session_info", JsonSerializer.SerializeToElement(new { name = sanitizedName }));
        Name = sanitizedName.Length == 0 ? null : sanitizedName;
        return new SessionNameChange(this, entry, previousName, previousHeadId);
    }

    private void RollbackSessionNameChange(ConversationNode entry, string? previousName, string? previousHeadId)
    {
        Tree.RollbackAppend(entry, previousHeadId);
        Name = previousName;
    }

    public sealed class SessionNameChange : IDisposable
    {
        private ConversationSession? _session;
        private readonly ConversationNode _entry;
        private readonly string? _previousName;
        private readonly string? _previousHeadId;

        internal SessionNameChange(ConversationSession session, ConversationNode entry, string? previousName,
            string? previousHeadId)
        {
            _session = session;
            _entry = entry;
            _previousName = previousName;
            _previousHeadId = previousHeadId;
        }

        public void Commit() => _session = null;

        public void Dispose()
        {
            var session = _session;
            _session = null;
            session?.RollbackSessionNameChange(_entry, _previousName, _previousHeadId);
        }
    }

    public void AppendThinkingLevelChange(string level)
    {
        if (string.IsNullOrWhiteSpace(level)) throw new ArgumentException("Thinking level cannot be empty.", nameof(level));
        Tree.Append("thinking_level_change", JsonSerializer.SerializeToElement(new { thinkingLevel = level }));
    }

    internal ConversationNode AppendContextOmission(string targetId)
    {
        if (string.IsNullOrWhiteSpace(targetId)) throw new ArgumentException("A context-edit target is required.", nameof(targetId));
        return Tree.Append("context_edit", JsonSerializer.SerializeToElement(new { targetId, replacement = (string?)null }));
    }

    public void SelectModel(string model, string? endpoint, string? provider = null)
    {
        if (string.IsNullOrWhiteSpace(model)) throw new ArgumentException("Model ID cannot be empty.", nameof(model));
        lock (_metadataGate)
        {
            _model = model;
            _provider = provider;
            _endpoint = endpoint;
            Tree.Append("model_change", JsonSerializer.SerializeToElement(new { model, provider, endpoint }));
        }
    }

    /// <summary>Rollback a model change that failed before becoming authoritative.</summary>
    public void RevertModel(string model, string? endpoint, string? previousHead, string? provider = null)
    {
        lock (_metadataGate)
        {
            _model = model;
            _provider = provider;
            _endpoint = endpoint;
            Tree.Select(previousHead);
        }
    }

    internal void RestoreModelMetadata(string model, string? endpoint, string? provider)
    {
        lock (_metadataGate)
        {
            _model = model;
            _provider = provider;
            _endpoint = endpoint;
        }
    }

    // M.E.AI deliberately does not serialize function exceptions. Capture failures explicitly
    // so a failed invocation cannot become successful after restoring a branch.
    public void Append(ChatMessage message)
        => Append(message, null);

    internal void Append(ChatMessage message, PiSharpNestedToolCalls? nestedToolCalls)
    {
        var errors = message.Contents.Select((content, index) => new { content, index })
            .Where(item => item.content is FunctionResultContent { Exception: not null } or FunctionCallContent { Exception: not null })
            .Select(item => new ToolError(item.index, item.content is FunctionResultContent ? "result" : "call",
                item.content is FunctionResultContent result ? result.Exception!.Message : ((FunctionCallContent)item.content).Exception!.Message))
            .ToArray();
        Tree.Append("chat", JsonSerializer.SerializeToElement(new ChatRecord(
            JsonSerializer.SerializeToElement(message, AIJsonUtilities.DefaultOptions), errors,
            NestedToolCalls: nestedToolCalls)));
    }

    /// <summary>Returns the active branch's last persisted declaration set, if one exists.</summary>
    internal IReadOnlyList<string>? ActiveToolLoadout()
    {
        var payload = Tree.ActivePath().Select(node => ToolLoadoutPayload(node)).LastOrDefault(value => value is not null);
        if (payload is null) return null;
        if (!payload.Value.TryGetProperty("activeTools", out var tools) || tools.ValueKind != JsonValueKind.Array ||
            tools.EnumerateArray().Any(item => item.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(item.GetString())))
            throw new InvalidDataException("Invalid tool loadout in the selected conversation branch.");
        return tools.EnumerateArray().Select(item => item.GetString()!).Distinct(StringComparer.Ordinal).ToArray();
    }

    private static JsonElement? ToolLoadoutPayload(ConversationNode node)
    {
        if (node.Type == "tool_loadout") return node.Payload;
        if (PiJsonlSessionInterchange.OriginalEntry(node) is not { } entry ||
            PiJsonlSessionInterchange.StringProperty(entry, "type") != "custom" ||
            PiJsonlSessionInterchange.StringProperty(entry, "customType") != "pisharp.tool_loadout" ||
            !entry.TryGetProperty("data", out var payload) || payload.ValueKind != JsonValueKind.Object)
            return null;
        return payload.Clone();
    }

    /// <summary>Persist the declarations used by the next request on the selected branch.</summary>
    internal void AppendToolLoadout(IEnumerable<string> activeToolNames)
    {
        ArgumentNullException.ThrowIfNull(activeToolNames);
        var names = activeToolNames.Where(name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.Ordinal).ToArray();
        if (ActiveToolLoadout() is { } current && current.SequenceEqual(names, StringComparer.Ordinal)) return;
        Tree.Append("tool_loadout", JsonSerializer.SerializeToElement(new { activeTools = names }));
    }

    /// <summary>Reads the selected branch's last successful Codemode store snapshot.</summary>
    internal IReadOnlyDictionary<string, JsonElement>? ActiveCodemodeStore()
    {
        var payload = Tree.ActivePath().Select(node => RuntimePayload(node, "codemode_store"))
            .LastOrDefault(value => value is not null);
        if (payload is null) return null;
        if (!payload.Value.TryGetProperty("values", out var values) || values.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("Invalid Codemode store in the selected conversation branch.");
        return values.EnumerateObject().ToDictionary(item => item.Name, item => item.Value.Clone(),
            StringComparer.Ordinal);
    }

    internal void AppendCodemodeStore(IReadOnlyDictionary<string, JsonElement> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        if (JsonSerializer.Serialize(ActiveCodemodeStore()) == JsonSerializer.Serialize(values)) return;
        Tree.Append("codemode_store", JsonSerializer.SerializeToElement(new { values }));
    }

    /// <summary>Returns the latest router state for a logical model on the selected branch.</summary>
    public JsonElement? ActiveVirtualModelState(string provider, string modelId)
    {
        foreach (var node in Tree.ActivePath().Reverse())
        {
            var payload = VirtualModelStatePayload(node);
            if (payload is not { } value ||
                PiJsonlSessionInterchange.StringProperty(value, "provider") != provider ||
                PiJsonlSessionInterchange.StringProperty(value, "modelId") != modelId ||
                !value.TryGetProperty("state", out var state)) continue;
            return state.Clone();
        }
        return null;
    }

    /// <summary>Stores router state as a branch-local entry compatible with Pi JSONL custom entries.</summary>
    public void AppendVirtualModelState(string provider, string modelId, JsonElement state)
    {
        if (string.IsNullOrWhiteSpace(provider) || string.IsNullOrWhiteSpace(modelId))
            throw new ArgumentException("Virtual model provider and id cannot be empty.");
        var current = ActiveVirtualModelState(provider, modelId);
        if (current is { } existing && JsonElement.DeepEquals(existing, state)) return;
        Tree.Append("virtual_model_state", JsonSerializer.SerializeToElement(new
        {
            provider,
            modelId,
            state = state.Clone()
        }));
    }

    private static JsonElement? VirtualModelStatePayload(ConversationNode node)
    {
        if (node.Type == "virtual_model_state") return node.Payload;
        if (PiJsonlSessionInterchange.OriginalEntry(node) is not { } entry ||
            PiJsonlSessionInterchange.StringProperty(entry, "type") != "custom" ||
            PiJsonlSessionInterchange.StringProperty(entry, "customType") != "pi.virtual-model-state" ||
            !entry.TryGetProperty("data", out var payload) || payload.ValueKind != JsonValueKind.Object)
            return null;
        return payload.Clone();
    }

    private static JsonElement? RuntimePayload(ConversationNode node, string kind)
    {
        if (node.Type == kind) return node.Payload;
        if (PiJsonlSessionInterchange.OriginalEntry(node) is not { } entry ||
            PiJsonlSessionInterchange.StringProperty(entry, "type") != "custom" ||
            PiJsonlSessionInterchange.StringProperty(entry, "customType") != "pisharp." + kind ||
            !entry.TryGetProperty("data", out var payload) || payload.ValueKind != JsonValueKind.Object)
            return null;
        return payload.Clone();
    }

    internal static PiSharpNestedToolCalls? NestedToolCallsFor(ConversationNode node)
    {
        if (node.Type != "chat" || node.Payload.ValueKind != JsonValueKind.Object) return null;
        return node.Payload.Deserialize<ChatRecord>()?.NestedToolCalls;
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

    /// <summary>Nested calls grouped by the parent tool-result call ID on the selected branch.</summary>
    public IReadOnlyDictionary<string, PiSharpNestedToolCalls> ActiveNestedToolCallsByResultCallId()
    {
        var calls = new Dictionary<string, PiSharpNestedToolCalls>(StringComparer.Ordinal);
        foreach (var node in Tree.ActivePath())
        {
            if (NestedToolCallsFor(node) is not { } nestedCalls) continue;
            foreach (var callId in RestoreEntry(node).Contents.OfType<FunctionResultContent>().Select(result => result.CallId))
                calls[callId] = nestedCalls;
        }
        return new System.Collections.ObjectModel.ReadOnlyDictionary<string, PiSharpNestedToolCalls>(calls);
    }

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
        var projectedNodes = path;
        var context = new List<ChatMessage>();
        if (compactAt >= 0)
        {
            var compact = path[compactAt].Payload;
            var kept = compact.GetProperty("firstKeptEntryId").GetString();
            from = path.ToList().FindIndex(node => node.Id == kept);
            var piBoundary = PiJsonlSessionInterchange.OriginalEntry(path[compactAt]) is not null;
            if (from < 0 || from >= compactAt)
                throw new InvalidDataException("Invalid compaction boundary.");
            if (!piBoundary)
            {
                var role = ContextMessageForNode(path[from])?.Role;
                if (role != ChatRole.User && role != ChatRole.Assistant)
                    throw new InvalidDataException("Invalid compaction boundary.");
            }
            if (PiJsonlSessionInterchange.CompactionSystemMessage(path[compactAt]) is { } systemMessage)
                context.Add(systemMessage);
            context.Add(CompactionSummaryMessage(compact.GetProperty("summary").GetString() ?? ""));
            projectedNodes = path.Skip(from).Where((node, offset) => from + offset >= compactAt ||
                ContextMessageForNode(node)?.Role != ChatRole.System).ToList();
        }
        var edits = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var node in projectedNodes)
            if (PiJsonlSessionInterchange.TryGetContextEdit(node, out var targetId, out var replacement))
                edits[targetId] = replacement;
        foreach (var entry in ProjectContextEntries(projectedNodes))
        {
            var node = entry.Node;
            var message = entry.Message;
            if (edits.TryGetValue(node.Id, out var replacement))
                message = PiJsonlSessionInterchange.ApplyContextEdit(node, message, replacement);
            if (message is not null) context.Add(message);
        }
        return context;
    }

    internal static ChatMessage CompactionSummaryMessage(string summary) => new(ChatRole.User,
        "The conversation history before this point was compacted into the following summary:\n\n<summary>\n" +
        summary + "\n</summary>");

    /// <summary>Plan against context-visible entry boundaries while retaining the canonical tree as source.</summary>
    public CompactionPlan? PrepareCompaction(int? keepRecentTokens = null) =>
        ConversationCompactionCoordinator.PreparePlan(this, keepRecentTokens);

    internal IReadOnlyList<CompactionContextEntry> CompactionContextEntries(out string? previousSummary)
    {
        var path = Tree.ActivePath();
        var compactionIndex = path.ToList().FindLastIndex(node => node.Type == "compaction");
        previousSummary = compactionIndex < 0
            ? null
            : path[compactionIndex].Payload.GetProperty("summary").GetString() ?? "";
        var retainedFrom = 0;
        if (compactionIndex >= 0)
        {
            var firstKeptId = path[compactionIndex].Payload.GetProperty("firstKeptEntryId").GetString();
            retainedFrom = path.ToList().FindIndex(node => node.Id == firstKeptId);
            if (retainedFrom < 0 || retainedFrom >= compactionIndex)
                throw new InvalidDataException("Invalid compaction boundary.");
        }

        var edits = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        for (var index = retainedFrom; index < path.Count; index++)
        {
            if (index == compactionIndex) continue;
            if (PiJsonlSessionInterchange.TryGetContextEdit(path[index], out var targetId, out var replacement))
                edits[targetId] = replacement;
        }

        var projected = new List<CompactionContextEntry>();
        string? lastAssistantText = null;
        if (compactionIndex >= 0)
        {
            var summary = previousSummary ?? "";
            projected.Add(new CompactionContextEntry(path[compactionIndex].Id, compactionIndex,
                [new ChatMessage(ChatRole.User, summary)], IsPreviousSummary: true));
        }

        for (var index = retainedFrom; index < path.Count; index++)
        {
            if (index == compactionIndex) continue;
            var node = path[index];
            var message = ContextMessageForNode(node);
            if (message is null) continue;
            if (compactionIndex >= 0 && index < compactionIndex && message.Role == ChatRole.System) continue;
            if (edits.TryGetValue(node.Id, out var replacement))
                message = PiJsonlSessionInterchange.ApplyContextEdit(node, message, replacement);
            if (message is null) continue;
            if (node.Type == "interrupted" && message.Role == ChatRole.Assistant && message.Text.Length > 0 &&
                lastAssistantText == message.Text)
                continue;
            if (message.Role == ChatRole.Assistant) lastAssistantText = message.Text;
            projected.Add(new CompactionContextEntry(node.Id, index, [message]));
        }
        return projected;
    }

    /// <summary>
    /// Return the first omitted assistant attempt when every context-visible entry after a
    /// candidate boundary is closed by recovery omission edits. This keeps compaction from
    /// stopping immediately before an invisible failed-attempt suffix.
    /// </summary>
    internal string? RecoveryOmittedAssistantAfter(int sourceIndex)
    {
        var path = Tree.ActivePath();
        if (sourceIndex < 0 || sourceIndex >= path.Count - 1) return null;

        var compactIndex = path.ToList().FindLastIndex(node => node.Type == "compaction");
        var retainedFrom = 0;
        if (compactIndex >= 0)
        {
            var firstKeptId = path[compactIndex].Payload.GetProperty("firstKeptEntryId").GetString();
            retainedFrom = path.ToList().FindIndex(node => node.Id == firstKeptId);
            if (retainedFrom < 0 || retainedFrom >= compactIndex || sourceIndex < retainedFrom) return null;
        }

        var edits = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        for (var index = retainedFrom; index < path.Count; index++)
            if (index != compactIndex && PiJsonlSessionInterchange.TryGetContextEdit(path[index], out var targetId, out var replacement))
                edits[targetId] = replacement;

        var suffix = path.Skip(sourceIndex + 1).ToArray();
        ChatMessage? Project(ConversationNode node)
        {
            var message = ContextMessageForNode(node);
            return message is not null && edits.TryGetValue(node.Id, out var replacement)
                ? PiJsonlSessionInterchange.ApplyContextEdit(node, message, replacement)
                : message;
        }

        bool IsIntrinsicallyVisible(ConversationNode node) =>
            node.Type != "context_edit" && ContextMessageForNode(node) is not null;

        bool IsOmitted(ConversationNode node) =>
            IsIntrinsicallyVisible(node) && Project(node) is null;

        var omittedIds = suffix.Where(IsOmitted).Select(node => node.Id).ToHashSet(StringComparer.Ordinal);
        var hasExternalReplacement = suffix.Any(node =>
            PiJsonlSessionInterchange.TryGetContextEdit(node, out var targetId, out var replacement) &&
            replacement.ValueKind != JsonValueKind.Null && !omittedIds.Contains(targetId));
        var hasOmittedAssistant = suffix.Any(node => omittedIds.Contains(node.Id) &&
            ContextMessageForNode(node)?.Role == ChatRole.Assistant);
        if (!hasOmittedAssistant || hasExternalReplacement || suffix.Any(node =>
                node.Type == "compaction" || IsIntrinsicallyVisible(node) && !omittedIds.Contains(node.Id)))
            return null;

        return suffix.First(node => omittedIds.Contains(node.Id) &&
            ContextMessageForNode(node)?.Role == ChatRole.Assistant).Id;
    }

    public void AppendCompaction(CompactionPlan plan, string summary, int? keepRecentTokens = null,
        int tokensBefore = 0, ConversationCompactionDetails? details = null)
    {
        if (string.IsNullOrWhiteSpace(summary) || summary.Length > 64 * 1024)
            throw new InvalidDataException("Compaction summary must be nonempty and at most 64KB.");
        if (PrepareCompaction(keepRecentTokens)?.FirstKeptEntryId != plan.FirstKeptEntryId)
            throw new InvalidOperationException("Conversation changed during compaction.");
        Tree.Append("compaction", JsonSerializer.SerializeToElement(new CompactionRecord(summary,
            plan.FirstKeptEntryId, tokensBefore, details)));
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
        if (node.Type == "interrupted" &&
            PiJsonlSessionInterchange.StringProperty(node.Payload, "partialAssistantText") is { Length: > 0 } partialText)
            return new ChatMessage(ChatRole.Assistant, partialText);
        if (node.Type is "custom_message" or "branch_summary")
            return PiJsonlSessionInterchange.ImportedContextMessage(node);
        if (node.Type != "bash_execution") return null;
        var execution = RestoreBashExecution(node.Payload, node.Id);
        return execution.ExcludeFromContext ? null : BashExecutionContextMessage(execution);
    }

    private static List<ProjectedContextEntry> ProjectContextEntries(IReadOnlyList<ConversationNode> nodes)
    {
        var entries = new List<ProjectedContextEntry>();
        foreach (var (node, index) in nodes.Select((node, index) => (node, index)))
        {
            var message = ContextMessageForNode(node);
            if (message is null) continue;
            if (node.Type == "interrupted" && message.Text.Length > 0 &&
                entries.LastOrDefault(entry => entry.Message.Role == ChatRole.Assistant)?.Message.Text == message.Text)
                continue;
            entries.Add(new ProjectedContextEntry(node, index, message));
        }
        return entries;
    }

    private sealed record ProjectedContextEntry(ConversationNode Node, int Index, ChatMessage Message);

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
        .Where(item => item.Message.Role == ChatRole.User)
        .Select(item => (item.Id, Text: ExtractUserMessageText(item.Message)))
        .Where(item => item.Text.Length > 0)
        .Select(item => (item.Id, item.Text))
        .ToArray();

    /// <summary>Create a separate session ending immediately before a selected user message.
    /// Return its editable prompt without silently sending it to the provider.</summary>
    public (ConversationSession Session, string Prompt) ForkAtUser(string id, string? parentSessionPath = null)
    {
        if (Tree.Entries.All(node => node.Id != id))
            throw new ArgumentException("Select a user message.", nameof(id));
        var branchPath = Tree.ClonePath(id).ActivePath();
        var entry = branchPath[^1];
        var activePath = Tree.ActivePath();
        var activeIndex = activePath.ToList().FindIndex(node => node.Id == id);
        if (activeIndex >= 0 && activePath.Skip(activeIndex).Any(node => node.Type == "model_change"))
            throw new InvalidOperationException("Forking across a model change requires per-branch provider selection.");
        var branchModelChange = branchPath.Take(branchPath.Count - 1)
            .LastOrDefault(node => node.Type == "model_change");
        if (activeIndex < 0 && branchModelChange is null && activePath.Any(node => node.Type == "model_change"))
            throw new InvalidOperationException("The selected branch model cannot be resolved safely.");
        if (entry.Type != "chat")
            throw new ArgumentException("Select a user message.", nameof(id));
        var message = RestoreEntry(entry);
        if (message.Role != ChatRole.User)
            throw new ArgumentException("Select a user message.", nameof(id));
        var selectedText = ExtractUserMessageText(message);

        var model = Model;
        var endpoint = Endpoint;
        var provider = Provider;
        if (branchModelChange is { } change)
        {
            model = change.Payload.GetProperty("model").GetString() ?? model;
            endpoint = change.Payload.TryGetProperty("endpoint", out var endpointValue) &&
                endpointValue.ValueKind == JsonValueKind.String ? endpointValue.GetString() : null;
            provider = change.Payload.TryGetProperty("provider", out var providerValue) &&
                providerValue.ValueKind == JsonValueKind.String ? providerValue.GetString() : null;
        }

        var previous = Tree.ClonePath(entry.ParentId);
        return (new ConversationSession(Guid.NewGuid().ToString("N"), WorkingDirectory, model, endpoint, provider,
            Name, previous, parentSessionPath: parentSessionPath), selectedText);
    }

    private static string ExtractUserMessageText(ChatMessage message) =>
        string.Concat(message.Contents.OfType<TextContent>().Select(content => content.Text));

    public ConversationSession Fork(string? parentSessionPath = null) =>
        ForkPath(Tree.ActivePath(), parentSessionPath);

    public ConversationSession ForkForSessionReplacement(string? parentSessionPath = null)
    {
        var path = Tree.ActivePath();
        var start = path.LastOrDefault(node => node.Type == "run_started");
        if (start is not null && start.Payload.TryGetProperty("runId", out var runIdValue) &&
            runIdValue.ValueKind == JsonValueKind.String)
        {
            var runId = runIdValue.GetString();
            var settled = path.Any(node => node.Type == "run_recovered" && HasRunId(node, runId)) ||
                path.Any(node => node.Type == "run_finished" && HasRunId(node, runId) &&
                    node.Payload.TryGetProperty("completed", out var completed) && completed.ValueKind == JsonValueKind.True);
            if (!settled)
                path = path.Where(node => !IsExecutionCheckpoint(node, runId)).ToArray();
        }
        return ForkPath(path, parentSessionPath);
    }

    private ConversationSession ForkPath(IReadOnlyList<ConversationNode> path, string? parentSessionPath)
    {
        var entries = path.Select((node, index) => node with { ParentId = index == 0 ? null : path[index - 1].Id }).ToArray();
        var tree = new ConversationTree(entries, entries.LastOrDefault()?.Id);
        return new ConversationSession(Guid.NewGuid().ToString("N"), WorkingDirectory, Model, Endpoint, Provider, Name,
            tree, parentSessionPath: parentSessionPath);
    }

    private static bool IsExecutionCheckpoint(ConversationNode node, string? runId) =>
        (node.Type is "run_started" or "run_finished" or "assistant_progress" or "tool_intent" or "tool_outcome" or "tool_skipped") &&
        HasRunId(node, runId);

    private static bool HasRunId(ConversationNode node, string? runId) =>
        node.Payload.ValueKind == JsonValueKind.Object && node.Payload.TryGetProperty("runId", out var value) &&
        value.ValueKind == JsonValueKind.String && value.GetString() == runId;

    public ConversationSession ForkInto(string workingDirectory, string? parentSessionPath = null) =>
        new(Guid.NewGuid().ToString("N"), Path.GetFullPath(workingDirectory), Model, Endpoint, Provider, Name,
            Tree.Clone(), parentSessionPath: parentSessionPath);

    public string ToJson()
    {
        lock (_metadataGate)
        {
            var document = new Document(FormatVersion, Id, WorkingDirectory, _model, _endpoint, Name, Tree.HeadId,
                Tree.Entries.ToArray(), _provider, PiJsonlHeader, ParentSessionPath);
            return JsonSerializer.Serialize(document);
        }
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
        var tree = ConversationTree.FromEntries(entries);
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
            var boundaryRole = boundary >= 0 ? ContextMessageForNode(path[boundary])?.Role : null;
            if (boundary <= previous || boundary >= path.Count ||
                PiJsonlSessionInterchange.OriginalEntry(node) is null &&
                boundaryRole != ChatRole.User && boundaryRole != ChatRole.Assistant)
                throw new InvalidDataException($"Invalid compaction boundary at {node.Id}.");
        }
        tree.Select(document.HeadId); // An explicit null selection is distinct from the last appended entry.
        return new ConversationSession(document.Id, document.WorkingDirectory, document.Model, document.Endpoint,
            document.Provider, document.Name, tree, document.PiHeader, document.ParentSessionPath);
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

    public sealed record CompactionPlan(string FirstKeptEntryId, IReadOnlyList<ChatMessage> MessagesToSummarize,
        IReadOnlyList<ChatMessage>? TurnPrefixMessages = null, bool IsSplitTurn = false, string? PreviousSummary = null);

    internal sealed record CompactionContextEntry(string? SourceEntryId, int SourceIndex,
        IReadOnlyList<ChatMessage> Messages, bool IsPreviousSummary = false);

    private sealed record CompactionRecord(
        [property: JsonPropertyName("summary")] string Summary,
        [property: JsonPropertyName("firstKeptEntryId")] string FirstKeptEntryId,
        [property: JsonPropertyName("tokensBefore")] int TokensBefore,
        [property: JsonPropertyName("details"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        ConversationCompactionDetails? Details);

    private sealed record ChatRecord(JsonElement Message, ToolError[]? Errors, JsonElement? PiEntry = null,
        [property: JsonPropertyName("nestedCalls"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        PiSharpNestedToolCalls? NestedToolCalls = null);
    private sealed record ToolError(int Index, string Type, string Message);
    private sealed record Document(int Version, string Id, string WorkingDirectory, string Model, string? Endpoint,
        string? Name, string? HeadId, ConversationNode[] Entries, string? Provider = null, JsonElement? PiHeader = null,
        string? ParentSessionPath = null);
}
