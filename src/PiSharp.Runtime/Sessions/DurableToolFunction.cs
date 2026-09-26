using System.Runtime.ExceptionServices;
using Microsoft.Extensions.AI;
using PiSharp.Runtime;
using PiSharp.Runtime.Tools;

namespace PiSharp.Runtime.Sessions;

/// <summary>Tool lifecycle originates here, at invocation time rather than from inferred model updates.</summary>
internal sealed class DurableToolFunction(AIFunction inner, Func<DurableExecution?> current,
    Action<AgentLifecycleEvent> publish) : DelegatingAIFunction(inner)
{
    protected override async ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var callContent = FunctionInvokingChatClient.CurrentContext?.CallContent;
        var callArguments = callContent is { Arguments: { } argumentsContent }
            ? argumentsContent.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal)
            : null;
        var execution = current();
        var id = execution is null ? Guid.NewGuid().ToString("N") :
            await execution.StartToolAsync(Name, arguments, cancellationToken);
        var displayArguments = arguments.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
        publish(new AgentLifecycleEvent("tool_execution_started", Tool: Name, OperationId: id)
        {
            ToolArguments = callArguments ?? displayArguments,
            ToolCallId = callContent?.CallId
        });
        object? value = null;
        Exception? failure = null;
        IDictionary<object, object?>? context = null;
        object? previousUpdate = null;
        var hadUpdate = false;
        if (Name == "bash")
        {
            context = arguments.Context ??= new Dictionary<object, object?>();
            hadUpdate = context.TryGetValue(CodingTools.BashOutputContextKey, out previousUpdate);
            context[CodingTools.BashOutputContextKey] = (Action<string>)(text =>
                publish(new AgentLifecycleEvent("tool_execution_update", Text: text, Tool: Name, OperationId: id)
                {
                    ToolArguments = callArguments ?? displayArguments,
                    ToolCallId = callContent?.CallId
                }));
        }
        try { value = await base.InvokeCoreAsync(arguments, cancellationToken); }
        catch (Exception error) { failure = error; }
        finally
        {
            if (context is not null)
            {
                if (hadUpdate) context[CodingTools.BashOutputContextKey] = previousUpdate!;
                else context.Remove(CodingTools.BashOutputContextKey);
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
            ToolCallId = callContent?.CallId,
            ToolResultMessage = callContent is null ? null : new ChatMessage(ChatRole.Tool,
                [new FunctionResultContent(callContent.CallId, resultText) { Exception = failure }])
        });
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
        return value;
    }
}
