using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.AI;
using PiSharp.Runtime.Sessions;
using PiSharp.Runtime.Tools;

namespace PiSharp.Runtime.Extensions;

/// <summary>A bounded record of one tool call made by another tool.</summary>
public sealed record PiSharpNestedToolCall(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("parentToolCallId")] string ParentToolCallId,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("arguments"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] JsonElement? Arguments = null,
    [property: JsonPropertyName("argumentsBytes"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? ArgumentsBytes = null,
    [property: JsonPropertyName("durationMs"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] long? DurationMs = null,
    [property: JsonPropertyName("error"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Error = null);

/// <summary>Nested tool calls made beneath one model-issued tool call.</summary>
public sealed record PiSharpNestedToolCalls(
    [property: JsonPropertyName("calls")] IReadOnlyList<PiSharpNestedToolCall> Calls,
    [property: JsonPropertyName("complete")] bool Complete);

/// <summary>The outcome of a nested tool invocation. Tool failures are returned as outcomes.</summary>
public sealed record PiSharpToolExecutionResult(
    string ToolCallId,
    string ToolName,
    object? Value,
    string Text,
    bool IsError,
    string? Error = null,
    object? Details = null);

/// <summary>
/// Per-invocation access to the session's tool registry, active loadout, nested execution and progress channel.
/// Nested calls use the same wrapped functions as model-issued calls.
/// </summary>
public sealed class PiSharpToolExecutionContext
{
    private const int MaximumNestedCalls = 256;
    private const int MaximumArgumentBytesPerCall = 8 * 1024;
    private const int MaximumArgumentBytesTotal = 32 * 1024;
    private const int MaximumErrorCharacters = 500;
    private static readonly object s_contextKey = new();
    private static readonly object s_invocationKey = new();

    private readonly ToolLoadout _loadout;
    private readonly ToolInvocationScope _scope;
    private readonly Action<string>? _nestedUpdate;
    private readonly string? _operationId;
    private readonly string _toolName;
    private readonly IReadOnlyDictionary<string, object?> _arguments;

    internal static object ContextKey => s_contextKey;
    internal static object InvocationKey => s_invocationKey;

    private PiSharpToolExecutionContext(ToolLoadout loadout, ToolInvocationScope scope, string toolCallId,
        string? parentToolCallId, string? operationId, string toolName,
        IReadOnlyDictionary<string, object?> arguments, Action<string>? nestedUpdate)
    {
        _loadout = loadout;
        _scope = scope;
        ToolCallId = toolCallId;
        ParentToolCallId = parentToolCallId;
        _operationId = operationId;
        _toolName = toolName;
        _arguments = arguments;
        _nestedUpdate = nestedUpdate;
    }

    internal static PiSharpToolExecutionContext CreateRoot(ToolLoadout loadout,
        Func<IReadOnlyDictionary<string, AIFunction>> functions, Action<AgentLifecycleEvent> publish,
        string toolCallId, string? operationId, string toolName,
        IReadOnlyDictionary<string, object?> arguments) =>
        new(loadout, new ToolInvocationScope(functions, publish), toolCallId, null, operationId,
            toolName, arguments, null);

    internal string ToolCallId { get; }
    internal string? ParentToolCallId { get; }
    internal PiSharpNestedToolCalls? NestedCalls => ParentToolCallId is null ? _scope.Snapshot() : null;

    /// <summary>The immutable tool set visible to this invocation.</summary>
    public ToolLoadoutSnapshot Snapshot => _loadout.Snapshot;

    /// <summary>Replace the declarations used for the next model request in this session.</summary>
    public void SetActiveTools(IEnumerable<string> toolNames)
    {
        _loadout.SetActiveTools(toolNames);
        _scope.Publish(new AgentLifecycleEvent("tool_loadout_changed")
        {
            ToolLoadoutNames = Snapshot.ActiveToolNames
        });
    }

    /// <summary>Emit progress for this invocation, including its call and parent identities.</summary>
    public void ReportProgress(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        _scope.Publish(new AgentLifecycleEvent("tool_execution_update", Text: text, Tool: _toolName,
            OperationId: _operationId)
        {
            ToolArguments = _arguments,
            ToolCallId = ToolCallId,
            ParentToolCallId = ParentToolCallId
        });
        _nestedUpdate?.Invoke(text);
    }

    /// <summary>
    /// Invoke a registered callable tool through the normal PiSharp tool wrapper. Validation,
    /// cancellation, durable checkpoints, lifecycle events and loadout guards still apply.
    /// </summary>
    public Task<PiSharpToolExecutionResult> ExecuteToolAsync(string name,
        IReadOnlyDictionary<string, object?>? arguments = null,
        CancellationToken cancellationToken = default,
        Action<PiSharpToolExecutionResult>? onUpdate = null) =>
        _scope.ExecuteAsync(this, name, arguments, cancellationToken, onUpdate);

    public static PiSharpToolExecutionContext? Get(AIFunctionArguments arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        return arguments.Context is { } context && context.TryGetValue(s_contextKey, out var value)
            ? value as PiSharpToolExecutionContext
            : null;
    }

    internal static NestedInvocation? GetNestedInvocation(AIFunctionArguments arguments) =>
        arguments.Context is { } context && context.TryGetValue(s_invocationKey, out var value)
            ? value as NestedInvocation
            : null;

    internal PiSharpToolExecutionContext ForToolCall(string toolCallId, string? parentToolCallId,
        string? operationId, string toolName, IReadOnlyDictionary<string, object?> arguments,
        Action<string>? nestedUpdate = null) =>
        new(_loadout, _scope, toolCallId, parentToolCallId, operationId, toolName, arguments, nestedUpdate);

    internal sealed class NestedInvocation(PiSharpToolExecutionContext parent, string callId,
        string parentCallId, Action<PiSharpToolExecutionResult>? onUpdate)
    {
        public PiSharpToolExecutionContext Parent { get; } = parent;
        public string CallId { get; } = callId;
        public string ParentCallId { get; } = parentCallId;
        public Action<PiSharpToolExecutionResult>? OnUpdate { get; } = onUpdate;
        public bool IsError { get; set; }
        public string? Error { get; set; }
    }

    private sealed class ToolInvocationScope(Func<IReadOnlyDictionary<string, AIFunction>> functions,
        Action<AgentLifecycleEvent> publish)
    {
        private readonly object _gate = new();
        private readonly Dictionary<string, int> _nextChild = new(StringComparer.Ordinal);
        private readonly List<MutableCall> _calls = [];
        private bool _complete = true;
        private int _argumentBytes;

        public void Publish(AgentLifecycleEvent item) => publish(item);

        public async Task<PiSharpToolExecutionResult> ExecuteAsync(PiSharpToolExecutionContext caller,
            string name, IReadOnlyDictionary<string, object?>? arguments, CancellationToken cancellationToken,
            Action<PiSharpToolExecutionResult>? onUpdate)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(name);
            cancellationToken.ThrowIfCancellationRequested();

            var callId = NextCallId(caller.ToolCallId);
            var input = arguments is null
                ? new Dictionary<string, object?>(StringComparer.Ordinal)
                : arguments.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
            var record = StartCall(callId, name, caller.ToolCallId, input);
            var snapshot = caller.Snapshot;
            var registration = snapshot.Callable.FirstOrDefault(tool =>
                string.Equals(tool.Function.Name, name, StringComparison.Ordinal));
            if (registration is null || !functions().TryGetValue(name, out var function))
            {
                const string error = "Tool is not callable in this session.";
                Publish(new AgentLifecycleEvent("tool_execution_started", Tool: name)
                {
                    ToolArguments = input,
                    ToolCallId = callId,
                    ParentToolCallId = caller.ToolCallId
                });
                Publish(new AgentLifecycleEvent("tool_execution_finished", Tool: name, IsError: true, Error: error)
                {
                    ToolArguments = input,
                    ToolCallId = callId,
                    ParentToolCallId = caller.ToolCallId
                });
                FinishCall(record, isError: true, error);
                return new(callId, name, null, error, true, error);
            }

            var childUpdate = onUpdate;
            var nested = new NestedInvocation(caller, callId, caller.ToolCallId, childUpdate);
            var functionArguments = new AIFunctionArguments(input)
            {
                Context = new Dictionary<object, object?>
                {
                    [ContextKey] = caller,
                    [InvocationKey] = nested
                }
            };

            try
            {
                var value = await function.InvokeAsync(functionArguments, cancellationToken);
                ToolResultOutput.TryRead(value, out var structuredText, out var details);
                var text = structuredText.Length > 0 ? structuredText : value?.ToString() ?? string.Empty;
                var result = new PiSharpToolExecutionResult(callId, name, value, text, nested.IsError,
                    nested.Error, details);
                FinishCall(record, nested.IsError, nested.Error);
                return result;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                FinishCall(record, isError: true, "Tool call was cancelled.");
                throw;
            }
            catch (Exception error)
            {
                FinishCall(record, isError: true, error.Message);
                return new(callId, name, null, error.Message, true, error.Message);
            }
        }

        private string NextCallId(string parentCallId)
        {
            lock (_gate)
            {
                var next = _nextChild.TryGetValue(parentCallId, out var current) ? current + 1 : 1;
                _nextChild[parentCallId] = next;
                return $"{parentCallId}/{next}";
            }
        }

        private MutableCall? StartCall(string callId, string name, string parentCallId,
            IReadOnlyDictionary<string, object?> arguments)
        {
            lock (_gate)
            {
                if (_calls.Count >= MaximumNestedCalls)
                {
                    _complete = false;
                    return null;
                }

                JsonElement? capturedArguments = null;
                int? argumentsBytes = null;
                try
                {
                    var serialized = JsonSerializer.SerializeToUtf8Bytes(arguments);
                    if (serialized.Length <= MaximumArgumentBytesPerCall &&
                        _argumentBytes + serialized.Length <= MaximumArgumentBytesTotal)
                    {
                        using var document = JsonDocument.Parse(serialized);
                        capturedArguments = document.RootElement.Clone();
                        _argumentBytes += serialized.Length;
                    }
                    else
                    {
                        argumentsBytes = serialized.Length;
                        _complete = false;
                    }
                }
                catch (Exception error) when (error is JsonException or NotSupportedException)
                {
                    _complete = false;
                }

                var call = new MutableCall(callId, name, parentCallId, capturedArguments, argumentsBytes,
                    Stopwatch.GetTimestamp());
                _calls.Add(call);
                return call;
            }
        }

        private void FinishCall(MutableCall? call, bool isError, string? error)
        {
            if (call is null) return;
            lock (_gate)
            {
                call.Status = isError ? "error" : "ok";
                call.DurationMs = (long)Math.Round(Stopwatch.GetElapsedTime(call.StartedAt).TotalMilliseconds);
                if (isError && !string.IsNullOrEmpty(error))
                    call.Error = error.Length <= MaximumErrorCharacters ? error : error[..MaximumErrorCharacters];
            }
        }

        public PiSharpNestedToolCalls? Snapshot()
        {
            lock (_gate)
            {
                if (_calls.Count == 0 && _complete) return null;
                var calls = _calls.Select(call => call.ToRecord()).ToArray();
                return new PiSharpNestedToolCalls(calls,
                    _complete && calls.All(call => !string.Equals(call.Status, "unfinished", StringComparison.Ordinal)));
            }
        }

        private sealed class MutableCall(string id, string name, string parentToolCallId,
            JsonElement? arguments, int? argumentsBytes, long startedAt)
        {
            public string Status { get; set; } = "unfinished";
            public long? DurationMs { get; set; }
            public string? Error { get; set; }
            public PiSharpNestedToolCall ToRecord() => new(id, name, parentToolCallId, Status,
                arguments, argumentsBytes, DurationMs, Error);
            public long StartedAt { get; } = startedAt;
        }
    }
}
