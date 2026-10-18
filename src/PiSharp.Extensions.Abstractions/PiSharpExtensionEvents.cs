using System.Collections.ObjectModel;
using System.Text.Json;
using Microsoft.Extensions.AI;

namespace PiSharp.Extensions.Abstractions;

/// <summary>Why a session compaction ran.</summary>
public enum PiSharpExtensionCompactionReason
{
    /// <summary>An explicit compaction request.</summary>
    Manual,
    /// <summary>The automatic context threshold was reached.</summary>
    Threshold,
    /// <summary>Context overflow recovery requested compaction.</summary>
    Overflow
}

/// <summary>Where an effective model selection came from.</summary>
public enum PiSharpExtensionModelSelectionSource
{
    /// <summary>A direct model selection.</summary>
    Set,
    /// <summary>Model cycling.</summary>
    Cycle,
    /// <summary>Restoring the selected model from session state.</summary>
    Restore
}

/// <summary>The termination outcome of an agent run.</summary>
public enum PiSharpExtensionAgentOutcome
{
    /// <summary>The run completed normally.</summary>
    Completed,
    /// <summary>The run was aborted.</summary>
    Aborted,
    /// <summary>The run failed.</summary>
    Error
}

/// <summary>A known context usage snapshot; token usage and percentage may be unknown.</summary>
public sealed record PiSharpExtensionContextUsage(long? Tokens, int ContextWindow, double? Percent);

/// <summary>Stable, detached details for one completed compaction.</summary>
public sealed record PiSharpExtensionCompactionResult
{
    private readonly JsonElement? _details;
    private readonly JsonElement? _usage;

    /// <summary>Creates a detached compaction result projection.</summary>
    public PiSharpExtensionCompactionResult(string summary, string firstKeptEntryId, int tokensBefore,
        int estimatedTokensAfter, JsonElement? details = null, JsonElement? usage = null)
    {
        ArgumentNullException.ThrowIfNull(summary);
        ArgumentException.ThrowIfNullOrWhiteSpace(firstKeptEntryId);
        if (tokensBefore < 0) throw new ArgumentOutOfRangeException(nameof(tokensBefore));
        if (estimatedTokensAfter < 0) throw new ArgumentOutOfRangeException(nameof(estimatedTokensAfter));
        Summary = summary;
        FirstKeptEntryId = firstKeptEntryId;
        TokensBefore = tokensBefore;
        EstimatedTokensAfter = estimatedTokensAfter;
        _details = details?.Clone();
        _usage = usage?.Clone();
    }

    /// <summary>The persisted summary text.</summary>
    public string Summary { get; }
    /// <summary>The first canonical entry retained after compaction.</summary>
    public string FirstKeptEntryId { get; }
    /// <summary>The estimated context token count before compaction.</summary>
    public int TokensBefore { get; }
    /// <summary>The estimated context token count after compaction.</summary>
    public int EstimatedTokensAfter { get; }
    /// <summary>Detached JSON details, when supplied by the host.</summary>
    public JsonElement? Details => _details?.Clone();
    /// <summary>Detached JSON usage metadata, when supplied by the host.</summary>
    public JsonElement? Usage => _usage?.Clone();
}

/// <summary>Notification that the active session name was committed.</summary>
public sealed record PiSharpExtensionSessionInfoChangedEvent(string? Name);

/// <summary>Notification that session compaction completed and was persisted.</summary>
public sealed record PiSharpExtensionSessionCompactEvent(
    PiSharpExtensionCompactionResult Result,
    string EntryId,
    DateTimeOffset Timestamp,
    PiSharpExtensionCompactionReason Reason,
    bool FromExtension,
    bool WillRetry);

/// <summary>Notification that compaction failed or was aborted.</summary>
public sealed record PiSharpExtensionSessionCompactFailedEvent(
    PiSharpExtensionCompactionReason Reason,
    bool WillRetry,
    bool Aborted,
    string? ErrorMessage,
    bool FromExtension);

