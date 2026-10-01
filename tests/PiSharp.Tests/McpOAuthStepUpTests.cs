using System.Net;
using System.Text.Json;
using System.Web;
using ModelContextProtocol.Authentication;
using ModelContextProtocol.Client;
using PiSharp.Runtime.Mcp;

namespace PiSharp.Tests;

[Collection("MCP OAuth")]
public sealed class McpOAuthStepUpTests
{
    [Fact]
    public async Task InsufficientScopeAuthorizationKeepsTheExistingGrant()
    {
        var issuer = new Uri("http://127.0.0.1:45671");
        var serverUrl = new Uri(issuer, "/mcp");
        var redirect = new Uri("http://127.0.0.1:45672/callback");
        var root = Path.Combine(Path.GetTempPath(), "pisharp-mcp-oauth-step-up-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var cache = new McpTokenCache(root).ForServerWithRefresh(serverUrl);
            await cache.StoreTokensAsync(new TokenContainer
            {
                AccessToken = "old-access",
                RefreshToken = "old-refresh",
                TokenType = "Bearer",
                ExpiresIn = 3600,
                ObtainedAt = DateTimeOffset.UtcNow,
                Scope = "repo:read",
                ClientId = "fixture-client",
                AuthorizationServer = issuer.AbsoluteUri
            }, CancellationToken.None);
            using var handler = new OAuthFixtureHandler(serverUrl, issuer);
            using var http = new HttpClient(handler);
            var authorizationUris = new List<Uri>();
            var settings = new McpOAuthSettings("fixture-client", null, null, ["repo:read"]);
            var oauth = settings.CreateOptions(serverUrl, cache, redirect, (context, _) =>
            {
                authorizationUris.Add(context.AuthorizationUri);
                var state = HttpUtility.ParseQueryString(context.AuthorizationUri.Query)["state"];
                return Task.FromResult<AuthorizationResult?>(new AuthorizationResult
                {
                    Code = "fixture-code",
                    State = state
                });
            });
            var transportOptions = new HttpClientTransportOptions
            {
                Name = "oauth-step-up-fixture",
                Endpoint = serverUrl,
                TransportMode = HttpTransportMode.StreamableHttp,
                OAuth = oauth
            };
            await using var transport = new HttpClientTransport(transportOptions, http,
                loggerFactory: null, ownsHttpClient: false);

            await using var _ = await McpClient.CreateAsync(transport, new McpClientOptions(), loggerFactory: null,
                CancellationToken.None);

            var authorization = Assert.Single(authorizationUris);
            var query = HttpUtility.ParseQueryString(authorization.Query);
            Assert.Equal("repo:read issues:write", query["scope"]);
            Assert.Equal("repo:read issues:write", (await cache.GetTokensAsync(CancellationToken.None))?.Scope);
            Assert.Equal(1, handler.TokenRequests);
            Assert.Equal(1, handler.UnauthorizedMcpRequests);
            Assert.True(handler.McpRequests > handler.UnauthorizedMcpRequests);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private sealed class OAuthFixtureHandler(Uri serverUrl, Uri issuer) : HttpMessageHandler
    {
        public int TokenRequests { get; private set; }
        public int McpRequests { get; private set; }
        public int UnauthorizedMcpRequests { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var uri = request.RequestUri ?? throw new InvalidOperationException("OAuth fixture request has no URL.");
            if (uri.AbsolutePath == "/.well-known/oauth-protected-resource")
                return JsonResponse(new
                {
                    resource = serverUrl.AbsoluteUri,
                    authorization_servers = new[] { issuer.AbsoluteUri },
                    scopes_supported = new[] { "repo:read", "issues:write" }
                }, request);
            if (uri.AbsolutePath == "/.well-known/oauth-authorization-server")
                return JsonResponse(new
                {
                    issuer = issuer.AbsoluteUri,
                    authorization_endpoint = new Uri(issuer, "/authorize").AbsoluteUri,
                    token_endpoint = new Uri(issuer, "/token").AbsoluteUri,
                    response_types_supported = new[] { "code" },
                    code_challenge_methods_supported = new[] { "S256" },
                    token_endpoint_auth_methods_supported = new[] { "none" }
                }, request);
            if (uri.AbsolutePath == "/token")
            {
                TokenRequests++;
                return JsonResponse(new
                {
                    access_token = "new-access",
                    refresh_token = "new-refresh",
                    token_type = "Bearer",
                    expires_in = 3600,
                    scope = "repo:read issues:write"
                }, request);
            }
            if (uri.AbsolutePath != serverUrl.AbsolutePath)
                return new HttpResponseMessage(HttpStatusCode.NotFound) { RequestMessage = request };

            McpRequests++;
            if (request.Headers.Authorization?.Parameter != "new-access")
            {
                UnauthorizedMcpRequests++;
                var challenge = new HttpResponseMessage(HttpStatusCode.Forbidden) { RequestMessage = request };
                challenge.Headers.TryAddWithoutValidation("WWW-Authenticate",
                    "Bearer error=\"insufficient_scope\", scope=\"issues:write\", resource_metadata=\"" +
                    new Uri(issuer, "/.well-known/oauth-protected-resource").AbsoluteUri + "\"");
                return challenge;
            }

            using var body = await JsonDocument.ParseAsync(await request.Content!.ReadAsStreamAsync(cancellationToken),
                cancellationToken: cancellationToken);
            var message = body.RootElement;
            if (message.TryGetProperty("id", out var id))
            {
                var method = message.GetProperty("method").GetString();
                object result = method switch
                {
                    "server/discover" => new
                    {
                        supportedVersions = new[] { "2025-11-25" },
                        capabilities = new { }
                    },
                    "initialize" => new
                    {
                        protocolVersion = message.GetProperty("params").GetProperty("protocolVersion").GetString(),
                        capabilities = new { },
                        serverInfo = new { name = "oauth-fixture", version = "1.0" }
                    },
                    _ => new { }
                };
                return JsonResponse(new { jsonrpc = "2.0", id = id.Clone(), result }, request);
            }
            return new HttpResponseMessage(HttpStatusCode.Accepted) { RequestMessage = request };
        }

        private static HttpResponseMessage JsonResponse<T>(T value, HttpRequestMessage request) =>
            new(HttpStatusCode.OK)
            {
                RequestMessage = request,
                Content = new StringContent(JsonSerializer.Serialize(value), System.Text.Encoding.UTF8,
                    "application/json")
            };
    }
}
