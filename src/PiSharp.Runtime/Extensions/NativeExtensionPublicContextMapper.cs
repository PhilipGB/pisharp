using NativeExtensions = PiSharp.Extensions.Abstractions;

namespace PiSharp.Runtime.Extensions;

internal static class NativeExtensionPublicContextMapper
{
    internal static NativeExtensions.PiSharpExtensionContext Create(PiSharpExtensionContext context) =>
        new(context.WorkingDirectory)
        {
            Mode = context.Mode,
            HasUserInterface = context.HasUserInterface,
            Ui = context.Ui,
            IsIdle = context.IsIdle,
            IsProjectTrusted = context.IsProjectTrusted,
            HasPendingMessages = context.HasPendingMessages
        };
}
