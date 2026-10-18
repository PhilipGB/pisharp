using System.Text.Json;
using Microsoft.Extensions.AI;

namespace PiSharp.Extensions.Abstractions;

/// <summary>Base type for the closed set of session boundary drafts.</summary>
public abstract record PiSharpSessionBoundaryDraft
{
    private protected PiSharpSessionBoundaryDraft() { }
}

/// <summary>Draft to append extension-owned JSON metadata to the session.</summary>
public sealed record PiSharpCustomEntryDraft : PiSharpSessionBoundaryDraft
{
    private readonly JsonElement? _data;

    /// <summary>Creates a custom entry draft with optional detached JSON data.</summary>
    public PiSharpCustomEntryDraft(string customType, JsonElement? data = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(customType);
        if (data is { ValueKind: JsonValueKind.Undefined })
            throw new ArgumentException("Data must be a JSON value when supplied.", nameof(data));
        CustomType = customType;
        _data = data?.Clone();
    }

    /// <summary>The extension-owned entry type.</summary>
    public string CustomType { get; }
    /// <summary>Detached JSON payload, when supplied.</summary>
    public JsonElement? Data => _data?.Clone();
}

/// <summary>Draft to append a custom message to the session context.</summary>
public sealed record PiSharpCustomMessageEntryDraft : PiSharpSessionBoundaryDraft
{
    private readonly AIContent[] _content;
    private readonly JsonElement? _details;

    /// <summary>Creates a text-only custom message draft.</summary>
    public PiSharpCustomMessageEntryDraft(string customType, string content, bool display,
        JsonElement? details = null)
        : this(customType, [new TextContent(content ?? throw new ArgumentNullException(nameof(content)))], display,
            details)
    {
    }

    /// <summary>Creates a custom message draft from detached AI content.</summary>
    public PiSharpCustomMessageEntryDraft(string customType, IEnumerable<AIContent> content, bool display,
        JsonElement? details = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(customType);
        ArgumentNullException.ThrowIfNull(content);
        if (details is { ValueKind: JsonValueKind.Undefined })
            throw new ArgumentException("Details must be a JSON value when supplied.", nameof(details));
        var contentItems = content.ToArray();
        if (contentItems.Any(item => item is null || !PiSharpBoundaryContentValidation.IsCustomMessageContent(item)))
            throw new ArgumentException("Custom message content supports text and valid image data only.", nameof(content));
        _content = contentItems.Select(PiSharpExtensionEventSnapshots.CloneContent).ToArray();
        CustomType = customType;
        Display = display;
        _details = details?.Clone();
    }

    /// <summary>The extension-owned message type.</summary>
    public string CustomType { get; }
    /// <summary>Detached message content, including images where supported by the model.</summary>
    public IReadOnlyList<AIContent> Content => PiSharpExtensionEventSnapshots.SnapshotContents(_content);
    /// <summary>Whether the message should be shown in the user-facing transcript.</summary>
    public bool Display { get; }
    /// <summary>Detached JSON metadata associated with the message, when supplied.</summary>
    public JsonElement? Details => _details?.Clone();
}

/// <summary>Draft to replace or omit one existing entry's contribution to model context.</summary>
public sealed record PiSharpContextEditEntryDraft : PiSharpSessionBoundaryDraft
{
    private readonly AIContent[]? _replacement;

    /// <summary>Creates a content replacement, or passes null to omit the target from context.</summary>
    public PiSharpContextEditEntryDraft(string targetId, IEnumerable<AIContent>? replacement)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetId);
        if (replacement is not null)
        {
            var replacementItems = replacement.ToArray();
            if (replacementItems.Any(item => item is null || !PiSharpBoundaryContentValidation.IsContextEditContent(item)))
                throw new ArgumentException(
                    "Context edit content supports text, reasoning, valid image data, and tool calls only.",
                    nameof(replacement));
            _replacement = replacementItems.Select(PiSharpExtensionEventSnapshots.CloneContent).ToArray();
        }
        TargetId = targetId;
    }

    /// <summary>Creates a text replacement for one existing entry.</summary>
    public static PiSharpContextEditEntryDraft FromText(string targetId, string replacementText)
    {
        ArgumentNullException.ThrowIfNull(replacementText);
        return new(targetId, [new TextContent(replacementText)]);
    }

    /// <summary>The canonical target entry ID.</summary>
    public string TargetId { get; }
    /// <summary>Detached replacement content, or null to omit the target from model context.</summary>
    public IReadOnlyList<AIContent>? Replacement => _replacement is null
        ? null
        : PiSharpExtensionEventSnapshots.SnapshotContents(_replacement);
}

/// <summary>Draft to append a compaction boundary to the session.</summary>
public sealed record PiSharpCompactionEntryDraft : PiSharpSessionBoundaryDraft
{
    private readonly JsonElement? _details;

