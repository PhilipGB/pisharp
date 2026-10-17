using NativeExtensions = PiSharp.Extensions.Abstractions;

namespace PiSharp.Runtime.Extensions;

internal sealed partial class NativeExtensionRegistrationAdapter
{
    public void AddResourceDiscoveryHandler(NativeExtensions.PiSharpResourceDiscoveryHandler handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        EnsureConfiguring();
        registration.AddResourceDiscoveryHandler(async (context, cancellationToken) =>
        {
            var publicReason = context.Reason switch
            {
                ExtensionResourceDiscoveryReason.Startup => NativeExtensions.PiSharpResourceDiscoveryReason.Startup,
                ExtensionResourceDiscoveryReason.Reload => NativeExtensions.PiSharpResourceDiscoveryReason.Reload,
                _ => throw new ArgumentOutOfRangeException(nameof(context), context.Reason, "Unknown resource discovery reason.")
            };
            var result = await handler(new(context.WorkingDirectory, publicReason), cancellationToken);
            return result is null
                ? null
                : new ExtensionResourceDiscoveryResult(result.SkillPaths, result.PromptPaths, result.ThemePaths);
        });
    }
}
