using PiSharp.Runtime.Resources;
using PiSharp.Runtime.Sessions;
using NativeExtensions = PiSharp.Extensions.Abstractions;

namespace PiSharp.Runtime.Extensions;

internal sealed partial class NativeExtensionEventDispatcher
{
    internal bool HasSessionBeforeCompactHandlers => registration.SessionBeforeCompactHandlers.Count > 0;

    internal Task<NativeExtensions.PiSharpExtensionSessionBeforeSwitchResult?> DispatchSessionBeforeSwitchAsync(
        NativeExtensions.PiSharpExtensionSessionBeforeSwitchEvent eventData, CancellationToken cancellationToken) =>
        DispatchDecisionAsync(registration.SessionBeforeSwitchHandlers, eventData, "session_before_switch",
            result => result.Cancel, cancellationToken);

    internal Task<NativeExtensions.PiSharpExtensionSessionBeforeForkResult?> DispatchSessionBeforeForkAsync(
        NativeExtensions.PiSharpExtensionSessionBeforeForkEvent eventData, CancellationToken cancellationToken) =>
        DispatchDecisionAsync(registration.SessionBeforeForkHandlers, eventData, "session_before_fork",
            result => result.Cancel, cancellationToken);

    internal Task<NativeExtensions.PiSharpExtensionSessionBeforeTreeResult?> DispatchSessionBeforeTreeAsync(
        NativeExtensions.PiSharpExtensionSessionBeforeTreeEvent eventData, CancellationToken cancellationToken) =>
        DispatchDecisionAsync(registration.SessionBeforeTreeHandlers, eventData, "session_before_tree",
            result => result.Cancel, cancellationToken);

    internal Task DispatchSessionTreeAsync(NativeExtensions.PiSharpExtensionSessionTreeEvent eventData,
        CancellationToken cancellationToken) => DispatchAsync(registration.SessionTreeHandlers, eventData,
        "session_tree", cancellationToken);

    internal async Task<(NativeExtensions.PiSharpExtensionSessionBeforeCompactResult? Result,
        ResourceSourceInfo? SourceInfo)> DispatchSessionBeforeCompactAsync(
        NativeExtensions.PiSharpExtensionSessionBeforeCompactEvent eventData, CancellationToken cancellationToken)
    {
        ResourceSourceInfo? sourceInfo = null;
        var result = await DispatchDecisionAsync(registration.SessionBeforeCompactHandlers, eventData,
            "session_before_compact", result => result.Cancel, cancellationToken,
            handlerSource => sourceInfo = handlerSource).ConfigureAwait(false);
        return (result, sourceInfo);
    }

    private async Task<TResult?> DispatchDecisionAsync<TEventData, TResult>(
        IReadOnlyList<ExtensionHandlerRegistration<ExtensionDecisionEventHandler<TEventData, TResult>>> handlers,
        TEventData eventData, string eventType, Func<TResult, bool> isCancelled,
        CancellationToken cancellationToken, Action<ResourceSourceInfo>? onResultSource = null) where TResult : class
    {
        TResult? result = null;
        foreach (var handler in handlers)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Task<TResult?> pending;
            try
            {
                pending = handler.Handler(eventData, context, cancellationToken).AsTask();
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception error)
            {
                Report(error, handler.SourceInfo, eventType);
                continue;
            }

            TResult? handlerResult;
            try
            {
                handlerResult = await pending.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                ObserveIfStillRunning(pending);
                throw;
            }
            catch (Exception error)
            {
                Report(error, handler.SourceInfo, eventType);
                continue;
            }

            if (handlerResult is null) continue;
            result = handlerResult;
            onResultSource?.Invoke(handler.SourceInfo);
            if (isCancelled(handlerResult)) return handlerResult;
        }
        return result;
    }

    private void Report(Exception error, ResourceSourceInfo sourceInfo, string eventType)
    {
        try
        {
            report(new AgentLifecycleEvent("extension_error", Error: error.Message,
                Details: new { extensionPath = sourceInfo.Path, eventType }));
        }
        catch (Exception)
        {
            // Diagnostics must not replace the extension event's owning operation.
        }
    }

}