/// <summary>Notification that one logical agent run started.</summary>
public sealed record PiSharpExtensionAgentStartEvent;

/// <summary>Notification that one logical agent run ended.</summary>
public sealed record PiSharpExtensionAgentEndEvent
{
    private readonly ChatMessage[] _messages;

    /// <summary>Creates a run-end snapshot with detached messages.</summary>
    public PiSharpExtensionAgentEndEvent(IEnumerable<ChatMessage> messages,
        PiSharpExtensionAgentOutcome outcome, string? errorMessage = null)
    {
        ArgumentNullException.ThrowIfNull(messages);
        _messages = messages.Select(PiSharpExtensionEventSnapshots.CloneMessage).ToArray();
        Outcome = outcome;
        ErrorMessage = errorMessage;
    }

    /// <summary>Detached messages captured when the run ended.</summary>
    public IReadOnlyList<ChatMessage> Messages => PiSharpExtensionEventSnapshots.SnapshotMessages(_messages);
    /// <summary>How the run ended.</summary>
    public PiSharpExtensionAgentOutcome Outcome { get; }
    /// <summary>Error text when the run failed, if available.</summary>
    public string? ErrorMessage { get; }
}

/// <summary>Notification that one model turn started.</summary>
public sealed record PiSharpExtensionTurnStartEvent(int TurnIndex, DateTimeOffset Timestamp);

/// <summary>Observational snapshot of a completed or interrupted turn.</summary>
public sealed record PiSharpExtensionTurnEndEvent
{
    private readonly ChatMessage? _message;
    private readonly ChatMessage[] _toolResults;
    private readonly string[] _toolResultEntryIds;

    /// <summary>Creates a detached turn-end snapshot.</summary>
    public PiSharpExtensionTurnEndEvent(int turnIndex, ChatMessage? message,
        IEnumerable<ChatMessage>? toolResults, string? messageEntryId,
        IEnumerable<string>? toolResultEntryIds, bool aborted, string? errorMessage = null)
    {
        TurnIndex = turnIndex;
        _message = message is null ? null : PiSharpExtensionEventSnapshots.CloneMessage(message);
        _toolResults = (toolResults ?? []).Select(PiSharpExtensionEventSnapshots.CloneMessage).ToArray();
        MessageEntryId = messageEntryId;
        _toolResultEntryIds = (toolResultEntryIds ?? []).ToArray();
        Aborted = aborted;
        ErrorMessage = errorMessage;
    }

    /// <summary>Zero-based logical turn index.</summary>
    public int TurnIndex { get; }
    /// <summary>The finalized assistant message, or null if execution ended before one existed.</summary>
    public ChatMessage? Message => _message is null ? null : PiSharpExtensionEventSnapshots.CloneMessage(_message);
    /// <summary>Detached finalized tool-result messages for this turn.</summary>
    public IReadOnlyList<ChatMessage> ToolResults => PiSharpExtensionEventSnapshots.SnapshotMessages(_toolResults);
    /// <summary>The canonical entry ID for the assistant message, when available.</summary>
    public string? MessageEntryId { get; }
    /// <summary>Canonical entry IDs for finalized tool-result messages, in turn order.</summary>
    public IReadOnlyList<string> ToolResultEntryIds => Array.AsReadOnly(_toolResultEntryIds.ToArray());
    /// <summary>Whether the turn was aborted.</summary>
    public bool Aborted { get; }
    /// <summary>Error text when the turn failed, if available.</summary>
    public string? ErrorMessage { get; }
}

/// <summary>Notification that a tool invocation is about to execute.</summary>
public sealed record PiSharpExtensionToolExecutionStartEvent
{
    private readonly JsonElement _arguments;

