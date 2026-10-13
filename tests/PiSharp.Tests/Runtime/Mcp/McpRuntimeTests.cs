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
    [Theory]
    [InlineData("[1,2]", false)]
    [InlineData("null", false)]
    [InlineData("\"text\"", false)]
    [InlineData("3", false)]
    [InlineData("false", false)]
    [InlineData("[1,2]", true)]
    [InlineData("null", true)]
    public async Task StructuredOnlyResultsPreserveJsonValuesForScriptsAndTheModel(string json, bool omitContent)
    {
        var root = Path.Combine(Path.GetTempPath(), "pisharp-mcp-json-value-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var fixture = Path.Combine(AppContext.BaseDirectory, "TestInfrastructure", "Fixtures", "mcp_fixture.py");
            await File.WriteAllTextAsync(Path.Combine(root, "mcp.json"), JsonSerializer.Serialize(new
            {
                mcpServers = new Dictionary<string, object>
                {
                    ["json"] = new
                    {
                        command = "python3",
                        args = new[] { fixture },
                        exposure = "direct",
                        toolExposure = new { echo = "codemode" },
                        env = new Dictionary<string, string>
                        {
                            ["MCP_FIXTURE_STRUCTURED_JSON"] = json,
                            ["MCP_FIXTURE_OMIT_CONTENT"] = omitContent ? "1" : "0"
                        }
                    }
                }
            }));
            using var catalog = ExtensionCatalog.Load(root, root, false, discover: false);
            var configuration = await McpConfiguration.LoadAsync(root, root, false);
            Assert.Empty(await McpRuntime.RegisterAsync(configuration, catalog, root));
            var tool = catalog.Registration.ToolDefinitions.Single(tool => tool.Function.Name == "mcp__json__echo");

            var result = Assert.IsType<PiSharpToolResult>(await tool.Function.InvokeAsync(
                new AIFunctionArguments(new Dictionary<string, object?> { ["value"] = "unused" })));

            using var expected = JsonDocument.Parse(json);
            Assert.True(JsonElement.DeepEquals(expected.RootElement,
                result.StructuredContent!.Value.GetProperty("structuredContent")));
            Assert.Empty(result.StructuredContent.Value.GetProperty("content").EnumerateArray());
            Assert.Equal(JsonSerializer.Serialize(expected.RootElement,
                new JsonSerializerOptions { WriteIndented = true }), result.Text);
            Assert.False(result.IsError);
            new ToolResultSchemaValidator(tool.OutputSchema).Validate(result);
            Assert.Empty(tool.OutputSchema!.Value.GetProperty("properties")
                .GetProperty("structuredContent").EnumerateObject());
            if (json is "null" or "[1,2]" && !omitContent)
            {
                var registry = new PiSharpToolRegistry(catalog.Registration.ToolDefinitions);
                var functions = catalog.Registration.ToolDefinitions.ToDictionary(
                    item => item.Function.Name, item => item.Function);
                var context = PiSharpToolExecutionContext.CreateRoot(registry.CreateLoadout([tool.Function.Name]),
                    () => functions, _ => { }, "test", null, "codemode", new Dictionary<string, object?>());
                var nested = await CodemodeSandbox.ExecuteAsync(
                    "const r=await tools.mcp__json__echo({value:'unused'});text(JSON.stringify(r));", context,
                    new Dictionary<string, JsonElement>(), CancellationToken.None);
                Assert.True(nested.Ok, nested.Error);
                using var scriptResult = JsonDocument.Parse(nested.Text);
                Assert.True(JsonElement.DeepEquals(result.StructuredContent.Value, scriptResult.RootElement));
            }
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task ConnectionPublishesToolsWithoutListingResourcesUntilRequested()
    {
        var root = Path.Combine(Path.GetTempPath(), "pisharp-mcp-resource-demand-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var requests = Path.Combine(root, "requests.txt");
            var fixture = Path.Combine(AppContext.BaseDirectory, "TestInfrastructure", "Fixtures", "mcp_fixture.py");
            await File.WriteAllTextAsync(Path.Combine(root, "mcp.json"), JsonSerializer.Serialize(new
            {
                mcpServers = new Dictionary<string, object>
                {
                    ["demand"] = new
                    {
                        command = "python3",
                        args = new[] { fixture },
                        exposure = "direct",
                        env = new { MCP_FIXTURE_REQUEST_LOG = requests }
                    }
                }
            }));
            using var catalog = ExtensionCatalog.Load(root, root, false, discover: false);
            var configuration = await McpConfiguration.LoadAsync(root, root, false);

            Assert.Empty(await McpRuntime.RegisterAsync(configuration, catalog, root));

            Assert.Contains(catalog.Registration.ToolDefinitions, tool => tool.Function.Name == "mcp__demand__echo");
            var atConnection = await File.ReadAllLinesAsync(requests);
            Assert.DoesNotContain("resources/list", atConnection);
            Assert.DoesNotContain("resources/templates/list", atConnection);
            var list = catalog.Registration.ToolDefinitions.Single(tool => tool.Function.Name == "list_mcp_resources");
            Assert.True(ToolResultOutput.TryReadContract(await list.Function.InvokeAsync(new AIFunctionArguments()),
                out var result));
            Assert.Contains("fixture://note", result.Text);
            var afterListing = await File.ReadAllLinesAsync(requests);
            Assert.Single(afterListing, method => method == "resources/list");
            Assert.DoesNotContain("resources/templates/list", afterListing);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task FailedConnectionReportsItsTypeWithoutLeakingChildErrorDetails()
    {
        var root = Path.Combine(Path.GetTempPath(), "pisharp-mcp-error-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            const string sensitive = "fixture-sensitive-credential";
            await File.WriteAllTextAsync(Path.Combine(root, "mcp.json"), JsonSerializer.Serialize(new
            {
                mcpServers = new Dictionary<string, object>
                {
                    ["broken"] = new
                    {
                        command = "python3",
                        args = new[] { "-c", $"raise RuntimeError('{sensitive}')" },
                        exposure = "direct"
                    }
                }
            }));
            var configuration = await McpConfiguration.LoadAsync(root, root, false);
            using var catalog = ExtensionCatalog.Load(root, root, false, discover: false);

            var error = Assert.Single(await McpRuntime.RegisterAsync(configuration, catalog, root));

            Assert.StartsWith("MCP server broken could not connect (", error);
            Assert.EndsWith(").", error);
            Assert.DoesNotContain(sensitive, error);
            Assert.DoesNotContain("RuntimeError", error);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task ResourceOnlyServerDoesNotNeedToolCapability()
    {
        var root = Path.Combine(Path.GetTempPath(), "pisharp-mcp-resources-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var fixture = Path.Combine(AppContext.BaseDirectory, "TestInfrastructure", "Fixtures", "mcp_fixture.py");
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
            var fixture = Path.Combine(AppContext.BaseDirectory, "TestInfrastructure", "Fixtures", "mcp_fixture.py");
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
            Assert.Equal(["my-fixture"], echo.Namespace?.Aliases);
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
            Assert.Equal("content", echo.OutputSchema?.GetProperty("required")[0].GetString());
            Assert.Equal("array", echo.OutputSchema?.GetProperty("properties").GetProperty("content")
                .GetProperty("type").GetString());
            Assert.Equal("echo", echo.OutputSchema?.GetProperty("properties").GetProperty("structuredContent")
                .GetProperty("required")[0].GetString());
            var value = await echo.Function.InvokeAsync(new AIFunctionArguments(new Dictionary<string, object?>
            {
                ["value"] = "hello"
            }));
            var result = Assert.IsType<PiSharpToolResult>(value);
            Assert.Equal("hello", result.Text);
            Assert.Equal("hello", result.StructuredContent?.GetProperty("structuredContent")
                .GetProperty("echo").GetString());
            Assert.Equal("hello", result.StructuredContent?.GetProperty("content")[0]
                .GetProperty("text").GetString());
            Assert.False(result.StructuredContent?.GetProperty("isError").GetBoolean());
            Assert.False(result.StructuredContent!.Value.TryGetProperty("_meta", out _));
            Assert.False(result.IsError);
            new ToolResultSchemaValidator(echo.OutputSchema).Validate(result);

            var fail = catalog.Registration.ToolDefinitions.Single(tool =>
                tool.Function.Name == "mcp__my_fixture__fail");
            var failed = Assert.IsType<PiSharpToolResult>(await fail.Function.InvokeAsync(new AIFunctionArguments()));
            Assert.True(failed.IsError);
            Assert.Equal("fixture failure", failed.Text);
            Assert.True(failed.StructuredContent?.GetProperty("isError").GetBoolean());
            Assert.Equal("fixture failure", failed.StructuredContent?.GetProperty("content")[0]
                .GetProperty("text").GetString());

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
            var fixture = Path.Combine(AppContext.BaseDirectory, "TestInfrastructure", "Fixtures", "mcp_fixture.py");
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
                "const aliases=await Promise.all(['docs','mcp__docs'].map(name=>describeNamespace(name))); " +
                "const tickets=await describeNamespace('mcp__tickets'); " +
                "const searched=await searchTools('Echo input text.',{namespace:'docs'}); " +
                "const docResult=await tools.mcp__docs__echo({value:'manual'}); " +
                "const ticketResult=await tools.mcp__tickets__echo({value:'issue'}); " +
                "const ticketFailure=await tools.mcp__tickets__fail({}); " +
                "let hiddenState='blocked'; try { await tools.mcp__docs__fail({}); hiddenState='called'; } catch {} " +
                "text(JSON.stringify({docs,aliasesSame:aliases.every(alias=>JSON.stringify(alias)===JSON.stringify(docs))," +
                "tickets,docValue:docResult.structuredContent.echo,docText:docResult.content[0].text," +
                "docIsError:docResult.isError,ticketValue:ticketResult.structuredContent.echo," +
                "ticketFailureIsError:ticketFailure.isError," +
                "ticketFailureText:ticketFailure.content[0].text," +
                "searched:searched.map(tool=>tool.name),hiddenState}));";
            var waitHook = Assert.Single(catalog.Registration.ToolCallHooks);
            var codemode = catalog.Registration.ToolDefinitions.Single(tool => tool.Function.Name == "codemode");
            await waitHook(new PiSharpToolCallContext("codemode", "codemode-call", null,
                new Dictionary<string, object?> { ["code"] = script }, codemode.ReachesTools), CancellationToken.None);

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
            Assert.True(result.Ok, result.Error + " | output=" + result.Text);
            using var output = JsonDocument.Parse(result.Text);
            var rootElement = output.RootElement;
            var docs = rootElement.GetProperty("docs");
            Assert.True(rootElement.GetProperty("aliasesSame").GetBoolean());
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
            Assert.Equal("manual", rootElement.GetProperty("docText").GetString());
            Assert.False(rootElement.GetProperty("docIsError").GetBoolean());
            Assert.Equal("issue", rootElement.GetProperty("ticketValue").GetString());
            Assert.True(rootElement.GetProperty("ticketFailureIsError").GetBoolean());
            Assert.Equal("fixture failure", rootElement.GetProperty("ticketFailureText").GetString());
            Assert.Equal(["mcp__docs__echo"], rootElement.GetProperty("searched").EnumerateArray()
                .Select(item => item.GetString()!).ToArray());
            Assert.Equal("blocked", rootElement.GetProperty("hiddenState").GetString());
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}
