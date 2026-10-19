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
            var publicEvent = new NativeExtensions.PiSharpProviderRequestEvent(eventData.ProviderId,
                eventData.ModelId, eventData.Protocol, eventData.Payload);
            return await InvokeWithRevocableContextAsync(publicContext, cancellationToken,
                () => handler(publicEvent, publicContext, cancellationToken)).ConfigureAwait(false);
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
            var publicEvent = new NativeExtensions.PiSharpProviderHeadersEvent(eventData.ProviderId,
                eventData.ModelId, eventData.Protocol,
                new Dictionary<string, string?>(eventData.Headers, StringComparer.OrdinalIgnoreCase));
            try
            {
                await InvokeWithRevocableContextAsync(publicContext, cancellationToken,
                    () => handler(publicEvent, publicContext, cancellationToken)).ConfigureAwait(false);
            }
            finally
            {
                eventData.Headers.Clear();
                foreach (var (name, value) in publicEvent.Headers) eventData.Headers[name] = value;
            }
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
            var publicEvent = new NativeExtensions.PiSharpProviderResponseEvent(eventData.ProviderId,
                eventData.ModelId, eventData.Protocol, eventData.StatusCode, eventData.Headers);
            await InvokeWithRevocableContextAsync(publicContext, cancellationToken,
                () => handler(publicEvent, publicContext, cancellationToken)).ConfigureAwait(false);
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
            var publicEvent = new NativeExtensions.PiSharpProviderStreamEvent(eventData.ProviderId,
                eventData.ModelId, eventData.Protocol, eventData.EventType, eventData.Data);
            await InvokeWithRevocableContextAsync(publicContext, cancellationToken,
                () => handler(publicEvent, publicContext, cancellationToken)).ConfigureAwait(false);
        });
    }

    private static async ValueTask<TResult> InvokeWithRevocableContextAsync<TResult>(
        NativeExtensions.PiSharpExtensionContext publicContext, CancellationToken cancellationToken,
        Func<ValueTask<TResult>> invoke)
    {
        var revoked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancellationRegistration = cancellationToken.Register(static state =>
        {
            var (context, completion) =
                ((NativeExtensions.PiSharpExtensionContext, TaskCompletionSource))state!;
            try { context.Dispose(); }
            catch (Exception)
            {
                // Cancellation callbacks must not be able to break host cancellation cleanup.
            }
            finally { completion.TrySetResult(); }
        }, (publicContext, revoked));
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var handlerTask = invoke().AsTask();
            if (!handlerTask.IsCompleted)
            {
                var completed = await Task.WhenAny(handlerTask, revoked.Task).ConfigureAwait(false);
                if (completed == revoked.Task)
                {
                    ObserveIfFaulted(handlerTask);
                    throw new OperationCanceledException(cancellationToken);
                }
            }
            return await handlerTask.ConfigureAwait(false);
        }
        finally { publicContext.Dispose(); }
    }
}
