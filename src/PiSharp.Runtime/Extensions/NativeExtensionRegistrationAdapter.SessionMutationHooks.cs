using NativeExtensions = PiSharp.Extensions.Abstractions;

namespace PiSharp.Runtime.Extensions;

internal sealed partial class NativeExtensionRegistrationAdapter
{
    public void AddSessionBeforeSwitchHandler(NativeExtensions.PiSharpExtensionSessionBeforeSwitchHandler handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        EnsureConfiguring();
        var source = registration.CurrentSourceInfo?.Path;
        registration.AddSessionBeforeSwitchHandler(async (eventData, context, cancellationToken) =>
        {
            var publicContext = NativeExtensionPublicContextMapper.Create(context, source);
            return await InvokeDecisionWithRevocableContextAsync(publicContext, cancellationToken,
                () => handler(eventData, publicContext, cancellationToken)).ConfigureAwait(false);
        });
    }

    public void AddSessionBeforeForkHandler(NativeExtensions.PiSharpExtensionSessionBeforeForkHandler handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        EnsureConfiguring();
        var source = registration.CurrentSourceInfo?.Path;
        registration.AddSessionBeforeForkHandler(async (eventData, context, cancellationToken) =>
        {
            var publicContext = NativeExtensionPublicContextMapper.Create(context, source);
            return await InvokeDecisionWithRevocableContextAsync(publicContext, cancellationToken,
                () => handler(eventData, publicContext, cancellationToken)).ConfigureAwait(false);
        });
    }

    public void AddSessionBeforeTreeHandler(NativeExtensions.PiSharpExtensionSessionBeforeTreeHandler handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        EnsureConfiguring();
        var source = registration.CurrentSourceInfo?.Path;
        registration.AddSessionBeforeTreeHandler(async (eventData, context, cancellationToken) =>
        {
            var publicContext = NativeExtensionPublicContextMapper.Create(context, source);
            return await InvokeDecisionWithRevocableContextAsync(publicContext, cancellationToken,
                () => handler(eventData, publicContext, cancellationToken)).ConfigureAwait(false);
        });
    }

    public void AddSessionTreeHandler(NativeExtensions.PiSharpExtensionSessionTreeHandler handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        EnsureConfiguring();
        var source = registration.CurrentSourceInfo?.Path;
        registration.AddSessionTreeHandler(async (eventData, context, cancellationToken) =>
        {
            var publicContext = NativeExtensionPublicContextMapper.Create(context, source);
            await InvokeWithRevocableContextAsync(publicContext, cancellationToken,
                () => handler(eventData, publicContext, cancellationToken)).ConfigureAwait(false);
        });
    }

    public void AddSessionBeforeCompactHandler(NativeExtensions.PiSharpExtensionSessionBeforeCompactHandler handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        EnsureConfiguring();
        var source = registration.CurrentSourceInfo?.Path;
        registration.AddSessionBeforeCompactHandler(async (eventData, context, cancellationToken) =>
        {
            var publicContext = NativeExtensionPublicContextMapper.Create(context, source);
            return await InvokeDecisionWithRevocableContextAsync(publicContext, cancellationToken,
                () => handler(eventData, publicContext, cancellationToken)).ConfigureAwait(false);
        });
    }

    private static async ValueTask<TResult?> InvokeDecisionWithRevocableContextAsync<TResult>(
        NativeExtensions.PiSharpExtensionContext publicContext, CancellationToken cancellationToken,
        Func<ValueTask<TResult?>> invoke) where TResult : class
    {
        var revoked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancellationRegistration = cancellationToken.Register(static state =>
        {
            var (context, completion) =
                ((NativeExtensions.PiSharpExtensionContext, TaskCompletionSource))state!;
            try { context.Dispose(); }
            catch (Exception)
            {
                // Cancellation callbacks must not break host cancellation cleanup.
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
