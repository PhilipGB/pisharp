using NativeExtensions = PiSharp.Extensions.Abstractions;

namespace PiSharp.Runtime.Extensions;

/// <summary>Source-owned boundary handlers registered by compiled native extensions.</summary>
public sealed partial class ExtensionRegistration
{
    private readonly List<ExtensionHandlerRegistration<ExtensionDecisionEventHandler<
        NativeExtensions.PiSharpExtensionTurnEndBoundaryEvent, NativeExtensions.PiSharpBoundaryResult>>>
        _turnEndBoundaryHandlers = [];
    private readonly List<ExtensionHandlerRegistration<ExtensionDecisionEventHandler<
        NativeExtensions.PiSharpExtensionAgentBeforeSettleEvent, NativeExtensions.PiSharpBoundaryResult>>>
        _agentBeforeSettleHandlers = [];
    private readonly List<ExtensionHandlerRegistration<ExtensionEventHandler<
        NativeExtensions.PiSharpExtensionAgentSettledEvent>>>
        _agentSettledHandlers = [];

    internal IReadOnlyList<ExtensionHandlerRegistration<ExtensionDecisionEventHandler<
        NativeExtensions.PiSharpExtensionTurnEndBoundaryEvent, NativeExtensions.PiSharpBoundaryResult>>>
        TurnEndBoundaryHandlers => _turnEndBoundaryHandlers.ToArray();
    internal IReadOnlyList<ExtensionHandlerRegistration<ExtensionDecisionEventHandler<
        NativeExtensions.PiSharpExtensionAgentBeforeSettleEvent, NativeExtensions.PiSharpBoundaryResult>>>
        AgentBeforeSettleHandlers => _agentBeforeSettleHandlers.ToArray();
    internal IReadOnlyList<ExtensionHandlerRegistration<ExtensionEventHandler<
        NativeExtensions.PiSharpExtensionAgentSettledEvent>>>
        AgentSettledHandlers => _agentSettledHandlers.ToArray();

    internal void AddTurnEndBoundaryHandler(ExtensionDecisionEventHandler<
        NativeExtensions.PiSharpExtensionTurnEndBoundaryEvent, NativeExtensions.PiSharpBoundaryResult> handler) =>
        AddBoundaryHandler(_turnEndBoundaryHandlers, handler);

    internal void AddAgentBeforeSettleHandler(ExtensionDecisionEventHandler<
        NativeExtensions.PiSharpExtensionAgentBeforeSettleEvent, NativeExtensions.PiSharpBoundaryResult> handler) =>
        AddBoundaryHandler(_agentBeforeSettleHandlers, handler);

    internal void AddAgentSettledHandler(ExtensionEventHandler<NativeExtensions.PiSharpExtensionAgentSettledEvent> handler) =>
        AddBoundaryHandler(_agentSettledHandlers, handler);

    private void AddBoundaryHandler<T>(List<ExtensionHandlerRegistration<T>> handlers, T handler) where T : Delegate
    {
        ArgumentNullException.ThrowIfNull(handler);
        handlers.Add(new(handler, CurrentSourceInfoFor(handler)));
    }
}
