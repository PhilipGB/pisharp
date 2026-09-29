using System.Runtime.ExceptionServices;
using Microsoft.Extensions.AI;
using PiSharp.Runtime;
using PiSharp.Runtime.Extensions;
using PiSharp.Runtime.Tools;

namespace PiSharp.Runtime.Sessions;

/// <summary>Tool lifecycle originates here, at invocation time rather than from inferred model updates.</summary>
internal sealed class DurableToolFunction(AIFunction inner, Func<DurableExecution?> current,
    Action<AgentLifecycleEvent> publish, Func<ToolLoadout?>? currentLoadout = null,
    Func<IReadOnlyDictionary<string, AIFunction>>? allToolFunctions = null) : DelegatingAIFunction(inner)
{
    protected override async ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var loadout = currentLoadout?.Invoke();
        var snapshot = loadout?.Snapshot;
        var isCallable = snapshot is null || snapshot.ActiveToolNames.Contains(Name, StringComparer.Ordinal) ||
            snapshot.Callable.Any(tool => string.Equals(tool.Function.Name, Name, StringComparison.Ordinal));
        var nestedInvocation = PiSharpToolExecutionContext.GetNestedInvocation(arguments);
        var callContent = nestedInvocation is null ? FunctionInvokingChatClient.CurrentContext?.CallContent : null;
        var toolCallId = nestedInvocation?.CallId ?? callContent?.CallId ?? Guid.NewGuid().ToString("N");
        var parentToolCallId = nestedInvocation?.ParentCallId;
        var callArguments = callContent is { Arguments: { } argumentsContent }
            ? argumentsContent.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal)
            : null;
        var execution = current();
        var id = execution is null ? Guid.NewGuid().ToString("N") :
            await execution.StartToolAsync(Name, arguments, cancellationToken);
        var displayArguments = arguments.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
        PiSharpToolExecutionContext? toolExecutionContext = null;
        if (loadout is not null)
        {
            Action<string>? onNestedUpdate = nestedInvocation?.OnUpdate is { } onUpdate
                ? text => onUpdate(new PiSharpToolExecutionResult(toolCallId, Name, null, text, false))
                : null;
            toolExecutionContext = nestedInvocation is null
                ? PiSharpToolExecutionContext.CreateRoot(loadout,
                    allToolFunctions ?? (() => new Dictionary<string, AIFunction>(StringComparer.Ordinal)),
                    publish, toolCallId, id, Name, callArguments ?? displayArguments)
                : nestedInvocation.Parent.ForToolCall(toolCallId, parentToolCallId, id, Name,
                    callArguments ?? displayArguments, onNestedUpdate);
        }
        publish(new AgentLifecycleEvent("tool_execution_started", Tool: Name, OperationId: id)
        {
            ToolArguments = callArguments ?? displayArguments,
            ToolCallId = toolCallId,
            ParentToolCallId = parentToolCallId
        });
        object? value = null;
        Exception? failure = null;
        IDictionary<object, object?>? argumentContext = null;
        object? previousUpdate = null;
        var hadUpdate = false;
        object? previousPiSharpContext = null;
        var hadPiSharpContext = false;
        if (Name == "bash" || loadout is not null)
        {
            argumentContext = arguments.Context ??= new Dictionary<object, object?>();
            if (Name == "bash")
            {
                hadUpdate = argumentContext.TryGetValue(CodingTools.BashOutputContextKey, out previousUpdate);
                argumentContext[CodingTools.BashOutputContextKey] = (Action<string>)(text =>
                {
                    toolExecutionContext?.ReportProgress(text);
                    if (toolExecutionContext is null)
                        publish(new AgentLifecycleEvent("tool_execution_update", Text: text, Tool: Name, OperationId: id)
                        {
                            ToolArguments = callArguments ?? displayArguments,
                            ToolCallId = toolCallId,
                            ParentToolCallId = parentToolCallId
                        });
                });
            }
            if (loadout is not null)
            {
                hadPiSharpContext = argumentContext.TryGetValue(PiSharpToolExecutionContext.ContextKey, out previousPiSharpContext);
                argumentContext[PiSharpToolExecutionContext.ContextKey] = toolExecutionContext!;
            }
        }
        try
        {
            if (!isCallable)
                throw new InvalidOperationException($"Tool '{Name}' is not active or callable in this session.");
            value = await base.InvokeCoreAsync(arguments, cancellationToken);
        }
        catch (Exception error) { failure = error; }
        finally
        {
            if (argumentContext is not null)
            {
                if (Name == "bash")
                {
                    if (hadUpdate) argumentContext[CodingTools.BashOutputContextKey] = previousUpdate!;
                    else argumentContext.Remove(CodingTools.BashOutputContextKey);
                }
                if (loadout is not null)
                {
                    if (hadPiSharpContext) argumentContext[PiSharpToolExecutionContext.ContextKey] = previousPiSharpContext!;
                    else argumentContext.Remove(PiSharpToolExecutionContext.ContextKey);
                }
            }
        }
        var hasStructuredOutput = ToolResultOutput.TryRead(value, out var resultText, out var details);
        if (!hasStructuredOutput) resultText = value?.ToString();
        try { if (execution is not null) await execution.EndToolAsync(id, resultText, failure); }
        catch (Exception error)
        {
            publish(new("tool_outcome_unknown", Tool: Name, OperationId: id, IsError: true, Error: error.Message));
            throw;
        }
        IReadOnlyList<Microsoft.Extensions.AI.DataContent>? toolImages =
            ReadToolOutput.TryRead(value, out var readOutput) && readOutput.TryCreateImageContent(out var image) ? [image] : null;
        publish(new("tool_execution_finished", Text: resultText, Tool: Name, OperationId: id,
            IsError: failure is not null, Error: failure?.Message,
            Details: hasStructuredOutput ? details : null)
        {
            Images = toolImages,
            ToolArguments = callArguments ?? displayArguments,
            ToolCallId = toolCallId,
            ParentToolCallId = parentToolCallId,
            NestedToolCalls = toolExecutionContext?.NestedCalls,
            ToolResultMessage = callContent is null ? null : new ChatMessage(ChatRole.Tool,
                [new FunctionResultContent(callContent.CallId, value) { Exception = failure }])
        });
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
        return value;
    }
}
