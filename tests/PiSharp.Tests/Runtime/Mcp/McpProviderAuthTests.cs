using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;
using PiSharp.Cli;
using PiSharp.Cli.Sessions;
using PiSharp.Runtime.Extensions;
using PiSharp.Runtime.Mcp;
using PiSharp.Runtime.Resources;

namespace PiSharp.Tests;

public sealed class McpProviderAuthTests
{
    [Fact]
    public async Task ProviderTokenIsResolvedForEveryHttpRequestWithoutCreatingMcpOAuthCredentials()
    {
        var root = Path.Combine(Path.GetTempPath(), "pisharp-mcp-provider-token-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        await using var server = new McpLifecycleHttpServer(authorize: value =>
            value is "Bearer refreshed-provider-token" or "Bearer newly-logged-in-token");
        try
        {
            await WriteConfigurationAsync(root, server.Endpoint);
            var arguments = CliArguments.Parse(["--no-extensions", "--extension", "builtin:mcp"]);
            var trust = new ProjectTrust(root);
            var projectConfiguration = await ProjectRuntimeConfiguration.LoadAsync(root, root, arguments,
                trust, interactiveTrust: false, TextReader.Null, TextWriter.Null, trustedOverride: false);
            var refreshCount = 0;
            using var providerHttp = new HttpClient(new ProviderTokenRefreshHandler(() =>
                Interlocked.Increment(ref refreshCount)));
            var storage = new AuthStorage(Path.Combine(root, "auth.json"));
            await storage.StoreOAuthAsync("radius", "expired-provider-token", "refresh-token",
                DateTimeOffset.UtcNow.AddMinutes(1).ToUnixTimeMilliseconds(), "http://radius.test");
            var providers = await ProviderModelRuntime.CreateAsync(root, false, name => name switch
            {
                "PISHARP_RADIUS_GATEWAY" => "http://radius.test",
                _ => null
            }, providerHttp, offline: true);
            var resolverCount = 0;

            using var context = await ProjectRuntimeContext.LoadAsync(projectConfiguration, root, arguments, null,
                providerTokenResolver: (provider, token) =>
                {
                    Assert.Equal("radius", provider);
                    Interlocked.Increment(ref resolverCount);
                    return providers.GetApiKeyForProviderAsync(provider, token);
                });

            Assert.Contains(context.Extensions.Registration.ToolDefinitions, tool =>
                tool.Function.Name == "mcp__protected__echo");
            Assert.True(server.AuthorizationHeaders.Count >= 2);
            Assert.All(server.AuthorizationHeaders, value => Assert.Equal("Bearer refreshed-provider-token", value));
            Assert.Equal(server.AuthorizationHeaders.Count, resolverCount);
            Assert.Equal(1, refreshCount);
            Assert.Equal("refreshed-provider-token", (await storage.ReadAsync("radius"))?.Access);
            Assert.False(File.Exists(Path.Combine(root, "mcp-auth.json")));

            await storage.StoreOAuthAsync("radius", "newly-logged-in-token", "new-refresh-token",
                DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeMilliseconds(), "http://radius.test");
            var echo = context.Extensions.Registration.ToolDefinitions.Single(tool =>
                tool.Function.Name == "mcp__protected__echo");
            var result = Assert.IsType<PiSharpToolResult>(await echo.Function.InvokeAsync(
                new AIFunctionArguments(new Dictionary<string, object?> { ["value"] = "current token" })));

            Assert.Equal("reconnected:current token", result.Text);
            Assert.Equal("Bearer newly-logged-in-token", server.AuthorizationHeaders[^1]);
            Assert.Equal(server.AuthorizationHeaders.Count, resolverCount);
            Assert.False(File.Exists(Path.Combine(root, "mcp-auth.json")));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task UnauthorizedProviderTokenPointsToProviderLogin()
    {
        var root = Path.Combine(Path.GetTempPath(), "pisharp-mcp-provider-401-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        await using var server = new McpLifecycleHttpServer(authorize: _ => false);
        try
        {
            await WriteConfigurationAsync(root, server.Endpoint);
            var configuration = await McpConfiguration.LoadAsync(root, root, false);
            using var catalog = ExtensionCatalog.Load(root, root, false, discover: false);

            var errors = await McpRuntime.RegisterAsync(configuration, catalog, root,
                providerTokenResolver: (_, _) => Task.FromResult<string?>("expired-token"));

            Assert.Contains(errors, error => error.Contains("/login radius", StringComparison.Ordinal));
            Assert.False(File.Exists(Path.Combine(root, "mcp-auth.json")));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static Task WriteConfigurationAsync(string root, string endpoint) =>
        File.WriteAllTextAsync(Path.Combine(root, "mcp.json"), JsonSerializer.Serialize(new
        {
            mcpServers = new Dictionary<string, object>
            {
                ["protected"] = new
                {
                    url = endpoint,
                    auth = new { provider = "radius" },
                    exposure = "direct"
                }
            }
        }));

    private sealed class ProviderTokenRefreshHandler(Action onRefresh) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Assert.Equal("/v1/oauth/token", request.RequestUri?.AbsolutePath);
            var form = System.Web.HttpUtility.ParseQueryString(
                await request.Content!.ReadAsStringAsync(cancellationToken));
            Assert.Equal("refresh_token", form["grant_type"]);
            onRefresh();
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""
                    {"access_token":"refreshed-provider-token","refresh_token":"new-refresh-token","expires_in":3600,"scope":"gateway offline_access"}
                    """, Encoding.UTF8, "application/json")
            };
        }
    }
}
