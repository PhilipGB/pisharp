using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PiSharp.Cli;

namespace PiSharp.Tests;

public sealed class OpenAiCodexOAuthTests
{
    [Fact]
    public async Task DeviceCodeLoginPersistsPrivateRefreshableCredentialAndSurvivesRuntimeRestart()
    {
        var root = Path.Combine(Path.GetTempPath(), "pisharp-codex-oauth-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var token = CreateToken("account-device");
        var poll = 0;
        var requests = new List<(string Method, string Url, string? Body)>();
        using var http = new HttpClient(new DelegateHandler(async (request, cancellationToken) =>
        {
            var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            requests.Add((request.Method.Method, request.RequestUri!.ToString(), body));
            if (request.RequestUri.AbsolutePath.EndsWith("/deviceauth/usercode", StringComparison.Ordinal))
                return JsonResponse("""{"device_auth_id":"device-fixture","user_code":"ABCD-1234","interval":"0"}""");
            if (request.RequestUri.AbsolutePath.EndsWith("/deviceauth/token", StringComparison.Ordinal))
                return Interlocked.Increment(ref poll) == 1
                    ? new HttpResponseMessage(HttpStatusCode.Forbidden)
                    : JsonResponse("""{"authorization_code":"device-code","code_verifier":"device-verifier"}""");
            if (request.RequestUri.AbsolutePath == "/oauth/token")
            {
                var form = ParseForm(body!);
                Assert.Equal("authorization_code", form["grant_type"]);
                Assert.Equal("device-code", form["code"]);
                Assert.Equal("device-verifier", form["code_verifier"]);
                Assert.Equal("https://auth.openai.com/deviceauth/callback", form["redirect_uri"]);
                return TokenResponse(token, "refresh-device");
            }
            throw new InvalidOperationException($"Unexpected OAuth URL {request.RequestUri}");
        }));

        try
        {
            var interaction = new RecordingInteraction("device_code");
            var runtime = await ProviderModelRuntime.CreateAsync(root, false, _ => null, http);
            await runtime.LoginOAuthAsync("openai-codex", interaction);

            var stored = await new AuthStorage(Path.Combine(root, "auth.json")).ReadAsync("openai-codex");
            Assert.Equal("oauth", stored?.Type);
            Assert.Equal(token, stored?.Access);
            Assert.Equal("refresh-device", stored?.Refresh);
            Assert.Equal("account-device", stored?.AccountId);
            Assert.True(stored?.Expires > DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            Assert.Contains(interaction.Notices, notice => notice.Kind == "device_code" &&
                notice.UserCode == "ABCD-1234" && notice.Url?.ToString() == "https://auth.openai.com/codex/device");
            Assert.Equal(2, poll);
            Assert.Equal("app_EMoamEEZ73f0CkXaXp7hrann",
                ParseJson(requests[0].Body!)["client_id"]);

            if (OperatingSystem.IsLinux())
                Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite,
                    File.GetUnixFileMode(Path.Combine(root, "auth.json")));

            using var restartedHttp = new HttpClient(new DelegateHandler((_, _) =>
                throw new InvalidOperationException("A fresh OAuth token must not be refreshed.")));
            var restarted = await ProviderModelRuntime.CreateAsync(root, false, _ => null, restartedHttp);
            var selection = await restarted.ResolveAsync("openai-codex", "gpt-5.5");
            Assert.True(selection.Authenticated);
            Assert.Equal(token, selection.ApiKey);
            Assert.Equal("stored OAuth", selection.AuthSource);
            Assert.NotNull(selection.OAuthCredentialResolver);
            Assert.Equal((token, "account-device"),
                await selection.OAuthCredentialResolver!(CancellationToken.None));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task BrowserLoginUsesPkceStateAndThePiCallbackContract()
    {
        var accessToken = CreateToken("account-browser");
        var callbackFactory = new FakeCallbackServerFactory("browser-code");
        Uri? opened = null;
        Dictionary<string, string>? form = null;
        using var http = new HttpClient(new DelegateHandler(async (request, cancellationToken) =>
        {
            Assert.Equal("https://auth.openai.com/oauth/token", request.RequestUri?.ToString());
            form = ParseForm(await request.Content!.ReadAsStringAsync(cancellationToken));
            return TokenResponse(accessToken, "refresh-browser");
        }));
        var adapter = new OpenAiCodexOAuthAdapter(http, callbackFactory, uri => opened = uri);
        var interaction = new RecordingInteraction("browser");

        var credential = await adapter.LoginAsync("browser", interaction, CancellationToken.None);

        Assert.True(adapter.IsValidCredential(credential, out _));
        Assert.Equal("account-browser", credential.AccountId);
        Assert.Equal("http://localhost:1455/auth/callback", callbackFactory.RedirectUri?.ToString());
        Assert.Equal(callbackFactory.State, callbackFactory.ServerState);
        Assert.Equal("https://auth.openai.com/oauth/authorize", opened?.GetLeftPart(UriPartial.Path));
        Assert.Equal(opened, interaction.Notices.Single(notice => notice.Kind == "auth_url").Url);
        var query = ParseQuery(opened!);
        Assert.Equal("code", query["response_type"]);
        Assert.Equal("app_EMoamEEZ73f0CkXaXp7hrann", query["client_id"]);
        Assert.Equal("openid profile email offline_access", query["scope"]);
        Assert.Equal("S256", query["code_challenge_method"]);
        Assert.Equal(callbackFactory.State, query["state"]);
        Assert.Equal(43, query["code_challenge"].Length);
        Assert.Equal("true", query["codex_cli_simplified_flow"]);
        Assert.Equal("pi", query["originator"]);
        Assert.Equal("authorization_code", form?["grant_type"]);
        Assert.Equal("browser-code", form?["code"]);
        Assert.Equal(query["redirect_uri"], form?["redirect_uri"]);
        Assert.Equal("S256", query["code_challenge_method"]);
        Assert.False(form?.ContainsKey("code_challenge"));
        Assert.False(string.IsNullOrWhiteSpace(form?["code_verifier"]));
        Assert.Equal(query["code_challenge"], Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(form!["code_verifier"]))));
    }

    [Fact]
    public async Task ConcurrentCodexRequestsRefreshOnceAndPersistRotatedTokens()
    {
        var root = Path.Combine(Path.GetTempPath(), "pisharp-codex-refresh-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var original = CreateToken("account-refresh");
        var refreshed = CreateToken("account-refresh-new");
        var refreshCount = 0;
        using var http = new HttpClient(new DelegateHandler(async (request, cancellationToken) =>
        {
            Assert.Equal("https://auth.openai.com/oauth/token", request.RequestUri?.ToString());
            Interlocked.Increment(ref refreshCount);
            await Task.Delay(100, cancellationToken);
            return TokenResponse(refreshed, "refresh-rotated");
        }));
        try
        {
            var storage = new AuthStorage(Path.Combine(root, "auth.json"));
            await storage.StoreOAuthAsync("openai-codex", original, "refresh-old",
                DateTimeOffset.UtcNow.AddMinutes(1).ToUnixTimeMilliseconds(), "account-refresh");
            var runtime = await ProviderModelRuntime.CreateAsync(root, false, _ => null, http);

            var selections = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ =>
                runtime.ResolveAsync("openai-codex", "gpt-5.5")));

            Assert.All(selections, selection =>
            {
                Assert.True(selection.Authenticated);
                Assert.Equal(refreshed, selection.ApiKey);
            });
            Assert.Equal(1, refreshCount);
            var stored = await storage.ReadAsync("openai-codex");
            Assert.Equal(refreshed, stored?.Access);
            Assert.Equal("refresh-rotated", stored?.Refresh);
            Assert.Equal("account-refresh-new", stored?.AccountId);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task DeviceCodeLoginCancellationStopsPollingAndDoesNotPersistCredentials()
    {
        var root = Path.Combine(Path.GetTempPath(), "pisharp-codex-oauth-cancel-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var pollStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var http = new HttpClient(new DelegateHandler((request, _) =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/deviceauth/usercode", StringComparison.Ordinal))
                return Task.FromResult(JsonResponse("""{"device_auth_id":"device-fixture","user_code":"ABCD-1234","interval":"60"}"""));
            if (request.RequestUri.AbsolutePath.EndsWith("/deviceauth/token", StringComparison.Ordinal))
            {
                pollStarted.TrySetResult();
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Forbidden));
            }
            throw new InvalidOperationException("Cancelled device flow must not exchange a token.");
        }));
        try
        {
            var runtime = await ProviderModelRuntime.CreateAsync(root, false, _ => null, http);
            using var cancellation = new CancellationTokenSource();
            var login = runtime.LoginOAuthAsync("openai-codex", new RecordingInteraction("device_code"), cancellation.Token);
            await pollStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => login);
            Assert.Null(await new AuthStorage(Path.Combine(root, "auth.json")).ReadAsync("openai-codex"));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task InvalidOrUnrefreshableStoredOAuthNeverFallsBackToOtherCredentials()
    {
        var root = Path.Combine(Path.GetTempPath(), "pisharp-codex-invalid-oauth-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var calls = 0;
        using var http = new HttpClient(new DelegateHandler((_, _) =>
        {
            Interlocked.Increment(ref calls);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized));
        }));
        try
        {
            var storage = new AuthStorage(Path.Combine(root, "auth.json"));
            await storage.StoreOAuthAsync("openai-codex", "malformed-token", "refresh-invalid",
                DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeMilliseconds(), "account-fixture");
            var runtime = await ProviderModelRuntime.CreateAsync(root, false,
                name => name == "OPENAI_API_KEY" ? "ambient-secret" : null, http);
            var invalid = await runtime.ResolveAsync("openai-codex", "gpt-5.5");
            Assert.False(invalid.Authenticated);
            Assert.Contains("invalid", invalid.AuthSource, StringComparison.OrdinalIgnoreCase);
            Assert.NotEqual("ambient-secret", invalid.ApiKey);
            Assert.Equal(0, calls);

            await storage.StoreOAuthAsync("openai-codex", CreateToken("account-expired"), "refresh-invalid",
                DateTimeOffset.UtcNow.AddSeconds(-1).ToUnixTimeMilliseconds(), "account-expired");
            var expired = await runtime.ResolveAsync("openai-codex", "gpt-5.5");
            Assert.False(expired.Authenticated);
            Assert.Equal("OAuth refresh failed; run /login", expired.AuthSource);
            Assert.NotEqual("ambient-secret", expired.ApiKey);
            Assert.Equal(1, calls);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task CodexSubscriptionRejectsStoredAndNewApiKeys()
    {
        var root = Path.Combine(Path.GetTempPath(), "pisharp-codex-api-key-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        using var http = new HttpClient(new DelegateHandler((_, _) =>
            throw new InvalidOperationException("Static Codex metadata must not make a provider request.")));
        try
        {
            var storage = new AuthStorage(Path.Combine(root, "auth.json"));
            await storage.StoreApiKeyAsync("openai-codex", "stored-api-key");
            var runtime = await ProviderModelRuntime.CreateAsync(root, false,
                name => name == "OPENAI_API_KEY" ? "ambient-api-key" : null, http);

            var selection = await runtime.ResolveAsync("openai-codex", "gpt-5.5");

            Assert.False(selection.Authenticated);
            Assert.Equal("API key authentication unsupported for provider", selection.AuthSource);
            Assert.NotEqual("stored-api-key", selection.ApiKey);
            Assert.NotEqual("ambient-api-key", selection.ApiKey);
            await Assert.ThrowsAsync<InvalidOperationException>(() => runtime.LoginApiKeyAsync("openai-codex", "new-api-key"));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task LocalBrowserCallbackRejectsWrongStateAndAcceptsTheMatchingCode()
    {
        using var reservation = new TcpListener(IPAddress.Loopback, 0);
        reservation.Start();
        var port = ((IPEndPoint)reservation.LocalEndpoint).Port;
        reservation.Stop();
        var redirect = new Uri($"http://localhost:{port}/auth/callback");
        var factory = new OpenAiCodexOAuthCallbackServerFactory();
        await using var callback = factory.Start(redirect, "expected-state");
        using var http = new HttpClient(new SocketsHttpHandler { UseProxy = false });
        var wait = callback.WaitForCodeAsync(CancellationToken.None);

        using var wrong = await http.GetAsync(new Uri(redirect + "?code=ignored&state=wrong"));
        Assert.Equal(HttpStatusCode.BadRequest, wrong.StatusCode);
        using var valid = await http.GetAsync(new Uri(redirect + "?code=accepted&state=expected-state"));
        Assert.Equal(HttpStatusCode.OK, valid.StatusCode);
        Assert.Equal("accepted", await wait.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task LocalBrowserCallbackEndsPromptlyWhenAuthorizationIsDenied()
    {
        using var reservation = new TcpListener(IPAddress.Loopback, 0);
        reservation.Start();
        var port = ((IPEndPoint)reservation.LocalEndpoint).Port;
        reservation.Stop();
        var redirect = new Uri($"http://localhost:{port}/auth/callback");
        await using var callback = new OpenAiCodexOAuthCallbackServerFactory().Start(redirect, "expected-state");
        using var http = new HttpClient(new SocketsHttpHandler { UseProxy = false });
        var wait = callback.WaitForCodeAsync(CancellationToken.None);

        using var denied = await http.GetAsync(new Uri(redirect + "?error=access_denied&state=expected-state"));

        Assert.Equal(HttpStatusCode.BadRequest, denied.StatusCode);
        await Assert.ThrowsAsync<InvalidOperationException>(() => wait);
    }

    private static HttpResponseMessage JsonResponse(string json) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json")
    };

    private static HttpResponseMessage TokenResponse(string accessToken, string refreshToken) =>
        JsonResponse(JsonSerializer.Serialize(new
        {
            access_token = accessToken,
            refresh_token = refreshToken,
            expires_in = 3600
        }));

    private static string CreateToken(string accountId)
    {
        static string Encode(byte[] value) => Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var header = Encode(Encoding.UTF8.GetBytes("{}"));
        var payload = Encode(JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, object>
        {
            ["https://api.openai.com/auth"] = new Dictionary<string, string> { ["chatgpt_account_id"] = accountId }
        }));
        return $"{header}.{payload}.fixture-signature";
    }

    private static Dictionary<string, string> ParseForm(string text) =>
        text.Split('&', StringSplitOptions.RemoveEmptyEntries).Select(part => part.Split('=', 2))
            .ToDictionary(pair => Uri.UnescapeDataString(pair[0]), pair => Uri.UnescapeDataString(pair[1].Replace('+', ' ')));

    private static Dictionary<string, string> ParseJson(string text)
    {
        using var json = JsonDocument.Parse(text);
        return json.RootElement.EnumerateObject().ToDictionary(property => property.Name,
            property => property.Value.ValueKind == JsonValueKind.String ? property.Value.GetString()! : property.Value.GetRawText());
    }

    private static Dictionary<string, string> ParseQuery(Uri uri) =>
        uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries).Select(part => part.Split('=', 2))
            .ToDictionary(pair => Uri.UnescapeDataString(pair[0]), pair => Uri.UnescapeDataString(pair[1].Replace('+', ' ')));

    private static string Base64Url(byte[] value) =>
        Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private sealed class RecordingInteraction(string selectedMethod) : IProviderOAuthInteraction
    {
        public List<ProviderOAuthNotice> Notices { get; } = [];

        public Task<string> SelectLoginMethodAsync(IReadOnlyList<ProviderOAuthLoginMethod> methods,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Assert.Contains(methods, method => method.Id == selectedMethod);
            return Task.FromResult(selectedMethod);
        }

        public void Notify(ProviderOAuthNotice notice) => Notices.Add(notice);
    }

    private sealed class DelegateHandler(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken) => send(request, cancellationToken);
    }

    private sealed class FakeCallbackServerFactory(string authorizationCode) : IProviderOAuthCallbackServerFactory
    {
        public Uri? RedirectUri { get; private set; }
        public string? State { get; private set; }
        public string? ServerState { get; private set; }

        public IProviderOAuthCallbackServer Start(Uri redirectUri, string expectedState)
        {
            RedirectUri = redirectUri;
            State = expectedState;
            ServerState = expectedState;
            return new FakeCallbackServer(authorizationCode);
        }
    }

    private sealed class FakeCallbackServer(string authorizationCode) : IProviderOAuthCallbackServer
    {
        public Task<string> WaitForCodeAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(authorizationCode);
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