    /// <summary>Creates a compaction draft. A null first-kept ID retains no preceding entries.</summary>
    public PiSharpCompactionEntryDraft(string summary, string? firstKeptEntryId, JsonElement? details = null,
        PiSharpExtensionCompactionUsage? usage = null)
    {
        ArgumentNullException.ThrowIfNull(summary);
        if (details is { ValueKind: JsonValueKind.Undefined })
            throw new ArgumentException("Details must be a JSON value when supplied.", nameof(details));
        Summary = summary;
        FirstKeptEntryId = firstKeptEntryId;
        _details = details?.Clone();
        Usage = usage;
    }

    /// <summary>The summary text for the compaction entry.</summary>
    public string Summary { get; }
    /// <summary>The first retained entry ID, or null for a self-retaining boundary.</summary>
    public string? FirstKeptEntryId { get; }
    /// <summary>Detached JSON details associated with the compaction.</summary>
    public JsonElement? Details => _details?.Clone();
    /// <summary>Optional provider token usage.</summary>
    public PiSharpExtensionCompactionUsage? Usage { get; }
}

/// <summary>Replacement fields returned by a mutable boundary handler.</summary>
public sealed record PiSharpBoundaryResult
{
    /// <summary>Creates a result; supplied entries are copied into a read-only snapshot.</summary>
    public PiSharpBoundaryResult(IReadOnlyList<PiSharpSessionBoundaryDraft>? entries = null,
        bool? @continue = null)
    {
        if (entries is not null && entries.Any(entry => entry is null))
            throw new ArgumentException("Entries cannot contain null items.", nameof(entries));
        Entries = entries is null ? null : Array.AsReadOnly(entries.ToArray());
        Continue = @continue;
    }

    /// <summary>Replacement draft list; null leaves the prior list unchanged.</summary>
    public IReadOnlyList<PiSharpSessionBoundaryDraft>? Entries { get; }
    /// <summary>Replacement continuation decision; null leaves the prior decision unchanged.</summary>
    public bool? Continue { get; }
}

/// <summary>Stable metadata for a canonical entry included in a boundary context preview.</summary>
public sealed record PiSharpBoundaryContextEntry
{
    private readonly JsonElement _metadata;

    /// <summary>Creates a detached canonical entry metadata snapshot.</summary>
    public PiSharpBoundaryContextEntry(string id, string type, JsonElement metadata)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentException.ThrowIfNullOrWhiteSpace(type);
        if (metadata.ValueKind == JsonValueKind.Undefined)
            throw new ArgumentException("Metadata must be a JSON value.", nameof(metadata));
        Id = id;
        Type = type;
        _metadata = metadata.Clone();
    }

    /// <summary>Canonical entry ID.</summary>
    public string Id { get; }
    /// <summary>Canonical entry type.</summary>
    public string Type { get; }
    /// <summary>Detached JSON metadata for this entry.</summary>
    public JsonElement Metadata => _metadata.Clone();
}

/// <summary>Detached projected context and pending messages at a mutable boundary.</summary>
public sealed record PiSharpBoundaryContextPreview
{
    private readonly PiSharpBoundaryContextEntry[] _contextEntries;
    private readonly ChatMessage[] _contextMessages;
    private readonly ChatMessage[] _pendingMessages;

    /// <summary>Creates detached snapshots of projected entries, model context, and pending messages.</summary>
    public PiSharpBoundaryContextPreview(IEnumerable<PiSharpBoundaryContextEntry> contextEntries,
        IEnumerable<ChatMessage> contextMessages, IEnumerable<ChatMessage> pendingMessages, bool canContinue)
    {
        ArgumentNullException.ThrowIfNull(contextEntries);
        ArgumentNullException.ThrowIfNull(contextMessages);
        ArgumentNullException.ThrowIfNull(pendingMessages);
        _contextEntries = contextEntries.ToArray();
        _contextMessages = contextMessages.Select(PiSharpExtensionEventSnapshots.CloneMessage).ToArray();
        _pendingMessages = pendingMessages.Select(PiSharpExtensionEventSnapshots.CloneMessage).ToArray();
        CanContinue = canContinue;
    }

    /// <summary>Detached canonical-entry metadata included in the projected context.</summary>
    public IReadOnlyList<PiSharpBoundaryContextEntry> ContextEntries => Array.AsReadOnly(
        _contextEntries.Select(entry => new PiSharpBoundaryContextEntry(entry.Id, entry.Type, entry.Metadata)).ToArray());
    /// <summary>
    /// Detached context messages in PiSharp's normalized Microsoft.Extensions.AI representation.
    /// This is the provider-facing message context; no separate LLM-message alias is exposed.
    /// </summary>
    public IReadOnlyList<ChatMessage> ContextMessages => PiSharpExtensionEventSnapshots.SnapshotMessages(_contextMessages);
    /// <summary>Detached messages waiting to be delivered to the agent.</summary>
    public IReadOnlyList<ChatMessage> PendingMessages => PiSharpExtensionEventSnapshots.SnapshotMessages(_pendingMessages);
    /// <summary>Whether the projected context and queued messages can start another model request.</summary>
    public bool CanContinue { get; }
}

