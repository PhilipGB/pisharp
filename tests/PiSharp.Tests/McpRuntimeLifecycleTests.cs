using PiSharp.Runtime.Extensions;
using PiSharp.Runtime.Mcp;
using PiSharp.Runtime.Tools;
using System.Net.Http;

namespace PiSharp.Tests;

public sealed class McpRuntimeLifecycleTests
{
    [Fact]
    public async Task ExpiredHttpSessionRetriesTheToolCallOnANewSessionOnce()
    {
        var root = Path.Combine(Path.GetTempPath(), "pisharp-mcp-expired-session-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        await using var server = new McpLifecycleHttpServer(expireFirstToolCall: true);
        try
        {
            await WriteHttpConfigurationAsync(root, server.Endpoint);
            var configuration = await McpConfiguration.LoadAsync(root, root, false);
            using var catalog = ExtensionCatalog.Load(root, root, false, discover: false);
            Assert.Empty(await McpRuntime.RegisterAsync(configuration, catalog, root));
            var echo = catalog.Registration.ToolDefinitions.Single(tool =>
                tool.Function.Name == "mcp__fixture__echo");
            var result = Assert.IsType<PiSharpToolResult>(await echo.Function.InvokeAsync(
                new Microsoft.Extensions.AI.AIFunctionArguments(new Dictionary<string, object?>
                {
                    ["value"] = "first"
                })));

            Assert.Equal("reconnected:first", result.Text);
            Assert.Equal(2, server.InitializeCount);
            Assert.Equal(2, server.ToolCallCount);
            Assert.Equal(["session-1", "session-2"], server.ToolCallSessions);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task TransientResourceReadRetriesButTransientToolCallIsNotReplayed()
    {
        var root = Path.Combine(Path.GetTempPath(), "pisharp-mcp-safe-retry-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        await using var server = new McpLifecycleHttpServer(transientFirstResourceRead: true,
            transientFirstToolCall: true);
        try
        {
            await WriteHttpConfigurationAsync(root, server.Endpoint);
            var configuration = await McpConfiguration.LoadAsync(root, root, false);
            using var catalog = ExtensionCatalog.Load(root, root, false, discover: false);
            Assert.Empty(await McpRuntime.RegisterAsync(configuration, catalog, root));
            var read = catalog.Registration.ToolDefinitions.Single(tool =>
                tool.Function.Name == "read_mcp_resource");
            Assert.True(ToolResultOutput.TryReadContract(await read.Function.InvokeAsync(
                new Microsoft.Extensions.AI.AIFunctionArguments(new Dictionary<string, object?>
                {
                    ["server"] = "fixture",
                    ["uri"] = "fixture://note"
                })), out var readResult));
            Assert.Equal("fixture resource", readResult.Text);
            Assert.Equal(2, server.ResourceReadCount);
            Assert.Equal(1, server.InitializeCount);

            var echo = catalog.Registration.ToolDefinitions.Single(tool =>
                tool.Function.Name == "mcp__fixture__echo");
            await Assert.ThrowsAnyAsync<HttpRequestException>(async () =>
                await echo.Function.InvokeAsync(new Microsoft.Extensions.AI.AIFunctionArguments(
                    new Dictionary<string, object?> { ["value"] = "uncertain" })));
            Assert.Equal(1, server.ToolCallCount);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task StdioListChangedNotificationsRefreshToolRegistrationAndResourceLists()
    {
        var root = Path.Combine(Path.GetTempPath(), "pisharp-mcp-notifications-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var fixture = Path.Combine(AppContext.BaseDirectory, "Fixtures", "mcp_notification_fixture.py");
            var statePath = Path.Combine(root, "state.json");
            await File.WriteAllTextAsync(Path.Combine(root, "mcp.json"),
                System.Text.Json.JsonSerializer.Serialize(new
                {
                    mcpServers = new Dictionary<string, object>
                    {
                        ["notify"] = new
                        {
                            command = "python3",
                            args = new[] { fixture },
                            exposure = "direct",
                            env = new Dictionary<string, string>
                            {
                                ["MCP_FIXTURE_NOTIFICATION_STATE"] = statePath
                            }
                        }
                    }
                }));

            var configuration = await McpConfiguration.LoadAsync(root, root, false);
            var manager = new McpRuntimeManager();
            using var catalog = ExtensionCatalog.Load(root, root, false, discover: false,
                additionalPaths: ["builtin:mcp"], builtins: [McpBuiltin.CreateDefinition(manager)]);
            Assert.Empty(await McpRuntime.RegisterAsync(configuration, catalog, root, manager: manager));
            Assert.Contains("mcp", catalog.LoadedBuiltins);
            Assert.Contains(catalog.Registration.ToolDefinitions, tool =>
                tool.Function.Name == "mcp__notify__echo");
            Assert.Equal("builtin:mcp", catalog.Registration.ToolSourceInfo["mcp__notify__echo"].Path);

            var echo = catalog.Registration.ToolDefinitions.Single(tool =>
                tool.Function.Name == "mcp__notify__echo");
            var echoResult = Assert.IsType<PiSharpToolResult>(await echo.Function.InvokeAsync(
                new Microsoft.Extensions.AI.AIFunctionArguments(new Dictionary<string, object?>
                {
                    ["value"] = "before"
                })));
            Assert.Equal("echo:before", echoResult.Text);

            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            while (!catalog.Registration.ToolDefinitions.Any(tool =>
                       tool.Function.Name == "mcp__notify__added") ||
                   !await NotificationFixtureRefreshedResourcesAsync(statePath))
                await Task.Delay(20, deadline.Token);

            Assert.Equal(ToolExposure.Hidden, catalog.Registration.ToolDefinitions.Single(tool =>
                tool.Function.Name == "mcp__notify__echo").Exposure);
            var added = catalog.Registration.ToolDefinitions.Single(tool =>
                tool.Function.Name == "mcp__notify__added");
            Assert.Equal("builtin:mcp", catalog.Registration.ToolSourceInfo["mcp__notify__added"].Path);
            Assert.Contains("notify: connected", await catalog.Registration.Commands["mcp"]("status",
                CancellationToken.None), StringComparison.Ordinal);
            var addedResult = Assert.IsType<PiSharpToolResult>(await added.Function.InvokeAsync(
                new Microsoft.Extensions.AI.AIFunctionArguments(new Dictionary<string, object?>
                {
                    ["value"] = "after"
                })));
            Assert.Equal("added:after", addedResult.Text);

            var listResources = catalog.Registration.ToolDefinitions.Single(tool =>
                tool.Function.Name == "list_mcp_resources");
            Assert.Equal("builtin:mcp", catalog.Registration.ToolSourceInfo["list_mcp_resources"].Path);
            Assert.True(ToolResultOutput.TryReadContract(await listResources.Function.InvokeAsync(
                new Microsoft.Extensions.AI.AIFunctionArguments(new Dictionary<string, object?>
                {
                    ["server"] = "notify"
                })), out var resourceResult));
            Assert.Contains("updated", resourceResult.Text, StringComparison.Ordinal);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

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
            var manager = new McpRuntimeManager();
            using var catalog = ExtensionCatalog.Load(root, root, false, discover: false,
                additionalPaths: ["builtin:mcp"], builtins: [McpBuiltin.CreateDefinition(manager)]);
            Assert.Empty(await McpRuntime.RegisterAsync(configuration, catalog, root, manager: manager));
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

            Assert.Contains("reconnect: disconnected", await catalog.Registration.Commands["mcp"]("status",
                CancellationToken.None), StringComparison.Ordinal);
            Assert.Equal("Reconnected MCP server reconnect.", await catalog.Registration.Commands["mcp"](
                "reconnect reconnect", CancellationToken.None));
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

    private static Task WriteHttpConfigurationAsync(string root, string endpoint) =>
        File.WriteAllTextAsync(Path.Combine(root, "mcp.json"), System.Text.Json.JsonSerializer.Serialize(new
        {
            mcpServers = new Dictionary<string, object>
            {
                ["fixture"] = new
                {
                    url = endpoint,
                    headers = new Dictionary<string, string> { ["Authorization"] = "Bearer fixture" },
                    exposure = "direct",
                    timeout = 10
                }
            }
        }));

    private static async Task<bool> NotificationFixtureRefreshedResourcesAsync(string statePath)
    {
        if (!File.Exists(statePath)) return false;
        try
        {
            using var state = System.Text.Json.JsonDocument.Parse(await File.ReadAllTextAsync(statePath));
            return state.RootElement.GetProperty("resourcesList").GetInt32() >= 2 &&
                state.RootElement.GetProperty("templatesList").GetInt32() >= 2;
        }
        catch (System.Text.Json.JsonException) { return false; }
    }
}
