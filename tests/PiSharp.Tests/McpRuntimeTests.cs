using Microsoft.Extensions.AI;
using PiSharp.Runtime.Extensions;
using PiSharp.Runtime.Mcp;
using PiSharp.Runtime.Tools;
using PiSharp.Cli.Tui;

namespace PiSharp.Tests;

public sealed class McpRuntimeTests
{
    [Fact]
    public async Task ResourceOnlyServerDoesNotNeedToolCapability()
    {
        var root = Path.Combine(Path.GetTempPath(), "pisharp-mcp-resources-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var fixture = Path.Combine(AppContext.BaseDirectory, "Fixtures", "mcp_fixture.py");
            await File.WriteAllTextAsync(Path.Combine(root, "mcp.json"),
                System.Text.Json.JsonSerializer.Serialize(new
                {
                    mcpServers = new Dictionary<string, object>
                    {
                        ["resources"] = new
                        {
                            command = "python3",
                            args = new[] { fixture },
                            exposure = "direct",
                            env = new { MCP_FIXTURE_RESOURCES_ONLY = "1" }
                        }
                    }
                }));
            var configuration = await McpConfiguration.LoadAsync(root, root, false);
            using var catalog = ExtensionCatalog.Load(root, root, false, discover: false);
            Assert.Empty(await McpRuntime.RegisterAsync(configuration, catalog, root));
            Assert.DoesNotContain(catalog.Registration.ToolDefinitions, tool =>
                tool.Function.Name.StartsWith("mcp__resources__", StringComparison.Ordinal));
            Assert.Contains(catalog.Registration.ToolDefinitions, tool =>
                tool.Function.Name == "list_mcp_resources");
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task StdioToolsKeepNamesSchemasStructuredResultsAndErrors()
    {
        var root = Path.Combine(Path.GetTempPath(), "pisharp-mcp-runtime-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var fixture = Path.Combine(AppContext.BaseDirectory, "Fixtures", "mcp_fixture.py");
            var configurationPath = Path.Combine(root, "mcp.json");
            await File.WriteAllTextAsync(configurationPath, System.Text.Json.JsonSerializer.Serialize(new
            {
                mcpServers = new Dictionary<string, object>
                {
                    ["fixture"] = new { command = "python3", args = new[] { fixture }, exposure = "direct" }
                }
            }));
            var configuration = await McpConfiguration.LoadAsync(root, root, false);
            Assert.Empty(configuration.Errors);
            using var catalog = ExtensionCatalog.Load(root, root, false, discover: false);
            var errors = await McpRuntime.RegisterAsync(configuration, catalog, root);
            Assert.Empty(errors);

            var echo = catalog.Registration.ToolDefinitions.Single(tool =>
                tool.Function.Name == "mcp__fixture__echo");
            Assert.Equal(ToolExposure.Direct, echo.Exposure);
            Assert.Equal("fixture", echo.Namespace?.Name["mcp__".Length..]);
            var renderer = catalog.Registration.GetToolRenderer("mcp__fixture__echo");
            Assert.NotNull(renderer);
            var arguments = new Dictionary<string, object?> { ["value"] = "hello" };
            var renderContext = new PiSharpToolRenderContext("mcp__fixture__echo", "call", root,
                arguments, true, true, false, false, false);
            var callView = renderer.RenderCall!(arguments, renderContext);
            Assert.Contains("fixture/echo", TerminalToolPresentation.Render(callView!, null));
            Assert.Contains("value=\"hello\"", TerminalToolPresentation.Render(callView!, null));
            var sixLines = string.Join('\n', Enumerable.Range(1, 6).Select(index => "line " + index));
            var resultView = renderer.RenderResult!(new PiSharpToolRenderResult(sixLines, null, null, false),
                renderContext);
            Assert.Contains("line 5", TerminalToolPresentation.Render(resultView!, null));
            Assert.DoesNotContain("line 6", TerminalToolPresentation.Render(resultView!, null));
            Assert.Contains("1 more line", TerminalToolPresentation.Render(resultView!, null));
            var expandedView = renderer.RenderResult!(new PiSharpToolRenderResult(sixLines, null, null, false),
                renderContext with { IsExpanded = true });
            Assert.Contains("line 6", TerminalToolPresentation.Render(expandedView!, null));
            Assert.True(echo.Function.JsonSchema.TryGetProperty("properties", out var properties));
            Assert.True(properties.TryGetProperty("value", out _));
            Assert.Equal("echo", echo.OutputSchema?.GetProperty("required")[0].GetString());
            var value = await echo.Function.InvokeAsync(new AIFunctionArguments(new Dictionary<string, object?>
            {
                ["value"] = "hello"
            }));
            var result = Assert.IsType<PiSharpToolResult>(value);
            Assert.Equal("hello", result.Text);
            Assert.Equal("hello", result.StructuredContent?.GetProperty("echo").GetString());
            Assert.False(result.IsError);
            new ToolResultSchemaValidator(echo.OutputSchema).Validate(result);

            var fail = catalog.Registration.ToolDefinitions.Single(tool =>
                tool.Function.Name == "mcp__fixture__fail");
            var failed = Assert.IsType<PiSharpToolResult>(await fail.Function.InvokeAsync(new AIFunctionArguments()));
            Assert.True(failed.IsError);
            Assert.Equal("fixture failure", failed.Text);

            var list = catalog.Registration.ToolDefinitions.Single(tool =>
                tool.Function.Name == "list_mcp_resources");
            Assert.True(ToolResultOutput.TryReadContract(await list.Function.InvokeAsync(new AIFunctionArguments()),
                out var listed));
            Assert.Single(listed.StructuredContent?.GetProperty("resources").EnumerateArray()!);
            Assert.Equal("fixture://note", listed.StructuredContent?.GetProperty("resources")[0]
                .GetProperty("uri").GetString());
            Assert.Contains("fixture://note", listed.Text);
            Assert.DoesNotContain("ui://fixture", listed.Text);

            var templates = catalog.Registration.ToolDefinitions.Single(tool =>
                tool.Function.Name == "list_mcp_resource_templates");
            Assert.True(ToolResultOutput.TryReadContract(await templates.Function.InvokeAsync(
                new AIFunctionArguments()), out var templateList));
            Assert.Contains("fixture://notes/{id}", templateList.Text);

            var read = catalog.Registration.ToolDefinitions.Single(tool =>
                tool.Function.Name == "read_mcp_resource");
            Assert.True(ToolResultOutput.TryReadContract(await read.Function.InvokeAsync(
                new AIFunctionArguments(new Dictionary<string, object?>
                {
                    ["server"] = "fixture",
                    ["uri"] = "fixture://note"
                })), out var readResult));
            Assert.Equal("fixture resource", readResult.Text);
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}
