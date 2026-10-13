using System.Text.Json;
using PiSharp.Cli;

namespace PiSharp.Tests;

public sealed class RadiusLoginFlowTests
{
    private const string ExistingMcp = """{"mcpServers":{"custom":{"url":"https://radius.pi.dev/mcp/","enabled":false,"headers":{"X-Fixture":"preserved"},"oauth":{}}}}""";

    [Theory]
    [InlineData("yes", "\n")]
    [InlineData("no", "\u001b[B\n")]
    [InlineData("cancel", "\u001b")]
    public async Task DeviceLoginOffersGlobalMcpAndHonorsTheChoice(string choice, string keys)
    {
        if (!OperatingSystem.IsLinux() || !File.Exists("/usr/bin/script")) return;
        await using var server = new RadiusLoginServer();
        await using var terminal = new RadiusLoginTerminal(server.Origin, ExistingMcp);
        await terminal.OpenRadiusFromMenuAsync();
        var mark = terminal.Mark;
        await terminal.SendAsync("\u001b[B\n");
        await terminal.WaitTextAsync("ABCD-1234", mark);
        await terminal.WaitTextAsync("Configure Radius MCP in ", mark);
        var credential = await new AuthStorage(terminal.AuthPath).ReadAsync("radius");
        Assert.Equal("oauth", credential?.Type);
        Assert.Equal("fixture-radius-access", credential?.Access);
        Assert.DoesNotContain("fixture-radius-access", terminal.Output);
        Assert.DoesNotContain("fixture-radius-refresh", terminal.Output);
        await File.WriteAllTextAsync(Path.Combine(terminal.AgentDirectory, "settings.json"),
            "{\"quietStartup\":true,\"hideThinkingBlock\":true}");
        mark = terminal.Mark;
        await terminal.SendAsync(keys);
        await terminal.WaitEditorAsync(mark);
        // A command submitted after the prompt closes also waits for a reload to finish.
        mark = terminal.Mark;
        await terminal.SendAsync("/session\n");
        await terminal.WaitTextAsync("active messages", mark);
        var saved = await File.ReadAllTextAsync(terminal.McpPath);
        if (choice == "yes")
        {
            using var config = JsonDocument.Parse(saved);
            var mcp = config.RootElement.GetProperty("mcpServers").GetProperty("custom");
            Assert.Equal("radius", mcp.GetProperty("auth").GetProperty("provider").GetString());
            Assert.False(mcp.TryGetProperty("oauth", out _));
            Assert.False(mcp.GetProperty("enabled").GetBoolean());
            Assert.Equal("preserved", mcp.GetProperty("headers").GetProperty("X-Fixture").GetString());
        }
        else Assert.Equal(ExistingMcp, saved);
        mark = terminal.Mark;
        await terminal.SendAsync("/settings\n");
        var settings = await terminal.WaitFrameAsync(frame => frame.Contains("Settings scope", StringComparison.Ordinal) ||
            frame.Contains("Hide thinking", StringComparison.Ordinal), mark);
        if (settings.Contains("Settings scope", StringComparison.Ordinal))
        {
            mark = terminal.Mark;
            await terminal.SendAsync("\n");
            settings = await terminal.WaitFrameAsync(frame => frame.Contains("Hide thinking", StringComparison.Ordinal), mark);
        }
        // The settings changed on disk during the offer. Only accepting it reloads the runtime snapshot.
        Assert.Contains("Hide thinking · " + (choice == "yes" ? "enabled" : "inherit/default"), settings);
        mark = terminal.Mark;
        await terminal.SendAsync("\u001b");
        await terminal.WaitTextAsync("Settings closed.", mark);
        mark = terminal.Mark;
        await terminal.SendAsync("/login\n");
        await terminal.WaitTextAsync("Sign in with Radius ✓ configured", mark);
        mark = terminal.Mark;
        await terminal.SendAsync("\u001b");
        await terminal.WaitEditorAsync(mark);
        await terminal.QuitAsync();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancelingTheMethodReturnsToTheMenuOrEditorThatStartedLogin(bool direct)
    {
        if (!OperatingSystem.IsLinux() || !File.Exists("/usr/bin/script")) return;
        await using var server = new RadiusLoginServer();
        await using var terminal = new RadiusLoginTerminal(server.Origin, ExistingMcp);
        if (direct)
        {
            await terminal.WaitEditorAsync();
            var opened = terminal.Mark;
            await terminal.SendAsync("/login radius\n");
            await terminal.WaitTextAsync("Select an OAuth login method:", opened);
        }
        else await terminal.OpenRadiusFromMenuAsync();
        var mark = terminal.Mark;
        await terminal.SendAsync("\u001b");
        if (!direct)
        {
            await terminal.WaitTextAsync("Select authentication method:", mark);
            mark = terminal.Mark;
            await terminal.SendAsync("\u001b");
        }
        await terminal.WaitEditorAsync(mark);
        Assert.False(File.Exists(terminal.AuthPath));
        Assert.Equal(ExistingMcp, await File.ReadAllTextAsync(terminal.McpPath));
        await terminal.QuitAsync();
    }

    [Theory]
    [InlineData(false, "\u001b")]
    [InlineData(true, "\u0011")]
    public async Task InterruptCancelsAnOutstandingTokenRequestAndRestoresTheLoginMenu(bool custom, string interrupt)
    {
        if (!OperatingSystem.IsLinux() || !File.Exists("/usr/bin/script")) return;
        await using var server = new RadiusLoginServer(holdToken: true);
        await using var terminal = new RadiusLoginTerminal(server.Origin, ExistingMcp, custom);
        await terminal.OpenRadiusFromMenuAsync();
        await terminal.SendAsync("\u001b[B\n");
        await server.TokenRequested.Task.WaitAsync(TimeSpan.FromSeconds(20));
        var mark = terminal.Mark;
        await terminal.SendAsync(interrupt);
        await terminal.WaitTextAsync("Select authentication method:", mark);
        Assert.False(File.Exists(terminal.AuthPath));
        Assert.Equal(ExistingMcp, await File.ReadAllTextAsync(terminal.McpPath));
        mark = terminal.Mark;
        await terminal.SendAsync("\u001b");
        await terminal.WaitEditorAsync(mark);
        await terminal.QuitAsync();
    }
}
