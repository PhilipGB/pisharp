using NativeExtensions = PiSharp.Extensions.Abstractions;

namespace PiSharp.Runtime.Extensions;

public sealed partial class ExtensionRegistration
{
    private readonly List<ExtensionHandlerRegistration<ExtensionEventHandler<NativeExtensions.PiSharpContextEvent>>>
        _contextHandlers = [];
    private readonly List<ExtensionHandlerRegistration<ExtensionEventHandler<NativeExtensions.PiSharpContextWithSystemEvent>>>
        _contextWithSystemHandlers = [];

    internal IReadOnlyList<ExtensionHandlerRegistration<ExtensionEventHandler<NativeExtensions.PiSharpContextEvent>>>
        ContextHandlers => _contextHandlers.ToArray();
    internal IReadOnlyList<ExtensionHandlerRegistration<ExtensionEventHandler<NativeExtensions.PiSharpContextWithSystemEvent>>>
        ContextWithSystemHandlers => _contextWithSystemHandlers.ToArray();

    internal void AddContextHandler(
        ExtensionEventHandler<NativeExtensions.PiSharpContextEvent> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        _contextHandlers.Add(new(handler, CurrentSourceInfoFor(handler)));
    }

    internal void AddContextWithSystemHandler(
        ExtensionEventHandler<NativeExtensions.PiSharpContextWithSystemEvent> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        _contextWithSystemHandlers.Add(new(handler, CurrentSourceInfoFor(handler)));
    }
}
