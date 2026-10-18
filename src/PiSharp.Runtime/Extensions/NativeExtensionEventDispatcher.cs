using PiSharp.Runtime.Resources;
using PiSharp.Runtime.Sessions;
using NativeExtensions = PiSharp.Extensions.Abstractions;

namespace PiSharp.Runtime.Extensions;

/// <summary>Sequentially dispatches source-owned native extension event observers.</summary>
internal sealed partial class NativeExtensionEventDispatcher(ExtensionRegistration registration,
    PiSharpExtensionContext context, Action<AgentLifecycleEvent> report)
{
    internal Task DispatchSessionInfoChangedAsync(NativeExtensions.PiSharpExtensionSessionInfoChangedEvent eventData,
        CancellationToken cancellationToken) => DispatchAsync(registration.SessionInfoChangedHandlers, eventData,
        "session_info_changed", cancellationToken);

    internal Task DispatchSessionCompactAsync(NativeExtensions.PiSharpExtensionSessionCompactEvent eventData,
        CancellationToken cancellationToken) => DispatchAsync(registration.SessionCompactHandlers, eventData,
        "session_compact", cancellationToken);

    internal Task DispatchSessionCompactFailedAsync(NativeExtensions.PiSharpExtensionSessionCompactFailedEvent eventData,
        CancellationToken cancellationToken) => DispatchAsync(registration.SessionCompactFailedHandlers, eventData,
        "session_compact_failed", cancellationToken);

    internal Task DispatchAgentStartAsync(NativeExtensions.PiSharpExtensionAgentStartEvent eventData,
        CancellationToken cancellationToken, Action<AgentLifecycleEvent>? reportEvent = null) =>
        DispatchAsync(registration.AgentStartHandlers, eventData, "agent_start", cancellationToken, reportEvent);

    internal Task DispatchAgentEndAsync(NativeExtensions.PiSharpExtensionAgentEndEvent eventData,
        CancellationToken cancellationToken, Action<AgentLifecycleEvent>? reportEvent = null) =>
        DispatchAsync(registration.AgentEndHandlers, eventData, "agent_end", cancellationToken, reportEvent);

    internal Task DispatchTurnStartAsync(NativeExtensions.PiSharpExtensionTurnStartEvent eventData,
        CancellationToken cancellationToken, Action<AgentLifecycleEvent>? reportEvent = null) =>
        DispatchAsync(registration.TurnStartHandlers, eventData, "turn_start", cancellationToken, reportEvent);

    internal Task DispatchTurnEndAsync(NativeExtensions.PiSharpExtensionTurnEndEvent eventData,
        CancellationToken cancellationToken, Action<AgentLifecycleEvent>? reportEvent = null) =>
        DispatchAsync(registration.TurnEndHandlers, eventData, "turn_end", cancellationToken, reportEvent);

    internal Task DispatchToolExecutionStartAsync(NativeExtensions.PiSharpExtensionToolExecutionStartEvent eventData,
        CancellationToken cancellationToken) => DispatchAsync(registration.ToolExecutionStartHandlers, eventData,
        "tool_execution_start", cancellationToken);

    internal Task DispatchToolExecutionUpdateAsync(NativeExtensions.PiSharpExtensionToolExecutionUpdateEvent eventData,
        CancellationToken cancellationToken) => DispatchAsync(registration.ToolExecutionUpdateHandlers, eventData,
        "tool_execution_update", cancellationToken);

    internal Task DispatchToolExecutionEndAsync(NativeExtensions.PiSharpExtensionToolExecutionEndEvent eventData,
        CancellationToken cancellationToken) => DispatchAsync(registration.ToolExecutionEndHandlers, eventData,
        "tool_execution_end", cancellationToken);

    internal Task DispatchModelSelectAsync(NativeExtensions.PiSharpExtensionModelSelectEvent eventData,
        CancellationToken cancellationToken) => DispatchAsync(registration.ModelSelectHandlers, eventData,
        "model_select", cancellationToken);

    internal Task DispatchThinkingLevelSelectAsync(NativeExtensions.PiSharpExtensionThinkingLevelSelectEvent eventData,
        CancellationToken cancellationToken) => DispatchAsync(registration.ThinkingLevelSelectHandlers, eventData,
        "thinking_level_select", cancellationToken);

    private async Task DispatchAsync<TEventData>(
        IReadOnlyList<ExtensionHandlerRegistration<ExtensionEventHandler<TEventData>>> handlers,
        TEventData eventData, string eventType, CancellationToken cancellationToken,
        Action<AgentLifecycleEvent>? reportEvent = null)
    {
        foreach (var handler in handlers)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Task pending;
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
                Report(error, handler.SourceInfo, eventType, reportEvent);
                continue;
            }

            try
            {
                await pending.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                ObserveIfStillRunning(pending);
                throw;
            }
            catch (Exception error)
            {
                Report(error, handler.SourceInfo, eventType, reportEvent);
            }
        }
    }

    private void Report(Exception error, ResourceSourceInfo sourceInfo, string eventType,
        Action<AgentLifecycleEvent>? reportEvent)
    {
        try
        {
            (reportEvent ?? report)(new AgentLifecycleEvent("extension_error", Error: error.Message,
                Details: new { extensionPath = sourceInfo.Path, eventType }));
        }
        catch (Exception)
        {
            // Diagnostics must not replace the extension event's owning operation.
        }
    }

    private static void ObserveIfStillRunning(Task pending)
    {
        if (pending.IsCompleted) return;
        _ = pending.ContinueWith(static task => _ = task.Exception, CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }
}
