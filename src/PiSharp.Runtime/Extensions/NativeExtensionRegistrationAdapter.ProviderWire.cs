using NativeExtensions = PiSharp.Extensions.Abstractions;

namespace PiSharp.Runtime.Extensions;

internal sealed partial class NativeExtensionRegistrationAdapter
{
    public void AddBeforeProviderRequestHandler(NativeExtensions.PiSharpBeforeProviderRequestHandler handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        EnsureConfiguring();
        var source = registration.CurrentSourceInfo?.Path;
        registration.AddBeforeProviderRequestHandler(async (eventData, context, cancellationToken) =>
        {
            var publicContext = NativeExtensionPublicContextMapper.Create(context, source);
            try
            {
                var publicEvent = new NativeExtensions.PiSharpProviderRequestEvent(eventData.ProviderId,
                    eventData.ModelId, eventData.Protocol, eventData.Payload);
                return await handler(publicEvent, publicContext, cancellationToken).ConfigureAwait(false);
            }
            finally { publicContext.Dispose(); }
        });
    }

    public void AddBeforeProviderHeadersHandler(NativeExtensions.PiSharpBeforeProviderHeadersHandler handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        EnsureConfiguring();
        var source = registration.CurrentSourceInfo?.Path;
        registration.AddBeforeProviderHeadersHandler(async (eventData, context, cancellationToken) =>
        {
            var publicContext = NativeExtensionPublicContextMapper.Create(context, source);
            try
            {
                var publicEvent = new NativeExtensions.PiSharpProviderHeadersEvent(eventData.ProviderId,
                    eventData.ModelId, eventData.Protocol, eventData.Headers);
                return await handler(publicEvent, publicContext, cancellationToken).ConfigureAwait(false);
            }
            finally { publicContext.Dispose(); }
        });
    }

    public void AddAfterProviderResponseHandler(NativeExtensions.PiSharpAfterProviderResponseHandler handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        EnsureConfiguring();
        var source = registration.CurrentSourceInfo?.Path;
        registration.AddAfterProviderResponseHandler(async (eventData, context, cancellationToken) =>
        {
            var publicContext = NativeExtensionPublicContextMapper.Create(context, source);
            try
            {
                var publicEvent = new NativeExtensions.PiSharpProviderResponseEvent(eventData.ProviderId,
                    eventData.ModelId, eventData.Protocol, eventData.StatusCode, eventData.Headers);
                await handler(publicEvent, publicContext, cancellationToken).ConfigureAwait(false);
            }
            finally { publicContext.Dispose(); }
        });
    }

    public void AddProviderStreamEventHandler(NativeExtensions.PiSharpProviderStreamEventHandler handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        EnsureConfiguring();
        var source = registration.CurrentSourceInfo?.Path;
        registration.AddProviderStreamEventHandler(async (eventData, context, cancellationToken) =>
        {
            var publicContext = NativeExtensionPublicContextMapper.Create(context, source);
            try
            {
                var publicEvent = new NativeExtensions.PiSharpProviderStreamEvent(eventData.ProviderId,
                    eventData.ModelId, eventData.Protocol, eventData.EventType, eventData.Data);
                await handler(publicEvent, publicContext, cancellationToken).ConfigureAwait(false);
            }
            finally { publicContext.Dispose(); }
        });
    }
}
