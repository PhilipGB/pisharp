using PiSharp.Core.Models;
using PiSharp.Core.Models.Auth;
using PiSharp.Core.Models.Providers;

namespace PiSharp.Core.Tests;

/// <summary>Provider composition, catalog store, remote catalog parsing, and resolution.</summary>
public class ModelsProviderTests
{
    private static ModelInfo Model(string provider, string id, int contextWindow = 128_000) => new()
    {
        Id = id,
        Name = id,
        Api = "openai-completions",
        Provider = provider,
        BaseUrl = "http://localhost",
        Input = ["text"],
        Cost = new ModelCost { Input = 1, Output = 1, CacheRead = 0, CacheWrite = 0 },
        ContextWindow = contextWindow,
        MaxTokens = 8_192,
    };

    private static ProviderSpec Provider(string id, params ModelInfo[] models) => new()
    {
        Id = id,
        Name = id,
        BaseUrl = "http://localhost",
        // Pinned Pi: every provider declares auth; the default double is a minimal
        // ambient api-key strategy whose behavior tests can replace.
        Auth = new ProviderAuth(
            new EnvApiKeyAuth { Name = "k", EnvironmentVariableNames = [id.ToUpperInvariant() + "_KEY"] }),
        GetModels = () => models,
        DefaultApi = "openai-completions",
    };

    private static Task<ModelConfig> ConfigFromJsonAsync(string json)
    {
        using var temp = TempDirectory.Create();
        var path = Path.Combine(temp.Path, "models.json");
        File.WriteAllText(path, json);
        return ModelConfig.LoadAsync(path);
    }

    // ── ProviderComposer ─────────────────────────────────────────────────

    [Fact]
    public async Task Composer_ModelsJson_ProvidesModelsAndAuth()
    {
        var config = await ConfigFromJsonAsync("""
        {
          "providers": {
            "custom": {
              "baseUrl": "http://custom:1",
              "apiKey": "sk-static",
              "models": [ { "id": "c1", "api": "openai-completions" } ]
            }
          }
        }
        """);
        var provider = ProviderComposer.ComposeModelProvider("custom", null, config);
        Assert.Equal("http://custom:1", provider.BaseUrl);
        var model = Assert.Single(provider.GetModels());
        Assert.Equal("custom", model.Provider);
        Assert.Equal(128_000, model.ContextWindow);
        Assert.Equal(16_384, model.MaxTokens);
    }

    [Fact]
    public async Task Composer_BaseProvider_OverrideWinsForDefinedValues()
    {
        var base_ = Provider("base", Model("base", "m1", 1000));
        var config = await ConfigFromJsonAsync("""
        {
          "providers": {
            "base": {
              "models": [ { "id": "m2", "api": "openai-completions" } ]
            },
            "other": {
              "modelOverrides": {
                "m1": { "contextWindow": 2000, "name": "M1 overridden" }
              }
            }
          }
        }
        """);
        var provider = ProviderComposer.ComposeModelProvider("base", base_, config);
        var models = provider.GetModels();
        Assert.Equal(2, models.Count); // ok: explicit count check for two providers
        var m1 = models.First(m => m.Id == "m1");
        Assert.Equal(1000, m1.ContextWindow);
        var m2 = models.First(m => m.Id == "m2");
        Assert.Equal("base", m2.Provider);
    }

    [Fact]
    public async Task Composer_BaseUrlOnly_ComposesAmbientApiKeyAuth()
    {
        // Pinned Pi (provider-composer.ts composeApiKeyAuth): a provider with models/
        // baseUrl but no explicit key is NOT auth-less. It receives a fabricated
        // api-key auth whose resolution reports unconfigured until a key is stored or
        // entered via the login prompt.
        var config = await ConfigFromJsonAsync("""
        { "providers": { "p": { "baseUrl": "http://x" } } }
        """);
        var provider = ProviderComposer.ComposeModelProvider("p", null, config);
        Assert.NotNull(provider.Auth.ApiKey);
        Assert.Null(provider.Auth.OAuth);
        Assert.NotNull(provider.Auth.ApiKey!.Login);

        var check = await provider.Auth.ApiKey.Check!(new ApiKeyAuthInput
        {
            Context = AmbientContext(),
            CancellationToken = CancellationToken.None,
        });
        Assert.Null(check);
    }

