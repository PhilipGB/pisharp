using PiSharp.ResourceDiscoveryExtension;
using PiSharp.Runtime.Extensions;

namespace PiSharp.Tests;

[Collection("Extension environment")]
public sealed class ExtensionPathIdentityTests
{
    [Theory]
    [InlineData("plain")]
    [InlineData("file-url")]
    [InlineData("alias")]
    public void ExplicitExtensionPathsResolveOnceAndRetainSelectedPackageOrCliMetadata(string mode)
    {
        var root = Path.Combine(Path.GetTempPath(), "pisharp-extension-path-" + Guid.NewGuid().ToString("N"));
        var agent = Path.Combine(root, "agent");
        var package = Path.Combine(root, "package");
        var extension = Path.Combine(package, "extensions", "index.dll");
        Directory.CreateDirectory(agent);
        Directory.CreateDirectory(Path.GetDirectoryName(extension)!);
        try
        {
            File.Copy(typeof(ResourceDiscoveryFixture).Assembly.Location, extension);
            File.WriteAllText(Path.Combine(package, "package.json"), "{\"pi\":{\"extensions\":[\"extensions\"]}}");
            var selected = extension;
            if (mode == "alias")
            {
                if (!OperatingSystem.IsLinux()) return;
                selected = Path.Combine(root, "alias.dll");
                File.CreateSymbolicLink(selected, extension);
            }
            var value = mode == "file-url" ? new Uri(selected).AbsoluteUri : selected;

            using var catalog = ExtensionCatalog.Load(agent, root, false,
                additionalPaths: [value], userPackages: [new("../package")]);

            Assert.Empty(catalog.LoadErrors);
            var loaded = Assert.Single(catalog.LoadedExtensions);
            Assert.Equal(selected, loaded.Path);
            Assert.Equal(mode == "alias" ? "cli" : "../package", loaded.Source);
            Assert.Equal(mode == "alias" ? "temporary" : "user", loaded.Scope);
            Assert.Equal(mode == "alias" ? "top-level" : "package", loaded.Origin);
            Assert.Equal(mode == "alias" ? null : package, loaded.BaseDir);
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}
