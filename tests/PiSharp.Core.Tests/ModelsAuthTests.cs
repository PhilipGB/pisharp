using PiSharp.Core.Models;
using PiSharp.Core.Models.Auth;
using PiSharp.Core.Models.OAuth;

namespace PiSharp.Core.Tests;

/// <summary>Credential (de)serialization, stores, and resolution precedence.</summary>
public class ModelsAuthTests
{
    [Fact]
    public void CredentialJson_RoundTrips()
    {
        var credentials = new Dictionary<string, Credential>
        {
            ["p1"] = new ApiKeyCredential("sk-123"),
            ["p2"] = new OAuthCredential
            {
                Access = "access-token",
                Refresh = "refresh-token",
                Expires = 1_900_000_000_000,
                Scope = "openid",
                Email = "user@example.com",
            },
        };
        var json = CredentialJson.Serialize(credentials);
        var parsed = CredentialJson.Deserialize(json);
        Assert.IsType<ApiKeyCredential>(parsed["p1"]);
        var oauth = Assert.IsType<OAuthCredential>(parsed["p2"]);
        Assert.Equal("access-token", oauth.Access);
        Assert.Equal(1_900_000_000_000L, oauth.Expires);
        Assert.Equal("openid", oauth.Scope);
        Assert.Equal("user@example.com", oauth.Email);
    }

    [Fact]
    public void CredentialJson_MalformedRoot_Errors()
    {
        Assert.Throws<InvalidOperationException>(() => CredentialJson.Deserialize("[1,2]"))
            .Message.Should_StartWith("Invalid auth.json");
        Assert.Throws<InvalidOperationException>(() => CredentialJson.Deserialize("{ bad"))
            .Message.Should_StartWith("Failed to read auth.json");
    }

    [Fact]
    public void CredentialJson_InvalidCredential_Errors()
    {
        var ex = Assert.Throws<InvalidOperationException>(
            () => CredentialJson.Deserialize("""{ "p": { "type": "oauth", "access": "a" } }"""));
        Assert.Contains("Invalid auth.json credential for provider \"p\"", ex.Message);
    }

    [Fact]
    public async Task FileCredentialStore_WritesAndReads()
    {
        using var temp = TempDirectory.Create();
        var path = Path.Combine(temp.Path, "auth.json");
        var store = new FileCredentialStore(path);
        await store.ModifyAsync("p", _ => Task.FromResult<Credential?>(new ApiKeyCredential("sk-file")));
        var read = await store.ReadAsync("p");
        Assert.Equal("sk-file", ((ApiKeyCredential)read!).Key);
        Assert.True(File.Exists(path));
        var listed = await store.ListAsync();
        Assert.Single(listed);
        await store.DeleteAsync("p");
        Assert.Null(await store.ReadAsync("p"));
    }

    [Fact]
    public async Task InMemoryCredentialStore_ModifyIsSerialized()
    {
        var store = new InMemoryCredentialStore();
        await store.ModifyAsync("p", _ => Task.FromResult<Credential?>(new ApiKeyCredential("a")));
        await store.ModifyAsync("p", credential => Task.FromResult<Credential?>(new ApiKeyCredential(((ApiKeyCredential)credential!).Key + "b")));
        var read = await store.ReadAsync("p");
        Assert.Equal("ab", ((ApiKeyCredential)read!).Key);
    }

    [Fact]
    public async Task RuntimeCredentialStore_ShadowsStored()
    {
        var inner = new InMemoryCredentialStore();
        await inner.ModifyAsync("p", _ => Task.FromResult<Credential?>(new ApiKeyCredential("stored-key")));
        var runtime = new RuntimeCredentialStore(inner);

        Assert.Equal("stored-key", ((ApiKeyCredential)(await runtime.ReadAsync("p"))!).Key);

        runtime.SetRuntimeApiKey("p", "runtime-key");
        Assert.True(runtime.HasRuntimeApiKey("p"));
        Assert.Equal("runtime-key", ((ApiKeyCredential)(await runtime.ReadAsync("p"))!).Key);

        await runtime.DeleteAsync("p");
        Assert.False(runtime.HasRuntimeApiKey("p"));
        Assert.Null(await runtime.ReadAsync("p"));
    }