    /// <summary>Creates an immutable tool-start snapshot with detached JSON arguments.</summary>
    public PiSharpExtensionToolExecutionStartEvent(string toolCallId, string toolName,
        JsonElement arguments, string? parentToolCallId = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(toolCallId);
        ArgumentException.ThrowIfNullOrWhiteSpace(toolName);
        ToolCallId = toolCallId;
        ToolName = toolName;
        _arguments = arguments.Clone();
        ParentToolCallId = parentToolCallId;
    }

    /// <summary>Stable call ID.</summary>
    public string ToolCallId { get; }
    /// <summary>Registered tool name.</summary>
    public string ToolName { get; }
    /// <summary>Detached JSON arguments after existing call hooks have run.</summary>
    public JsonElement Arguments => _arguments.Clone();
    /// <summary>Parent call ID for nested tools, when present.</summary>
    public string? ParentToolCallId { get; }
}

/// <summary>Notification containing actual partial output emitted during tool execution.</summary>
public sealed record PiSharpExtensionToolExecutionUpdateEvent
{
    private readonly JsonElement _arguments;
    private readonly JsonElement _partialResult;

    /// <summary>Creates an immutable tool-update snapshot with detached arguments and partial result.</summary>
    public PiSharpExtensionToolExecutionUpdateEvent(string toolCallId, string toolName,
        JsonElement arguments, JsonElement partialResult, string? parentToolCallId = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(toolCallId);
        ArgumentException.ThrowIfNullOrWhiteSpace(toolName);
        ToolCallId = toolCallId;
        ToolName = toolName;
        _arguments = arguments.Clone();
        _partialResult = partialResult.Clone();
        ParentToolCallId = parentToolCallId;
    }

    /// <summary>Stable call ID.</summary>
    public string ToolCallId { get; }
    /// <summary>Registered tool name.</summary>
    public string ToolName { get; }
    /// <summary>Detached JSON arguments after existing call hooks have run.</summary>
    public JsonElement Arguments => _arguments.Clone();
    /// <summary>Detached JSON partial result emitted by the tool.</summary>
    public JsonElement PartialResult => _partialResult.Clone();
    /// <summary>Parent call ID for nested tools, when present.</summary>
    public string? ParentToolCallId { get; }
}

/// <summary>Notification that a tool invocation finished.</summary>
public sealed record PiSharpExtensionToolExecutionEndEvent
{
    private readonly JsonElement _arguments;
    private readonly JsonElement _result;

    /// <summary>Creates an immutable tool-end snapshot with detached arguments and final result.</summary>
    public PiSharpExtensionToolExecutionEndEvent(string toolCallId, string toolName,
        JsonElement arguments, JsonElement result, bool isError, double? durationMs = null,
        string? parentToolCallId = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(toolCallId);
        ArgumentException.ThrowIfNullOrWhiteSpace(toolName);
        ToolCallId = toolCallId;
        ToolName = toolName;
        _arguments = arguments.Clone();
        _result = result.Clone();
        IsError = isError;
        DurationMs = durationMs;
        ParentToolCallId = parentToolCallId;
    }

    /// <summary>Stable call ID.</summary>
    public string ToolCallId { get; }
    /// <summary>Registered tool name.</summary>
    public string ToolName { get; }
    /// <summary>Detached JSON arguments after existing call hooks have run.</summary>
    public JsonElement Arguments => _arguments.Clone();
    /// <summary>Detached JSON result after existing result hooks have run.</summary>
    public JsonElement Result => _result.Clone();
    /// <summary>Whether the finalized result is an error.</summary>
    public bool IsError { get; }
    /// <summary>Execution duration in milliseconds, or null when the tool did not execute.</summary>
    public double? DurationMs { get; }
    /// <summary>Parent call ID for nested tools, when present.</summary>
    public string? ParentToolCallId { get; }
}

/// <summary>Notification that the effective selected model changed.</summary>
public sealed record PiSharpExtensionModelSelectEvent(
    PiSharpExtensionModelInfo Model,
    PiSharpExtensionModelInfo? PreviousModel,
    PiSharpExtensionModelSelectionSource Source);

