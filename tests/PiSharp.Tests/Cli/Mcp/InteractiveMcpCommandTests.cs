using PiSharp.Cli.Tui;
using PiSharp.Runtime.Extensions;
using PiSharp.Runtime.Mcp;
using PiSharp.Runtime.Resources;
using System.Text.Json;

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
                () => Task.CompletedTask, () => []);

            Assert.Contains("/mcp status | details <server> | reconnect <server>", output.ToString());
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
                () => { reloaded = true; return Task.CompletedTask; }, () => []);

            Assert.True(reloaded);
            Assert.Contains("Reloaded MCP servers", output.ToString());
        }
        finally
        {
            Console.SetOut(previous);
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task DetailsShowsStdioMetadataWithoutExecutableArgumentsOrEnvironmentValues()
    {
        var manager = new McpRuntimeManager();
        manager.Register(Configuration("stdio", "/usr/bin/private-mcp", ["secret-argument"],
            new Dictionary<string, string> { ["TOKEN"] = "environment-secret" }), null);

        var details = await manager.HandleCommandAsync("details stdio", CancellationToken.None);

        Assert.Contains("Transport: stdio (private-mcp)", details);
        Assert.Contains("Auth: environment may provide credentials (values hidden)", details);
        Assert.DoesNotContain("secret-argument", details);
        Assert.DoesNotContain("environment-secret", details);
        Assert.DoesNotContain("TOKEN", details);
    }

    [Fact]
    public async Task DetailsSanitizesOAuthEndpointAndNeverReportsOAuthSecretsOrTokenState()
    {
        using var oauth = JsonDocument.Parse("""{"clientId":"id-secret","clientSecret":"oauth-secret"}""");
        var manager = new McpRuntimeManager();
        manager.Register(Configuration("oauth", null, [], new Dictionary<string, string>(),
            new Uri("https://url-user:url-password@example.test/mcp?access_token=url-query-secret#url-fragment-secret"),
            new Dictionary<string, string> { ["X-Api-Key"] = "header-secret" }, oauth.RootElement.Clone()), null);

        var details = await manager.HandleCommandAsync("details oauth", CancellationToken.None);

        Assert.Contains("Transport: http (https://example.test/mcp)", details);
        Assert.Contains("Auth: OAuth", details);
        Assert.DoesNotContain("url-user", details);
        Assert.DoesNotContain("url-password", details);
        Assert.DoesNotContain("url-query-secret", details);
        Assert.DoesNotContain("url-fragment-secret", details);
        Assert.DoesNotContain("id-secret", details);
        Assert.DoesNotContain("oauth-secret", details);
        Assert.DoesNotContain("header-secret", details);
        Assert.DoesNotContain("token", details, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task DetailsReportsProviderAuthWithoutProviderCredentials()
    {
        var manager = new McpRuntimeManager();
        manager.Register(Configuration("provider", null, [], new Dictionary<string, string>(),
            new Uri("https://provider.example/mcp?key=query-secret"), provider: "secret-provider-id"), null);

        var details = await manager.HandleCommandAsync("details provider", CancellationToken.None);

        Assert.Contains("Auth: provider sign-in configured", details);
        Assert.DoesNotContain("secret-provider-id", details);
        Assert.DoesNotContain("query-secret", details);
    }

    private static McpServerConfiguration Configuration(string name, string? command,
        IReadOnlyList<string> arguments, IReadOnlyDictionary<string, string> environment, Uri? url = null,
        IReadOnlyDictionary<string, string>? headers = null, JsonElement? oauth = null, string? provider = null) =>
        new(name, "config.json", "global", true, McpToolExposure.Direct,
            new Dictionary<string, McpToolExposure>(), TimeSpan.FromSeconds(5), command, arguments, null,
            environment, url, headers ?? new Dictionary<string, string>(), oauth)
        { AuthProvider = provider };
}
