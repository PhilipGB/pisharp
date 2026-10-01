using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using PiSharp.Runtime.Mcp;

namespace PiSharp.Tests;

[Collection("MCP OAuth")]
public sealed class McpOAuthMetadataTests
{
    [Theory]
    [InlineData("https://idp.example/oauth-metadata")]
    [InlineData("http://localhost:8080/oauth-metadata")]
    [InlineData("http://127.0.0.1:8080/oauth-metadata")]
    [InlineData("http://[::1]:8080/oauth-metadata")]
    public void ParseAcceptsHttpsOrLoopbackAuthorizationServerMetadata(string metadataUrl)
    {
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(new
        {
            authServerMetadataUrl = metadataUrl
        }));

        var settings = McpOAuthSettings.Parse(document.RootElement);

        Assert.Equal(new Uri(metadataUrl), settings.AuthServerMetadataUrl);
    }

    [Theory]
    [InlineData("http://idp.example/oauth-metadata")]
    [InlineData("file:///tmp/oauth-metadata.json")]
    [InlineData("not a URL")]
    public void ParseRejectsInsecureOrInvalidAuthorizationServerMetadata(string metadataUrl)
    {
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(new
        {
            authServerMetadataUrl = metadataUrl
        }));

        var error = Assert.Throws<ArgumentException>(() => McpOAuthSettings.Parse(document.RootElement));

        Assert.Contains("authServerMetadataUrl", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("https://idp.example", true)]
    [InlineData("https://attacker.example", false)]
    [InlineData(null, false)]
    public async Task ConfiguredMetadataReplacesDiscoveryAndValidatesIssuerBeforeExchange(
        string? callbackIssuer, bool shouldExchange)
    {
        var root = Path.Combine(Path.GetTempPath(), "pisharp-mcp-oauth-metadata-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        await using var server = new OAuthServer();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        try
        {
            using var config = JsonDocument.Parse(JsonSerializer.Serialize(new
            {
                url = server.Origin + "/mcp",
                oauth = new
                {
                    clientId = "fixture-client",
                    authServerMetadataUrl = server.Origin + "/idp/metadata"
                }
            }));
            var entry = McpConfiguration.Parse("protected", config.RootElement, "mcp.json", "global");
            var output = new LoginOutput();
            var login = McpOAuthLogin.SignInAsync(entry, root, output, false,
                TimeSpan.FromSeconds(10), deadline.Token);
            var authorizationTask = output.Authorization.Task;
            if (await Task.WhenAny(authorizationTask, login) == login) await login;
            var authorizationUri = new Uri(await authorizationTask.WaitAsync(deadline.Token));
            var plainOutput = output.ToString();
            Assert.Contains(authorizationUri.AbsoluteUri, plainOutput, StringComparison.Ordinal);
            Assert.DoesNotContain("\u001b]8;;", plainOutput, StringComparison.Ordinal);
            var query = System.Web.HttpUtility.ParseQueryString(authorizationUri.Query);

            Assert.Equal("/idp/authorize", authorizationUri.AbsolutePath);
            var callbackQuery = "code=fixture-code&state=" + Uri.EscapeDataString(query["state"]!);
            if (callbackIssuer is not null)
                callbackQuery += "&iss=" + Uri.EscapeDataString(callbackIssuer);
            var callbackUri = new UriBuilder(new Uri(query["redirect_uri"]!)) { Query = callbackQuery }.Uri;
            using var browser = new HttpClient();
            Assert.Equal(HttpStatusCode.OK, (await browser.GetAsync(callbackUri, deadline.Token)).StatusCode);

            var failure = await Record.ExceptionAsync(() => login);
            if (shouldExchange)
                Assert.Null(failure);
            else
            {
                Assert.NotNull(failure);
                if (callbackIssuer is not null)
                    Assert.Contains("issuer", failure.Message, StringComparison.OrdinalIgnoreCase);
            }

            Assert.Equal(0, server.NormalDiscoveryRequests);
            Assert.True(server.ConfiguredMetadataRequests > 0);
            Assert.Equal(shouldExchange ? 1 : 0, server.TokenRequests);
        }
        finally
        {
            deadline.Cancel();
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task InvalidAuthorizationServerUrlFallsBackToTheMcpServerOrigin()
    {
        var root = Path.Combine(Path.GetTempPath(), "pisharp-mcp-oauth-resource-fallback-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        await using var server = new OAuthServer(invalidAuthorizationServerUrl: true);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        try
        {
            using var config = JsonDocument.Parse(JsonSerializer.Serialize(new
            {
                url = server.Origin + "/mcp",
                oauth = new { clientId = "fixture-client" }
            }));
            var entry = McpConfiguration.Parse("protected", config.RootElement, "mcp.json", "global");
            var output = new LoginOutput();
            var login = McpOAuthLogin.SignInAsync(entry, root, output, false,
                TimeSpan.FromSeconds(10), deadline.Token);
            var authorizationTask = output.Authorization.Task;
            if (await Task.WhenAny(authorizationTask, login) == login) await login;
            var authorizationUri = new Uri(await authorizationTask.WaitAsync(deadline.Token));
            var query = System.Web.HttpUtility.ParseQueryString(authorizationUri.Query);
            Assert.Equal(server.Origin + "/authorize", authorizationUri.GetLeftPart(UriPartial.Path));

            var callbackQuery = "code=fixture-code&state=" + Uri.EscapeDataString(query["state"]!) +
                "&iss=" + Uri.EscapeDataString(server.Origin);
            var callbackUri = new UriBuilder(new Uri(query["redirect_uri"]!)) { Query = callbackQuery }.Uri;
            using var browser = new HttpClient();
            Assert.Equal(HttpStatusCode.OK, (await browser.GetAsync(callbackUri, deadline.Token)).StatusCode);
            await login.WaitAsync(deadline.Token);

            Assert.True(server.NormalDiscoveryRequests > 0);
            Assert.Equal(1, server.TokenRequests);
        }
        finally
        {
            deadline.Cancel();
            Directory.Delete(root, recursive: true);
        }
    }

    private sealed class LoginOutput : StringWriter
    {
        public TaskCompletionSource<string> Authorization { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override Task WriteLineAsync(string? value)
        {
            if (value?.Contains("http://", StringComparison.Ordinal) == true ||
                value?.Contains("https://", StringComparison.Ordinal) == true)
                Authorization.TrySetResult(value.Split('\n').Last());
            return base.WriteLineAsync(value);
        }
    }

    private sealed class OAuthServer : IAsyncDisposable
    {
        private readonly HttpListener _listener = new();
        private readonly CancellationTokenSource _shutdown = new(TimeSpan.FromSeconds(20));
        private readonly Task _serve;
        private readonly bool _invalidAuthorizationServerUrl;
        private int _configuredMetadataRequests;
        private int _normalDiscoveryRequests;
        private int _tokenRequests;

        public string Origin { get; }
        public int ConfiguredMetadataRequests => Volatile.Read(ref _configuredMetadataRequests);
        public int NormalDiscoveryRequests => Volatile.Read(ref _normalDiscoveryRequests);
        public int TokenRequests => Volatile.Read(ref _tokenRequests);

        public OAuthServer(bool invalidAuthorizationServerUrl = false)
        {
            _invalidAuthorizationServerUrl = invalidAuthorizationServerUrl;
            using var reservation = new TcpListener(IPAddress.Loopback, 0);
            reservation.Start();
            var port = ((IPEndPoint)reservation.LocalEndpoint).Port;
            reservation.Stop();
            Origin = "http://127.0.0.1:" + port;
            _listener.Prefixes.Add(Origin + "/");
            _listener.Start();
            _serve = Task.Run(ServeAsync);
        }

        private async Task ServeAsync()
        {
            while (!_shutdown.IsCancellationRequested)
            {
                HttpListenerContext context;
                try { context = await _listener.GetContextAsync().WaitAsync(_shutdown.Token); }
                catch (Exception error) when (error is OperationCanceledException or HttpListenerException or
                    ObjectDisposedException)
                { return; }

                var response = context.Response;
                response.ContentType = "application/json";
                string body;
                switch (context.Request.Url!.AbsolutePath)
                {
                    case "/.well-known/oauth-protected-resource/mcp":
                        body = JsonSerializer.Serialize(new
                        {
                            resource = Origin + "/mcp",
                            authorization_servers = new[]
                            {
                                _invalidAuthorizationServerUrl ? "not a URL" : Origin
                            }
                        });
                        break;
                    case "/.well-known/oauth-authorization-server":
                        Interlocked.Increment(ref _normalDiscoveryRequests);
                        body = AuthorizationMetadata(Origin, "/authorize", "/token");
                        break;
                    case "/idp/metadata":
                        Interlocked.Increment(ref _configuredMetadataRequests);
                        body = AuthorizationMetadata("https://idp.example", "/idp/authorize", "/idp/token");
                        break;
                    case "/idp/token":
                    case "/token":
                        Interlocked.Increment(ref _tokenRequests);
                        body = """{"access_token":"fixture-access","refresh_token":"fixture-refresh","token_type":"Bearer","expires_in":3600}""";
                        break;
                    case "/mcp":
                        if (!string.Equals(context.Request.Headers["Authorization"], "Bearer fixture-access",
                                StringComparison.Ordinal))
                        {
                            response.StatusCode = (int)HttpStatusCode.Unauthorized;
                            response.AddHeader("WWW-Authenticate", "Bearer resource_metadata=\"" + Origin +
                                "/.well-known/oauth-protected-resource/mcp\"");
                            body = "{}";
                            break;
                        }
                        using (var input = new StreamReader(context.Request.InputStream, context.Request.ContentEncoding))
                        using (var message = JsonDocument.Parse(await input.ReadToEndAsync(_shutdown.Token)))
                        {
                            if (!message.RootElement.TryGetProperty("id", out var id))
                            {
                                response.StatusCode = 202;
                                body = "";
                                break;
                            }
                            var method = message.RootElement.GetProperty("method").GetString();
                            object result = method switch
                            {
                                "server/discover" => new
                                {
                                    supportedVersions = new[] { "2026-07-28" },
                                    capabilities = new { }
                                },
                                "initialize" => new
                                {
                                    protocolVersion = message.RootElement.GetProperty("params")
                                        .GetProperty("protocolVersion").GetString(),
                                    capabilities = new { },
                                    serverInfo = new { name = "oauth-metadata-fixture", version = "1.0" }
                                },
                                "tools/list" => new { tools = Array.Empty<object>() },
                                _ => new { }
                            };
                            body = JsonSerializer.Serialize(new
                            {
                                jsonrpc = "2.0",
                                id = id.Clone(),
                                result
                            });
                        }
                        break;
                    default:
                        response.StatusCode = (int)HttpStatusCode.NotFound;
                        body = "{}";
                        break;
                }

                var bytes = Encoding.UTF8.GetBytes(body);
                response.ContentLength64 = bytes.Length;
                await response.OutputStream.WriteAsync(bytes, _shutdown.Token);
                response.Close();
            }
        }

        private string AuthorizationMetadata(string issuer, string authorizePath, string tokenPath) =>
            JsonSerializer.Serialize(new
            {
                issuer,
                authorization_endpoint = Origin + authorizePath,
                token_endpoint = Origin + tokenPath,
                response_types_supported = new[] { "code" },
                token_endpoint_auth_methods_supported = new[] { "none" },
                code_challenge_methods_supported = new[] { "S256" },
                authorization_response_iss_parameter_supported = true
            });

        public async ValueTask DisposeAsync()
        {
            _shutdown.Cancel();
            _listener.Close();
            try { await _serve; }
            catch (Exception error) when (error is OperationCanceledException or HttpListenerException or IOException) { }
            _shutdown.Dispose();
        }
    }
}