/// <summary>Notification that the effective thinking level changed.</summary>
public sealed record PiSharpExtensionThinkingLevelSelectEvent(
    PiSharpThinkingLevel Level,
    PiSharpThinkingLevel PreviousLevel);

/// <summary>Handles a committed session-info change.</summary>
public delegate ValueTask PiSharpExtensionSessionInfoChangedHandler(
    PiSharpExtensionSessionInfoChangedEvent eventData, PiSharpExtensionContext context,
    CancellationToken cancellationToken);

/// <summary>Handles successful persisted compaction.</summary>
public delegate ValueTask PiSharpExtensionSessionCompactHandler(
    PiSharpExtensionSessionCompactEvent eventData, PiSharpExtensionContext context,
    CancellationToken cancellationToken);

/// <summary>Handles failed or aborted compaction.</summary>
public delegate ValueTask PiSharpExtensionSessionCompactFailedHandler(
    PiSharpExtensionSessionCompactFailedEvent eventData, PiSharpExtensionContext context,
    CancellationToken cancellationToken);

/// <summary>Handles the start of an agent run.</summary>
public delegate ValueTask PiSharpExtensionAgentStartHandler(
    PiSharpExtensionAgentStartEvent eventData, PiSharpExtensionContext context,
    CancellationToken cancellationToken);

/// <summary>Handles the end of an agent run.</summary>
public delegate ValueTask PiSharpExtensionAgentEndHandler(
    PiSharpExtensionAgentEndEvent eventData, PiSharpExtensionContext context,
    CancellationToken cancellationToken);

/// <summary>Handles the start of an agent turn.</summary>
public delegate ValueTask PiSharpExtensionTurnStartHandler(
    PiSharpExtensionTurnStartEvent eventData, PiSharpExtensionContext context,
    CancellationToken cancellationToken);

/// <summary>Handles an observational turn-end snapshot.</summary>
public delegate ValueTask PiSharpExtensionTurnEndHandler(
    PiSharpExtensionTurnEndEvent eventData, PiSharpExtensionContext context,
    CancellationToken cancellationToken);

/// <summary>Handles the start of a tool execution.</summary>
public delegate ValueTask PiSharpExtensionToolExecutionStartHandler(
    PiSharpExtensionToolExecutionStartEvent eventData, PiSharpExtensionContext context,
    CancellationToken cancellationToken);

/// <summary>Handles an actual partial tool result.</summary>
public delegate ValueTask PiSharpExtensionToolExecutionUpdateHandler(
    PiSharpExtensionToolExecutionUpdateEvent eventData, PiSharpExtensionContext context,
    CancellationToken cancellationToken);

/// <summary>Handles a finalized tool execution result.</summary>
public delegate ValueTask PiSharpExtensionToolExecutionEndHandler(
    PiSharpExtensionToolExecutionEndEvent eventData, PiSharpExtensionContext context,
    CancellationToken cancellationToken);

/// <summary>Handles a committed model selection.</summary>
public delegate ValueTask PiSharpExtensionModelSelectHandler(
    PiSharpExtensionModelSelectEvent eventData, PiSharpExtensionContext context,
    CancellationToken cancellationToken);

/// <summary>Handles a committed thinking-level selection.</summary>
public delegate ValueTask PiSharpExtensionThinkingLevelSelectHandler(
    PiSharpExtensionThinkingLevelSelectEvent eventData, PiSharpExtensionContext context,
    CancellationToken cancellationToken);

internal static class PiSharpExtensionEventSnapshots
{
    internal static IReadOnlyList<ChatMessage> SnapshotMessages(IEnumerable<ChatMessage> messages) =>
        Array.AsReadOnly(messages.Select(CloneMessage).ToArray());

