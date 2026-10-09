using PiSharp.Runtime.Extensions;

namespace PiSharp.ResourceDiscoveryExtension;

public sealed class ResourceDiscoveryFixture : IPiSharpExtension
{
    public void Configure(ExtensionRegistration registration) =>
        registration.AddResourceDiscoveryHandler((context, _) =>
        {
            var phase = context.Reason == ExtensionResourceDiscoveryReason.Reload ? "reloaded" : "startup";
            var root = Path.Combine(context.WorkingDirectory, ".pi", "extension-resources", phase);
            return Task.FromResult<ExtensionResourceDiscoveryResult?>(new(
                [new Uri(Path.Combine(root, "skills")).AbsoluteUri],
                [Path.Combine(root, "prompts")],
                [Path.Combine(root, "themes")]));
        });
}
