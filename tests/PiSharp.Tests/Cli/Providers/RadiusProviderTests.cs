using System.Collections.Specialized;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PiSharp.Cli;
using PiSharp.Runtime.Providers;
using Microsoft.Extensions.AI;

namespace PiSharp.Tests;

public sealed class RadiusProviderTests
{
    [Fact]
    public void BuiltinProfileUsesCurrentPiCatalogAndCustomGatewayStartsWithoutPublicModels()
    {
        var profile = BuiltinProviderProfiles.Create(_ => null)["radius"];

        Assert.Equal("pi-messages", profile.Api);
        Assert.True(profile.OAuthSupported);
        Assert.Equal("RADIUS_API_KEY", profile.ApiKeyEnvironment);
        Assert.Equal(25, profile.Models.Count);
        var balanced = Assert.Single(profile.Models, model => model.Id == "balanced");
        Assert.Equal("pi-messages", balanced.Api);
        Assert.Equal("https://radius.pi.dev/v1", balanced.BaseUrl);
        Assert.Equal(1048576, balanced.ContextLength);
        Assert.Contains("image", balanced.Input!);

        var custom = BuiltinProviderProfiles.Create(name => name == "PISHARP_RADIUS_GATEWAY"
            ? "http://radius.internal:8788" : null)["radius"];
        Assert.Equal("http://radius.internal:8788/", custom.Endpoint.ToString());
        Assert.Empty(custom.Models);

        var pathGateway = BuiltinProviderProfiles.Create(name => name == "PISHARP_RADIUS_GATEWAY"
            ? "https://radius.pi.dev/development" : null)["radius"];
        Assert.Empty(pathGateway.Models);
    }

    [Fact]
    public async Task ConfiguredRadiusGatewayPreservesPiMessagesAndOAuthProfile()
    {
        var root = CreateRoot("radius-configured-gateway");
        try
        {
            await File.WriteAllTextAsync(Path.Combine(root, "models.json"), """
                {"providers":{"radius":{"baseUrl":"http://radius.internal:8788","models":[{"id":"custom-model"}]}}}
                """);
            using var http = new HttpClient(new DelegateHandler((_, _) =>
                Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound))));
            var runtime = await ProviderModelRuntime.CreateAsync(root, false,
                name => name == "PISHARP_RADIUS_GATEWAY" ? "http://radius.internal:8788" : null, http);

            var profile = runtime.GetProvider("radius");
            var selection = await runtime.ResolveAsync("radius", "custom-model");