/// <summary>Mutable turn-end boundary state presented to native extensions.</summary>
public sealed record PiSharpExtensionTurnEndBoundaryEvent
{
    private readonly PiSharpSessionBoundaryDraft[] _entries;

    /// <summary>Creates a detached turn boundary snapshot.</summary>
    public PiSharpExtensionTurnEndBoundaryEvent(PiSharpExtensionTurnEndEvent turn,
        PiSharpExtensionAgentOutcome outcome, IEnumerable<PiSharpSessionBoundaryDraft> entries, bool @continue,
        PiSharpBoundaryContextPreview context)
    {
        ArgumentNullException.ThrowIfNull(turn);
        ArgumentNullException.ThrowIfNull(entries);
        ArgumentNullException.ThrowIfNull(context);
        _entries = entries.ToArray();
        if (_entries.Any(entry => entry is null))
            throw new ArgumentException("Entries cannot contain null items.", nameof(entries));
        Turn = turn;
        Outcome = outcome;
        Continue = @continue;
        Context = context;
    }

    /// <summary>The existing detached turn-end event and its message/entry identity.</summary>
    public PiSharpExtensionTurnEndEvent Turn { get; }
    /// <summary>Outcome of the activity being presented at the boundary.</summary>
    public PiSharpExtensionAgentOutcome Outcome { get; }
    /// <summary>Current effective boundary drafts.</summary>
    public IReadOnlyList<PiSharpSessionBoundaryDraft> Entries => Array.AsReadOnly(_entries.ToArray());
    /// <summary>Current effective continuation decision.</summary>
    public bool Continue { get; }
    /// <summary>Detached preview rebuilt from the current effective drafts.</summary>
    public PiSharpBoundaryContextPreview Context { get; }
}

/// <summary>Mutable state presented before the logical agent run settles.</summary>
public sealed record PiSharpExtensionAgentBeforeSettleEvent
{
    private readonly PiSharpSessionBoundaryDraft[] _entries;

    /// <summary>Creates a detached pre-settlement boundary snapshot.</summary>
    public PiSharpExtensionAgentBeforeSettleEvent(PiSharpExtensionAgentOutcome outcome,
        IEnumerable<PiSharpSessionBoundaryDraft> entries, bool @continue, PiSharpBoundaryContextPreview context)
    {
        ArgumentNullException.ThrowIfNull(entries);
        ArgumentNullException.ThrowIfNull(context);
        _entries = entries.ToArray();
        if (_entries.Any(entry => entry is null))
            throw new ArgumentException("Entries cannot contain null items.", nameof(entries));
        Outcome = outcome;
        Continue = @continue;
        Context = context;
    }

    /// <summary>Outcome of the activity being presented at the boundary.</summary>
    public PiSharpExtensionAgentOutcome Outcome { get; }
    /// <summary>Current effective boundary drafts.</summary>
    public IReadOnlyList<PiSharpSessionBoundaryDraft> Entries => Array.AsReadOnly(_entries.ToArray());
    /// <summary>Current effective continuation decision.</summary>
    public bool Continue { get; }
    /// <summary>Detached preview rebuilt from the current effective drafts.</summary>
    public PiSharpBoundaryContextPreview Context { get; }
}

/// <summary>Notification that a logical agent run fully settled.</summary>
public sealed record PiSharpExtensionAgentSettledEvent(bool Aborted);

/// <summary>Handles a turn-end boundary and may replace drafts or request continuation.</summary>
public delegate ValueTask<PiSharpBoundaryResult?> PiSharpExtensionTurnEndBoundaryHandler(
    PiSharpExtensionTurnEndBoundaryEvent eventData, PiSharpExtensionContext context,
    CancellationToken cancellationToken);

/// <summary>Handles a pre-settlement boundary and may replace drafts or request continuation.</summary>
public delegate ValueTask<PiSharpBoundaryResult?> PiSharpExtensionAgentBeforeSettleHandler(
    PiSharpExtensionAgentBeforeSettleEvent eventData, PiSharpExtensionContext context,
    CancellationToken cancellationToken);

/// <summary>Observes a fully settled logical agent run.</summary>
public delegate ValueTask PiSharpExtensionAgentSettledHandler(
    PiSharpExtensionAgentSettledEvent eventData, PiSharpExtensionContext context,
    CancellationToken cancellationToken);

internal static class PiSharpBoundaryContentValidation
{
    internal static bool IsCustomMessageContent(AIContent content) => content is TextContent ||
        content is DataContent data && IsImage(data);

    internal static bool IsContextEditContent(AIContent content) => content is TextContent or
        TextReasoningContent or FunctionCallContent || content is DataContent data && IsImage(data);

    private static bool IsImage(DataContent data) =>
        data.MediaType is { } mediaType && mediaType.StartsWith("image/", StringComparison.OrdinalIgnoreCase) &&
        data.Data.Length > 0;
}