    [Fact]
    public async Task Composer_OAuthOnly_Base_NoFabricatedApiKey()
    {
        // Pinned Pi: "OAuth-only providers get no fabricated API-key login method."
        var base_ = new ProviderSpec
        {
            Id = "p",
            Name = "P",
            BaseUrl = "http://p",
            Auth = new ProviderAuth(null, new StubOAuth { Name = "p oauth" }),
            GetModels = () => [Model("p", "m1")],
            DefaultApi = "openai-completions",
        };
        var config = await ConfigFromJsonAsync("""
        { "providers": { "p": { "baseUrl": "http://overlay" } } }
        """);
        var provider = ProviderComposer.ComposeModelProvider("p", base_, config);
        Assert.Null(provider.Auth.ApiKey);
        Assert.NotNull(provider.Auth.OAuth);
    }

    [Fact]
    public async Task Composer_StructuralErrors_Throw()
    {
        // A model with no resolvable api (model level, provider level, or base) is a
        // structural error surfaced eagerly at composition (pinned Pi: modelFromJson).
        var noModelApi = await ConfigFromJsonAsync("""
        {
          "providers": {
            "p": {
              "apiKey": "sk",
              "models": [ { "id": "m", "name": "m" } ]
            }
          }
        }
        """);
        Assert.Throws<InvalidOperationException>(() => ProviderComposer.ComposeModelProvider("p", null, noModelApi))
            .Message.Should_Contain("no \"api\" specified");
    }

    [Fact]
    public async Task Composer_AuthHeader_AddsAuthorizationToResolvedAuth()
    {
        // Pinned Pi: authHeader is applied to the resolved request auth (withConfiguredAuth),
        // not to the provider's static headers.
        var config = await ConfigFromJsonAsync("""
        {
          "providers": {
            "p": {
              "baseUrl": "http://x",
              "apiKey": "sk-1",
              "authHeader": true,
              "models": [ { "id": "m", "api": "openai-completions" } ]
            }
          }
        }
        """);
        var provider = ProviderComposer.ComposeModelProvider("p", null, config);
        Assert.Null(provider.Headers);

        var result = await provider.Auth.ApiKey!.ResolveAsync(new ApiKeyAuthInput
        {
            Context = AmbientContext(),
            CancellationToken = CancellationToken.None,
        });
        Assert.NotNull(result);
        Assert.Equal("sk-1", result!.Auth.ApiKey);
        Assert.Equal("Bearer sk-1", result.Auth.Headers!["Authorization"]);
    }

    private static AuthContext AmbientContext() => new()
    {
        Env = _ => Task.FromResult<string?>(null),
        FileExists = _ => Task.FromResult(false),
    };

    private sealed class StubOAuth : OAuthAuth
    {
        public StubOAuth()
        {
            Name = "stub";
        }

        public override Task<OAuthCredential> LoginAsync(IAuthInteraction interaction)
            => throw new NotSupportedException();

        public override Task<OAuthCredential> RefreshAsync(OAuthCredential credential, CancellationToken cancellationToken)
            => Task.FromResult(credential);

        public override Task<ModelAuth> ToAuthAsync(OAuthCredential credential)
            => Task.FromResult(new ModelAuth { ApiKey = credential.Access });
    }

    // ── ModelsStoreJson / FileModelsStore ────────────────────────────────

    [Fact]
    public async Task FileModelsStore_RoundTrips()
    {
        using var temp = TempDirectory.Create();
        var store = new FileModelsStore(Path.Combine(temp.Path, "models-store.json"));
        var entry = new ModelsStoreEntry
        {
            Models = [Model("p", "m1", 4096)],
            CheckedAt = 1_000_000,
            LastModified = 2_000_000,
            Etag = "abc",
        };
        await store.WriteAsync("p", entry);
        var read = await store.ReadAsync("p");
        Assert.NotNull(read);
        Assert.Equal(4096, read!.Models[0].ContextWindow);
        Assert.Equal("abc", read.Etag);
        await store.DeleteAsync("p");
        Assert.Null(await store.ReadAsync("p"));
    }