    // ── CredentialResolver precedence ────────────────────────────────────

    private static AuthContext TestContext(
        IReadOnlyDictionary<string, string>? env = null) => new()
    {
        Env = name => Task.FromResult(env?.GetValueOrDefault(name)),
        FileExists = _ => Task.FromResult(false),
    };

    private sealed class FakeProvider : IAuthProvidingProvider
    {
        public FakeProvider(string id, ApiKeyAuth apiKey, OAuthAuth? oauth = null)
        {
            Id = id;
            // The invariant is Pi's: every provider declares at least one auth method,
            // so test providers always carry a real (overridable) ApiKey strategy.
            Auth = new ProviderAuth(apiKey, oauth);
        }

        public string Id { get; }
        public ProviderAuth Auth { get; }
    }

    [Fact]
    public void ProviderAuth_WithoutApiKeyAndOAuth_IsRejected()
    {
        // Pinned Pi (packages/ai/src/models.ts): Provider.auth is required and must
        // contain at least one of apiKey/oauth, including keyless local providers.
        var ex = Assert.Throws<ArgumentException>(() => new ProviderAuth(null, null));
        Assert.Contains("at least one of ApiKey/OAuth", ex.Message);
    }

    [Fact]
    public async Task Resolve_ExplicitOverride_Wins()
    {
        var store = new InMemoryCredentialStore();
        await store.ModifyAsync("p", _ => Task.FromResult<Credential?>(new ApiKeyCredential("stored")));
        var env = new Dictionary<string, string> { ["TEST_KEY_XYZ"] = "env-key" };
        var provider = new FakeProvider("p", new EnvApiKeyAuth { Name = "k", EnvironmentVariableNames = ["TEST_KEY_XYZ"] });
        var result = await CredentialResolver.ResolveProviderAuthAsync(
            provider, store, TestContext(env),
            new AuthResolutionOverrides { ApiKey = "override" },
            CancellationToken.None);
        Assert.Equal("override", result!.Auth.ApiKey);
        // Pinned Pi (auth/resolve.ts): the override is resolved through the provider's
        // own apiKey.resolve as a credential, so the reported source is what that
        // implementation reports ("stored credential" for envApiKeyAuth), not a
        // special "explicit" label.
        Assert.Equal("stored credential", result.Source);
    }

    [Fact]
    public async Task Resolve_StoredApiKey_BeatsEnv()
    {
        var store = new InMemoryCredentialStore();
        await store.ModifyAsync("p", _ => Task.FromResult<Credential?>(new ApiKeyCredential("stored-key")));
        var env = new Dictionary<string, string> { ["TEST_KEY_XYZ"] = "env-key" };
        var provider = new FakeProvider("p", new EnvApiKeyAuth { Name = "k", EnvironmentVariableNames = ["TEST_KEY_XYZ"] });
        var result = await CredentialResolver.ResolveProviderAuthAsync(
            provider, store, TestContext(env), null, CancellationToken.None);
        Assert.Equal("stored-key", result!.Auth.ApiKey);
        Assert.Equal("stored credential", result.Source);
    }

    [Fact]
    public async Task Resolve_EnvOnly_WhenNothingStored()
    {
        var store = new InMemoryCredentialStore();
        var env = new Dictionary<string, string> { ["TEST_KEY_XYZ"] = "env-key" };
        var provider = new FakeProvider("p", new EnvApiKeyAuth { Name = "k", EnvironmentVariableNames = ["TEST_KEY_XYZ"] });
        var result = await CredentialResolver.ResolveProviderAuthAsync(
            provider, store, TestContext(env), null, CancellationToken.None);
        Assert.Equal("env-key", result!.Auth.ApiKey);
        // Pinned Pi (auth/helpers.ts envApiKeyAuth): the resolved source is the env
        // variable name itself; "environment" is an AuthStatus label, a different type.
        Assert.Equal("TEST_KEY_XYZ", result.Source);
    }

