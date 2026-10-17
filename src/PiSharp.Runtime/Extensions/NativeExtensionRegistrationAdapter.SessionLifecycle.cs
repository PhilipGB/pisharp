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
            var publicContext = NativeExtensionPublicContextMapper.Create(context, source);
            try
            {
                await handler(new(reason), publicContext, cancellationToken).ConfigureAwait(false);
            }
            finally { publicContext.Dispose(); }
        });
    }

    public void AddSessionShutdownHandler(NativeExtensions.PiSharpExtensionSessionShutdownHandler handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        EnsureConfiguring();
        var source = registration.CurrentSourceInfo?.Path;
        registration.AddSessionShutdownHandler(async (reason, context, cancellationToken) =>
        {
            var publicContext = NativeExtensionPublicContextMapper.Create(context, source);
            try
            {
                await handler(new(reason), publicContext, cancellationToken).ConfigureAwait(false);
            }
            finally { publicContext.Dispose(); }
        });
    }
}