    // ── RemoteCatalogProvider (offline parts) ────────────────────────────

    [Fact]
    public void RemoteCatalog_ParsesArrayAndObjectForms()
    {
        var fromArray = RemoteCatalogProvider.ParseCatalog("p", """
        [ { "id": "a", "api": "openai-completions", "contextWindow": 1000 } ]
        """, "openai-completions");
        Assert.Equal("a", fromArray[0].Id);
        Assert.Equal(1000, fromArray[0].ContextWindow);
        Assert.Equal("p", fromArray[0].Provider);

        var fromObject = RemoteCatalogProvider.ParseCatalog("p", """
        { "models": [ { "id": "b" } ] }
        """, "anthropic-messages");
        Assert.Equal("anthropic-messages", fromObject[0].Api);

        var fromMap = RemoteCatalogProvider.ParseCatalog("p", """
        { "b": { "id": "b" }, "skip": "not-a-model" }
        """, null);
        Assert.Single(fromMap);

        Assert.Throws<InvalidOperationException>(() =>
            RemoteCatalogProvider.ParseCatalog("p", "{ no models }", null))
            .Message.Should_Contain("Invalid model catalog for provider \"p\"");
    }

    [Fact]
    public void RemoteCatalog_Merge_OverridesById()
    {
        var baseline = new[] { Model("p", "m1", 1000), Model("p", "m2", 2000) };
        var dynamic = new[] { Model("p", "m2", 3000) };
        var merged = RemoteCatalogProvider.MergeModels(baseline, dynamic);
        Assert.Equal(2, merged.Count);
        Assert.Equal(3000, merged.First(m => m.Id == "m2").ContextWindow);
    }

    [Fact]
    public void RemoteModels_RespectsLocalGeneratedAt()
    {
        var entry = new ModelsStoreEntry
        {
            Models = [Model("p", "m1")],
            CheckedAt = 1_000_000,
            LastModified = 5_000,
        };
        Assert.Single(RemoteCatalogProvider.RemoteModels(entry, 4_000));
        Assert.Empty(RemoteCatalogProvider.RemoteModels(entry, 5_000));
        Assert.Empty(RemoteCatalogProvider.RemoteModels(null, null));
    }

    // ── ModelResolver ────────────────────────────────────────────────────

    private static async Task<ModelRuntime> StubRuntimeAsync(IReadOnlyList<ModelInfo> models)
    {
        var runtime = await ModelRuntime.CreateAsync(new CreateModelRuntimeOptions
        {
            Credentials = new InMemoryCredentialStore(),
            Builtins = [],
            ModelsStore = new InMemoryModelsStore(),
            ModelsPath = null,
            NetworkEnabled = false,
            RefreshOnCreate = false,
        });
        foreach (var model in models)
        {
            runtime.RegisterProvider(Provider(model.Provider, model));
        }

        return runtime;
    }

    [Fact]
    public void Resolver_ExactProviderModelId()
    {
        var models = new[]
        {
            Model("openai", "gpt-4o"),
            Model("openrouter", "openai/gpt-4o"),
        };
        var match = ModelResolver.FindExactModelReferenceMatch("openrouter/openai/gpt-4o", models);
        Assert.NotNull(match);
        Assert.Equal("openrouter", match!.Provider);
    }

    [Fact]
    public void Resolver_PartialMatches_PreferAlias()
    {
        var models = new[]
        {
            Model("p", "claude-sonnet-4-5"),
            Model("p", "claude-sonnet-4-5-20250929"),
        };
        var match = ModelResolver.TryMatchModel("claude-sonnet", models);
        Assert.Equal("claude-sonnet-4-5", match!.Id);
    }

    [Fact]
    public void Resolver_PartialMatches_NoAliasPicksLatestDated()
    {
        var models = new[]
        {
            Model("p", "gpt-4o-20240513"),
            Model("p", "gpt-4o-20240806"),
        };
        var match = ModelResolver.TryMatchModel("gpt-4o", models);
        Assert.Equal("gpt-4o-20240806", match!.Id);
    }

