using NativeExtensions = PiSharp.Extensions.Abstractions;
using PiSharp.Runtime.Resources;

namespace PiSharp.Runtime.Extensions;

internal delegate ValueTask RuntimeSessionStartHandler(
    NativeExtensions.PiSharpExtensionSessionStartReason reason,
    PiSharpExtensionContext context,
    CancellationToken cancellationToken);

internal delegate ValueTask RuntimeSessionShutdownHandler(
    NativeExtensions.PiSharpExtensionSessionShutdownReason reason,
    PiSharpExtensionContext context,
    CancellationToken cancellationToken);

internal sealed record ExtensionSessionStartHandlerRegistration(RuntimeSessionStartHandler Handler,
    ResourceSourceInfo SourceInfo);

internal sealed record ExtensionSessionShutdownHandlerRegistration(RuntimeSessionShutdownHandler Handler,
    ResourceSourceInfo SourceInfo);

public sealed partial class ExtensionRegistration
{
    private readonly List<ExtensionSessionStartHandlerRegistration> _sessionStartHandlers = [];
    private readonly List<ExtensionSessionShutdownHandlerRegistration> _sessionShutdownHandlers = [];

    internal IReadOnlyList<ExtensionSessionStartHandlerRegistration> SessionStartHandlers =>
        _sessionStartHandlers.ToArray();

    internal IReadOnlyList<ExtensionSessionShutdownHandlerRegistration> SessionShutdownHandlers =>
        _sessionShutdownHandlers.ToArray();

    internal void AddSessionStartHandler(RuntimeSessionStartHandler handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        _sessionStartHandlers.Add(new(handler, CurrentSourceInfoFor(handler)));
    }

    internal void AddSessionShutdownHandler(RuntimeSessionShutdownHandler handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        _sessionShutdownHandlers.Add(new(handler, CurrentSourceInfoFor(handler)));
    }

    private ResourceSourceInfo CurrentSourceInfoFor(Delegate handler) =>
        CurrentSourceInfo ?? SourceInfoFromHandler(handler);
}
