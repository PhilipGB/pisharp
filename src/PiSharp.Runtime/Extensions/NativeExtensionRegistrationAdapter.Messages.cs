using NativeExtensions = PiSharp.Extensions.Abstractions;

namespace PiSharp.Runtime.Extensions;

internal sealed partial class NativeExtensionRegistrationAdapter
{
    public void AddMessageStartHandler(NativeExtensions.PiSharpExtensionMessageStartHandler handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        EnsureConfiguring();
        var source = registration.CurrentSourceInfo?.Path;
        registration.AddMessageStartHandler(async (eventData, context, cancellationToken) =>
        {
            var publicContext = NativeExtensionPublicContextMapper.Create(context, source);
            await InvokeWithRevocableContextAsync(publicContext, cancellationToken,
                () => handler(eventData, publicContext, cancellationToken)).ConfigureAwait(false);
        });
    }

    public void AddMessageUpdateHandler(NativeExtensions.PiSharpExtensionMessageUpdateHandler handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        EnsureConfiguring();
        var source = registration.CurrentSourceInfo?.Path;
        registration.AddMessageUpdateHandler(async (eventData, context, cancellationToken) =>
        {
            var publicContext = NativeExtensionPublicContextMapper.Create(context, source);
            await InvokeWithRevocableContextAsync(publicContext, cancellationToken,
                () => handler(eventData, publicContext, cancellationToken)).ConfigureAwait(false);
        });
    }

    public void AddMessageEndHandler(NativeExtensions.PiSharpExtensionMessageEndHandler handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        EnsureConfiguring();
        var source = registration.CurrentSourceInfo?.Path;
        registration.AddMessageEndHandler(async (eventData, context, cancellationToken) =>
        {
            var publicContext = NativeExtensionPublicContextMapper.Create(context, source);
            return await InvokeDecisionWithRevocableContextAsync(publicContext, cancellationToken,
                () => handler(eventData, publicContext, cancellationToken)).ConfigureAwait(false);
        });
    }
}
