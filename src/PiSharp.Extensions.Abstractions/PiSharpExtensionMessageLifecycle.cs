using System.Collections.ObjectModel;
using System.Text.Json;

namespace PiSharp.Extensions.Abstractions;

/// <summary>The role of a message projected to a native extension.</summary>
public enum PiSharpExtensionMessageRole
{
    /// <summary>System instructions.</summary>
    System,
    /// <summary>User input.</summary>
    User,
    /// <summary>Assistant output.</summary>
    Assistant,
    /// <summary>A tool execution result.</summary>
    ToolResult,
    /// <summary>A PiSharp extension custom message.</summary>
    Custom,
    /// <summary>A role name not represented by another value.</summary>
    Other
}

/// <summary>The content kinds represented by the native message snapshot.</summary>
public enum PiSharpExtensionMessageContentKind
{
    /// <summary>Plain assistant or user text.</summary>
    Text,
    /// <summary>Model reasoning text.</summary>
    Reasoning,
    /// <summary>In-memory binary content.</summary>
    Data,
    /// <summary>Content referenced by a URI.</summary>
    Uri,
    /// <summary>A model function call.</summary>
    FunctionCall,
    /// <summary>A function result.</summary>
    FunctionResult,
    /// <summary>Provider-specific content represented as detached JSON.</summary>
    Opaque
}

/// <summary>Detached content for an extension-visible message.</summary>
public sealed class PiSharpExtensionMessageContent
{
    private readonly byte[]? _data;
    private readonly JsonElement? _json;
    private readonly IReadOnlyDictionary<string, JsonElement> _additionalProperties;

    /// <summary>Creates text or reasoning content.</summary>
    public PiSharpExtensionMessageContent(PiSharpExtensionMessageContentKind kind, string text,
        string? protectedData = null, IReadOnlyDictionary<string, JsonElement>? additionalProperties = null)
    {
        if (kind is not (PiSharpExtensionMessageContentKind.Text or PiSharpExtensionMessageContentKind.Reasoning))
            throw new ArgumentOutOfRangeException(nameof(kind));
        Kind = kind;
        Text = text ?? throw new ArgumentNullException(nameof(text));
        ProtectedData = protectedData;
        _additionalProperties = CloneProperties(additionalProperties);
    }

    /// <summary>Creates binary or URI content.</summary>
    public PiSharpExtensionMessageContent(PiSharpExtensionMessageContentKind kind, string uri,
        string mediaType, byte[]? data = null, string? name = null,
        IReadOnlyDictionary<string, JsonElement>? additionalProperties = null)
    {
        if (kind is not (PiSharpExtensionMessageContentKind.Data or PiSharpExtensionMessageContentKind.Uri))
            throw new ArgumentOutOfRangeException(nameof(kind));
        Kind = kind;
        Uri = uri ?? throw new ArgumentNullException(nameof(uri));
        MediaType = mediaType ?? throw new ArgumentNullException(nameof(mediaType));
        _data = data?.ToArray();
        Name = name;
        if (kind == PiSharpExtensionMessageContentKind.Data && _data is null)
            throw new ArgumentNullException(nameof(data));
        _additionalProperties = CloneProperties(additionalProperties);
    }

    /// <summary>Creates a function-call content item.</summary>
    public PiSharpExtensionMessageContent(string callId, string functionName, JsonElement arguments,
        string? errorMessage = null, bool informationalOnly = false,
        IReadOnlyDictionary<string, JsonElement>? additionalProperties = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(callId);
        ArgumentException.ThrowIfNullOrWhiteSpace(functionName);
        Kind = PiSharpExtensionMessageContentKind.FunctionCall;
        CallId = callId;
        FunctionName = functionName;
        _json = arguments.Clone();
        ErrorMessage = errorMessage;
        InformationalOnly = informationalOnly;
        _additionalProperties = CloneProperties(additionalProperties);
    }

    /// <summary>Creates a function-result content item.</summary>
    public PiSharpExtensionMessageContent(string callId, JsonElement result, bool isError = false,
        string? errorMessage = null, IReadOnlyDictionary<string, JsonElement>? additionalProperties = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(callId);
        Kind = PiSharpExtensionMessageContentKind.FunctionResult;
        CallId = callId;
        _json = result.Clone();
        IsError = isError;
        ErrorMessage = errorMessage;
        _additionalProperties = CloneProperties(additionalProperties);
    }

