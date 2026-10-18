using NativeExtensions = PiSharp.Extensions.Abstractions;
using PiSharp.Runtime.Resources;

namespace PiSharp.Runtime.Sessions;

public sealed partial class ConversationRun
{
    internal bool HasSessionBeforeCompactHandlers => _agent.ExtensionEvents.HasSessionBeforeCompactHandlers;

    internal Task<NativeExtensions.PiSharpExtensionSessionBeforeSwitchResult?> DispatchSessionBeforeSwitchAsync(
        NativeExtensions.PiSharpExtensionSessionSwitchReason reason, string? targetSessionFile,
        string? currentSessionFile, CancellationToken cancellationToken)
    {
        BindExtensionContext();
        return _agent.ExtensionEvents.DispatchSessionBeforeSwitchAsync(
            new NativeExtensions.PiSharpExtensionSessionBeforeSwitchEvent(Conversation.Id, currentSessionFile,
                reason, targetSessionFile), cancellationToken);
    }

    internal Task<NativeExtensions.PiSharpExtensionSessionBeforeForkResult?> DispatchSessionBeforeForkAsync(
        string entryId, NativeExtensions.PiSharpExtensionForkPosition position, string? currentSessionFile,
        CancellationToken cancellationToken)
    {
        BindExtensionContext();
        return _agent.ExtensionEvents.DispatchSessionBeforeForkAsync(
            new NativeExtensions.PiSharpExtensionSessionBeforeForkEvent(Conversation.Id, currentSessionFile,
                entryId, position), cancellationToken);
    }

    internal Task<NativeExtensions.PiSharpExtensionSessionBeforeTreeResult?> DispatchSessionBeforeTreeAsync(
        NativeExtensions.PiSharpExtensionSessionBeforeTreeEvent eventData, CancellationToken cancellationToken)
    {
        BindExtensionContext();
        return _agent.ExtensionEvents.DispatchSessionBeforeTreeAsync(eventData, cancellationToken);
    }

    internal Task DispatchSessionTreeAsync(NativeExtensions.PiSharpExtensionSessionTreeEvent eventData,
        CancellationToken cancellationToken)
    {
        BindExtensionContext();
        return _agent.ExtensionEvents.DispatchSessionTreeAsync(eventData, cancellationToken);
    }

    internal Task<(NativeExtensions.PiSharpExtensionSessionBeforeCompactResult? Result,
        ResourceSourceInfo? SourceInfo)> DispatchSessionBeforeCompactAsync(
        NativeExtensions.PiSharpExtensionSessionBeforeCompactEvent eventData, CancellationToken cancellationToken)
    {
        BindExtensionContext();
        return _agent.ExtensionEvents.DispatchSessionBeforeCompactAsync(eventData, cancellationToken);
    }
}
