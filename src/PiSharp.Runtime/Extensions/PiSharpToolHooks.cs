namespace PiSharp.Runtime.Extensions;

/// <summary>Arguments and identity for a model-issued or nested tool invocation.</summary>
public sealed class PiSharpToolCallContext(
    string toolName,
    string toolCallId,
    string? parentToolCallId,
    IDictionary<string, object?> arguments,
    Func<IDictionary<string, object?>, PiSharpToolReach?>? reachesTools = null,
    PiSharpToolNamespace? toolNamespace = null)
{
    public string ToolName { get; } = toolName;
    public string ToolCallId { get; } = toolCallId;
    public string? ParentToolCallId { get; } = parentToolCallId;
    public Func<IDictionary<string, object?>, PiSharpToolReach?>? ReachesTools { get; } = reachesTools;
    public PiSharpToolNamespace? Namespace { get; } = toolNamespace;

    /// <summary>
    /// Mutable arguments passed to the tool. Hooks run in registration order; later hooks and the
    /// tool see earlier changes. The tool function performs its normal binding after these hooks.
    /// </summary>
    public IDictionary<string, object?> Arguments { get; } = arguments;
}

/// <summary>Whether a tool call hook allows the invocation to proceed.</summary>
public sealed record PiSharpToolCallDecision(bool Allowed, string? Error = null)
{
    public static PiSharpToolCallDecision Allow { get; } = new(true);

    public static PiSharpToolCallDecision Block(string error) =>
        new(false, string.IsNullOrWhiteSpace(error) ? "Tool call was blocked by an extension." : error);
}

/// <summary>A post-invocation view that may replace the value or set explicit error state.</summary>
public sealed class PiSharpToolResultContext(
    string toolName,
    string toolCallId,
    string? parentToolCallId,
    IReadOnlyDictionary<string, object?> arguments,
    object? result,
    Exception? exception)
{
    public string ToolName { get; } = toolName;
    public string ToolCallId { get; } = toolCallId;
    public string? ParentToolCallId { get; } = parentToolCallId;
    public IReadOnlyDictionary<string, object?> Arguments { get; } = arguments;
    private object? _result = result;
    private bool _isError = exception is not null;

    public object? Result
    {
        get => _result;
        set
        {
            _result = value;
            ResultChanged = true;
            if (value is PiSharpToolResult typed && !IsErrorChanged)
            {
                _isError = typed.IsError;
                Error = typed.Error;
            }
        }
    }

    public Exception? Exception { get; set; } = exception;

    public bool IsError
    {
        get => _isError;
        set
        {
            _isError = value;
            IsErrorChanged = true;
        }
    }

    public string? Error { get; set; } = exception?.Message;

    internal bool ResultChanged { get; private set; }
    internal bool IsErrorChanged { get; private set; }

    internal void NormalizeAfterHooks()
    {
        if (Exception is not null)
        {
            _isError = true;
            Error ??= Exception.Message;
        }
        else if (!_isError)
            Error = null;
    }
}

/// <summary>Asynchronous policy hook run before every model or nested tool call.</summary>
public delegate ValueTask<PiSharpToolCallDecision> PiSharpToolCallHook(
    PiSharpToolCallContext context,
    CancellationToken cancellationToken);

/// <summary>Asynchronous result hook run after every model or nested tool call.</summary>
public delegate ValueTask PiSharpToolResultHook(
    PiSharpToolResultContext context,
    CancellationToken cancellationToken);

internal sealed class PiSharpToolHookPipeline(
    IReadOnlyList<PiSharpToolCallHook>? callHooks,
    IReadOnlyList<PiSharpToolResultHook>? resultHooks)
{
    private readonly IReadOnlyList<PiSharpToolCallHook> _callHooks = callHooks?.ToArray() ?? [];
    private readonly IReadOnlyList<PiSharpToolResultHook> _resultHooks = resultHooks?.ToArray() ?? [];

    public async ValueTask BeforeAsync(PiSharpToolCallContext context, CancellationToken cancellationToken)
    {
        foreach (var hook in _callHooks)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var decision = await hook(context, cancellationToken).ConfigureAwait(false);
            if (decision is null)
                throw new InvalidOperationException("Tool-call hooks must return an explicit decision.");
            if (!decision.Allowed)
                throw new PiSharpToolCallBlockedException(decision.Error ?? "Tool call was blocked by an extension.");
        }
    }

    public async ValueTask AfterAsync(PiSharpToolResultContext context, CancellationToken cancellationToken)
    {
        foreach (var hook in _resultHooks)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await hook(context, cancellationToken).ConfigureAwait(false);
        }

        context.NormalizeAfterHooks();
    }
}

internal sealed class PiSharpToolCallBlockedException(string message) : InvalidOperationException(message);
