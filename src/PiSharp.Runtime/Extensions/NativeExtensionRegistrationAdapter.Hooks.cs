using NativeExtensions = PiSharp.Extensions.Abstractions;
using RuntimeToolCallDecision = PiSharp.Runtime.Extensions.PiSharpToolCallDecision;

namespace PiSharp.Runtime.Extensions;

internal sealed partial class NativeExtensionRegistrationAdapter
{
    public void AddContextTransform(NativeExtensions.PiSharpContextTransform transform)
    {
        ArgumentNullException.ThrowIfNull(transform);
        EnsureConfiguring();
        registration.AddContextTransform((messages, cancellationToken) => transform(messages, cancellationToken));
    }

    public void AddToolCallHook(NativeExtensions.PiSharpToolCallHook hook)
    {
        ArgumentNullException.ThrowIfNull(hook);
        EnsureConfiguring();
        registration.AddToolCallHook(async (context, cancellationToken) =>
        {
            var publicContext = new NativeExtensions.PiSharpToolCallContext(context.ToolName,
                context.ToolCallId, context.ParentToolCallId, context.Arguments);
            var decision = await hook(publicContext, cancellationToken).ConfigureAwait(false);
            return decision is null
                ? null!
                : new RuntimeToolCallDecision(decision.Allowed, decision.Error);
        });
    }

    public void AddToolResultHook(NativeExtensions.PiSharpToolResultHook hook)
    {
        ArgumentNullException.ThrowIfNull(hook);
        EnsureConfiguring();
        registration.AddToolResultHook(async (context, cancellationToken) =>
        {
            var publicContext = new NativeExtensions.PiSharpToolResultContext(context.ToolName,
                context.ToolCallId, context.ParentToolCallId, context.Arguments, context.Result, context.Exception)
            {
                IsError = context.IsError,
                Error = context.Error
            };
            await hook(publicContext, cancellationToken).ConfigureAwait(false);
            context.Result = publicContext.Result;
            context.Exception = publicContext.Exception;
            context.IsError = publicContext.IsError;
            context.Error = publicContext.Error;
        });
    }
}
