using Microsoft.Extensions.AI;

namespace PiSharp.Extensions.Abstractions;

/// <summary>Transforms one provider-bound request without changing the saved conversation.</summary>
/// <param name="messages">The conversation projected for this provider request.</param>
/// <param name="cancellationToken">The cancellation token for the owning provider request.</param>
public delegate ValueTask<IReadOnlyList<ChatMessage>> PiSharpContextTransform(
    IReadOnlyList<ChatMessage> messages, CancellationToken cancellationToken);

/// <summary>Arguments and identity for a model-issued or nested tool invocation.</summary>
public sealed class PiSharpToolCallContext(
    string toolName,
    string toolCallId,
    string? parentToolCallId,
    IDictionary<string, object?> arguments)
{
    /// <summary>The registered tool name.</summary>
    public string ToolName { get; } = toolName;
    /// <summary>The stable ID of this tool call.</summary>
    public string ToolCallId { get; } = toolCallId;
    /// <summary>The parent tool call ID for nested calls, when present.</summary>
    public string? ParentToolCallId { get; } = parentToolCallId;

    /// <summary>
    /// Mutable arguments passed to the tool. Hooks run in registration order; later hooks and the
    /// tool see earlier changes. The tool performs normal binding after these hooks.
    /// </summary>
    public IDictionary<string, object?> Arguments { get; } = arguments;
}

/// <summary>Whether a tool call hook allows the invocation to proceed.</summary>
public sealed record PiSharpToolCallDecision(bool Allowed, string? Error = null)
{
    /// <summary>Allows the call to proceed.</summary>
    public static PiSharpToolCallDecision Allow { get; } = new(true);

    /// <summary>Blocks the call with the supplied error.</summary>
    public static PiSharpToolCallDecision Block(string error) =>
        new(false, string.IsNullOrWhiteSpace(error) ? "Tool call was blocked by an extension." : error);
}

/// <summary>A post-invocation view that may replace the result or set explicit error state.</summary>
public sealed class PiSharpToolResultContext(
    string toolName,
    string toolCallId,
    string? parentToolCallId,
    IReadOnlyDictionary<string, object?> arguments,
    object? result,
    Exception? exception)
{
    /// <summary>The registered tool name.</summary>
    public string ToolName { get; } = toolName;
    /// <summary>The stable ID of this tool call.</summary>
    public string ToolCallId { get; } = toolCallId;
    /// <summary>The parent tool call ID for nested calls, when present.</summary>
    public string? ParentToolCallId { get; } = parentToolCallId;
    /// <summary>Read-only arguments used for the completed call.</summary>
    public IReadOnlyDictionary<string, object?> Arguments { get; } = arguments;
    /// <summary>The result value that the hook may replace.</summary>
    public object? Result { get; set; } = result;
    /// <summary>The exception captured for the call, when one occurred.</summary>
    public Exception? Exception { get; set; } = exception;
    /// <summary>Whether the call result represents an error.</summary>
    public bool IsError { get; set; } = exception is not null;
    /// <summary>The error text associated with an error result.</summary>
    public string? Error { get; set; } = exception?.Message;
}

/// <summary>Asynchronous policy hook run before every model or nested tool call.</summary>
/// <param name="context">The mutable call view and stable call identity.</param>
/// <param name="cancellationToken">The cancellation token for the owning tool call.</param>
public delegate ValueTask<PiSharpToolCallDecision> PiSharpToolCallHook(
    PiSharpToolCallContext context, CancellationToken cancellationToken);

/// <summary>Asynchronous result hook run after every model or nested tool call.</summary>
/// <param name="context">The result view that may be modified before returning to the model.</param>
/// <param name="cancellationToken">The cancellation token for the owning tool call.</param>
public delegate ValueTask PiSharpToolResultHook(
    PiSharpToolResultContext context, CancellationToken cancellationToken);