            Assert.Equal("http://radius.internal:8788/", profile.Endpoint.ToString());
            Assert.True(profile.OAuthSupported);
            Assert.Equal("RADIUS_API_KEY", profile.ApiKeyEnvironment);
            Assert.Equal("pi-messages", ProviderChatClientFactory.ResolveProtocol(selection));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task GatewayCatalogUsesProviderCredentialAndMapsThePiMessagesModel()
    {
        var root = CreateRoot("radius-catalog");
        string? authorization = null;
        string? requestUri = null;
        using var http = new HttpClient(new DelegateHandler((request, _) =>
        {
            authorization = request.Headers.Authorization?.ToString();
            requestUri = request.RequestUri?.ToString();
            return Task.FromResult(JsonResponse(GatewayConfig("balanced", "Fresh Balanced")));
        }));
        try
        {
            var runtime = await ProviderModelRuntime.CreateAsync(root, false,
                name => name switch
                {
                    "PISHARP_RADIUS_GATEWAY" => "http://radius.test",
                    "RADIUS_API_KEY" => "radius-api-key",
                    _ => null
                }, http);

            var models = await runtime.ListModelsAsync("radius");

            var model = Assert.Single(models);
            Assert.Equal("balanced", model.Id);
            Assert.Equal("Fresh Balanced", model.Name);
            Assert.Equal(424242, model.ContextLength);
            Assert.Equal(32000, model.MaxOutputTokens);
            Assert.Equal("pi-messages", model.Api);
            Assert.Equal("http://models.radius.test/v1", model.BaseUrl);
            Assert.Equal("http://radius.test/v1/config", requestUri);
            Assert.Equal("Bearer radius-api-key", authorization);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task DeviceLoginPersistsTokensAndPiMessagesResolvesCurrentBearerAtRequestTime()
    {
        var root = CreateRoot("radius-oauth-device");
        var gateway = new Uri("http://radius.test");
        string? catalogAuthorization = null;
        string? providerAuthorization = null;
        var requests = new List<(string Url, string? Body)>();
        using var oauthHttp = new HttpClient(new DelegateHandler(async (request, cancellationToken) =>
        {
            var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            requests.Add((request.RequestUri!.ToString(), body));
            if (request.RequestUri.AbsolutePath == "/v1/oauth/device")
                return JsonResponse("""{"device_code":"device-1","user_code":"ABCD-1234","verification_uri":"https://radius-ui.test/pair","expires_in":600,"interval":0}""");
            if (request.RequestUri.AbsolutePath == "/v1/oauth/token")
            {
                var form = ParseForm(body!);
                Assert.Equal("pi-gateway", form["client_id"]);
                if (form["grant_type"] == "urn:ietf:params:oauth:grant-type:device_code")
                {
                    Assert.Equal("device-1", form["device_code"]);
                    return JsonResponse(TokenBody("device-access", "device-refresh", 3600));
                }
                if (form["grant_type"] == "refresh_token")
                {
                    Assert.Equal("device-refresh", form["refresh_token"]);
                    return JsonResponse(TokenBody("refreshed-access", "refreshed-refresh", 3600));
                }
            }
            if (request.RequestUri.AbsolutePath == "/v1/config")
            {
                catalogAuthorization = request.Headers.Authorization?.ToString();
                return JsonResponse(GatewayConfig("balanced", "Balanced"));
            }
            throw new InvalidOperationException($"Unexpected Radius request {request.RequestUri}");
        }));
        try
        {
            var env = new Dictionary<string, string?>
            {
                ["PISHARP_RADIUS_GATEWAY"] = gateway.ToString(),
                ["RADIUS_API_KEY"] = "ambient-key"
            };
            var runtime = await ProviderModelRuntime.CreateAsync(root, false,
                name => env.GetValueOrDefault(name), oauthHttp);
            var interaction = new RecordingInteraction("device-code");

            await runtime.LoginOAuthAsync("radius", interaction);

            var storage = new AuthStorage(Path.Combine(root, "auth.json"));
            var stored = await storage.ReadAsync("radius");
            Assert.Equal("device-access", stored?.Access);
            Assert.Equal("device-refresh", stored?.Refresh);
            Assert.Equal(gateway.GetLeftPart(UriPartial.Authority), stored?.AccountId);
            Assert.Contains(interaction.Notices, notice => notice.Kind == "device_code" &&
                notice.UserCode == "ABCD-1234" && notice.Url?.ToString() == "https://radius-ui.test/pair");
            var loginForm = ParseForm(requests[0].Body!);
            Assert.Equal("pi-gateway", loginForm["client_id"]);
            Assert.Equal("gateway offline_access", loginForm["scope"]);

            var selection = await runtime.ResolveAsync("radius", "balanced");
            Assert.True(selection.Authenticated);
            Assert.Equal("device-access", selection.ApiKey);
            Assert.Equal("stored OAuth", selection.AuthSource);
            Assert.Equal("Bearer device-access", catalogAuthorization);
            Assert.NotNull(selection.OAuthCredentialResolver);

            await storage.StoreOAuthAsync("radius", "rotated-access", "rotated-refresh",
                DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeMilliseconds(), gateway.GetLeftPart(UriPartial.Authority));
            using var client = PiMessagesChatClientFactory.Create(selection,
                new DelegateHandler((request, _) =>
                {
                    providerAuthorization = request.Headers.Authorization?.ToString();
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized)
                    {
                        Content = new StringContent("rotated-access was rejected")
                    });
                }));
            var updates = new List<ChatResponseUpdate>();
            await foreach (var update in client.GetStreamingResponseAsync([new ChatMessage(ChatRole.User, "hello")]))
                updates.Add(update);

            Assert.Equal("Bearer rotated-access", providerAuthorization);
            var error = Assert.Single(updates.SelectMany(update => update.Contents).OfType<ErrorContent>());
            Assert.DoesNotContain("rotated-access", error.Message, StringComparison.Ordinal);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task BrowserLoginUsesDiscoveredEndpointPkceStateAndGatewayTokenExchange()
    {
        var callbackFactory = new FakeCallbackServerFactory("browser-code");
        Uri? opened = null;
        NameValueCollection? exchange = null;
        using var http = new HttpClient(new DelegateHandler(async (request, cancellationToken) =>
        {
            if (request.RequestUri!.AbsolutePath == "/v1/oauth")
                return JsonResponse("""{"authorizationEndpoint":"https://radius-ui.test/authorize"}""");
            Assert.Equal("/v1/oauth/token", request.RequestUri.AbsolutePath);
            exchange = ParseForm(await request.Content!.ReadAsStringAsync(cancellationToken));
            return JsonResponse(TokenBody("browser-access", "browser-refresh", 3600));
        }));
        var adapter = new RadiusOAuthAdapter(http, new Uri("https://radius.test"), callbackFactory, uri => opened = uri);
        var interaction = new RecordingInteraction("browser");

        var credential = await adapter.LoginAsync("browser", interaction, CancellationToken.None);

        Assert.True(adapter.IsValidCredential(credential, out _));
        Assert.Equal("https://radius.test", credential.AccountId);
        Assert.Equal("https://radius-ui.test/authorize", opened?.GetLeftPart(UriPartial.Path));
        Assert.Equal(opened, interaction.Notices.Single(notice => notice.Kind == "auth_url").Url);
        Assert.Equal("http://127.0.0.1:1456/oauth/callback", callbackFactory.RedirectUri?.ToString());
        Assert.Equal(callbackFactory.State, callbackFactory.ExpectedState);
        var query = ParseQuery(opened!);
        Assert.Equal("code", query["response_type"]);
        Assert.Equal("pi-gateway", query["client_id"]);
        Assert.Equal("gateway offline_access", query["scope"]);
        Assert.Equal("S256", query["code_challenge_method"]);
        Assert.Equal("url", query["handoff"]);
        Assert.Equal(callbackFactory.State, query["state"]);
        Assert.Equal(43, query["code_challenge"]!.Length);
        Assert.Equal("authorization_code", exchange?["grant_type"]);
        Assert.Equal("browser-code", exchange?["code"]);
        Assert.Equal(query["redirect_uri"], exchange?["redirect_uri"]);
        Assert.Equal(query["code_challenge"], Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(exchange!["code_verifier"]!))));
    }

