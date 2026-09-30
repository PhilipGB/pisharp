using System.Text.Json;
using PiSharp.Runtime.Extensions;
using PiSharp.Runtime.Mcp;

namespace PiSharp.Tests;

public sealed class McpIdentifierTests
{
    [Fact]
    public void ToolIdentifiersAreJavaScriptSafeAndDisambiguateEveryCollisionDeterministically()
    {
        Assert.Equal("mcp__my_server__get_item_v2",
            McpToolIdentifiers.CreateNames("my-server", ["get.item/v2"], new HashSet<string>())[0]);

        var forward = McpToolIdentifiers.CreateNames("files", ["read-file", "read_file"],
            new HashSet<string>());
        var reverse = McpToolIdentifiers.CreateNames("files", ["read_file", "read-file"],
            new HashSet<string>());

        Assert.Equal(forward[0], reverse[1]);
        Assert.Equal(forward[1], reverse[0]);
        Assert.All(forward, name =>
        {
            Assert.StartsWith("mcp__files__read_file_", name, StringComparison.Ordinal);
            Assert.Matches("_[0-9a-f]{8}$", name);
            Assert.DoesNotContain('-', name);
        });
        Assert.Equal(2, forward.Distinct(StringComparer.Ordinal).Count());

        var longName = McpToolIdentifiers.CreateNames("files", [new string('x', 80)],
            new HashSet<string>());
        Assert.Equal(64, Assert.Single(longName).Length);
    }

    [Fact]
    public void ExtensionServersCannotRegisterDifferentNamesForOneNormalizedNamespace()
    {
        var root = Path.Combine(Path.GetTempPath(), "pisharp-mcp-extension-name-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            using var catalog = ExtensionCatalog.Load(root, root, false, discover: false);
            catalog.Registration.SetCurrentSourceInfo(new("first-extension", "cli", "temporary", "top-level", null));
            catalog.Registration.RegisterMcpServer("my-docs", JsonSerializer.SerializeToElement(new
            {
                command = "fixture"
            }));

            catalog.Registration.SetCurrentSourceInfo(new("second-extension", "cli", "temporary", "top-level", null));
            var error = Assert.Throws<InvalidOperationException>(() =>
                catalog.Registration.RegisterMcpServer("my_docs", JsonSerializer.SerializeToElement(new
                {
                    command = "fixture"
                })));

            Assert.Contains("my_docs", error.Message, StringComparison.Ordinal);
            Assert.Equal("my-docs", Assert.Single(catalog.Registration.McpServers).Configuration.Name);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void CodemodeServerContextUsesTheNormalizedNamespace()
    {
        using var json = JsonDocument.Parse("{\"command\":\"fixture\"}");
        var server = McpConfiguration.Parse("my-docs", json.RootElement, "mcp.json", "global");

        var context = McpRuntime.RenderServerContext([server]);

        Assert.Contains("mcp__my_docs", context, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ConfiguredServerOverridesExtensionWithTheSameNormalizedNamespace()
    {
        var root = Path.Combine(Path.GetTempPath(), "pisharp-mcp-configured-name-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            await File.WriteAllTextAsync(Path.Combine(root, "mcp.json"), """
                {"mcpServers":{"my_docs":{"command":"fixture","enabled":false}}}
                """);
            using var catalog = ExtensionCatalog.Load(root, root, false, discover: false);
            catalog.Registration.SetCurrentSourceInfo(new("fixture-extension", "cli", "temporary", "top-level", null));
            catalog.Registration.RegisterMcpServer("my-docs", JsonSerializer.SerializeToElement(new
            {
                command = "fixture",
                enabled = false
            }));

            var configuration = await McpConfiguration.LoadAsync(root, root, false);
            var errors = await McpRuntime.RegisterAsync(configuration, catalog, root);

            Assert.Contains(errors, error => error.Contains("my-docs", StringComparison.Ordinal) &&
                error.Contains("overridden", StringComparison.Ordinal));
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}
