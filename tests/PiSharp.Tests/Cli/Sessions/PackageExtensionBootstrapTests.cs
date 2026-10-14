using System.Text.Json;
using PiSharp.Cli;
using PiSharp.Cli.Sessions;
using PiSharp.ResourceDiscoveryExtension;
using PiSharp.Runtime.Resources;

namespace PiSharp.Tests;

[Collection("Extension environment")]
public sealed class PackageExtensionBootstrapTests
{
    [Theory]
    [InlineData(false, "manifest")]
    [InlineData(true, "manifest")]
    [InlineData(false, "file")]
    [InlineData(true, "file")]
    [InlineData(false, "root")]
    [InlineData(true, "root")]
    [InlineData(false, "index-with-sibling")]
    [InlineData(true, "index-with-sibling")]
    public async Task PersonalPackageTrustHandlersLoadBeforeTheHeadlessProjectDecision(bool noExtensions, string mode)
    {
        var root = Path.Combine(Path.GetTempPath(), "pisharp-package-bootstrap-" + Guid.NewGuid().ToString("N"));
        var agent = Path.Combine(root, "agent");
        var project = Path.Combine(root, "project");
        var package = Path.Combine(root, "package");
        var extension = Path.Combine(package, "extensions", "index.dll");
        Directory.CreateDirectory(agent);
        Directory.CreateDirectory(Path.Combine(project, ".pi"));
        Directory.CreateDirectory(Path.GetDirectoryName(extension)!);
        try
        {
            File.Copy(typeof(ResourceDiscoveryFixture).Assembly.Location, extension);
            if (mode == "index-with-sibling")
                File.Copy(extension, Path.Combine(Path.GetDirectoryName(extension)!, "sibling.dll"));
            await File.WriteAllTextAsync(Path.Combine(package, "package.json"), "{\"pi\":{\"extensions\":[\"extensions\"]}}");
            if (mode == "root")
            {
                File.Delete(Path.Combine(package, "package.json"));
                var oldExtension = extension;
                extension = Path.Combine(package, "index.dll");
                File.Move(oldExtension, extension);
                Directory.Delete(Path.GetDirectoryName(oldExtension)!);
            }
            var source = mode == "file" ? "../package/extensions/index.dll" : "../package";
            await File.WriteAllTextAsync(Path.Combine(agent, "settings.json"),
                JsonSerializer.Serialize(new { packages = new[] { source } }));
            await File.WriteAllTextAsync(Path.Combine(project, ".pi", "settings.json"), "{}");
            await File.WriteAllTextAsync(Path.Combine(project, ".pi", "project-trust-extension.json"),
                "{\"decision\":\"yes\",\"remember\":true}");
            var arguments = CliArguments.Parse(noExtensions ? ["--mode", "rpc", "--no-extensions"] : ["--mode", "rpc"]);

            using var configuration = await ProjectRuntimeConfiguration.LoadAsync(project, agent, arguments,
                new ProjectTrust(agent), interactiveTrust: false, TextReader.Null, TextWriter.Null);

            Assert.Equal(!noExtensions, configuration.Trusted);
            using var bootstrap = configuration.TakeBootstrapExtensions();
            if (noExtensions) Assert.Null(bootstrap);
            else
            {
                Assert.NotNull(bootstrap);
                Assert.Equal(new ResourceSourceInfo(extension, source, "user", "package", mode == "file" ? Path.GetDirectoryName(extension)! : package),
                    Assert.Single(bootstrap.LoadedExtensions));
            }
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}
