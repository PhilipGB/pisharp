using PiSharp.Runtime.Resources;

namespace PiSharp.Runtime.Extensions;

public enum ExtensionResourceDiscoveryReason
{
    Startup,
    Reload
}

public sealed record ExtensionResourceDiscoveryContext(string WorkingDirectory,
    ExtensionResourceDiscoveryReason Reason);

public sealed record ExtensionResourceDiscoveryResult(
    IReadOnlyList<string>? SkillPaths = null,
    IReadOnlyList<string>? PromptPaths = null,
    IReadOnlyList<string>? ThemePaths = null);

public sealed record ExtensionDiscoveredResourcePath(string Path, ResourceSourceInfo SourceInfo);

public sealed record ExtensionResourceDiscoveryError(string ExtensionPath, string Message);

public sealed record ExtensionResourceDiscovery(
    IReadOnlyList<ExtensionDiscoveredResourcePath> SkillPaths,
    IReadOnlyList<ExtensionDiscoveredResourcePath> PromptPaths,
    IReadOnlyList<ExtensionDiscoveredResourcePath> ThemePaths,
    IReadOnlyList<ExtensionResourceDiscoveryError> Errors);

public delegate Task<ExtensionResourceDiscoveryResult?> ExtensionResourceDiscoveryHandler(
    ExtensionResourceDiscoveryContext context, CancellationToken cancellationToken);

internal sealed record RegisteredResourceDiscoveryHandler(ExtensionResourceDiscoveryHandler Handler,
    ResourceSourceInfo ExtensionSource);
