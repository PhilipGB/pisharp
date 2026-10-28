using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Microsoft.Extensions.AI;
using PiSharp.Core;
using PiSharp.Runtime.Extensions;
using PiSharp.Runtime.Tools;
using NativeExtensions = PiSharp.Extensions.Abstractions;

using static PiSharp.Runtime.Sessions.ConversationMessageCodec;
using static PiSharp.Runtime.Sessions.ConversationContextProjection;

namespace PiSharp.Runtime.Sessions;

/// <summary>Application-owned conversation. MAF sessions are rebuilt from the selected branch.</summary>
public sealed class ConversationSession
{
    public const int FormatVersion = 2;
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

    /// <summary>Appends an entry that sets or clears a label on an existing conversation-tree entry.</summary>
    public SessionLabelChange BeginSessionLabelChange(string entryId, string? label)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(entryId);
        if (!Tree.Entries.Any(entry => entry.Id == entryId))
            throw new KeyNotFoundException($"Entry {entryId} not found");

        var previousHeadId = Tree.HeadId;
        var value = string.IsNullOrEmpty(label) ? null : label;
        var entry = Tree.Append("label", JsonSerializer.SerializeToElement(new { targetId = entryId, label = value }));
        return new SessionLabelChange(this, entry, previousHeadId);
    }

    /// <summary>Returns the current branch's label for an entry, if one is set.</summary>
    public string? GetEntryLabel(string entryId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(entryId);
        if (!Tree.Entries.Any(entry => entry.Id == entryId))
            throw new KeyNotFoundException($"Entry {entryId} not found");

        string? label = null;
        foreach (var entry in Tree.ActivePath())
        {
            if (entry.Type != "label" || entry.Payload.ValueKind != JsonValueKind.Object ||
                !entry.Payload.TryGetProperty("targetId", out var target) || target.ValueKind != JsonValueKind.String ||
                target.GetString() != entryId)
                continue;

            label = entry.Payload.TryGetProperty("label", out var value) && value.ValueKind == JsonValueKind.String
                ? value.GetString() : null;
            if (string.IsNullOrEmpty(label)) label = null;
        }
        return label;
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

    private void RollbackSessionLabelChange(ConversationNode entry, string? previousHeadId) =>
        Tree.RollbackAppend(entry, previousHeadId);

    /// <summary>A reversible label append committed after the session file has been saved.</summary>
    public sealed class SessionLabelChange : IDisposable
    {
        private ConversationSession? _session;
        private readonly ConversationNode _entry;
        private readonly string? _previousHeadId;

        internal SessionLabelChange(ConversationSession session, ConversationNode entry, string? previousHeadId)
        {
            _session = session;
            _entry = entry;
            _previousHeadId = previousHeadId;
        }

        public void Commit() => _session = null;

        public void Dispose()
        {
            var session = _session;
            _session = null;
            session?.RollbackSessionLabelChange(_entry, _previousHeadId);
        }
    }

    internal SessionExtensionStateChange BeginExtensionStateAppend(string customType, JsonElement data,
        string? source)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(customType);
        if (customType.Length > 256)
            throw new ArgumentException("Session entry type cannot exceed 256 characters.", nameof(customType));
        if (data.ValueKind == JsonValueKind.Undefined)
            throw new ArgumentException("Session entry data must be a JSON value.", nameof(data));
        if (System.Text.Encoding.UTF8.GetByteCount(data.GetRawText()) > 1024 * 1024)
            throw new ArgumentException("Session entry data cannot exceed 1 MiB.", nameof(data));

        var previousHeadId = Tree.HeadId;
        var entry = Tree.Append("extension_state", JsonSerializer.SerializeToElement(new
        {
            customType,
            data = data.Clone(),
            source
        }));
        return new SessionExtensionStateChange(this, entry, previousHeadId,
            ProjectExtensionStateEntry(entry));
    }

    private void RollbackExtensionStateAppend(ConversationNode entry, string? previousHeadId) =>
        Tree.RollbackAppend(entry, previousHeadId);

    internal IReadOnlyList<NativeExtensions.PiSharpExtensionSessionEntry> ExtensionStateEntries() =>
        Array.AsReadOnly(Tree.ActivePath().Where(node => node.Type == "extension_state")
            .Select(ProjectExtensionStateEntry).ToArray());

    private static NativeExtensions.PiSharpExtensionSessionEntry ProjectExtensionStateEntry(ConversationNode node)
    {
        var payload = node.Payload;
        if (payload.ValueKind != JsonValueKind.Object ||
            !payload.TryGetProperty("customType", out var type) || type.ValueKind != JsonValueKind.String ||
            string.IsNullOrWhiteSpace(type.GetString()) ||
            !payload.TryGetProperty("data", out var data) || data.ValueKind == JsonValueKind.Undefined)
            throw new InvalidDataException($"Invalid extension state entry at {node.Id}.");
        var source = payload.TryGetProperty("source", out var sourceValue) &&
            sourceValue.ValueKind == JsonValueKind.String ? sourceValue.GetString() : null;
        return new NativeExtensions.PiSharpExtensionSessionEntry(node.Id, type.GetString()!, data,
            node.Timestamp, source);
    }

    internal sealed class SessionExtensionStateChange : IDisposable
    {
        private ConversationSession? _session;
        private readonly ConversationNode _entry;
        private readonly string? _previousHeadId;

        internal SessionExtensionStateChange(ConversationSession session, ConversationNode entry,
            string? previousHeadId, NativeExtensions.PiSharpExtensionSessionEntry projectedEntry)
        {
            _session = session;
            _entry = entry;
            _previousHeadId = previousHeadId;
            Entry = projectedEntry;
        }

        internal NativeExtensions.PiSharpExtensionSessionEntry Entry { get; }

        internal void Commit() => _session = null;

        public void Dispose()
        {
            var session = _session;
            _session = null;
            session?.RollbackExtensionStateAppend(_entry, _previousHeadId);
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
        if (PiSharpCustomMessageProjection.TryRead(message, out var customMessage))
        {
            message.MessageId = AppendCustomMessage(customMessage).Id;
            return;
        }
        Tree.Append("chat", ConversationMessageCodec.EncodeMessage(message, nestedToolCalls));
    }

    internal ConversationNode AppendCustomMessage(PiSharpCustomMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        ArgumentException.ThrowIfNullOrWhiteSpace(message.CustomType);
        ArgumentNullException.ThrowIfNull(message.Content);

        var timestamp = DateTimeOffset.UtcNow;
        var timestampUnixMilliseconds = timestamp.ToUnixTimeMilliseconds();
        var content = new[] { new { type = "text", text = message.Content } };
        var piEntry = JsonSerializer.SerializeToElement(new
        {
            type = "custom_message",
            customType = message.CustomType,
            content,
            display = message.Display,
            timestamp = timestampUnixMilliseconds
        });
        var payload = JsonSerializer.SerializeToElement(new
        {
            customType = message.CustomType,
            content,
            display = message.Display,
            source = message.Source,
            piOriginalEntry = piEntry
        });
        return Tree.Append("custom_message", payload, timestamp);
    }

    /// <summary>Returns the active branch's last persisted declaration set, if one exists.</summary>
    internal IReadOnlyList<string>? ActiveToolLoadout()
    {
        var path = Tree.ActivePath();
        var latestRecord = path.Select((node, index) => (Payload: ToolLoadoutPayload(node), Index: index))
            .LastOrDefault(item => item.Payload is not null);
        var payload = latestRecord.Payload;
        if (payload is null || path.Skip(latestRecord.Index + 1).Any(node =>
                (ConversationContextProjection.ContextMessageForNode(node) ??
                    (node.Type == "compaction" ? PiJsonlEntryCodec.CompactionSystemMessage(node) : null)) is { } message &&
                SystemMessageTranscript.HasToolDeclarations(message)))
        {
            var messages = ContextMessages();
            return messages.Any(SystemMessageTranscript.HasToolDeclarations)
                ? SystemMessageTranscript.CurrentTools(messages).Select(tool => tool.GetProperty("name").GetString()!).ToArray()
                : null;
        }
        if (!payload.Value.TryGetProperty("activeTools", out var tools) || tools.ValueKind != JsonValueKind.Array ||
            tools.EnumerateArray().Any(item => item.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(item.GetString())))
            throw new InvalidDataException("Invalid tool loadout in the selected conversation branch.");
        return tools.EnumerateArray().Select(item => item.GetString()!).Distinct(StringComparer.Ordinal).ToArray();
    }

    private static JsonElement? ToolLoadoutPayload(ConversationNode node)
    {
        if (node.Type == "tool_loadout") return node.Payload;
        if (PiJsonlEntryCodec.OriginalEntry(node) is not { } entry ||
            PiJsonlEntryCodec.StringProperty(entry, "type") != "custom" ||
            PiJsonlEntryCodec.StringProperty(entry, "customType") != "pisharp.tool_loadout" ||
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
                PiJsonlEntryCodec.StringProperty(value, "provider") != provider ||
                PiJsonlEntryCodec.StringProperty(value, "modelId") != modelId ||
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
        if (PiJsonlEntryCodec.OriginalEntry(node) is not { } entry ||
            PiJsonlEntryCodec.StringProperty(entry, "type") != "custom" ||
            PiJsonlEntryCodec.StringProperty(entry, "customType") != "pi.virtual-model-state" ||
            !entry.TryGetProperty("data", out var payload) || payload.ValueKind != JsonValueKind.Object)
            return null;
        return payload.Clone();
    }

    private static JsonElement? RuntimePayload(ConversationNode node, string kind)
    {
        if (node.Type == kind) return node.Payload;
        if (PiJsonlEntryCodec.OriginalEntry(node) is not { } entry ||
            PiJsonlEntryCodec.StringProperty(entry, "type") != "custom" ||
            PiJsonlEntryCodec.StringProperty(entry, "customType") != "pisharp." + kind ||
            !entry.TryGetProperty("data", out var payload) || payload.ValueKind != JsonValueKind.Object)
            return null;
        return payload.Clone();
    }

    public void AppendBashExecution(BashExecutionRecord execution) =>
        Tree.Append("bash_execution", ConversationMessageCodec.EncodeBashExecution(execution));

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
        NativeSessionDocumentCodec.ValidateUsage(usage, Tree.HeadId ?? "new entry");
        Tree.Append("usage", JsonSerializer.SerializeToElement(usage));
    }

    /// <summary>Appends a Pi-compatible summary for the branch that was left during tree navigation.</summary>
    public ConversationNode AppendBranchSummary(string fromId, string summary, JsonElement? details = null,
        bool fromExtension = false, UsageRecord? usage = null)
    {
        if (string.IsNullOrWhiteSpace(fromId)) throw new ArgumentException("A source branch is required.", nameof(fromId));
        if (string.IsNullOrWhiteSpace(summary) || summary.Length > 64 * 1024)
            throw new InvalidDataException("Branch summary must be nonempty and at most 64KB.");
        if (details is { ValueKind: JsonValueKind.Undefined })
            throw new ArgumentException("Branch summary details must be a JSON value when supplied.", nameof(details));
        if (usage is not null) NativeSessionDocumentCodec.ValidateUsage(usage, Tree.HeadId ?? "new entry");

        object? projectedUsage = usage is null ? null : PiJsonlEntryCodec.ProjectUsage(usage);
        var payload = JsonSerializer.SerializeToElement(new
        {
            fromId,
            summary,
            details,
            fromHook = fromExtension,
            usage = projectedUsage,
            nativeUsage = usage
        });
        return Tree.Append("branch_summary", payload);
    }

    public IReadOnlyList<UsageRecord> ActiveUsage()
    {
        var usage = new List<UsageRecord>();
        foreach (var node in Tree.ActivePath())
        {
            if (node.Type == "usage")
            {
                usage.Add(node.Payload.Deserialize<UsageRecord>() ??
                    throw new InvalidDataException($"Invalid usage record at {node.Id}."));
            }
            else if (PiJsonlEntryCodec.ImportedUsage(node) is { } importedUsage)
            {
                usage.Add(importedUsage);
            }
        }

        return usage;
    }

    /// <summary>Record an in-flight-only summary so later budget checks never mistake its lower provider usage for raw context.</summary>
    public void MarkInFlightProjection() => Tree.Append("context_projection", JsonSerializer.SerializeToElement(new { }));

    /// <summary>Latest provider-reported context after the current compaction boundary.</summary>
    public long? LatestContextUsageTokens()
    {
        var projection = ProjectContextUsageForEstimate();
        return projection.HasValidAssistantUsage && !projection.IsInvalidated ? projection.UsageTokens : null;
    }

    /// <summary>Plan against context-visible entry boundaries while retaining the canonical tree as source.</summary>
    public CompactionPlan? PrepareCompaction(int? keepRecentTokens = null) =>
        ConversationCompactionCoordinator.PreparePlan(this, keepRecentTokens);

    public void AppendCompaction(CompactionPlan plan, string summary, int? keepRecentTokens = null,
        int tokensBefore = 0, ConversationCompactionDetails? details = null, string? firstKeptEntryId = null,
        JsonElement? extensionDetails = null, bool fromExtension = false, UsageRecord? usage = null)
    {
        if (string.IsNullOrWhiteSpace(summary) || summary.Length > 64 * 1024)
            throw new InvalidDataException("Compaction summary must be nonempty and at most 64KB.");
        if (PrepareCompaction(keepRecentTokens)?.FirstKeptEntryId != plan.FirstKeptEntryId)
            throw new InvalidOperationException("Conversation changed during compaction.");
        var retainedEntryId = firstKeptEntryId ?? plan.FirstKeptEntryId;
        ValidateCompactionBoundary(retainedEntryId);
        var persistedDetails = extensionDetails?.Clone() ?? (details is null
            ? null
            : JsonSerializer.SerializeToElement(details));
        var projectedUsage = usage is null ? (JsonElement?)null :
            JsonSerializer.SerializeToElement(PiJsonlEntryCodec.ProjectUsage(usage));
        Tree.Append("compaction", JsonSerializer.SerializeToElement(new CompactionRecord(summary,
            retainedEntryId, tokensBefore, persistedDetails, fromExtension, projectedUsage, usage)));
    }

    internal void ValidateCompactionBoundary(string firstKeptEntryId)
    {
        var path = Tree.ActivePath();
        var boundaryIndex = path.ToList().FindIndex(entry => entry.Id == firstKeptEntryId);
        var previousCompactionIndex = path.ToList().FindLastIndex(entry => entry.Type == "compaction");
        var role = boundaryIndex < 0 ? null : ConversationContextProjection.ContextMessageForNode(path[boundaryIndex])?.Role;
        if (boundaryIndex <= previousCompactionIndex ||
            role != ChatRole.User && role != ChatRole.Assistant)
            throw new InvalidDataException("Compaction boundary must identify a user or assistant entry on the active branch after the previous compaction.");
        var entries = CompactionContextEntries(out var previousSummary);
        var retainedIndex = entries.ToList().FindIndex(entry => entry.SourceEntryId == firstKeptEntryId);
        var recoveryOmittedBoundary = retainedIndex < 0 && role == ChatRole.Assistant &&
            Enumerable.Range(Math.Max(0, previousCompactionIndex + 1), Math.Max(0, boundaryIndex - previousCompactionIndex - 1))
                .Any(index => ConversationContextProjection.ContextMessageForNode(path[index])?.Role == ChatRole.User &&
                    RecoveryOmittedAssistantAfter(index) == firstKeptEntryId);
        var summarizedPrefix = entries.Where(entry => entry.SourceIndex < boundaryIndex &&
            (previousSummary is null || !entry.IsPreviousSummary)).SelectMany(entry => entry.Messages).ToArray();
        if (retainedIndex < 0 && !recoveryOmittedBoundary ||
            !ConversationCompactionCoordinator.HasCompleteToolCallPairs(summarizedPrefix))
            throw new InvalidDataException("Compaction boundary splits an incomplete tool call/result pair.");
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
        var finished = path.LastOrDefault(node => node.Type == "run_finished" &&
            node.Payload.GetProperty("runId").GetString() == runId);
        var prompt = start.Payload.GetProperty("prompt").GetString();
        var intents = path.SkipWhile(node => node.Id != start.Id).Where(node => node.Type == "tool_intent" &&
            node.Payload.GetProperty("runId").GetString() == runId).ToArray();
        var outcomes = path.Where(node => (node.Type is "tool_outcome" or "tool_skipped") &&
            node.Payload.GetProperty("runId").GetString() == runId)
            .Select(node => node.Payload.GetProperty("operationId").GetString()).ToHashSet();
        var unknown = intents.Where(node => !outcomes.Contains(node.Payload.GetProperty("operationId").GetString()))
            .Select(node => node.Payload.GetProperty("name").GetString()).ToArray();
        var warning = unknown.Length == 0 ? "No tool outcome is unknown."
            : $"Outcome UNKNOWN for {string.Join(", ", unknown)}; a side effect may already have happened. Inspect before repeating any operation.";
        var progress = path.LastOrDefault(node => node.Type == "assistant_progress" &&
            node.Payload.GetProperty("runId").GetString() == runId)?.Payload.GetProperty("text").GetString();
        var hasUncheckpointedOutput = path.SkipWhile(node => node.Id != start.Id).Any(node =>
            node.Type == "interrupted" && node.Payload.TryGetProperty("prompt", out var interruptedPrompt) &&
            interruptedPrompt.ValueKind == JsonValueKind.String && interruptedPrompt.GetString() == prompt &&
            ((node.Payload.TryGetProperty("partialText", out var partialText) &&
              partialText.ValueKind == JsonValueKind.String && !string.IsNullOrEmpty(partialText.GetString())) ||
             (node.Payload.TryGetProperty("partialAssistantText", out var partialAssistantText) &&
              partialAssistantText.ValueKind == JsonValueKind.String &&
              !string.IsNullOrEmpty(partialAssistantText.GetString()))));
        if (finished is not null && finished.Payload.TryGetProperty("aborted", out var aborted) &&
            aborted.ValueKind == JsonValueKind.True && intents.Length == 0 && string.IsNullOrEmpty(progress) &&
            !hasUncheckpointedOutput)
            return false;
        Tree.Append("run_recovered", JsonSerializer.SerializeToElement(new { runId, unknownOperations = unknown }));
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
            var document = new NativeSessionDocumentCodec.Document(FormatVersion, Id, WorkingDirectory, _model, _endpoint, Name, Tree.HeadId,
                Tree.Entries.ToArray(), _provider, PiJsonlHeader, ParentSessionPath);
            return JsonSerializer.Serialize(document);
        }
    }

    public static ConversationSession Parse(string json)
    {
        var (document, tree) = NativeSessionDocumentCodec.Parse(json);
        return new ConversationSession(document.Id, document.WorkingDirectory, document.Model, document.Endpoint,
            document.Provider, document.Name, tree, document.PiHeader, document.ParentSessionPath);
    }

    public sealed record CompactionPlan(string FirstKeptEntryId, IReadOnlyList<ChatMessage> MessagesToSummarize,
        IReadOnlyList<ChatMessage>? TurnPrefixMessages = null, bool IsSplitTurn = false, string? PreviousSummary = null);

    private sealed record CompactionRecord(
        [property: JsonPropertyName("summary")] string Summary,
        [property: JsonPropertyName("firstKeptEntryId")] string FirstKeptEntryId,
        [property: JsonPropertyName("tokensBefore")] int TokensBefore,
        [property: JsonPropertyName("details"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        JsonElement? Details,
        [property: JsonPropertyName("fromHook"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        bool FromExtension,
        [property: JsonPropertyName("usage"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        JsonElement? Usage,
        [property: JsonPropertyName("nativeUsage"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        UsageRecord? NativeUsage);

    internal ConversationContextProjection.ContextUsageProjection ProjectContextUsageForEstimate() =>
        ConversationContextProjection.ProjectContextUsageForEstimate(Tree.ActivePath());

    public List<ChatMessage> ContextMessages() => ConversationContextProjection.ContextMessages(Tree.ActivePath());

    internal IReadOnlyList<ConversationContextProjection.CompactionContextEntry> CompactionContextEntries(out string? previousSummary) =>
        ConversationContextProjection.CompactionContextEntries(Tree.ActivePath(), out previousSummary);

    internal string? RecoveryOmittedAssistantAfter(int sourceIndex) =>
        ConversationContextProjection.RecoveryOmittedAssistantAfter(Tree.ActivePath(), sourceIndex);

}
