using NativeExtensions = PiSharp.Extensions.Abstractions;

namespace PiSharp.Runtime.Extensions;

internal sealed partial class NativeExtensionRegistrationAdapter
{
    public void AddTurnEndBoundaryHandler(NativeExtensions.PiSharpExtensionTurnEndBoundaryHandler handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        EnsureConfiguring();
        var source = registration.CurrentSourceInfo?.Path;
        registration.AddTurnEndBoundaryHandler(async (eventData, context, cancellationToken) =>
        {
            var publicContext = NativeExtensionPublicContextMapper.Create(context, source);
            return await InvokeDecisionWithRevocableContextAsync(publicContext, cancellationToken,
                () => handler(eventData, publicContext, cancellationToken)).ConfigureAwait(false);
        });
    }

    public void AddAgentBeforeSettleHandler(NativeExtensions.PiSharpExtensionAgentBeforeSettleHandler handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        EnsureConfiguring();
        var source = registration.CurrentSourceInfo?.Path;
        registration.AddAgentBeforeSettleHandler(async (eventData, context, cancellationToken) =>
        {
            var publicContext = NativeExtensionPublicContextMapper.Create(context, source);
            return await InvokeDecisionWithRevocableContextAsync(publicContext, cancellationToken,
                () => handler(eventData, publicContext, cancellationToken)).ConfigureAwait(false);
        });
    }

    public void AddAgentSettledHandler(NativeExtensions.PiSharpExtensionAgentSettledHandler handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        EnsureConfiguring();
        var source = registration.CurrentSourceInfo?.Path;
        registration.AddAgentSettledHandler(async (eventData, context, cancellationToken) =>
        {
            var publicContext = NativeExtensionPublicContextMapper.Create(context, source);
            await InvokeWithRevocableContextAsync(publicContext, cancellationToken,
                () => handler(eventData, publicContext, cancellationToken)).ConfigureAwait(false);
        });
    }
}
