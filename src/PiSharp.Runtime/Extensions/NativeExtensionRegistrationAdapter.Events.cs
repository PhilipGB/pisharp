using NativeExtensions = PiSharp.Extensions.Abstractions;

namespace PiSharp.Runtime.Extensions;

internal sealed partial class NativeExtensionRegistrationAdapter
{
    public void AddSessionInfoChangedHandler(NativeExtensions.PiSharpExtensionSessionInfoChangedHandler handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        EnsureConfiguring();
        var source = registration.CurrentSourceInfo?.Path;
        registration.AddSessionInfoChangedHandler(async (eventData, context, cancellationToken) =>
        {
            var publicContext = NativeExtensionPublicContextMapper.Create(context, source);
            await InvokeWithRevocableContextAsync(publicContext, cancellationToken,
                () => handler(eventData, publicContext, cancellationToken)).ConfigureAwait(false);
        });
    }

    public void AddSessionCompactHandler(NativeExtensions.PiSharpExtensionSessionCompactHandler handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        EnsureConfiguring();
        var source = registration.CurrentSourceInfo?.Path;
        registration.AddSessionCompactHandler(async (eventData, context, cancellationToken) =>
        {
            var publicContext = NativeExtensionPublicContextMapper.Create(context, source);
            await InvokeWithRevocableContextAsync(publicContext, cancellationToken,
                () => handler(eventData, publicContext, cancellationToken)).ConfigureAwait(false);
        });
    }

    public void AddSessionCompactFailedHandler(NativeExtensions.PiSharpExtensionSessionCompactFailedHandler handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        EnsureConfiguring();
        var source = registration.CurrentSourceInfo?.Path;
        registration.AddSessionCompactFailedHandler(async (eventData, context, cancellationToken) =>
        {
            var publicContext = NativeExtensionPublicContextMapper.Create(context, source);
            await InvokeWithRevocableContextAsync(publicContext, cancellationToken,
                () => handler(eventData, publicContext, cancellationToken)).ConfigureAwait(false);
        });
    }

    public void AddAgentStartHandler(NativeExtensions.PiSharpExtensionAgentStartHandler handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        EnsureConfiguring();
        var source = registration.CurrentSourceInfo?.Path;
        registration.AddAgentStartHandler(async (eventData, context, cancellationToken) =>
        {
            var publicContext = NativeExtensionPublicContextMapper.Create(context, source);
            await InvokeWithRevocableContextAsync(publicContext, cancellationToken,
                () => handler(eventData, publicContext, cancellationToken)).ConfigureAwait(false);
        });
    }

    public void AddAgentEndHandler(NativeExtensions.PiSharpExtensionAgentEndHandler handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        EnsureConfiguring();
        var source = registration.CurrentSourceInfo?.Path;
        registration.AddAgentEndHandler(async (eventData, context, cancellationToken) =>
        {
            var publicContext = NativeExtensionPublicContextMapper.Create(context, source);
            await InvokeWithRevocableContextAsync(publicContext, cancellationToken,
                () => handler(eventData, publicContext, cancellationToken)).ConfigureAwait(false);
        });
    }

    public void AddTurnStartHandler(NativeExtensions.PiSharpExtensionTurnStartHandler handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        EnsureConfiguring();
        var source = registration.CurrentSourceInfo?.Path;
        registration.AddTurnStartHandler(async (eventData, context, cancellationToken) =>
        {
            var publicContext = NativeExtensionPublicContextMapper.Create(context, source);
            await InvokeWithRevocableContextAsync(publicContext, cancellationToken,
                () => handler(eventData, publicContext, cancellationToken)).ConfigureAwait(false);
        });
    }

    public void AddTurnEndHandler(NativeExtensions.PiSharpExtensionTurnEndHandler handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        EnsureConfiguring();
        var source = registration.CurrentSourceInfo?.Path;
        registration.AddTurnEndHandler(async (eventData, context, cancellationToken) =>
        {
            var publicContext = NativeExtensionPublicContextMapper.Create(context, source);
            await InvokeWithRevocableContextAsync(publicContext, cancellationToken,
                () => handler(eventData, publicContext, cancellationToken)).ConfigureAwait(false);
        });
    }

    public void AddToolExecutionStartHandler(NativeExtensions.PiSharpExtensionToolExecutionStartHandler handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        EnsureConfiguring();
        var source = registration.CurrentSourceInfo?.Path;
        registration.AddToolExecutionStartHandler(async (eventData, context, cancellationToken) =>
        {
            var publicContext = NativeExtensionPublicContextMapper.Create(context, source);
            await InvokeWithRevocableContextAsync(publicContext, cancellationToken,
                () => handler(eventData, publicContext, cancellationToken)).ConfigureAwait(false);
        });
    }

    public void AddToolExecutionUpdateHandler(NativeExtensions.PiSharpExtensionToolExecutionUpdateHandler handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        EnsureConfiguring();
        var source = registration.CurrentSourceInfo?.Path;
        registration.AddToolExecutionUpdateHandler(async (eventData, context, cancellationToken) =>
        {
            var publicContext = NativeExtensionPublicContextMapper.Create(context, source);
            await InvokeWithRevocableContextAsync(publicContext, cancellationToken,
                () => handler(eventData, publicContext, cancellationToken)).ConfigureAwait(false);
        });
    }

    public void AddToolExecutionEndHandler(NativeExtensions.PiSharpExtensionToolExecutionEndHandler handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        EnsureConfiguring();
        var source = registration.CurrentSourceInfo?.Path;
        registration.AddToolExecutionEndHandler(async (eventData, context, cancellationToken) =>
        {
            var publicContext = NativeExtensionPublicContextMapper.Create(context, source);
            await InvokeWithRevocableContextAsync(publicContext, cancellationToken,
                () => handler(eventData, publicContext, cancellationToken)).ConfigureAwait(false);
        });
    }

    public void AddModelSelectHandler(NativeExtensions.PiSharpExtensionModelSelectHandler handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        EnsureConfiguring();
        var source = registration.CurrentSourceInfo?.Path;
        registration.AddModelSelectHandler(async (eventData, context, cancellationToken) =>
        {
            var publicContext = NativeExtensionPublicContextMapper.Create(context, source);
            await InvokeWithRevocableContextAsync(publicContext, cancellationToken,
                () => handler(eventData, publicContext, cancellationToken)).ConfigureAwait(false);
        });
    }

    public void AddThinkingLevelSelectHandler(NativeExtensions.PiSharpExtensionThinkingLevelSelectHandler handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        EnsureConfiguring();
        var source = registration.CurrentSourceInfo?.Path;
        registration.AddThinkingLevelSelectHandler(async (eventData, context, cancellationToken) =>
        {
            var publicContext = NativeExtensionPublicContextMapper.Create(context, source);
            await InvokeWithRevocableContextAsync(publicContext, cancellationToken,
                () => handler(eventData, publicContext, cancellationToken)).ConfigureAwait(false);
        });
    }

    private static async Task InvokeWithRevocableContextAsync(
        NativeExtensions.PiSharpExtensionContext publicContext,
        CancellationToken cancellationToken,
        Func<ValueTask> invoke)
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
            await handlerTask.ConfigureAwait(false);
        }
        finally { publicContext.Dispose(); }
    }

    private static void ObserveIfFaulted(Task task)
    {
        _ = task.ContinueWith(static completed => _ = completed.Exception, CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }
}