    [Fact]
    public async Task Resolve_StoredOAuth_RefreshesWhenExpired()
    {
        var store = new InMemoryCredentialStore();
        var past = (long)(DateTime.UtcNow.AddHours(-1) - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalMilliseconds;
        await store.ModifyAsync("p", _ => Task.FromResult<Credential?>(new OAuthCredential
        {
            Access = "old",
            Refresh = "refresh-1",
            Expires = past,
        }));
        var provider = new FakeProvider("p",
            new EnvApiKeyAuth { Name = "k", EnvironmentVariableNames = ["TEST_MISSING_XYZ"] },
            new FakeOAuth { Name = "fake" });
        var result = await CredentialResolver.ResolveProviderAuthAsync(
            provider, store, TestContext(null), null, CancellationToken.None);
        Assert.Equal("fresh-access", result!.Auth.ApiKey);
        var updated = Assert.IsType<OAuthCredential>(await store.ReadAsync("p"));
        Assert.Equal("fresh-refresh", updated.Refresh);
    }

    [Fact]
    public async Task Resolve_StoredOAuth_RefreshFailure_PreservesStoredCredential()
    {
        var store = new InMemoryCredentialStore();
        var past = (long)(DateTime.UtcNow.AddHours(-1) - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalMilliseconds;
        await store.ModifyAsync("p", _ => Task.FromResult<Credential?>(new OAuthCredential
        {
            Access = "old-access",
            Refresh = "old-refresh",
            Expires = past,
        }));
        var provider = new FakeProvider("p",
            new EnvApiKeyAuth { Name = "k", EnvironmentVariableNames = ["TEST_MISSING_XYZ"] },
            new FailingRefreshOAuth { Name = "fake" });
        // Pinned Pi: a failed refresh surfaces as an error and leaves the stored
        // credential untouched (the modify callback throws before the write).
        await Assert.ThrowsAsync<ModelsException>(() => CredentialResolver.ResolveProviderAuthAsync(
            provider, store, TestContext(null), null, CancellationToken.None));
        var stored = Assert.IsType<OAuthCredential>(await store.ReadAsync("p"));
        Assert.Equal("old-refresh", stored.Refresh);
    }

    [Fact]
    public async Task Resolve_StoredCredential_WithNoHandler_NoEnvFallback()
    {
        var store = new InMemoryCredentialStore();
        await store.ModifyAsync("p", _ => Task.FromResult<Credential?>(new ApiKeyCredential("stored-key")));
        // Provider without any api-key auth: the stored credential has no handler.
        var provider = new FakeProvider("p", new NoAuth { Name = "none" });
        var result = await CredentialResolver.ResolveProviderAuthAsync(
            provider, store, TestContext(new Dictionary<string, string> { ["TEST_KEY_XYZ"] = "env" }),
            null, CancellationToken.None);
        Assert.Null(result);
    }

    private sealed class NoAuth : ApiKeyAuth
    {
        public override Task<AuthResult?> ResolveAsync(ApiKeyAuthInput input)
            => Task.FromResult<AuthResult?>(null);
    }

    private sealed class FakeOAuth : OAuthAuth
    {
        public FakeOAuth()
        {
            Name = "fake";
        }

        public override Task<OAuthCredential> LoginAsync(IAuthInteraction interaction)
            => throw new NotSupportedException();

        public override Task<OAuthCredential> RefreshAsync(OAuthCredential credential, CancellationToken cancellationToken)
        {
            var future = (long)(DateTime.UtcNow.AddHours(1) - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalMilliseconds;
            return Task.FromResult(new OAuthCredential
            {
                Access = "fresh-access",
                Refresh = "fresh-refresh",
                Expires = future,
            });
        }

        public override Task<ModelAuth> ToAuthAsync(OAuthCredential credential)
            => Task.FromResult(new ModelAuth { ApiKey = credential.Access });
    }

    private sealed class FailingRefreshOAuth : OAuthAuth
    {
        public FailingRefreshOAuth()
        {
            Name = "fake";
        }

        public override Task<OAuthCredential> LoginAsync(IAuthInteraction interaction)
            => throw new NotSupportedException();

        public override Task<OAuthCredential> RefreshAsync(OAuthCredential credential, CancellationToken cancellationToken)
            => throw new InvalidOperationException("refresh endpoint down");

        public override Task<ModelAuth> ToAuthAsync(OAuthCredential credential)
            => Task.FromResult(new ModelAuth { ApiKey = credential.Access });
    }

    // ── OAuth primitives ─────────────────────────────────────────────────

    [Fact]
    public void Pkce_GeneratesVerifiablePair()
    {
        var pair = Pkce.Generate();
        Assert.Equal(43, pair.Verifier.Length);
        var expected = Pkce.Base64UrlEncode(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(pair.Verifier)));
        Assert.Equal(expected, pair.Challenge);
    }

    [Fact]
    public async Task DeviceCodePoller_CompletesOnSuccess()
    {
        var polls = 0;
        var result = await DeviceCodePoller.PollAsync<int>(
            async () =>
            {
                polls++;
                await Task.Delay(1);
                return polls >= 2
                    ? new DeviceCodePollResult.Complete<int>(42)
                    : new DeviceCodePollResult.Pending();
            },
            1, 60, false, CancellationToken.None);
        Assert.Equal(42, result);
    }

    [Fact]
    public async Task DeviceCodePoller_SlowDownIncreasesInterval()
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var polls = 0;
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => DeviceCodePoller.PollAsync<int>(
            async () =>
            {
                polls++;
                await Task.Delay(1);
                return new DeviceCodePollResult.SlowDown(null);
            },
            1, 8, false, CancellationToken.None));
        sw.Stop();
        // Pinned Pi (pollOAuthDeviceCodeFlow): the post-slow_down interval is 1s + 5s = 6s,
        // so an 8s deadline yields polls at t=0 and t~6s. (Without the increase there
        // would be ~8.) The final capped sleep wakes a sub-millisecond margin before
        // the deadline because remaining is truncated to int milliseconds, so a third
        // boundary poll is possible; the 3s deadline in the previous version made even
        // the second poll a coin flip, in Pi and here alike.
        Assert.InRange(polls, 2, 3);
        Assert.True(sw.ElapsedMilliseconds >= 7_900, $"expected ~8s, was {sw.ElapsedMilliseconds}ms");
        Assert.Contains("slow_down", ex.Message);
    }

