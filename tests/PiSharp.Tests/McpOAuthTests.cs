using System.Text.Json;
using System.Net;
using System.Net.Sockets;
using ModelContextProtocol.Authentication;
using PiSharp.Cli;
using PiSharp.Runtime.Extensions;
using PiSharp.Runtime.Mcp;

namespace PiSharp.Tests;

public sealed class McpOAuthTests
{
    [Fact]
    public async Task ExplicitLoginUsesLoopbackCodeAndPersistsSdkTokens()
    {
        if (!OperatingSystem.IsLinux()) return;
        var root = Path.Combine(Path.GetTempPath(), "pisharp-mcp-oauth-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        using var reservation = new TcpListener(IPAddress.Loopback, 0);
        reservation.Start();
        var port = ((IPEndPoint)reservation.LocalEndpoint).Port;
        reservation.Stop();
        var origin = "http://127.0.0.1:" + port;
        using var listener = new HttpListener();
        listener.Prefixes.Add(origin + "/");
        listener.Start();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var issued = 0;
        var server = Task.Run(async () =>
        {
            while (!deadline.IsCancellationRequested)
            {
                HttpListenerContext context;
                try { context = await listener.GetContextAsync().WaitAsync(deadline.Token); }
                catch (Exception error) when (error is OperationCanceledException or HttpListenerException or
                    ObjectDisposedException)
                { break; }
                var request = context.Request;
                var response = context.Response;
                response.ContentType = "application/json";
                string body;
                switch (request.Url!.AbsolutePath)
                {
                    case "/.well-known/oauth-protected-resource":
                        body = JsonSerializer.Serialize(new
                        {
                            resource = origin + "/mcp",
                            authorization_servers = new[] { origin }
                        });
                        break;
                    case "/.well-known/oauth-authorization-server":
                        body = JsonSerializer.Serialize(new
                        {
                            issuer = origin,
                            authorization_endpoint = origin + "/authorize",
                            token_endpoint = origin + "/token",
                            response_types_supported = new[] { "code" },
                            code_challenge_methods_supported = new[] { "S256" },
                            token_endpoint_auth_methods_supported = new[] { "none" }
                        });
                        break;
                    case "/token":
                        Interlocked.Increment(ref issued);
                        body = """{"access_token":"fixture-access","refresh_token":"fixture-refresh","token_type":"Bearer","expires_in":3600}""";
                        break;
                    case "/mcp":
                        if (request.HttpMethod != "POST")
                        {
                            response.StatusCode = 405;
                            body = "{}";
                            break;
                        }
                        if (request.Headers["Authorization"] != "Bearer fixture-access")
                        {
                            response.StatusCode = 401;
                            response.AddHeader("WWW-Authenticate", "Bearer resource_metadata=\"" + origin +
                                "/.well-known/oauth-protected-resource\"");
                            body = "{}";
                            break;
                        }
                        using (var input = new StreamReader(request.InputStream))
                        {
                            var payload = await input.ReadToEndAsync(deadline.Token);
                            using var message = JsonDocument.Parse(payload);
                            var rootMessage = message.RootElement;
                            if (!rootMessage.TryGetProperty("id", out var id))
                            {
                                response.StatusCode = 202;
                                body = "";
                            }
                            else if (rootMessage.GetProperty("method").GetString() == "initialize")
                                body = JsonSerializer.Serialize(new
                                {
                                    jsonrpc = "2.0",
                                    id = id.Clone(),
                                    result = new
                                    {
                                        protocolVersion = rootMessage.GetProperty("params")
                                            .GetProperty("protocolVersion").GetString(),
                                        capabilities = new { },
                                        serverInfo = new { name = "oauth-fixture", version = "1.0" }
                                    }
                                });
                            else body = JsonSerializer.Serialize(new
                            {
                                jsonrpc = "2.0",
                                id = id.Clone(),
                                error = new
                                {
                                    code = -32601,
                                    message = "Method not found"
                                }
                            });
                        }
                        break;
                    default:
                        response.StatusCode = 404;
                        body = "{}";
                        break;
                }
                var bytes = System.Text.Encoding.UTF8.GetBytes(body);
                response.ContentLength64 = bytes.Length;
                await response.OutputStream.WriteAsync(bytes, deadline.Token);
                response.Close();
            }
        }, deadline.Token);
        try
        {
            var config = JsonSerializer.SerializeToElement(new
            {
                url = origin + "/mcp",
                oauth = new { clientId = "fixture-client" }
            });
            var entry = McpConfiguration.Parse("protected", config, "mcp.json", "global");
            var configuration = new McpConfiguration([entry], true, []) { AgentDirectory = root };
            await File.WriteAllTextAsync(Path.Combine(root, "mcp.json"), JsonSerializer.Serialize(new
            {
                mcpServers = new Dictionary<string, JsonElement> { ["protected"] = config }
            }));
            using (var catalog = ExtensionCatalog.Load(root, root, false, discover: false))
            {
                var failures = await McpRuntime.RegisterAsync(configuration, catalog, root, deadline.Token);
                Assert.Contains(failures, failure => failure.Contains("needs authorization", StringComparison.Ordinal));
            }
            var status = new StringWriter();
            Assert.Equal(1, await McpCommand.RunAsync(["list", "--json"], root, root, status,
                new StringWriter(), deadline.Token));
            using (var reported = JsonDocument.Parse(status.ToString()))
                Assert.Equal("needs-auth", reported.RootElement.GetProperty("servers")[0]
                    .GetProperty("state").GetString());
            var output = new LoginOutput();
            var login = McpOAuthLogin.SignInAsync(entry, root, output, false, TimeSpan.FromSeconds(10), deadline.Token);
            var authorization = new Uri(await output.Authorization.Task.WaitAsync(deadline.Token));
            var query = System.Web.HttpUtility.ParseQueryString(authorization.Query);
            var callback = query["redirect_uri"] + "?code=fixture-code&state=" +
                Uri.EscapeDataString(query["state"]!) + "&iss=" + Uri.EscapeDataString(origin);
            using var browser = new HttpClient();
            Assert.Equal(HttpStatusCode.OK, (await browser.GetAsync(callback, deadline.Token)).StatusCode);
            await login.WaitAsync(deadline.Token);
            Assert.Equal(1, issued);
            var tokens = await new McpTokenCache(root).ForServer(new Uri(origin + "/mcp"))
                .GetTokensAsync(default);
            Assert.Equal("fixture-access", tokens?.AccessToken);
            Assert.Equal("fixture-refresh", tokens?.RefreshToken);
            using (var catalog = ExtensionCatalog.Load(root, root, false, discover: false))
                Assert.Empty(await McpRuntime.RegisterAsync(configuration, catalog, root, deadline.Token));
            status.GetStringBuilder().Clear();
            Assert.Equal(0, await McpCommand.RunAsync(["list", "--json"], root, root, status,
                new StringWriter(), deadline.Token));
            using (var reported = JsonDocument.Parse(status.ToString()))
                Assert.Equal("connected", reported.RootElement.GetProperty("servers")[0]
                    .GetProperty("state").GetString());
        }
        finally
        {
            deadline.Cancel();
            listener.Close();
            try { await server; }
            catch (Exception error) when (error is OperationCanceledException or HttpListenerException or IOException) { }
            Directory.Delete(root, recursive: true);
        }
    }

    private sealed class LoginOutput : StringWriter
    {
        public TaskCompletionSource<string> Authorization { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override Task WriteLineAsync(string? value)
        {
            if (value?.Contains("http://", StringComparison.Ordinal) == true)
                Authorization.TrySetResult(value.Split('\n').Last());
            return base.WriteLineAsync(value);
        }
    }

    [Fact]
    public async Task TokensArePrivatePersistentAndBoundToServerUrl()
    {
        var root = Path.Combine(Path.GetTempPath(), "pisharp-mcp-oauth-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var one = new Uri("https://example.test/one");
            var two = new Uri("https://example.test/two");
            var cache = new McpTokenCache(root);
            var token = new TokenContainer
            {
                TokenType = "Bearer",
                AccessToken = "secret-access",
                RefreshToken = "secret-refresh",
                ObtainedAt = DateTimeOffset.UtcNow,
                ClientId = "client",
                AuthorizationServer = "https://auth.example.test"
            };
            await cache.ForServer(one).StoreTokensAsync(token, default);
            Assert.Null(await new McpTokenCache(root).ForServer(two).GetTokensAsync(default));
            var restored = await new McpTokenCache(root).ForServer(one).GetTokensAsync(default);
            Assert.Equal("secret-refresh", restored?.RefreshToken);
            Assert.Equal("client", restored?.ClientId);
            Assert.Equal("https://auth.example.test", restored?.AuthorizationServer);
            if (OperatingSystem.IsLinux())
                Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite,
                    File.GetUnixFileMode(Path.Combine(root, "mcp-auth.json")));
            Assert.True(await cache.RemoveAsync(one));
            Assert.False(await cache.RemoveAsync(one));
            Assert.Null(await cache.ForServer(one).GetTokensAsync(default));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task ConcurrentServerUpdatesDoNotLoseCredentials()
    {
        var root = Path.Combine(Path.GetTempPath(), "pisharp-mcp-oauth-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            await Task.WhenAll(Enumerable.Range(0, 12).Select(async index =>
                await new McpTokenCache(root).ForServer(new Uri("https://example.test/" + index))
                    .StoreTokensAsync(new TokenContainer
                    {
                        TokenType = "Bearer",
                        AccessToken = "access-" + index,
                        ObtainedAt = DateTimeOffset.UtcNow
                    }, default)));
            for (var index = 0; index < 12; index++)
                Assert.Equal("access-" + index,
                    (await new McpTokenCache(root).ForServer(new Uri("https://example.test/" + index))
                        .GetTokensAsync(default))?.AccessToken);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task LogoutDeletesOnlyConfiguredServerCredentials()
    {
        var root = Path.Combine(Path.GetTempPath(), "pisharp-mcp-oauth-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var first = new Uri("https://example.test/one");
            var second = new Uri("https://example.test/two");
            await File.WriteAllTextAsync(Path.Combine(root, "mcp.json"), JsonSerializer.Serialize(new
            {
                mcpServers = new Dictionary<string, object>
                {
                    ["first"] = new { url = first.AbsoluteUri },
                    ["second"] = new { url = second.AbsoluteUri }
                }
            }));
            var cache = new McpTokenCache(root);
            foreach (var url in new[] { first, second })
                await cache.ForServer(url).StoreTokensAsync(new TokenContainer
                {
                    TokenType = "Bearer",
                    AccessToken = url.AbsolutePath,
                    ObtainedAt = DateTimeOffset.UtcNow
                }, default);
            var output = new StringWriter();
            var errors = new StringWriter();
            Assert.Equal(0, await McpCommand.RunAsync(["logout", "first"], root, root, output, errors));
            Assert.Contains("Signed out", output.ToString());
            Assert.Null(await cache.ForServer(first).GetTokensAsync(default));
            Assert.NotNull(await cache.ForServer(second).GetTokensAsync(default));
            Assert.Equal("", errors.ToString());
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void OAuthConfigurationRejectsUnsafeCallbacksAndAuthorizationHeaders()
    {
        foreach (var invalid in new[]
        {
            new { url = "https://example.test/mcp", oauth = new { callbackUrl = "https://example.test/callback" } },
            new { url = "https://example.test/mcp", oauth = new { callbackUrl = "http://0.0.0.0:9000/callback" } }
        })
            Assert.Throws<ArgumentException>(() => McpConfiguration.Parse("test", JsonSerializer.SerializeToElement(invalid),
                "mcp.json", "global"));
        var mixed = JsonSerializer.SerializeToElement(new
        {
            url = "https://example.test/mcp",
            headers = new { Authorization = "Bearer token" },
            oauth = new { clientId = "client" }
        });
        Assert.Throws<ArgumentException>(() => McpConfiguration.Parse("test", mixed, "mcp.json", "global"));
    }
}