    /// <summary>Creates a detached description of content unsupported by this API version.</summary>
    public PiSharpExtensionMessageContent(PiSharpExtensionMessageContentKind kind, string contentType, JsonElement value,
        IReadOnlyDictionary<string, JsonElement>? additionalProperties = null)
    {
        if (kind != PiSharpExtensionMessageContentKind.Opaque) throw new ArgumentOutOfRangeException(nameof(kind));
        ArgumentException.ThrowIfNullOrWhiteSpace(contentType);
        Kind = kind;
        ContentType = contentType;
        _json = value.Clone();
        _additionalProperties = CloneProperties(additionalProperties);
    }

    /// <summary>Gets the content kind.</summary>
    public PiSharpExtensionMessageContentKind Kind { get; }
    /// <summary>Gets text for text and reasoning content.</summary>
    public string? Text { get; }
    /// <summary>Gets the URI for URI or data content.</summary>
    public string? Uri { get; }
    /// <summary>Gets the media type for binary or URI content.</summary>
    public string? MediaType { get; }
    /// <summary>Gets the content name, when available.</summary>
    public string? Name { get; }
    /// <summary>Gets a copy of binary data, when present.</summary>
    public byte[]? Data => _data?.ToArray();
    /// <summary>Gets the function call or result identifier.</summary>
    public string? CallId { get; }
    /// <summary>Gets the function name for a call.</summary>
    public string? FunctionName { get; }
    /// <summary>Gets detached JSON arguments or result.</summary>
    public JsonElement? JsonValue => _json?.Clone();
    /// <summary>Gets whether a function result represents an error.</summary>
    public bool IsError { get; }
    /// <summary>Gets diagnostic text associated with malformed or failed function content.</summary>
    public string? ErrorMessage { get; }
    /// <summary>Gets provider reasoning data that should be returned to the same provider.</summary>
    public string? ProtectedData { get; }
    /// <summary>Gets whether the function call is informational and must not execute.</summary>
    public bool InformationalOnly { get; }
    /// <summary>Gets the original content type for opaque content.</summary>
    public string? ContentType { get; }
    /// <summary>Gets detached JSON content metadata.</summary>
    public IReadOnlyDictionary<string, JsonElement> AdditionalProperties =>
        new System.Collections.ObjectModel.ReadOnlyDictionary<string, JsonElement>(
            _additionalProperties.ToDictionary(pair => pair.Key, pair => pair.Value.Clone(), StringComparer.Ordinal));

    private static IReadOnlyDictionary<string, JsonElement> CloneProperties(
        IReadOnlyDictionary<string, JsonElement>? source)
    {
        var properties = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        if (source is not null)
            foreach (var (key, value) in source) properties.Add(key, value.Clone());
        return new System.Collections.ObjectModel.ReadOnlyDictionary<string, JsonElement>(properties);
    }
}

/// <summary>A detached message snapshot that can be returned as a finalized replacement.</summary>
public sealed class PiSharpExtensionMessage
{
    private readonly IReadOnlyList<PiSharpExtensionMessageContent> _contents;
    private readonly IReadOnlyDictionary<string, JsonElement> _additionalProperties;

    /// <summary>Creates a message for use as a lifecycle replacement.</summary>
    public PiSharpExtensionMessage(PiSharpExtensionMessageRole role,
        IEnumerable<PiSharpExtensionMessageContent>? contents = null, string? roleName = null,
        string? messageId = null, string? authorName = null, DateTimeOffset? createdAt = null,
        IReadOnlyDictionary<string, JsonElement>? additionalProperties = null,
        PiSharpExtensionMessageResponseMetadata? responseMetadata = null)
    {
        if (!Enum.IsDefined(role)) throw new ArgumentOutOfRangeException(nameof(role));
        if (role == PiSharpExtensionMessageRole.Other && string.IsNullOrWhiteSpace(roleName))
            throw new ArgumentException("An Other message must include its original role name.", nameof(roleName));
        Role = role;
        RoleName = roleName;
        MessageId = messageId;
        AuthorName = authorName;
        CreatedAt = createdAt;
        ResponseMetadata = responseMetadata;
        _contents = Array.AsReadOnly((contents ?? []).Select(content =>
            content ?? throw new ArgumentException("Message content cannot contain null items.", nameof(contents))).ToArray());
        var properties = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        if (additionalProperties is not null)
            foreach (var (key, value) in additionalProperties)
                properties.Add(key, value.Clone());
        _additionalProperties = new ReadOnlyDictionary<string, JsonElement>(properties);
    }

