using PiSharp.Runtime.Extensions;
using PiSharp.Runtime.Mcp;

namespace PiSharp.Tests;

public sealed class McpRuntimeLifecycleTests
{
    [Fact]
    public async Task DroppedStdioConnectionReconnectsForTheNextToolCall()
    {
        var root = Path.Combine(Path.GetTempPath(), "pisharp-mcp-reconnect-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var fixture = Path.Combine(AppContext.BaseDirectory, "Fixtures", "mcp_reconnect_fixture.py");
            var counter = Path.Combine(root, "starts");
            await File.WriteAllTextAsync(Path.Combine(root, "mcp.json"), System.Text.Json.JsonSerializer.Serialize(new
            {
                mcpServers = new Dictionary<string, object>
                {
                    ["reconnect"] = new
                    {
                        command = "python3",
                        args = new[] { fixture },
                        exposure = "direct",
                        env = new Dictionary<string, string> { ["MCP_FIXTURE_RECONNECT_COUNT"] = counter }
                    }
                }
            }));

            var configuration = await McpConfiguration.LoadAsync(root, root, false);
            using var catalog = ExtensionCatalog.Load(root, root, false, discover: false);
            Assert.Empty(await McpRuntime.RegisterAsync(configuration, catalog, root));
            var echo = catalog.Registration.ToolDefinitions.Single(tool =>
                tool.Function.Name == "mcp__reconnect__echo");
            var firstArguments = new Microsoft.Extensions.AI.AIFunctionArguments(new Dictionary<string, object?>
            {
                ["value"] = "first"
            });
            await Assert.ThrowsAnyAsync<Exception>(async () =>
            {
                await echo.Function.InvokeAsync(firstArguments);
            });

            var secondResult = Assert.IsType<PiSharpToolResult>(await echo.Function.InvokeAsync(
                new Microsoft.Extensions.AI.AIFunctionArguments(new Dictionary<string, object?>
                {
                    ["value"] = "second"
                })));

            Assert.Equal("reconnected:second", secondResult.Text);
            Assert.Equal("2", await File.ReadAllTextAsync(counter));
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}
