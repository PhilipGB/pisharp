using NativeExtensions = PiSharp.Extensions.Abstractions;

namespace PiSharp.Runtime.Extensions;

internal static class NativeExtensionPublicContextMapper
{
    internal static NativeExtensions.PiSharpExtensionContext Create(PiSharpExtensionContext context,
        string? sourcePath = null)
    {
        var session = context.CreatePublicSessionContext(sourcePath);
        var capabilities = context.CreatePublicCapabilities(sourcePath, session);
        return new(context.WorkingDirectory)
        {
            Mode = context.Mode,
            HasUserInterface = context.HasUserInterface,
            Ui = context.CreateCallbackUi(sourcePath),
            IsIdle = context.IsIdle,
            IsProjectTrusted = context.IsProjectTrusted,
            HasPendingMessages = context.HasPendingMessages,
            Session = session,
            Capabilities = capabilities
        };
    }
}