    [Fact]
    public async Task ConcurrentRadiusRequestsRefreshOnceAndStoredOAuthOwnsCredentialPrecedence()
    {
        var root = CreateRoot("radius-oauth-refresh");
        var gateway = new Uri("http://radius.test");
        var refreshCount = 0;
        using var http = new HttpClient(new DelegateHandler(async (request, cancellationToken) =>
        {
            if (request.RequestUri!.AbsolutePath != "/v1/oauth/token")
                throw new InvalidOperationException($"Unexpected Radius request {request.RequestUri}");
            var form = ParseForm(await request.Content!.ReadAsStringAsync(cancellationToken));
            Assert.Equal("refresh_token", form["grant_type"]);
            await Task.Delay(50, cancellationToken);
            Interlocked.Increment(ref refreshCount);
            return JsonResponse(TokenBody("new-access", "new-refresh", 3600));
        }));
        try
        {
            var storage = new AuthStorage(Path.Combine(root, "auth.json"));
            await storage.StoreOAuthAsync("radius", "old-access", "old-refresh",
                DateTimeOffset.UtcNow.AddMinutes(1).ToUnixTimeMilliseconds(), gateway.GetLeftPart(UriPartial.Authority));
            var runtime = await ProviderModelRuntime.CreateAsync(root, false, name => name switch
            {
                "PISHARP_RADIUS_GATEWAY" => gateway.ToString(),
                "RADIUS_API_KEY" => "ambient-api-key",
                _ => null
            }, http);

            var resolutions = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ =>
                runtime.ResolveAuthAsync("radius")));

            Assert.All(resolutions, resolved =>
            {
                Assert.True(resolved.Authenticated);
                Assert.Equal("new-access", resolved.Key);
            });
            Assert.Equal(1, refreshCount);
            Assert.Equal("new-access", await runtime.GetApiKeyForProviderAsync("radius"));
            Assert.Equal(1, refreshCount);
            var persisted = await storage.ReadAsync("radius");
            Assert.Equal("new-access", persisted?.Access);
            Assert.Equal("new-refresh", persisted?.Refresh);

