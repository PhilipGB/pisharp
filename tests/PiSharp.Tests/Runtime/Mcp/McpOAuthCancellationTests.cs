using System.Text.Json;
using Microsoft.Extensions.AI;
using ModelContextProtocol.Authentication;
using PiSharp.Runtime.Extensions;
using PiSharp.Runtime.Mcp;

namespace PiSharp.Tests;

[Collection("MCP OAuth")]
public sealed class McpOAuthCancellationTests
{
    [Theory]
    [InlineData(3600)]
    [InlineData(null)]
    public async Task CanceledAndTimedOutRequestsDoNotDispatchAfterOneSharedRefresh(int? tokenLifetime)
    {
        var root = Path.Combine(Path.GetTempPath(), "pisharp-mcp-oauth-cancellation-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        await using var server = new McpOAuthRecoveryServer(tokenLifetime);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var canceled = new CancellationTokenSource();
        using var timedOut = new CancellationTokenSource();
        try
        {
            await new McpTokenCache(root).ForServer("protected", server.Endpoint).StoreTokensAsync(new TokenContainer
            {
                AccessToken = "old",
                RefreshToken = "refresh-old",
                TokenType = "Bearer",
                ObtainedAt = DateTimeOffset.UtcNow,
                ExpiresIn = 3600,
                ClientId = "fixture-client",
                AuthorizationServer = server.Origin,
                TokenEndpointAuthMethod = "none"
            }, deadline.Token);
            using var configuration = JsonDocument.Parse(JsonSerializer.Serialize(new
            {
                url = server.Endpoint.AbsoluteUri,
                exposure = "direct",
                oauth = new { clientId = "fixture-client" }
            }));
            var entry = McpConfiguration.Parse("protected", configuration.RootElement, "mcp.json", "global");
            using var catalog = ExtensionCatalog.Load(root, root, false, discover: false);
            try
            {
                Assert.Empty(await McpRuntime.RegisterAsync(new McpConfiguration([entry], false, []) { AgentDirectory = root },
                    catalog, root, deadline.Token));
                var echo = catalog.Registration.ToolDefinitions.Single(tool => tool.Function.Name == "mcp__protected__echo").Function;
                Task<object?> Call(string value, CancellationToken token) => echo.InvokeAsync(new AIFunctionArguments(
                    new Dictionary<string, object?> { ["value"] = value }), token).AsTask();
                var aborted = Call("aborted", canceled.Token);
                if (await Task.WhenAny(aborted, server.RefreshStarted.Task) == aborted) await aborted;
                await server.RefreshStarted.Task.WaitAsync(deadline.Token);
                var expired = Call("timed-out", timedOut.Token);
                var kept = Call("kept", deadline.Token);
                await server.AllUnauthorized.Task.WaitAsync(deadline.Token);
                canceled.Cancel();
                timedOut.CancelAfter(TimeSpan.FromMilliseconds(100));
                Assert.IsAssignableFrom<OperationCanceledException>(await Record.ExceptionAsync(() => aborted.WaitAsync(TimeSpan.FromSeconds(2))));
                Assert.IsAssignableFrom<OperationCanceledException>(await Record.ExceptionAsync(() => expired.WaitAsync(TimeSpan.FromSeconds(2))));
                Assert.Empty(server.Executed);
                server.ReleaseRefresh.TrySetResult();
                var result = Assert.IsType<PiSharpToolResult>(await kept.WaitAsync(deadline.Token));
                Assert.Equal("kept", result.Text);
                Assert.Equal(["kept"], server.Executed);
                Assert.True(server.Refreshes == 1, string.Join('\n', server.TokenForms));
                var stored = await new McpTokenCache(root).ForServer("protected", server.Endpoint).GetTokensAsync(deadline.Token);
                Assert.Equal("new", stored?.AccessToken);
                Assert.Equal("refresh-new", stored?.RefreshToken);
                Assert.Equal("fixture-client", stored?.ClientId);
                Assert.Equal(server.Origin, stored?.AuthorizationServer);
                Assert.Equal(tokenLifetime, stored?.ExpiresIn);
                Assert.Equal("later", Assert.IsType<PiSharpToolResult>(await Call("later", deadline.Token)).Text);
                Assert.Equal(1, server.Initializations);
                server.ExpireAccessToken();
                Assert.Equal("refreshed again", Assert.IsType<PiSharpToolResult>(await Call("refreshed again", deadline.Token)).Text);
                Assert.Equal(2, server.Refreshes);
                Assert.Equal("new-2", (await new McpTokenCache(root).ForServer("protected", server.Endpoint)
                    .GetTokensAsync(deadline.Token))?.AccessToken);
            }
            finally { server.ReleaseRefresh.TrySetResult(); }
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task FailedRefreshPersistenceReleasesItsLeaseAndSettles()
    {
        var root = Path.Combine(Path.GetTempPath(), "pisharp-mcp-refresh-write-failure-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        await using var server = new McpOAuthRecoveryServer();
        try
        {
            var cache = new McpTokenCache(root).ForServerWithRefresh("protected", server.Endpoint);
            await cache.StoreTokensAsync(new TokenContainer
            {
                AccessToken = "old",
                RefreshToken = "refresh-old",
                TokenType = "Bearer",
                ObtainedAt = DateTimeOffset.UtcNow,
                ExpiresIn = 3600
            }, default);
            _ = await cache.GetTokensAsync(default);
            using var refresh = new McpOAuthRefreshHandler(cache);
            using var http = new HttpClient(new McpSharedOAuthRefreshHandler(cache, refresh));
            var grant = http.PostAsync(server.Origin + "/token", new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "refresh_token",
                ["refresh_token"] = "refresh-old"
            }));
            await server.RefreshStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var path = Path.Combine(root, "mcp-auth.json");
            File.Delete(path);
            Directory.CreateDirectory(path);
            server.ReleaseRefresh.TrySetResult();
            var error = await Record.ExceptionAsync(() => grant.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.True(error is IOException or UnauthorizedAccessException, error?.ToString());
            await refresh.WaitForSettledAsync().WaitAsync(TimeSpan.FromSeconds(2));
            Assert.False(cache.Owner.HasPendingRefresh(cache.Key));
            await using var lease = await cache.Owner.AcquireRefreshLockAsync(cache.Key, default);
        }
        finally
        {
            server.ReleaseRefresh.TrySetResult();
            Directory.Delete(root, true);
        }
    }
}
