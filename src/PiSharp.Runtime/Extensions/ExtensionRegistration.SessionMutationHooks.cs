using NativeExtensions = PiSharp.Extensions.Abstractions;

namespace PiSharp.Runtime.Extensions;

internal delegate ValueTask<TResult?> ExtensionDecisionEventHandler<in TEventData, TResult>(TEventData eventData,
    PiSharpExtensionContext context, CancellationToken cancellationToken) where TResult : class;

/// <summary>Source-owned typed session-mutation hooks registered by compiled native extensions.</summary>
public sealed partial class ExtensionRegistration
{
    private readonly List<ExtensionHandlerRegistration<ExtensionDecisionEventHandler<NativeExtensions.PiSharpExtensionSessionBeforeSwitchEvent,
        NativeExtensions.PiSharpExtensionSessionBeforeSwitchResult>>> _sessionBeforeSwitchHandlers = [];
    private readonly List<ExtensionHandlerRegistration<ExtensionDecisionEventHandler<NativeExtensions.PiSharpExtensionSessionBeforeForkEvent,
        NativeExtensions.PiSharpExtensionSessionBeforeForkResult>>> _sessionBeforeForkHandlers = [];
    private readonly List<ExtensionHandlerRegistration<ExtensionDecisionEventHandler<NativeExtensions.PiSharpExtensionSessionBeforeTreeEvent,
        NativeExtensions.PiSharpExtensionSessionBeforeTreeResult>>> _sessionBeforeTreeHandlers = [];
    private readonly List<ExtensionHandlerRegistration<ExtensionEventHandler<NativeExtensions.PiSharpExtensionSessionTreeEvent>>>
        _sessionTreeHandlers = [];
    private readonly List<ExtensionHandlerRegistration<ExtensionDecisionEventHandler<NativeExtensions.PiSharpExtensionSessionBeforeCompactEvent,
        NativeExtensions.PiSharpExtensionSessionBeforeCompactResult>>> _sessionBeforeCompactHandlers = [];

    internal IReadOnlyList<ExtensionHandlerRegistration<ExtensionDecisionEventHandler<NativeExtensions.PiSharpExtensionSessionBeforeSwitchEvent,
        NativeExtensions.PiSharpExtensionSessionBeforeSwitchResult>>> SessionBeforeSwitchHandlers => _sessionBeforeSwitchHandlers.ToArray();
    internal IReadOnlyList<ExtensionHandlerRegistration<ExtensionDecisionEventHandler<NativeExtensions.PiSharpExtensionSessionBeforeForkEvent,
        NativeExtensions.PiSharpExtensionSessionBeforeForkResult>>> SessionBeforeForkHandlers => _sessionBeforeForkHandlers.ToArray();
    internal IReadOnlyList<ExtensionHandlerRegistration<ExtensionDecisionEventHandler<NativeExtensions.PiSharpExtensionSessionBeforeTreeEvent,
        NativeExtensions.PiSharpExtensionSessionBeforeTreeResult>>> SessionBeforeTreeHandlers => _sessionBeforeTreeHandlers.ToArray();
    internal IReadOnlyList<ExtensionHandlerRegistration<ExtensionEventHandler<NativeExtensions.PiSharpExtensionSessionTreeEvent>>>
        SessionTreeHandlers => _sessionTreeHandlers.ToArray();
    internal IReadOnlyList<ExtensionHandlerRegistration<ExtensionDecisionEventHandler<NativeExtensions.PiSharpExtensionSessionBeforeCompactEvent,
        NativeExtensions.PiSharpExtensionSessionBeforeCompactResult>>> SessionBeforeCompactHandlers => _sessionBeforeCompactHandlers.ToArray();

    internal void AddSessionBeforeSwitchHandler(ExtensionDecisionEventHandler<NativeExtensions.PiSharpExtensionSessionBeforeSwitchEvent,
        NativeExtensions.PiSharpExtensionSessionBeforeSwitchResult> handler) => AddSessionMutationHandler(_sessionBeforeSwitchHandlers, handler);
    internal void AddSessionBeforeForkHandler(ExtensionDecisionEventHandler<NativeExtensions.PiSharpExtensionSessionBeforeForkEvent,
        NativeExtensions.PiSharpExtensionSessionBeforeForkResult> handler) => AddSessionMutationHandler(_sessionBeforeForkHandlers, handler);
    internal void AddSessionBeforeTreeHandler(ExtensionDecisionEventHandler<NativeExtensions.PiSharpExtensionSessionBeforeTreeEvent,
        NativeExtensions.PiSharpExtensionSessionBeforeTreeResult> handler) => AddSessionMutationHandler(_sessionBeforeTreeHandlers, handler);
    internal void AddSessionTreeHandler(ExtensionEventHandler<NativeExtensions.PiSharpExtensionSessionTreeEvent> handler) =>
        AddSessionMutationHandler(_sessionTreeHandlers, handler);
    internal void AddSessionBeforeCompactHandler(ExtensionDecisionEventHandler<NativeExtensions.PiSharpExtensionSessionBeforeCompactEvent,
        NativeExtensions.PiSharpExtensionSessionBeforeCompactResult> handler) => AddSessionMutationHandler(_sessionBeforeCompactHandlers, handler);

    private void AddSessionMutationHandler<T>(List<ExtensionHandlerRegistration<T>> handlers, T handler) where T : Delegate
    {
        ArgumentNullException.ThrowIfNull(handler);
        handlers.Add(new(handler, CurrentSourceInfoFor(handler)));
    }
}