    [Fact]
    public void Resolver_PatternWithThinkingLevel()
    {
        var models = new[] { Model("p", "gpt-4o") };
        var (model, level, warning) = ModelResolver.ParseModelPattern("gpt-4o:high", models);
        Assert.Equal("gpt-4o", model!.Id);
        Assert.Equal("high", level);
        Assert.Null(warning);

        var (model2, level2, warning2) = ModelResolver.ParseModelPattern("gpt-4o:bogus", models);
        Assert.Equal("gpt-4o", model2!.Id);
        Assert.Null(level2);
        Assert.Contains("Invalid thinking level", warning2);

        var (model3, _, _) = ModelResolver.ParseModelPattern("gpt-4o:bogus", models, false);
        Assert.Null(model3);
    }

    [Fact]
    public void Resolver_Scope_GlobsAndPatterns()
    {
        var models = new[]
        {
            Model("anthropic", "claude-sonnet-4-5"),
            Model("anthropic", "claude-opus-4-1"),
            Model("openai", "gpt-4o"),
        };
        var result = ModelResolver.ResolveModelScopeFromModels(["anthropic/*", "gpt-4o:bogus"], models);
        Assert.True(result.ScopedModels.Count == 3, $"expected 3 scoped models, got {result.ScopedModels.Count}");
        Assert.True(result.Diagnostics.Count == 1, $"expected 1 diagnostic, got {result.Diagnostics.Count}");
        Assert.Equal("invalid-thinking-level", result.Diagnostics[0].Code);
    }

    [Fact]
    public async Task Resolver_CliModel_ProviderSlashPreferred()
    {
        var models = new[]
        {
            Model("zai", "glm-5"),
            Model("vercel-ai-gateway", "zai/glm-5"),
        };
        var runtime = await StubRuntimeAsync(models);
        var result = ModelResolver.ResolveCliModel(null, "zai/glm-5", null, runtime);
        Assert.NotNull(result.Model);
        Assert.Equal("zai", result.Model!.Provider);
        Assert.Null(result.Error);
    }

    [Fact]
    public async Task Resolver_CliModel_UnknownProvider_Error()
    {
        var runtime = await StubRuntimeAsync(new[] { Model("openai", "gpt-4o") });
        var result = ModelResolver.ResolveCliModel(null, "nope/gpt", null, runtime);
        Assert.Null(result.Model);
        // "nope" is not a provider; no model id matches either
        Assert.Contains("not found", result.Error);
    }

    [Fact]
    public async Task Resolver_CliModel_FallbackCustomModelId()
    {
        var models = new[] { Model("custom", "base-model") };
        var runtime = await StubRuntimeAsync(models);
        var result = ModelResolver.ResolveCliModel("custom", "local-llama", null, runtime);
        Assert.NotNull(result.Model);
        Assert.Equal("local-llama", result.Model!.Id);
        Assert.Contains("Using custom model id", result.Warning);
    }

    [Fact]
    public async Task Resolver_InitialModel_PriorityOrder()
    {
        var models = new[]
        {
            Model("openai", "gpt-4o"),
            Model("custom", "picked"),
        };
        var runtime = await StubRuntimeAsync(models);

        // Scoped wins when not continuing
        var scoped = new[] { new ScopedModel(models[1], null) };
        var initial = ModelResolver.FindInitialModel(
            null, null, scoped, false, null, null, null, null, runtime);
        Assert.Equal("picked", initial.Model!.Id);

        // Continuing skips scoped, no default, first available (none without auth)
        var continuing = ModelResolver.FindInitialModel(
            null, null, scoped, true, null, null, null, null, runtime);
        Assert.Null(continuing.Model);
    }

    [Fact]
    public async Task Resolver_Restore_FallbackChain()
    {
        var models = new[] { Model("custom", "still-here") };
        var runtime = await StubRuntimeAsync(models);
        var messages = new List<string>();
        var (model, fallback) = ModelResolver.RestoreModelFromSession(
            "gone", "missing", null, messages.Add, runtime);
        Assert.Null(model);
        Assert.Null(fallback);
        Assert.Contains(messages, m => m.Contains("model no longer exists"));
    }
}
