using PiSharp.Cli.Tui;
using PiSharp.Runtime.Extensions;
using PiSharp.Runtime.Resources;

namespace PiSharp.Tests;

public sealed class InteractiveMcpCommandTests
{
    [Fact]
    public async Task HelpDescribesInteractiveManagementAndShellConfigurationCommands()
    {
        var root = Path.Combine(Path.GetTempPath(), "pisharp-mcp-interactive-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var previous = Console.Out;
        using var output = new StringWriter();
        try
        {
            Console.SetOut(output);
            var resources = await ResourceCatalog.LoadAsync(root, root, false, discoverSkills: false,
                discoverPrompts: false);
            var registration = new ExtensionRegistration();
            registration.AddCommand("mcp", (_, _) => Task.FromResult("status"), "Manage MCP servers.");

            await InteractiveResourceCommands.ExecuteAsync("/mcp", "help", "/mcp help", () => resources,
                () => registration, _ => Task.CompletedTask, null, null, _ => Task.CompletedTask,
                () => Task.CompletedTask);

            Assert.Contains("/mcp status | reconnect <server> | enable <server>", output.ToString());
            Assert.Contains("/mcp add ... or /mcp remove ... in the shell", output.ToString());
        }
        finally
        {
            Console.SetOut(previous);
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ReloadActionReloadsProjectRuntimeAndReportsCompletion()
    {
        var root = Path.Combine(Path.GetTempPath(), "pisharp-mcp-interactive-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var previous = Console.Out;
        using var output = new StringWriter();
        var reloaded = false;
        try
        {
            Console.SetOut(output);
            var resources = await ResourceCatalog.LoadAsync(root, root, false, discoverSkills: false,
                discoverPrompts: false);
            var registration = new ExtensionRegistration();
            registration.AddCommand("mcp", (_, _) => Task.FromResult("unexpected"), "Manage MCP servers.");

            await InteractiveResourceCommands.ExecuteAsync("/mcp", "reload", "/mcp reload", () => resources,
                () => registration, _ => Task.CompletedTask, null, null, _ => Task.CompletedTask,
                () => { reloaded = true; return Task.CompletedTask; });

            Assert.True(reloaded);
            Assert.Contains("Reloaded MCP servers", output.ToString());
        }
        finally
        {
            Console.SetOut(previous);
            Directory.Delete(root, recursive: true);
        }
    }
}