            await storage.StoreOAuthAsync("radius", "bad-access", null,
                DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeMilliseconds(), gateway.GetLeftPart(UriPartial.Authority));
            var invalid = await runtime.ResolveAuthAsync("radius");
            Assert.False(invalid.Authenticated);
            Assert.NotEqual("ambient-api-key", invalid.Key);
            Assert.Equal(1, refreshCount);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task DeviceLoginCancellationDoesNotPersistRadiusCredentials()
    {
        var root = CreateRoot("radius-oauth-cancel");
        var pollStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var http = new HttpClient(new DelegateHandler((request, _) =>
        {
            if (request.RequestUri!.AbsolutePath == "/v1/oauth/device")
                return Task.FromResult(JsonResponse("""{"device_code":"device-1","user_code":"ABCD-1234","verification_uri":"https://radius-ui.test/pair","expires_in":600,"interval":60}"""));
            if (request.RequestUri.AbsolutePath == "/v1/oauth/token")
            {
                pollStarted.TrySetResult();
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadRequest)
                {
                    Content = new StringContent("""{"error":"authorization_pending"}""")
                });
            }
            throw new InvalidOperationException($"Unexpected Radius request {request.RequestUri}");
        }));
        try
        {
            var runtime = await ProviderModelRuntime.CreateAsync(root, false,
                name => name == "PISHARP_RADIUS_GATEWAY" ? "http://radius.test" : null, http);
            using var cancellation = new CancellationTokenSource();
            var login = runtime.LoginOAuthAsync("radius", new RecordingInteraction("device-code"), cancellation.Token);

            await pollStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            cancellation.Cancel();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => login);
            Assert.Null(await new AuthStorage(Path.Combine(root, "auth.json")).ReadAsync("radius"));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task RadiusCatalogDiscoveryHonorsCancellation()
    {
        var root = CreateRoot("radius-catalog-cancel");
        var requestStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var http = new HttpClient(new DelegateHandler(async (_, cancellationToken) =>
        {
            requestStarted.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return JsonResponse("{}");
        }));
        try
        {
            var runtime = await ProviderModelRuntime.CreateAsync(root, false,
                name => name == "PISHARP_RADIUS_GATEWAY" ? "http://radius.test" : null, http);
            using var cancellation = new CancellationTokenSource();
            var listing = runtime.ListModelsAsync("radius", cancellationToken: cancellation.Token);

            await requestStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            cancellation.Cancel();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => listing);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static string CreateRoot(string suffix)
    {
        var root = Path.Combine(Path.GetTempPath(), $"pisharp-{suffix}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        return root;
    }

    private static string GatewayConfig(string id, string name) => JsonSerializer.Serialize(new
    {
        baseUrl = "http://models.radius.test/v1",
        models = new[]
        {
            new
            {
                id,
                name,
                reasoning = true,
                input = new[] { "text", "image" },
                cost = new { input = 1.0, output = 2.0, cacheRead = 0.1, cacheWrite = 0.0 },
                contextWindow = 424242,
                maxTokens = 32000
            }
        }
    });

    private static string TokenBody(string access, string refresh, int expiresIn) => JsonSerializer.Serialize(new
    {
        access_token = access,
        refresh_token = refresh,
        expires_in = expiresIn,
        scope = "gateway offline_access"
    });

    private static HttpResponseMessage JsonResponse(string body) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/json")
    };

    private static NameValueCollection ParseForm(string value) =>
        System.Web.HttpUtility.ParseQueryString(value);

    private static NameValueCollection ParseQuery(Uri uri) =>
        System.Web.HttpUtility.ParseQueryString(uri.Query);

    private static string Base64Url(byte[] value) =>
        Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private sealed class RecordingInteraction(string method) : IProviderOAuthInteraction
    {
        public List<ProviderOAuthNotice> Notices { get; } = [];

        public Task<string> SelectLoginMethodAsync(IReadOnlyList<ProviderOAuthLoginMethod> methods,
            CancellationToken cancellationToken)
        {
            Assert.Contains(methods, candidate => candidate.Id == method);
            return Task.FromResult(method);
        }

        public void Notify(ProviderOAuthNotice notice) => Notices.Add(notice);
    }

    private sealed class DelegateHandler(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken) => handler(request, cancellationToken);
    }

    private sealed class FakeCallbackServerFactory(string code) : IProviderOAuthCallbackServerFactory
    {
        public Uri? RedirectUri { get; private set; }
        public string? ExpectedState { get; private set; }
        public string? State { get; private set; }

        public IProviderOAuthCallbackServer Start(Uri redirectUri, string expectedState)
        {
            RedirectUri = redirectUri;
            ExpectedState = expectedState;
            State = expectedState;
            return new FakeCallbackServer(code);
        }
    }

    private sealed class FakeCallbackServer(string code) : IProviderOAuthCallbackServer
    {
        public Task<string> WaitForCodeAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(code);
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
