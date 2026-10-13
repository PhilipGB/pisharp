using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PiSharp.Cli;

namespace PiSharp.Tests;

public sealed class AnthropicOAuthTests
{
    [Fact]
    public async Task AnthropicProfileExposesOAuthLogin()
    {
        var root = Path.Combine(Path.GetTempPath(), "pisharp-anthropic-oauth-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        using var http = new HttpClient(new DelegateHandler((_, _) =>
            throw new InvalidOperationException("Profile inspection must not make an HTTP request.")));
        try
        {
            var runtime = await ProviderModelRuntime.CreateAsync(root, false, _ => null, http);

            Assert.True(runtime.GetProvider("anthropic").OAuthSupported);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task CopyCodeLoginUsesPkceStoresPrivateTokensAndPreservesProviderAuth()
    {
        var root = NewRoot();
        var tokenCalls = 0;
        Dictionary<string, string>? exchange = null;
        using var http = new HttpClient(new DelegateHandler(async (request, cancellationToken) =>
        {
            Assert.Equal("https://platform.claude.com/v1/oauth/token", request.RequestUri?.ToString());
            Assert.Equal("application/json", request.Content?.Headers.ContentType?.MediaType);
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
            exchange = ReadObject(body.RootElement);
            tokenCalls++;
            return TokenResponse("sk-ant-oat01-access", "refresh-original");
        }));
        try
        {
            var runtime = await ProviderModelRuntime.CreateAsync(root, false, _ => null, http);
            var interaction = new RecordingInteraction("copy_code", notices =>
            {
                var authorization = AuthorizationUri(notices);
                return "copied-code#" + Query(authorization)["state"];
            });

            await runtime.LoginOAuthAsync("anthropic", interaction);

            Assert.Equal(1, tokenCalls);
            Assert.Equal(
                [new ProviderOAuthLoginMethod("browser", "Browser login (default)"),
                    new ProviderOAuthLoginMethod("copy_code", "Copy code login (headless)")],
                interaction.Methods);
            Assert.Equal("Paste the code Anthropic shows after you sign in:", interaction.CodePrompt);
            Assert.Equal("code#state", interaction.CodePlaceholder);
            var authorizationQuery = Query(AuthorizationUri(interaction.Notices));
            Assert.Equal("https://claude.ai/oauth/authorize", AuthorizationUri(interaction.Notices).GetLeftPart(UriPartial.Path));
            Assert.Equal("true", authorizationQuery["code"]);
            Assert.Equal("9d1c250a-e61b-44d9-88ed-5944d1962f5e", authorizationQuery["client_id"]);
            Assert.Equal("code", authorizationQuery["response_type"]);
            Assert.Equal("https://platform.claude.com/oauth/code/callback", authorizationQuery["redirect_uri"]);
            Assert.Equal("S256", authorizationQuery["code_challenge_method"]);
            Assert.Equal(authorizationQuery["state"], exchange!["state"]);
            Assert.Equal("copied-code", exchange["code"]);
            Assert.Equal(authorizationQuery["redirect_uri"], exchange["redirect_uri"]);
            Assert.Equal(authorizationQuery["code_challenge"], PkceChallenge(exchange["code_verifier"]));
            Assert.Equal(authorizationQuery["state"], exchange["code_verifier"]);

            var stored = await new AuthStorage(Path.Combine(root, "auth.json")).ReadAsync("anthropic");
            Assert.Equal("oauth", stored?.Type);
            Assert.Equal("sk-ant-oat01-access", stored?.Access);
            Assert.Equal("refresh-original", stored?.Refresh);
            Assert.True(stored?.Expires > DateTimeOffset.UtcNow.AddMinutes(5).ToUnixTimeMilliseconds());
            if (OperatingSystem.IsLinux())
                Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite,
                    File.GetUnixFileMode(Path.Combine(root, "auth.json")));

            var selection = await runtime.ResolveAsync("anthropic", "claude-sonnet-4-6");
            Assert.True(selection.Authenticated);
            Assert.Equal("sk-ant-oat01-access", selection.ApiKey);
            Assert.True(selection.AnthropicIsOAuthToken);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task BrowserLoginUsesThePiCallbackAndBrowserRedirect()
    {
        var callback = new FakeCallbackServerFactory("browser-code");
        Uri? opened = null;
        Dictionary<string, string>? exchange = null;
        using var http = new HttpClient(new DelegateHandler(async (request, cancellationToken) =>
        {
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
            exchange = ReadObject(body.RootElement);
            return TokenResponse("sk-ant-oat01-browser", "refresh-browser");
        }));
        var adapter = new AnthropicOAuthAdapter(http, callback, uri => opened = uri);
        var interaction = new RecordingInteraction("browser", _ => throw new InvalidOperationException("Browser callback must not prompt for a code."));

        var credential = await adapter.LoginAsync("browser", interaction, CancellationToken.None);

        Assert.True(adapter.IsValidCredential(credential, out _));
        Assert.Equal("http://localhost:53692/callback", callback.RedirectUri?.ToString());
        Assert.Equal(callback.ExpectedState, Query(opened!)["state"]);
        Assert.Equal("https://claude.ai/oauth/authorize", opened?.GetLeftPart(UriPartial.Path));
        Assert.Equal(opened, interaction.Notices.Single(notice => notice.Kind == "auth_url").Url);
        Assert.Equal("browser-code", exchange!["code"]);
        Assert.Equal(callback.ExpectedState, exchange["state"]);
        Assert.Equal("http://localhost:53692/callback", exchange["redirect_uri"]);
        Assert.Equal(Query(opened!)["code_challenge"], PkceChallenge(exchange["code_verifier"]));
    }

    [Fact]
    public async Task CopyCodeLoginRejectsWrongStateBeforeExchangingOrPersisting()
    {
        var root = NewRoot();
        var tokenCalls = 0;
        using var http = new HttpClient(new DelegateHandler((_, _) =>
        {
            Interlocked.Increment(ref tokenCalls);
            return Task.FromResult(TokenResponse("sk-ant-oat01-invalid", "refresh-invalid"));
        }));
        try
        {
            var runtime = await ProviderModelRuntime.CreateAsync(root, false, _ => null, http);
            var interaction = new RecordingInteraction("copy_code", notices =>
                "copied-code#wrong-state");

            var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                runtime.LoginOAuthAsync("anthropic", interaction));

            Assert.Contains("state mismatch", error.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(0, tokenCalls);
            Assert.Null(await new AuthStorage(Path.Combine(root, "auth.json")).ReadAsync("anthropic"));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task CopyCodeLoginAcceptsRedirectUrlWithoutStateAndUsesTheGeneratedState()
    {
        var root = NewRoot();
        Dictionary<string, string>? exchange = null;
        using var http = new HttpClient(new DelegateHandler(async (request, cancellationToken) =>
        {
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
            exchange = ReadObject(body.RootElement);
            return TokenResponse("sk-ant-oat01-redirect", "refresh-redirect");
        }));
        try
        {
            var runtime = await ProviderModelRuntime.CreateAsync(root, false, _ => null, http);
            var interaction = new RecordingInteraction("copy_code", _ =>
                "https://platform.claude.com/oauth/code/callback?code=redirect-code");

            await runtime.LoginOAuthAsync("anthropic", interaction);

            var authorization = AuthorizationUri(interaction.Notices);
            Assert.Equal("redirect-code", exchange!["code"]);
            Assert.Equal(Query(authorization)["state"], exchange["state"]);
            Assert.Equal(Query(authorization)["code_challenge"], PkceChallenge(exchange["code_verifier"]));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task CopyCodeLoginCancellationDoesNotExchangeOrPersistTokens()
    {
        var root = NewRoot();
        var tokenCalls = 0;
        using var http = new HttpClient(new DelegateHandler((_, _) =>
        {
            Interlocked.Increment(ref tokenCalls);
            return Task.FromResult(TokenResponse("sk-ant-oat01-cancelled", "refresh-cancelled"));
        }));
        try
        {
            var runtime = await ProviderModelRuntime.CreateAsync(root, false, _ => null, http);
            var interaction = new RecordingInteraction("copy_code", _ => throw new OperationCanceledException());

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                runtime.LoginOAuthAsync("anthropic", interaction));

            Assert.Equal(0, tokenCalls);
            Assert.Null(await new AuthStorage(Path.Combine(root, "auth.json")).ReadAsync("anthropic"));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task ExpiredAnthropicOAuthRefreshesAndPersistsRotatedTokens()
    {
        var root = NewRoot();
        Dictionary<string, string>? refresh = null;
        using var http = new HttpClient(new DelegateHandler(async (request, cancellationToken) =>
        {
            Assert.Equal("https://platform.claude.com/v1/oauth/token", request.RequestUri?.ToString());
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
            refresh = ReadObject(body.RootElement);
            return TokenResponse("sk-ant-oat01-refreshed", "refresh-rotated");
        }));
        try
        {
            var storage = new AuthStorage(Path.Combine(root, "auth.json"));
            await storage.StoreOAuthAsync("anthropic", "sk-ant-oat01-expired", "refresh-before",
                DateTimeOffset.UtcNow.AddMinutes(-1).ToUnixTimeMilliseconds());
            var runtime = await ProviderModelRuntime.CreateAsync(root, false, _ => null, http);

            var selection = await runtime.ResolveAsync("anthropic", "claude-sonnet-4-6");

            Assert.True(selection.Authenticated);
            Assert.Equal("sk-ant-oat01-refreshed", selection.ApiKey);
            Assert.Equal("refresh_token", refresh!["grant_type"]);
            Assert.Equal("9d1c250a-e61b-44d9-88ed-5944d1962f5e", refresh["client_id"]);
            Assert.Equal("refresh-before", refresh["refresh_token"]);
            var stored = await storage.ReadAsync("anthropic");
            Assert.Equal("sk-ant-oat01-refreshed", stored?.Access);
            Assert.Equal("refresh-rotated", stored?.Refresh);
            Assert.True(stored?.Expires > DateTimeOffset.UtcNow.AddMinutes(5).ToUnixTimeMilliseconds());
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task InvalidAnthropicOAuthResponseDoesNotPersistPartialCredentials()
    {
        var root = NewRoot();
        using var http = new HttpClient(new DelegateHandler((_, _) => Task.FromResult(
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"access_token":"sk-ant-oat01-partial","expires_in":3600}""", Encoding.UTF8, "application/json")
            })));
        try
        {
            var runtime = await ProviderModelRuntime.CreateAsync(root, false, _ => null, http);
            var interaction = new RecordingInteraction("copy_code", notices =>
                "authorization-code#" + Query(AuthorizationUri(notices))["state"]);

            await Assert.ThrowsAsync<InvalidDataException>(() => runtime.LoginOAuthAsync("anthropic", interaction));

            Assert.Null(await new AuthStorage(Path.Combine(root, "auth.json")).ReadAsync("anthropic"));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private sealed class DelegateHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken) => send(request, cancellationToken);
    }

    private sealed class RecordingInteraction(string selectedMethod,
        Func<IReadOnlyList<ProviderOAuthNotice>, string> provideCode) : IProviderOAuthInteraction
    {
        public IReadOnlyList<ProviderOAuthLoginMethod> Methods { get; private set; } = [];
        public List<ProviderOAuthNotice> Notices { get; } = [];
        public string? CodePrompt { get; private set; }
        public string? CodePlaceholder { get; private set; }

        public Task<string> SelectLoginMethodAsync(IReadOnlyList<ProviderOAuthLoginMethod> methods,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Methods = methods.ToArray();
            return Task.FromResult(selectedMethod);
        }

        public Task<string> PromptForCodeAsync(string message, string placeholder,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CodePrompt = message;
            CodePlaceholder = placeholder;
            return Task.FromResult(provideCode(Notices));
        }

        public void Notify(ProviderOAuthNotice notice) => Notices.Add(notice);
    }

    private sealed class FakeCallbackServerFactory(string code) : IProviderOAuthCallbackServerFactory
    {
        public Uri? RedirectUri { get; private set; }
        public string? ExpectedState { get; private set; }

        public IProviderOAuthCallbackServer Start(Uri redirectUri, string expectedState)
        {
            RedirectUri = redirectUri;
            ExpectedState = expectedState;
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

    private static string NewRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "pisharp-anthropic-oauth-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static Uri AuthorizationUri(IEnumerable<ProviderOAuthNotice> notices) =>
        Assert.Single(notices, notice => notice.Kind == "auth_url").Url!;

    private static Dictionary<string, string> Query(Uri uri) => uri.Query.TrimStart('?')
        .Split('&', StringSplitOptions.RemoveEmptyEntries)
        .Select(part => part.Split('=', 2))
        .ToDictionary(part => Uri.UnescapeDataString(part[0]),
            part => Uri.UnescapeDataString(part.Length > 1 ? part[1].Replace('+', ' ') : ""), StringComparer.Ordinal);

    private static Dictionary<string, string> ReadObject(JsonElement element) =>
        element.EnumerateObject().ToDictionary(property => property.Name,
            property => property.Value.GetString()!, StringComparer.Ordinal);

    private static string PkceChallenge(string verifier) => Convert.ToBase64String(
        SHA256.HashData(Encoding.ASCII.GetBytes(verifier))).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static HttpResponseMessage TokenResponse(string access, string refresh) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(JsonSerializer.Serialize(new
        {
            access_token = access,
            refresh_token = refresh,
            expires_in = 3600
        }), Encoding.UTF8, "application/json")
    };
}
