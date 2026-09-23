using System.Runtime.ExceptionServices;
using Microsoft.Extensions.AI;

namespace PiSharp.Runtime.Sessions;

/// <summary>Invokes a tool only after its intention has reached the canonical session store.</summary>
internal sealed class DurableToolFunction(AIFunction inner, Func<DurableExecution?> current) : DelegatingAIFunction(inner)
{
    protected override async ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken)
    {
        var execution = current();
        if (execution is null) return await base.InvokeCoreAsync(arguments, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        var id = await execution.StartToolAsync(Name, arguments, cancellationToken);
        object? value = null;
        Exception? failure = null;
        try { value = await base.InvokeCoreAsync(arguments, cancellationToken); }
        catch (Exception error) { failure = error; }
        await execution.EndToolAsync(id, value, failure);
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
        return value;
    }
}
