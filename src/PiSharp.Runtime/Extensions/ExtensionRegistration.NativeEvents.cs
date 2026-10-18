using NativeExtensions = PiSharp.Extensions.Abstractions;

namespace PiSharp.Runtime.Extensions;

internal delegate ValueTask ExtensionEventHandler<in TEventData>(TEventData eventData,
    PiSharpExtensionContext context, CancellationToken cancellationToken);

/// <summary>Source-owned typed callbacks registered by compiled native extensions.</summary>
public sealed partial class ExtensionRegistration
{
    private readonly List<ExtensionHandlerRegistration<ExtensionEventHandler<NativeExtensions.PiSharpExtensionSessionInfoChangedEvent>>>
        _sessionInfoChangedHandlers = [];
    private readonly List<ExtensionHandlerRegistration<ExtensionEventHandler<NativeExtensions.PiSharpExtensionSessionCompactEvent>>>
        _sessionCompactHandlers = [];
    private readonly List<ExtensionHandlerRegistration<ExtensionEventHandler<NativeExtensions.PiSharpExtensionSessionCompactFailedEvent>>>
        _sessionCompactFailedHandlers = [];
    private readonly List<ExtensionHandlerRegistration<ExtensionEventHandler<NativeExtensions.PiSharpExtensionAgentStartEvent>>>
        _agentStartHandlers = [];
    private readonly List<ExtensionHandlerRegistration<ExtensionEventHandler<NativeExtensions.PiSharpExtensionAgentEndEvent>>>
        _agentEndHandlers = [];
    private readonly List<ExtensionHandlerRegistration<ExtensionEventHandler<NativeExtensions.PiSharpExtensionTurnStartEvent>>>
        _turnStartHandlers = [];
    private readonly List<ExtensionHandlerRegistration<ExtensionEventHandler<NativeExtensions.PiSharpExtensionTurnEndEvent>>>
        _turnEndHandlers = [];
    private readonly List<ExtensionHandlerRegistration<ExtensionEventHandler<NativeExtensions.PiSharpExtensionToolExecutionStartEvent>>>
        _toolExecutionStartHandlers = [];
    private readonly List<ExtensionHandlerRegistration<ExtensionEventHandler<NativeExtensions.PiSharpExtensionToolExecutionUpdateEvent>>>
        _toolExecutionUpdateHandlers = [];
    private readonly List<ExtensionHandlerRegistration<ExtensionEventHandler<NativeExtensions.PiSharpExtensionToolExecutionEndEvent>>>
        _toolExecutionEndHandlers = [];
    private readonly List<ExtensionHandlerRegistration<ExtensionEventHandler<NativeExtensions.PiSharpExtensionModelSelectEvent>>>
        _modelSelectHandlers = [];
    private readonly List<ExtensionHandlerRegistration<ExtensionEventHandler<NativeExtensions.PiSharpExtensionThinkingLevelSelectEvent>>>
        _thinkingLevelSelectHandlers = [];

