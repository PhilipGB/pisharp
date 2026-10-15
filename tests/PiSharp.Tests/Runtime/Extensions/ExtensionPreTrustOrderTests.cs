using PiSharp.ResourceDiscoveryExtension;
using PiSharp.Runtime.Extensions;

namespace PiSharp.Tests;

[Collection("Extension environment")]
public sealed class ExtensionPreTrustOrderTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ProjectModulesAndDiscoveryHandlersPrecedeRetainedPersonalModules(bool trusted)
    {
        var root = Path.Combine(Path.GetTempPath(), "pisharp-pretrust-order-" + Guid.NewGuid().ToString("N"));
        var agent = Path.Combine(root, "agent");
        var cwd = Path.Combine(root, "project");
        var trace = Path.Combine(root, "trace.log");
        Directory.CreateDirectory(agent);
        Directory.CreateDirectory(Path.Combine(cwd, ".pi"));
        var previousDiscovery = Environment.GetEnvironmentVariable("PISHARP_TEST_RESOURCE_DISCOVERY");
        var previousTrace = Environment.GetEnvironmentVariable("PISHARP_TEST_EXTENSION_LOAD_LOG");
        try
        {
            var userFile = CreatePackage("user-package");
            var projectFile = CreatePackage("project-package");
            Environment.SetEnvironmentVariable("PISHARP_TEST_RESOURCE_DISCOVERY", "true");
            Environment.SetEnvironmentVariable("PISHARP_TEST_EXTENSION_LOAD_LOG", trace);
            using var bootstrap = ExtensionCatalog.Load(agent, cwd, false, userPackages: [new("../user-package")]);
            using var final = ExtensionCatalog.Load(agent, cwd, trusted, preloaded: bootstrap,
                userPackages: [new("../user-package")], projectPackages: [new("../../project-package")]);

            var expected = trusted ? new[] { projectFile, userFile } : [userFile];
            Assert.Equal(expected, final.LoadedExtensions.Select(extension => extension.Path));
            Assert.Equal(expected.Length, File.ReadAllLines(trace).Length);
            Assert.Empty(final.LoadErrors);
            var discovered = await final.DiscoverResourcesAsync(cwd, ExtensionResourceDiscoveryReason.Startup);
            Assert.Empty(discovered.Errors);
            Assert.Equal(expected.Select(Path.GetDirectoryName), discovered.SkillPaths.Select(entry => entry.SourceInfo.BaseDir));
        }
        finally
        {
            Environment.SetEnvironmentVariable("PISHARP_TEST_RESOURCE_DISCOVERY", previousDiscovery);
            Environment.SetEnvironmentVariable("PISHARP_TEST_EXTENSION_LOAD_LOG", previousTrace);
            Directory.Delete(root, recursive: true);
        }

        string CreatePackage(string name)
        {
            var package = Path.Combine(root, name);
            var file = Path.Combine(package, "extensions", "index.dll");
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            File.Copy(typeof(ResourceDiscoveryFixture).Assembly.Location, file);
            File.WriteAllText(Path.Combine(package, "package.json"), "{\"pi\":{\"extensions\":[\"extensions\"]}}");
            return file;
        }
    }
}
