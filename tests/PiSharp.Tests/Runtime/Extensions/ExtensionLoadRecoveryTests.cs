using PiSharp.ResourceDiscoveryExtension;
using PiSharp.Runtime.Extensions;
using PiSharp.Runtime.Resources;

namespace PiSharp.Tests;

[Collection("Extension environment")]
public sealed class ExtensionLoadRecoveryTests
{
    [Fact]
    public void MissingExplicitExtensionReturnsADiagnosticAndDoesNotRetryAPreloadFailure()
    {
        var root = Path.Combine(Path.GetTempPath(), "pisharp-extension-missing-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var path = Path.Combine(root, "missing.dll");
            using var catalog = ExtensionCatalog.Load(root, root, false, discover: false, additionalPaths: [path]);
            var error = Assert.Single(catalog.LoadErrors);
            Assert.Equal(path, error.Path);
            Assert.Equal("Extension path does not exist: " + path, error.Error);
            Assert.Same(catalog, ExtensionCatalog.Load(root, root, false, discover: false,
                additionalPaths: [path], preloaded: catalog));
            Assert.Single(catalog.LoadErrors);
            Assert.Empty(catalog.LoadedExtensions);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Theory]
    [InlineData("configure")]
    [InlineData("assembly")]
    [InlineData("dependency-manifest")]
    public async Task FailedAssemblyOrConfigurationDiscardsPartialRegistrationAndLoadsValidSibling(string failureKind)
    {
        var root = Path.Combine(Path.GetTempPath(), "pisharp-extension-recovery-" + Guid.NewGuid().ToString("N"));
        var agent = Path.Combine(root, "agent");
        var package = Path.Combine(root, "package");
        var folder = Path.Combine(package, "extensions");
        var previousFailure = Environment.GetEnvironmentVariable("PISHARP_TEST_EXTENSION_PARTIAL_FAILURE");
        var previousDiscovery = Environment.GetEnvironmentVariable("PISHARP_TEST_RESOURCE_DISCOVERY");
        Directory.CreateDirectory(agent);
        Directory.CreateDirectory(folder);
        try
        {
            Environment.SetEnvironmentVariable("PISHARP_TEST_EXTENSION_PARTIAL_FAILURE", "true");
            Environment.SetEnvironmentVariable("PISHARP_TEST_RESOURCE_DISCOVERY", "true");
            if (failureKind == "assembly") await File.WriteAllTextAsync(Path.Combine(folder, "bad.dll"), "invalid assembly content");
            else File.Copy(typeof(ResourceDiscoveryFixture).Assembly.Location, Path.Combine(folder, "bad.dll"));
            if (failureKind == "dependency-manifest")
                await File.WriteAllTextAsync(Path.Combine(folder, "bad.deps.json"), "invalid dependency manifest");
            File.Copy(typeof(ResourceDiscoveryFixture).Assembly.Location, Path.Combine(folder, "guide.dll"));
            await File.WriteAllTextAsync(Path.Combine(package, "package.json"), "{\"pi\":{\"extensions\":[\"extensions\"]}}");

            using var catalog = ExtensionCatalog.Load(agent, root, false, userPackages: [new("../package")]);

            Assert.Equal(Path.Combine(folder, "guide.dll"), Assert.Single(catalog.LoadedExtensions).Path);
            Assert.DoesNotContain("partial_command", catalog.Registration.Commands.Keys);
            Assert.Empty(catalog.Registration.Tools);
            Assert.Empty(catalog.Registration.ToolSourceInfo);
            Assert.Empty(catalog.Registration.McpServers);
            Assert.Empty(catalog.Registration.VirtualModels.Models);
            var failure = Assert.Single(catalog.LoadErrors);
            Assert.Equal(Path.Combine(folder, "bad.dll"), failure.Path);
            if (failureKind == "configure") Assert.Equal("Failed to load extension: fixture extension configuration failure", failure.Error);
            else Assert.StartsWith("Failed to load extension: ", failure.Error);
            Assert.Same(catalog, ExtensionCatalog.Load(agent, root, false, preloaded: catalog,
                userPackages: [new("../package")]));
            Assert.Single(catalog.LoadErrors);
            Assert.Single(catalog.LoadedExtensions);
            var resources = await catalog.DiscoverResourcesAsync(root, ExtensionResourceDiscoveryReason.Startup);
            Assert.Single(resources.SkillPaths);
            Assert.Single(resources.PromptPaths);
            Assert.Single(resources.ThemePaths);
        }
        finally
        {
            Environment.SetEnvironmentVariable("PISHARP_TEST_EXTENSION_PARTIAL_FAILURE", previousFailure);
            Environment.SetEnvironmentVariable("PISHARP_TEST_RESOURCE_DISCOVERY", previousDiscovery);
            Directory.Delete(root, recursive: true);
        }
    }
}