    internal static ChatMessage CloneMessage(ChatMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        var clone = message.Clone();
        clone.RawRepresentation = null;
        clone.Contents = message.Contents.Select(CloneContent).ToList();
        if (message.AdditionalProperties is { } properties)
        {
            clone.AdditionalProperties = new();
            foreach (var pair in properties)
                clone.AdditionalProperties[pair.Key] = CloneValue(pair.Value);
        }
        return clone;
    }

    private static AIContent CloneContent(AIContent content)
    {
        AIContent clone = content switch
        {
            TextContent text => new TextContent(text.Text),
            TextReasoningContent reasoning => new TextReasoningContent(reasoning.Text)
            {
                ProtectedData = reasoning.ProtectedData
            },
            DataContent data => CloneDataContent(data),
            UriContent uri => new UriContent(uri.Uri, uri.MediaType),
            FunctionCallContent call => new FunctionCallContent(call.CallId, call.Name,
                call.Arguments?.ToDictionary(pair => pair.Key, pair => CloneValue(pair.Value)) ?? []),
            FunctionResultContent result => new FunctionResultContent(result.CallId, CloneValue(result.Result)),
            _ => new TextContent(content.ToString() ?? string.Empty)
        };
        clone.RawRepresentation = null;
        clone.Annotations = content.Annotations?.Select(CloneAnnotation).ToList();
        if (content.AdditionalProperties is { } properties)
        {
            clone.AdditionalProperties = new();
            foreach (var pair in properties)
                clone.AdditionalProperties[pair.Key] = CloneValue(pair.Value);
        }
        return clone;
    }

    private static AIAnnotation CloneAnnotation(AIAnnotation annotation)
    {
        AIAnnotation clone = annotation is CitationAnnotation citation
            ? new CitationAnnotation
            {
                Title = citation.Title,
                Url = citation.Url,
                FileId = citation.FileId,
                ToolName = citation.ToolName,
                Snippet = citation.Snippet
            }
            : new AIAnnotation();
        clone.RawRepresentation = null;
        clone.AnnotatedRegions = annotation.AnnotatedRegions?
            .OfType<TextSpanAnnotatedRegion>()
            .Select(region => (AnnotatedRegion)new TextSpanAnnotatedRegion
            {
                StartIndex = region.StartIndex,
                EndIndex = region.EndIndex
            }).ToList();
        if (annotation.AdditionalProperties is { } properties)
        {
            clone.AdditionalProperties = new();
            foreach (var pair in properties)
                clone.AdditionalProperties[pair.Key] = CloneValue(pair.Value);
        }
        return clone;
    }

    private static DataContent CloneDataContent(DataContent content)
    {
        var clone = content.Data is { } data
            ? new DataContent(data.ToArray(), content.MediaType)
            : new DataContent(content.Uri, content.MediaType);
        clone.Name = content.Name;
        return clone;
    }

    private static object? CloneValue(object? value) => value switch
    {
        null or string or bool or byte or sbyte or short or ushort or int or uint or long or ulong or float or double or decimal => value,
        JsonElement element => element.Clone(),
        JsonDocument document => document.RootElement.Clone(),
        IDictionary<string, object?> dictionary => new ReadOnlyDictionary<string, object?>(
            dictionary.ToDictionary(pair => pair.Key, pair => CloneValue(pair.Value), StringComparer.Ordinal)),
        IReadOnlyDictionary<string, object?> dictionary => new ReadOnlyDictionary<string, object?>(
            dictionary.ToDictionary(pair => pair.Key, pair => CloneValue(pair.Value), StringComparer.Ordinal)),
        IEnumerable<object?> values => Array.AsReadOnly(values.Select(CloneValue).ToArray()),
        _ => SerializeDetached(value)
    };

    private static JsonElement SerializeDetached(object value)
    {
        try { return JsonSerializer.SerializeToElement(value, value.GetType()).Clone(); }
        catch (Exception error) when (error is JsonException or NotSupportedException or InvalidOperationException)
        {
            return JsonSerializer.SerializeToElement(value.ToString());
        }
    }
}
