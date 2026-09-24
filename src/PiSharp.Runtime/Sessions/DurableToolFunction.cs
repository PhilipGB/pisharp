using System.Runtime.ExceptionServices;
using Microsoft.Extensions.AI;
using PiSharp.Runtime;

namespace PiSharp.Runtime.Sessions;

/// <summary>Tool lifecycle originates here, at invocation time rather than from inferred model updates.</summary>
internal sealed class DurableToolFunction(AIFunction inner, Func<DurableExecution?> current,
    Action<AgentLifecycleEvent> publish) : DelegatingAIFunction(inner)
{
    protected override async ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var execution = current();
        var id = execution is null ? Guid.NewGuid().ToString("N") :
            await execution.StartToolAsync(Name, arguments, cancellationToken);
        publish(new("tool_execution_started", Tool: Name, OperationId: id));
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
                publish(new("tool_execution_update", Text: text, Tool: Name, OperationId: id)));
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
        try { if (execution is not null) await execution.EndToolAsync(id, value, failure); }
        catch (Exception error)
        {
            publish(new("tool_outcome_unknown", Tool: Name, OperationId: id, IsError: true, Error: error.Message));
            throw;
        }
        publish(new("tool_execution_finished", Text: value?.ToString(), Tool: Name, OperationId: id,
            IsError: failure is not null, Error: failure?.Message));
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
        return value;
    }
}
