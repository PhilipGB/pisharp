using System.Text.Json;
using Microsoft.Extensions.AI;
using PiSharp.Runtime.Codemode;
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
                    ["my-fixture"] = new { command = "python3", args = new[] { fixture }, exposure = "direct" }
                }
            }));
            var configuration = await McpConfiguration.LoadAsync(root, root, false);
            Assert.Empty(configuration.Errors);
            using var catalog = ExtensionCatalog.Load(root, root, false, discover: false);
            var errors = await McpRuntime.RegisterAsync(configuration, catalog, root);
            Assert.Empty(errors);

            var echo = catalog.Registration.ToolDefinitions.Single(tool =>
                tool.Function.Name == "mcp__my_fixture__echo");
            Assert.Equal(ToolExposure.Direct, echo.Exposure);
            Assert.Equal("mcp__my_fixture", echo.Namespace?.Name);
            var renderer = catalog.Registration.GetToolRenderer("mcp__my_fixture__echo");
            Assert.NotNull(renderer);
            var arguments = new Dictionary<string, object?> { ["value"] = "hello" };
            var renderContext = new PiSharpToolRenderContext("mcp__my_fixture__echo", "call", root,
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
                tool.Function.Name == "mcp__my_fixture__fail");
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
                    ["server"] = "my-fixture",
                    ["uri"] = "fixture://note"
                })), out var readResult));
            Assert.Equal("fixture resource", readResult.Text);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task CodemodeDescribesMcpNamespacesAndOnlyInvokesCallableTools()
    {
        var root = Path.Combine(Path.GetTempPath(), "pisharp-mcp-codemode-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var fixture = Path.Combine(AppContext.BaseDirectory, "Fixtures", "mcp_fixture.py");
            var servers = new Dictionary<string, object>
            {
                ["docs"] = new
                {
                    command = "python3",
                    args = new[] { fixture },
                    description = "Search the product manuals.",
                    env = new Dictionary<string, string>
                    {
                        ["MCP_FIXTURE_INSTRUCTIONS"] = "Use the docs server to find product behavior."
                    },
                    toolExposure = new Dictionary<string, string> { ["fail"] = "hidden" }
                },
                ["tickets"] = new
                {
                    command = "python3",
                    args = new[] { fixture },
                    description = "Find project tickets.",
                    env = new Dictionary<string, string>
                    {
                        ["MCP_FIXTURE_INSTRUCTIONS"] = "Use the tickets server to inspect project work."
                    }
                }
            };
            await File.WriteAllTextAsync(Path.Combine(root, "mcp.json"),
                System.Text.Json.JsonSerializer.Serialize(new { mcpServers = servers }));
            var configuration = await McpConfiguration.LoadAsync(root, root, false);
            Assert.Empty(configuration.Errors);
            using var catalog = ExtensionCatalog.Load(root, root, false, discover: false);
            CodemodeBuiltin.Configure(catalog.Registration);
            Assert.Empty(await McpRuntime.RegisterAsync(configuration, catalog, root));

            var serverContext = McpRuntime.RenderServerContext(configuration.Servers);
            Assert.Contains("- mcp__docs (codemode): Search the product manuals.", serverContext);
            Assert.Contains("- mcp__tickets (codemode): Find project tickets.", serverContext);
            Assert.DoesNotContain("Use the docs server to find product behavior.", serverContext);

            var script = "const docs=await describeNamespace('mcp__docs'); " +
                "const tickets=await describeNamespace('mcp__tickets'); " +
                "const searched=await searchTools('Echo input text.',{namespace:'mcp__docs'}); " +
                "const docResult=await tools.mcp__docs__echo({value:'manual'}); " +
                "const ticketResult=await tools.mcp__tickets__echo({value:'issue'}); " +
                "let hiddenState='blocked'; try { await tools.mcp__docs__fail({}); hiddenState='called'; } catch {} " +
                "text(JSON.stringify({docs,tickets,docValue:docResult.echo,ticketValue:ticketResult.echo," +
                "searched:searched.map(tool=>tool.name),hiddenState}));";
            var waitHook = Assert.Single(catalog.Registration.ToolCallHooks);
            await waitHook(new PiSharpToolCallContext("codemode", "codemode-call", null,
                new Dictionary<string, object?> { ["code"] = script }), CancellationToken.None);

            var registry = new PiSharpToolRegistry(catalog.Registration.ToolDefinitions);
            var loadout = registry.CreateLoadout(["codemode"]);
            var description = Assert.Single(loadout.Snapshot.Declared).Description;
            Assert.DoesNotContain("mcp__docs", description);
            Assert.DoesNotContain("Search the product manuals.", description);
            Assert.DoesNotContain("mcp__tickets", description);
            Assert.DoesNotContain("Find project tickets.", description);
            Assert.DoesNotContain("Echo input text.", description);
            Assert.DoesNotContain("mcp__docs__echo", description);
            Assert.DoesNotContain("Use the docs server to find product behavior.", description);

            var functions = catalog.Registration.ToolDefinitions.ToDictionary(
                tool => tool.Function.Name, tool => tool.Function, StringComparer.Ordinal);
            var context = PiSharpToolExecutionContext.CreateRoot(loadout, () => functions, _ => { },
                "test", null, "codemode", new Dictionary<string, object?>());
            var result = await CodemodeSandbox.ExecuteAsync(script, context,
                new Dictionary<string, JsonElement>(), CancellationToken.None);
            Assert.True(result.Ok, result.Error);
            using var output = JsonDocument.Parse(result.Text);
            var rootElement = output.RootElement;
            var docs = rootElement.GetProperty("docs");
            Assert.Equal("mcp__docs", docs.GetProperty("name").GetString());
            Assert.Equal("Search the product manuals.", docs.GetProperty("description").GetString());
            Assert.Equal("Use the docs server to find product behavior.",
                docs.GetProperty("instructions").GetString());
            Assert.Equal(["mcp__docs__echo"], docs.GetProperty("tools").EnumerateArray()
                .Select(item => item.GetString()!).ToArray());
            var tickets = rootElement.GetProperty("tickets");
            Assert.Equal("Find project tickets.", tickets.GetProperty("description").GetString());
            Assert.Equal("Use the tickets server to inspect project work.",
                tickets.GetProperty("instructions").GetString());
            Assert.Equal("manual", rootElement.GetProperty("docValue").GetString());
            Assert.Equal("issue", rootElement.GetProperty("ticketValue").GetString());
            Assert.Equal(["mcp__docs__echo"], rootElement.GetProperty("searched").EnumerateArray()
                .Select(item => item.GetString()!).ToArray());
            Assert.Equal("blocked", rootElement.GetProperty("hiddenState").GetString());
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}
