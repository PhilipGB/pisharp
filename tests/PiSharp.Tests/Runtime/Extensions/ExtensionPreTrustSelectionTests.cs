using System.Text.Json;
using Microsoft.Extensions.AI;
using PiSharp.ResourceDiscoveryExtension;
using PiSharp.Runtime.Extensions;
using PiSharp.Runtime.Resources;
using PiSharp.Runtime.Tools;
using PiSharp.Runtime.VirtualModels;

namespace PiSharp.Tests;

[Collection("Extension environment")]
public sealed class ExtensionPreTrustSelectionTests
{
    [Theory]
    [InlineData(true, true)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task FinalPackageSelectionRemovesDisabledRegistrationsAndRetainsEnabledInstances(bool trusted, bool disabled)
    {
        var root = Path.Combine(Path.GetTempPath(), "pisharp-pretrust-selection-" + Guid.NewGuid().ToString("N"));
        var agent = Path.Combine(root, "agent");
        var cwd = Path.Combine(root, "project");
        var package = Path.Combine(root, "package");
        var file = Path.Combine(package, "extensions", "index.dll");
        Directory.CreateDirectory(agent);
        Directory.CreateDirectory(Path.Combine(cwd, ".pi"));
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        var previous = Environment.GetEnvironmentVariable("PISHARP_TEST_RESOURCE_DISCOVERY");
        try
        {
            Environment.SetEnvironmentVariable("PISHARP_TEST_RESOURCE_DISCOVERY", "true");
            File.Copy(typeof(ResourceDiscoveryFixture).Assembly.Location, file);
            File.WriteAllText(Path.Combine(package, "package.json"), "{\"pi\":{\"extensions\":[\"extensions\"]}}");
            File.WriteAllText(Path.Combine(cwd, ".pi", "project-trust-extension.json"), "{\"decision\":\"yes\"}");
            using var bootstrap = ExtensionCatalog.Load(agent, cwd, false, userPackages: [new("../package")]);
            var registration = bootstrap.Registration;
            registration.SetCurrentSourceInfo(Assert.Single(bootstrap.LoadedExtensions));
            registration.AddCommand("selected", (_, _) => Task.FromResult("selected"));
            registration.AddTool(AIFunctionFactory.Create(() => "selected", name: "selected"),
                new PiSharpToolRenderer(renderCall: (_, _) => PiSharpToolRenderView.FromText("selected")));
            registration.AddUserBashHandler((_, _) => Task.FromResult<BashExecutionResult?>(null));
            registration.AddToolCallHook((_, _) => ValueTask.FromResult(PiSharpToolCallDecision.Allow));
            registration.AddToolResultHook((_, _) => ValueTask.CompletedTask);
            registration.AddContextTransform((messages, _) => ValueTask.FromResult(messages));
            using var mcp = JsonDocument.Parse("{\"command\":\"false\"}");
            registration.RegisterMcpServer("selected", mcp.RootElement);
            registration.RegisterVirtualModel(new("selected", "model", "Selected model",
                (_, _) => Task.FromResult(new VirtualModelRoute("fixture", "model", "off"))));
            registration.SetCurrentSourceInfo(null);
            registration.AddCommand("host", (_, _) => Task.FromResult("host"));

            using var final = ExtensionCatalog.Load(agent, cwd, trusted, preloaded: bootstrap,
                userPackages: [new("../package")], projectPackages: [new("../../package", Extensions: disabled ? [] : null, HasFilter: disabled)]);

            Assert.Same(bootstrap, final);
            Assert.Empty(final.LoadErrors);
            Assert.True(registration.Commands.ContainsKey("host"));
            var retained = !trusted || !disabled;
            Assert.Equal(retained, registration.Commands.ContainsKey("selected"));
            Assert.Equal(retained ? 1 : 0, registration.Tools.Count);
            Assert.Equal(retained ? 1 : 0, registration.ToolDefinitions.Count);
            Assert.Equal(retained ? 1 : 0, registration.ToolRenderers.Count);
            Assert.Equal(retained ? 1 : 0, registration.ToolSourceInfo.Count);
            Assert.Equal(retained ? 1 : 0, registration.UserBashHandlers.Count);
            Assert.Equal(retained ? 1 : 0, registration.ToolCallHooks.Count);
            Assert.Equal(retained ? 1 : 0, registration.ToolResultHooks.Count);
            Assert.Equal(retained ? 1 : 0, registration.ContextTransforms.Count);
            Assert.Equal(retained ? 1 : 0, registration.McpServers.Count);
            Assert.Equal(retained ? 1 : 0, registration.VirtualModels.Models.Count);
            var discovered = await final.DiscoverResourcesAsync(cwd, ExtensionResourceDiscoveryReason.Startup);
            Assert.Equal(retained ? 1 : 0, discovered.SkillPaths.Count);
            var trust = await registration.ResolveProjectTrustAsync(new(cwd, "rpc", false,
                UnavailableProjectTrustExtensionUi.Instance), CancellationToken.None);
            Assert.Empty(trust.Errors);
            Assert.Equal(retained, trust.Result is not null);
            if (retained)
            {
                var source = Assert.Single(final.LoadedExtensions);
                Assert.Equal(trusted ? "project" : "user", source.Scope);
                Assert.Equal(source, registration.CommandInfo["selected"].SourceInfo);
                Assert.Equal(source, registration.ToolSourceInfo["selected"]);
            }
            else Assert.Empty(final.LoadedExtensions);
        }
        finally
        {
            Environment.SetEnvironmentVariable("PISHARP_TEST_RESOURCE_DISCOVERY", previous);
            Directory.Delete(root, recursive: true);
        }
    }
}
