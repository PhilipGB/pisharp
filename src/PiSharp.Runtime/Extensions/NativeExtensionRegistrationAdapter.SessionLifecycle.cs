using NativeExtensions = PiSharp.Extensions.Abstractions;

namespace PiSharp.Runtime.Extensions;

internal sealed partial class NativeExtensionRegistrationAdapter
{
    public void AddSessionStartHandler(NativeExtensions.PiSharpExtensionSessionStartHandler handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        EnsureConfiguring();
        var source = registration.CurrentSourceInfo?.Path;
        registration.AddSessionStartHandler(async (reason, context, cancellationToken) =>
        {
            var session = context.CreatePublicSessionContext(source);
            try
            {
                var publicContext = new NativeExtensions.PiSharpExtensionContext(context.WorkingDirectory)
                {
                    IsIdle = context.IsIdle,
                    IsProjectTrusted = context.IsProjectTrusted,
                    HasPendingMessages = context.HasPendingMessages,
                    Session = session
                };
                await handler(new(reason), publicContext, cancellationToken).ConfigureAwait(false);
            }
            finally { session?.Dispose(); }
        });
    }

    public void AddSessionShutdownHandler(NativeExtensions.PiSharpExtensionSessionShutdownHandler handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        EnsureConfiguring();
        var source = registration.CurrentSourceInfo?.Path;
        registration.AddSessionShutdownHandler(async (reason, context, cancellationToken) =>
        {
            var session = context.CreatePublicSessionContext(source);
            try
            {
                var publicContext = new NativeExtensions.PiSharpExtensionContext(context.WorkingDirectory)
                {
                    IsIdle = context.IsIdle,
                    IsProjectTrusted = context.IsProjectTrusted,
                    HasPendingMessages = context.HasPendingMessages,
                    Session = session
                };
                await handler(new(reason), publicContext, cancellationToken).ConfigureAwait(false);
            }
            finally { session?.Dispose(); }
        });
    }
}