    /// <summary>Gets the message role.</summary>
    public PiSharpExtensionMessageRole Role { get; }
    /// <summary>Gets the role name when <see cref="Role"/> is <see cref="PiSharpExtensionMessageRole.Other"/>.</summary>
    public string? RoleName { get; }
    /// <summary>Gets the message identifier, when supplied by the provider.</summary>
    public string? MessageId { get; }
    /// <summary>Gets the author name, when supplied by the provider.</summary>
    public string? AuthorName { get; }
    /// <summary>Gets the message timestamp, when supplied by the provider.</summary>
    public DateTimeOffset? CreatedAt { get; }
    /// <summary>Gets provider-response metadata when available for an assistant message.</summary>
    public PiSharpExtensionMessageResponseMetadata? ResponseMetadata { get; }
    /// <summary>Gets detached message content.</summary>
    public IReadOnlyList<PiSharpExtensionMessageContent> Contents => Array.AsReadOnly(_contents.ToArray());
    /// <summary>Gets detached JSON metadata.</summary>
    public IReadOnlyDictionary<string, JsonElement> AdditionalProperties =>
        new ReadOnlyDictionary<string, JsonElement>(_additionalProperties.ToDictionary(pair => pair.Key,
            pair => pair.Value.Clone(), StringComparer.Ordinal));

    /// <summary>Returns a message copy with the specified response metadata.</summary>
    public PiSharpExtensionMessage WithResponseMetadata(PiSharpExtensionMessageResponseMetadata? responseMetadata) =>
        new(Role, _contents, RoleName, MessageId, AuthorName, CreatedAt, _additionalProperties, responseMetadata);
}

/// <summary>Detached provider response details associated with an assistant message.</summary>
public sealed record PiSharpExtensionMessageResponseMetadata(string? ResponseId = null, string? ModelId = null,
    string? ProviderId = null, string? Api = null, string? FinishReason = null,
    PiSharpExtensionMessageUsage? Usage = null);

/// <summary>Known token usage values for an assistant response.</summary>
public sealed record PiSharpExtensionMessageUsage(long? InputTokens = null, long? OutputTokens = null,
    long? TotalTokens = null, long? CachedInputTokens = null, long? ReasoningTokens = null);

/// <summary>Notification that a message is about to enter working context.</summary>
public sealed record PiSharpExtensionMessageStartEvent(PiSharpExtensionMessage Message);

/// <summary>Notification of an assistant message update received during provider streaming.</summary>
public sealed record PiSharpExtensionMessageUpdateEvent(PiSharpExtensionMessage Message,
    string UpdateType, int? ContentIndex = null, string? TextDelta = null,
    PiSharpExtensionMessageContent? Content = null);

/// <summary>Notification that a message is finalized and can be replaced.</summary>
public sealed record PiSharpExtensionMessageEndEvent(PiSharpExtensionMessage Message);

/// <summary>Optional replacement returned by a message-end handler.</summary>
public sealed record PiSharpExtensionMessageEndResult(PiSharpExtensionMessage? Replacement = null);

/// <summary>Observes a newly accepted message before provider context is built.</summary>
public delegate ValueTask PiSharpExtensionMessageStartHandler(PiSharpExtensionMessageStartEvent eventData,
    PiSharpExtensionContext context, CancellationToken cancellationToken);

/// <summary>Observes partial assistant output while a provider response streams.</summary>
public delegate ValueTask PiSharpExtensionMessageUpdateHandler(PiSharpExtensionMessageUpdateEvent eventData,
    PiSharpExtensionContext context, CancellationToken cancellationToken);

/// <summary>May replace a finalized message before it is consumed or persisted.</summary>
public delegate ValueTask<PiSharpExtensionMessageEndResult?> PiSharpExtensionMessageEndHandler(
    PiSharpExtensionMessageEndEvent eventData, PiSharpExtensionContext context, CancellationToken cancellationToken);