    [Fact]
    public async Task DeviceCodePoller_Cancellation()
    {
        using var cts = new CancellationTokenSource(50);
        await Assert.ThrowsAsync<InvalidOperationException>(() => DeviceCodePoller.PollAsync<int>(
            async () =>
            {
                await Task.Delay(100);
                return new DeviceCodePollResult.Pending();
            },
            1, 60, false, cts.Token));
    }

    [Fact]
    public void AuthorizationInput_ParsesForms()
    {
        var url = AuthorizationInputParser.Parse("http://localhost:53692/callback?code=abc&state=xyz");
        Assert.Equal("abc", url.Code);
        Assert.Equal("xyz", url.State);

        var fragment = AuthorizationInputParser.Parse("abc#xyz");
        Assert.Equal("abc", fragment.Code);
        Assert.Equal("xyz", fragment.State);

        var query = AuthorizationInputParser.Parse("?code=abc&state=xyz");
        Assert.Equal("abc", query.Code);

        var bare = AuthorizationInputParser.Parse("just-a-code");
        Assert.Equal("just-a-code", bare.Code);
        Assert.Null(bare.State);
    }
}

/// <summary>Small assertion helpers for message prefixes.</summary>
public static class MessageAssert
{
    public static void Should_StartWith(this string message, string prefix)
    {
        if (!message.StartsWith(prefix, StringComparison.Ordinal))
        {
            throw new Xunit.Sdk.XunitException($"Expected message starting with \"{prefix}\" but got \"{message}\"");
        }
    }

    public static void Should_Contain(this string message, string fragment)
    {
        if (!message.Contains(fragment, StringComparison.Ordinal))
        {
            throw new Xunit.Sdk.XunitException($"Expected message containing \"{fragment}\" but got \"{message}\"");
        }
    }
}