    internal IReadOnlyList<ExtensionHandlerRegistration<ExtensionEventHandler<NativeExtensions.PiSharpExtensionSessionInfoChangedEvent>>>
        SessionInfoChangedHandlers => _sessionInfoChangedHandlers.ToArray();
    internal IReadOnlyList<ExtensionHandlerRegistration<ExtensionEventHandler<NativeExtensions.PiSharpExtensionSessionCompactEvent>>>
        SessionCompactHandlers => _sessionCompactHandlers.ToArray();
    internal IReadOnlyList<ExtensionHandlerRegistration<ExtensionEventHandler<NativeExtensions.PiSharpExtensionSessionCompactFailedEvent>>>
        SessionCompactFailedHandlers => _sessionCompactFailedHandlers.ToArray();
    internal IReadOnlyList<ExtensionHandlerRegistration<ExtensionEventHandler<NativeExtensions.PiSharpExtensionAgentStartEvent>>>
        AgentStartHandlers => _agentStartHandlers.ToArray();
    internal IReadOnlyList<ExtensionHandlerRegistration<ExtensionEventHandler<NativeExtensions.PiSharpExtensionAgentEndEvent>>>
        AgentEndHandlers => _agentEndHandlers.ToArray();
    internal IReadOnlyList<ExtensionHandlerRegistration<ExtensionEventHandler<NativeExtensions.PiSharpExtensionTurnStartEvent>>>
        TurnStartHandlers => _turnStartHandlers.ToArray();
    internal IReadOnlyList<ExtensionHandlerRegistration<ExtensionEventHandler<NativeExtensions.PiSharpExtensionTurnEndEvent>>>
        TurnEndHandlers => _turnEndHandlers.ToArray();
    internal IReadOnlyList<ExtensionHandlerRegistration<ExtensionEventHandler<NativeExtensions.PiSharpExtensionToolExecutionStartEvent>>>
        ToolExecutionStartHandlers => _toolExecutionStartHandlers.ToArray();
    internal IReadOnlyList<ExtensionHandlerRegistration<ExtensionEventHandler<NativeExtensions.PiSharpExtensionToolExecutionUpdateEvent>>>
        ToolExecutionUpdateHandlers => _toolExecutionUpdateHandlers.ToArray();
    internal IReadOnlyList<ExtensionHandlerRegistration<ExtensionEventHandler<NativeExtensions.PiSharpExtensionToolExecutionEndEvent>>>
        ToolExecutionEndHandlers => _toolExecutionEndHandlers.ToArray();
    internal IReadOnlyList<ExtensionHandlerRegistration<ExtensionEventHandler<NativeExtensions.PiSharpExtensionModelSelectEvent>>>
        ModelSelectHandlers => _modelSelectHandlers.ToArray();
    internal IReadOnlyList<ExtensionHandlerRegistration<ExtensionEventHandler<NativeExtensions.PiSharpExtensionThinkingLevelSelectEvent>>>
        ThinkingLevelSelectHandlers => _thinkingLevelSelectHandlers.ToArray();

    internal void AddSessionInfoChangedHandler(ExtensionEventHandler<NativeExtensions.PiSharpExtensionSessionInfoChangedEvent> handler) =>
        Add(_sessionInfoChangedHandlers, handler);
    internal void AddSessionCompactHandler(ExtensionEventHandler<NativeExtensions.PiSharpExtensionSessionCompactEvent> handler) =>
        Add(_sessionCompactHandlers, handler);
    internal void AddSessionCompactFailedHandler(ExtensionEventHandler<NativeExtensions.PiSharpExtensionSessionCompactFailedEvent> handler) =>
        Add(_sessionCompactFailedHandlers, handler);
    internal void AddAgentStartHandler(ExtensionEventHandler<NativeExtensions.PiSharpExtensionAgentStartEvent> handler) =>
        Add(_agentStartHandlers, handler);
    internal void AddAgentEndHandler(ExtensionEventHandler<NativeExtensions.PiSharpExtensionAgentEndEvent> handler) =>
        Add(_agentEndHandlers, handler);
    internal void AddTurnStartHandler(ExtensionEventHandler<NativeExtensions.PiSharpExtensionTurnStartEvent> handler) =>
        Add(_turnStartHandlers, handler);
    internal void AddTurnEndHandler(ExtensionEventHandler<NativeExtensions.PiSharpExtensionTurnEndEvent> handler) =>
        Add(_turnEndHandlers, handler);
    internal void AddToolExecutionStartHandler(ExtensionEventHandler<NativeExtensions.PiSharpExtensionToolExecutionStartEvent> handler) =>
        Add(_toolExecutionStartHandlers, handler);
    internal void AddToolExecutionUpdateHandler(ExtensionEventHandler<NativeExtensions.PiSharpExtensionToolExecutionUpdateEvent> handler) =>
        Add(_toolExecutionUpdateHandlers, handler);
    internal void AddToolExecutionEndHandler(ExtensionEventHandler<NativeExtensions.PiSharpExtensionToolExecutionEndEvent> handler) =>
        Add(_toolExecutionEndHandlers, handler);
    internal void AddModelSelectHandler(ExtensionEventHandler<NativeExtensions.PiSharpExtensionModelSelectEvent> handler) =>
        Add(_modelSelectHandlers, handler);
    internal void AddThinkingLevelSelectHandler(ExtensionEventHandler<NativeExtensions.PiSharpExtensionThinkingLevelSelectEvent> handler) =>
        Add(_thinkingLevelSelectHandlers, handler);

    private void Add<T>(List<ExtensionHandlerRegistration<T>> handlers, T handler) where T : Delegate
    {
        ArgumentNullException.ThrowIfNull(handler);
        handlers.Add(new(handler, CurrentSourceInfoFor(handler)));
    }
}
