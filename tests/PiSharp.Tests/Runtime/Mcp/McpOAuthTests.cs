using System.Text.Json;
using System.Text;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using ModelContextProtocol.Authentication;
using PiSharp.Cli;
using PiSharp.Runtime.Extensions;
using PiSharp.Runtime.Mcp;

namespace PiSharp.Tests;

[CollectionDefinition("MCP OAuth", DisableParallelization = true)]
public sealed class McpOAuthTestCollection { }

[Collection("MCP OAuth")]
public sealed class McpOAuthTests
{
    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "PiSharp.slnx")))
            directory = directory.Parent;
        return directory?.FullName ?? throw new DirectoryNotFoundException("Could not find PiSharp repository root.");
    }

    [Theory]
    [InlineData("Claude Code", "Claude Code")]
    [InlineData(null, "pi")]
    public async Task DynamicRegistrationUsesConfiguredClientNameAndPersistsSdkTokens(
        string? clientName, string expectedClientName)
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
        var registrationNames = new System.Collections.Concurrent.ConcurrentQueue<string?>();
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
                            registration_endpoint = origin + "/register",
                            response_types_supported = new[] { "code" },
                            code_challenge_methods_supported = new[] { "S256" },
                            token_endpoint_auth_methods_supported = new[] { "none" }
                        });
                        break;
                    case "/register":
                        using (var registration = new StreamReader(request.InputStream))
                        using (var metadata = JsonDocument.Parse(await registration.ReadToEndAsync(deadline.Token)))
                            registrationNames.Enqueue(metadata.RootElement.TryGetProperty("client_name", out var registeredName)
                                ? registeredName.GetString()
                                : null);
                        response.StatusCode = (int)HttpStatusCode.Created;
                        body = """{"client_id":"fixture-client"}""";
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
            var oauth = new Dictionary<string, string>(StringComparer.Ordinal);
            if (clientName is not null) oauth["clientName"] = clientName;
            var config = JsonSerializer.SerializeToElement(new
            {
                url = origin + "/mcp",
                oauth
            });
            var entry = McpConfiguration.Parse("protected", config, "mcp.json", "global");
            var configuration = new McpConfiguration([entry], true, []) { AgentDirectory = root };
            await File.WriteAllTextAsync(Path.Combine(root, "mcp.json"), JsonSerializer.Serialize(new
            {
                mcpServers = new Dictionary<string, JsonElement> { ["protected"] = config }
            }));
            using (var catalog = ExtensionCatalog.Load(root, root, false, discover: false))
            {
                var failures = await McpRuntime.RegisterForStatusAsync(configuration, catalog, root, deadline.Token);
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
            Assert.NotEmpty(registrationNames);
            Assert.All(registrationNames, name => Assert.Equal(expectedClientName, name));
            var tokens = await new McpTokenCache(root).ForServer("protected", new Uri(origin + "/mcp"))
                .GetTokensAsync(default);
            Assert.Equal("fixture-access", tokens?.AccessToken);
            Assert.Equal("fixture-refresh", tokens?.RefreshToken);
            using (var catalog = ExtensionCatalog.Load(root, root, false, discover: false))
                Assert.Empty(await McpRuntime.RegisterForStatusAsync(configuration, catalog, root, deadline.Token));
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
            await cache.ForServer("test", one).StoreTokensAsync(token, default);
            Assert.Null(await new McpTokenCache(root).ForServer("test", two).GetTokensAsync(default));
            var restored = await new McpTokenCache(root).ForServer("test", one).GetTokensAsync(default);
            Assert.Equal("secret-refresh", restored?.RefreshToken);
            Assert.Equal("client", restored?.ClientId);
            Assert.Equal("https://auth.example.test", restored?.AuthorizationServer);
            if (OperatingSystem.IsLinux())
                Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite,
                    File.GetUnixFileMode(Path.Combine(root, "mcp-auth.json")));
            Assert.True(await cache.RemoveAsync("test", one));
            Assert.False(await cache.RemoveAsync("test", one));
            Assert.Null(await cache.ForServer("test", one).GetTokensAsync(default));
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
                await new McpTokenCache(root).ForServer("test", new Uri("https://example.test/" + index))
                    .StoreTokensAsync(new TokenContainer
                    {
                        TokenType = "Bearer",
                        AccessToken = "access-" + index,
                        ObtainedAt = DateTimeOffset.UtcNow
                    }, default)));
            for (var index = 0; index < 12; index++)
                Assert.Equal("access-" + index,
                    (await new McpTokenCache(root).ForServer("test", new Uri("https://example.test/" + index))
                        .GetTokensAsync(default))?.AccessToken);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task ConcurrentOAuthRefreshesUseTheRotatedTokenFromTheFirstProcess()
    {
        var root = Path.Combine(Path.GetTempPath(), "pisharp-mcp-oauth-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var serverUrl = new Uri("https://mcp.example.test/mcp");
        var endpoint = new Uri("https://auth.example.test/token");
        var firstCache = new McpTokenCache(root).ForServerWithRefresh("test", serverUrl);
        var secondCache = new McpTokenCache(root).ForServerWithRefresh("test", serverUrl);
        await firstCache.StoreTokensAsync(new TokenContainer
        {
            TokenType = "Bearer",
            AccessToken = "access-1",
            RefreshToken = "refresh-1",
            ExpiresIn = 60,
            ObtainedAt = DateTimeOffset.UtcNow.AddHours(-1)
        }, default);
        Assert.Equal("access-1", (await firstCache.GetTokensAsync(default))?.AccessToken);
        Assert.Equal("access-1", (await secondCache.GetTokensAsync(default))?.AccessToken);

        var tokenEndpoint = new RotatingTokenEndpoint();
        var firstHandler = new McpOAuthRefreshHandler(firstCache, tokenEndpoint);
        using var firstHttp = new HttpClient(firstHandler);
        using var secondHttp = new HttpClient(new McpOAuthRefreshHandler(secondCache, tokenEndpoint));
        var request = () => new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "refresh_token",
                ["refresh_token"] = "refresh-1"
            })
        };

        try
        {
            var firstRefresh = firstHttp.SendAsync(request());
            await tokenEndpoint.FirstRequest.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var secondRefresh = secondHttp.SendAsync(request());
            tokenEndpoint.ContinueFirstRequest.TrySetResult();

            using var firstResponse = await firstRefresh.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(HttpStatusCode.OK, firstResponse.StatusCode);
            var settled = firstHandler.WaitForSettledAsync();
            Assert.False(settled.IsCompleted);
            using var cancelled = new CancellationTokenSource();
            cancelled.Cancel();
            await firstCache.StoreTokensAsync(new TokenContainer
            {
                TokenType = "Bearer",
                AccessToken = "access-2",
                RefreshToken = "refresh-2",
                ExpiresIn = 3600,
                ObtainedAt = DateTimeOffset.UtcNow
            }, cancelled.Token);
            await settled.WaitAsync(TimeSpan.FromSeconds(5));

            using var secondResponse = await secondRefresh.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(HttpStatusCode.OK, secondResponse.StatusCode);
            using var refreshed = JsonDocument.Parse(await secondResponse.Content.ReadAsStringAsync());
            Assert.Equal("access-2", refreshed.RootElement.GetProperty("access_token").GetString());
            Assert.Equal("refresh-2", refreshed.RootElement.GetProperty("refresh_token").GetString());
            await secondCache.StoreTokensAsync(new TokenContainer
            {
                TokenType = "Bearer",
                AccessToken = "access-2",
                RefreshToken = "refresh-2",
                ExpiresIn = refreshed.RootElement.GetProperty("expires_in").GetInt32(),
                ObtainedAt = DateTimeOffset.UtcNow
            }, default);

            Assert.Equal(1, tokenEndpoint.RefreshRequests);
            Assert.Equal("refresh-2", (await new McpTokenCache(root).ForServer("test", serverUrl)
                .GetTokensAsync(default))?.RefreshToken);
        }
        finally
        {
            tokenEndpoint.ContinueFirstRequest.TrySetResult();
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task RefreshWithEmptyOptionalFieldsPreservesTheStoredRefreshToken()
    {
        var root = Path.Combine(Path.GetTempPath(), "pisharp-mcp-oauth-optional-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var serverUrl = new Uri("https://mcp.example.test/mcp");
        var cache = new McpTokenCache(root).ForServerWithRefresh("test", serverUrl);
        await cache.StoreTokensAsync(new TokenContainer
        {
            TokenType = "Bearer",
            AccessToken = "access-1",
            RefreshToken = "refresh-1",
            ExpiresIn = 60,
            ObtainedAt = DateTimeOffset.UtcNow.AddHours(-1)
        }, default);
        _ = await cache.GetTokensAsync(default);
        var handler = new McpOAuthRefreshHandler(cache, new StaticTokenEndpoint(
            """{"access_token":"access-2","refresh_token":"","token_type":"Bearer","expires_in":3600,"scope":"","id_token":""}"""));
        using var http = new HttpClient(handler);
        try
        {
            using var response = await http.SendAsync(RefreshRequest());
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var body = document.RootElement;
            var refreshToken = body.GetProperty("refresh_token").GetString();
            var scopePresent = body.TryGetProperty("scope", out _);
            var idTokenPresent = body.TryGetProperty("id_token", out _);
            await cache.StoreTokensAsync(new TokenContainer
            {
                TokenType = body.GetProperty("token_type").GetString()!,
                AccessToken = body.GetProperty("access_token").GetString()!,
                RefreshToken = refreshToken,
                ExpiresIn = body.GetProperty("expires_in").GetInt32(),
                ObtainedAt = DateTimeOffset.UtcNow,
                Scope = body.TryGetProperty("scope", out var scope) ? scope.GetString() : null
            }, default);
            await handler.WaitForSettledAsync();

            Assert.Equal("refresh-1", refreshToken);
            Assert.False(scopePresent);
            Assert.False(idTokenPresent);
            Assert.Equal("refresh-1", (await cache.GetTokensAsync(default))?.RefreshToken);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RefreshWithNullOrEmptyExpiryKeepsTheTokenPersistenceLeaseUntilStored(bool emptyStringExpiry)
    {
        var root = Path.Combine(Path.GetTempPath(), "pisharp-mcp-oauth-null-expiry-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var serverUrl = new Uri("https://mcp.example.test/mcp");
        var cache = new McpTokenCache(root).ForServerWithRefresh("test", serverUrl);
        await cache.StoreTokensAsync(new TokenContainer
        {
            TokenType = "Bearer",
            AccessToken = "access-1",
            RefreshToken = "refresh-1",
            ExpiresIn = 60,
            ObtainedAt = DateTimeOffset.UtcNow.AddHours(-1)
        }, default);
        _ = await cache.GetTokensAsync(default);
        var responseBody = JsonSerializer.Serialize(new
        {
            access_token = "access-2",
            refresh_token = "refresh-2",
            token_type = "Bearer",
            expires_in = emptyStringExpiry ? "" : null
        });
        var handler = new McpOAuthRefreshHandler(cache, new StaticTokenEndpoint(responseBody));
        using var http = new HttpClient(handler);
        try
        {
            using var response = await http.SendAsync(RefreshRequest());
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var body = document.RootElement;
            var settled = handler.WaitForSettledAsync();
            var leaseRemainsPending = !settled.IsCompleted;
            await cache.StoreTokensAsync(new TokenContainer
            {
                TokenType = body.GetProperty("token_type").GetString()!,
                AccessToken = body.GetProperty("access_token").GetString()!,
                RefreshToken = body.GetProperty("refresh_token").GetString(),
                ExpiresIn = body.TryGetProperty("expires_in", out var expiresIn) &&
                    expiresIn.ValueKind == JsonValueKind.Number
                    ? expiresIn.GetInt32()
                    : null,
                ObtainedAt = DateTimeOffset.UtcNow
            }, default);
            await settled.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.True(leaseRemainsPending, "A null expiry is a valid token response and must stay under the refresh lease.");
            Assert.Null((await cache.GetTokensAsync(default))?.ExpiresIn);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task MCPRefreshLockSerializesSeparateProcesses()
    {
        if (!OperatingSystem.IsLinux()) return;
        var root = Path.Combine(Path.GetTempPath(), "pisharp-mcp-oauth-process-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        using var reservation = new TcpListener(IPAddress.Loopback, 0);
        reservation.Start();
        var port = ((IPEndPoint)reservation.LocalEndpoint).Port;
        reservation.Stop();
        var origin = "http://127.0.0.1:" + port;
        var serverUrl = new Uri(origin + "/mcp");
        using var listener = new HttpListener();
        listener.Prefixes.Add(origin + "/");
        listener.Start();
        var releaseFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstRequest = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var stopServer = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var refreshRequests = 0;
        var readyFiles = new[] { Path.Combine(root, "ready-1"), Path.Combine(root, "ready-2") };
        var children = new List<Process>();
        var server = Task.Run(async () =>
        {
            try
            {
                while (!stopServer.Task.IsCompleted)
                {
                    var pending = listener.GetContextAsync();
                    if (await Task.WhenAny(pending, stopServer.Task) != pending) break;
                    var context = await pending;
                    refreshRequests++;
                    using var reader = new StreamReader(context.Request.InputStream);
                    var form = await reader.ReadToEndAsync();
                    if (refreshRequests == 1)
                    {
                        firstRequest.TrySetResult(form);
                        await releaseFirst.Task;
                        context.Response.StatusCode = 200;
                        context.Response.ContentType = "application/json";
                        var body = Encoding.UTF8.GetBytes(
                            "{\"access_token\":\"access-2\",\"refresh_token\":\"refresh-2\",\"token_type\":\"Bearer\",\"expires_in\":3600}");
                        context.Response.ContentLength64 = body.Length;
                        await context.Response.OutputStream.WriteAsync(body);
                    }
                    else
                    {
                        context.Response.StatusCode = 400;
                        context.Response.ContentLength64 = 0;
                    }
                    context.Response.Close();
                }
            }
            catch (Exception error) when (error is HttpListenerException or ObjectDisposedException or
                OperationCanceledException)
            { }
        });

        try
        {
            await new McpTokenCache(root).ForServer("test", serverUrl).StoreTokensAsync(new TokenContainer
            {
                TokenType = "Bearer",
                AccessToken = "access-1",
                RefreshToken = "refresh-1",
                ExpiresIn = 60,
                ObtainedAt = DateTimeOffset.UtcNow.AddHours(-1)
            }, default);
            var project = Path.Combine(FindRepositoryRoot(), "tests", "PiSharp.Tests", "PiSharp.Tests.csproj");
            foreach (var ready in readyFiles)
            {
                var start = new ProcessStartInfo("dotnet")
                {
                    WorkingDirectory = FindRepositoryRoot(),
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };
                start.ArgumentList.Add("test");
                start.ArgumentList.Add(project);
                start.ArgumentList.Add("--no-build");
                start.ArgumentList.Add("--no-restore");
                start.ArgumentList.Add("--filter");
                start.ArgumentList.Add("FullyQualifiedName~McpOAuthRefreshProcessProbe");
                start.Environment["PISHARP_MCP_REFRESH_TEST_AGENT_DIR"] = root;
                start.Environment["PISHARP_MCP_REFRESH_TEST_SERVER_URL"] = serverUrl.AbsoluteUri;
                start.Environment["PISHARP_MCP_REFRESH_TEST_ENDPOINT"] = origin + "/token";
                start.Environment["PISHARP_MCP_REFRESH_TEST_READY_FILE"] = ready;
                children.Add(Process.Start(start) ?? throw new InvalidOperationException("Could not start refresh test process."));
            }

            var submittedForm = await firstRequest.Task.WaitAsync(TimeSpan.FromSeconds(20));
            Assert.Contains("refresh_token=refresh-1", submittedForm, StringComparison.Ordinal);
            var deadline = Stopwatch.StartNew();
            while (readyFiles.Any(path => !File.Exists(path)))
            {
                if (deadline.Elapsed > TimeSpan.FromSeconds(20))
                    throw new TimeoutException("Both MCP refresh processes did not load the initial token.");
                await Task.Delay(25);
            }
            releaseFirst.TrySetResult();

            foreach (var child in children)
            {
                var stdout = child.StandardOutput.ReadToEndAsync();
                var stderr = child.StandardError.ReadToEndAsync();
                await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(25));
                Assert.True(child.ExitCode == 0,
                    "Child refresh process failed. stdout: " + await stdout + " stderr: " + await stderr);
            }
            stopServer.TrySetResult();
            await server.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(1, refreshRequests);
            Assert.Equal("refresh-2", (await new McpTokenCache(root).ForServer("test", serverUrl)
                .GetTokensAsync(default))?.RefreshToken);
        }
        finally
        {
            releaseFirst.TrySetResult();
            stopServer.TrySetResult();
            listener.Close();
            foreach (var child in children)
                try
                {
                    if (!child.HasExited)
                    {
                        child.Kill(entireProcessTree: true);
                        child.WaitForExit(5000);
                    }
                    child.Dispose();
                }
                catch (InvalidOperationException) { }
            try { await server; }
            catch (Exception error) when (error is HttpListenerException or ObjectDisposedException) { }
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task McpOAuthRefreshProcessProbe()
    {
        var agentDirectory = Environment.GetEnvironmentVariable("PISHARP_MCP_REFRESH_TEST_AGENT_DIR");
        if (string.IsNullOrEmpty(agentDirectory)) return;
        var serverUrl = new Uri(Environment.GetEnvironmentVariable("PISHARP_MCP_REFRESH_TEST_SERVER_URL")!);
        var endpoint = new Uri(Environment.GetEnvironmentVariable("PISHARP_MCP_REFRESH_TEST_ENDPOINT")!);
        var readyFile = Environment.GetEnvironmentVariable("PISHARP_MCP_REFRESH_TEST_READY_FILE")!;
        var tokenCache = new McpTokenCache(agentDirectory).ForServerWithRefresh("test", serverUrl);
        Assert.Equal("access-1", (await tokenCache.GetTokensAsync(default))?.AccessToken);
        await File.WriteAllTextAsync(readyFile, "ready");
        using var http = new HttpClient(new McpOAuthRefreshHandler(tokenCache));
        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "refresh_token",
                ["refresh_token"] = "refresh-1"
            })
        };
        using var response = await http.SendAsync(request).WaitAsync(TimeSpan.FromSeconds(20));
        response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = document.RootElement;
        await tokenCache.StoreTokensAsync(new TokenContainer
        {
            TokenType = root.GetProperty("token_type").GetString()!,
            AccessToken = root.GetProperty("access_token").GetString()!,
            RefreshToken = root.GetProperty("refresh_token").GetString(),
            ExpiresIn = root.GetProperty("expires_in").GetInt32(),
            ObtainedAt = DateTimeOffset.UtcNow
        }, default);
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
                await cache.ForServer(url == first ? "first" : "second", url).StoreTokensAsync(new TokenContainer
                {
                    TokenType = "Bearer",
                    AccessToken = url.AbsolutePath,
                    ObtainedAt = DateTimeOffset.UtcNow
                }, default);
            var output = new StringWriter();
            var errors = new StringWriter();
            Assert.Equal(0, await McpCommand.RunAsync(["logout", "first"], root, root, output, errors));
            Assert.Contains("Signed out", output.ToString());
            Assert.Null(await cache.ForServer("first", first).GetTokensAsync(default));
            Assert.NotNull(await cache.ForServer("second", second).GetTokensAsync(default));
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

    [Fact]
    public async Task OAuthClientNameRejectsEmptyValuesWithoutLeakingSecrets()
    {
        var root = Path.Combine(Path.GetTempPath(), "pisharp-mcp-oauth-config-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            await File.WriteAllTextAsync(Path.Combine(root, "mcp.json"), """
                {"mcpServers":{"invalid":{"url":"https://example.test/mcp","oauth":{
                  "clientName":"   ","clientSecret":"sentinel-oauth-secret"}}}}
                """);
            var configuration = await McpConfiguration.LoadAsync(root, root, false);
            Assert.Empty(configuration.Servers);
            var error = Assert.Single(configuration.Errors);
            Assert.Contains("oauth.clientName must be a non-empty string", error);
            Assert.DoesNotContain("sentinel-oauth-secret", error);

            var wrongType = JsonSerializer.SerializeToElement(new
            {
                url = "https://example.test/mcp",
                oauth = new { clientName = 42, clientSecret = "sentinel-oauth-secret" }
            });
            var typeError = Assert.Throws<ArgumentException>(() =>
                McpConfiguration.Parse("invalid", wrongType, "mcp.json", "global"));
            Assert.Contains("oauth.clientName must be a string", typeError.Message);
            Assert.DoesNotContain("sentinel-oauth-secret", typeError.Message);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static HttpRequestMessage RefreshRequest() => new(HttpMethod.Post,
        new Uri("https://auth.example.test/token"))
    {
        Content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = "refresh-1"
        })
    };

    private sealed class StaticTokenEndpoint(string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                RequestMessage = request,
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            });
    }

    private sealed class RotatingTokenEndpoint : HttpMessageHandler
    {
        public TaskCompletionSource FirstRequest { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ContinueFirstRequest { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int RefreshRequests => Volatile.Read(ref _refreshRequests);
        private int _refreshRequests;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref _refreshRequests) == 1)
            {
                FirstRequest.TrySetResult();
                await ContinueFirstRequest.Task.WaitAsync(cancellationToken);
            }
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                RequestMessage = request,
                Content = new StringContent(
                    "{\"access_token\":\"access-2\",\"refresh_token\":\"refresh-2\",\"token_type\":\"Bearer\",\"expires_in\":3600}",
                    Encoding.UTF8, "application/json")
            };
        }
    }
}
