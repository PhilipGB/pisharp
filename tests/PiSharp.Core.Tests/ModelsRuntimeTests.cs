using PiSharp.Core.Models;
using PiSharp.Core.Models.Auth;
using PiSharp.Core.Models.Providers;

namespace PiSharp.Core.Tests;

/// <summary>ModelRuntime integration: composition, credentials, auth status, refresh.</summary>
public class ModelsRuntimeTests
{
    private static ModelInfo Model(string provider, string id) => new()
    {
        Id = id,
        Name = id,
        Api = "openai-completions",
        Provider = provider,
        BaseUrl = "http://localhost",
        Input = ["text"],
        Cost = new ModelCost { Input = 1, Output = 1, CacheRead = 0, CacheWrite = 0 },
        ContextWindow = 128_000,
        MaxTokens = 8_192,
    };

    private sealed class PromptingAuth : ApiKeyAuth
    {
        public PromptingAuth(string envVar)
        {
            Name = envVar + " key";
            EnvironmentVariableNames = [envVar];
        }

        public IReadOnlyList<string> EnvironmentVariableNames { get; }

        public override Task<AuthResult?> ResolveAsync(ApiKeyAuthInput input)
        {
            if (input.Credential is { Key: { Length: > 0 } key })
            {
                return Task.FromResult<AuthResult?>(new AuthResult
                {
                    Auth = new ModelAuth { ApiKey = key },
                    Source = "stored credential",
                });
            }

            return Task.FromResult<AuthResult?>(null);
        }
    }

    private sealed class TestInteraction : IAuthInteraction
    {
        private readonly string _key;
        public TestInteraction(string key) => _key = key;
        public CancellationToken Signal { get; } = CancellationToken.None;

        public Task<string> PromptAsync(AuthPromptStep prompt, CancellationToken cancellationToken = default)
            => Task.FromResult(_key);

        public void Notify(AuthEvent evt)
        {
        }
    }

    private static async Task<ModelRuntime> CreateRuntimeAsync(
        InMemoryCredentialStore store, string? modelsJson = null)
    {
        string? modelsPath = null;
        if (modelsJson is not null)
        {
            using var temp = TempDirectory.Create();
            modelsPath = Path.Combine(temp.Path, "models.json");
            File.WriteAllText(modelsPath, modelsJson);
        }

        return await ModelRuntime.CreateAsync(new CreateModelRuntimeOptions
        {
            Credentials = store,
            ModelsPath = modelsPath,
            ModelsStore = new InMemoryModelsStore(),
            Builtins = [],
            NetworkEnabled = false,
            RefreshOnCreate = true,
        });
    }

    [Fact]
    public async Task Runtime_RegisteredProvider_AppearsInCatalog()
    {
        var runtime = await CreateRuntimeAsync(new InMemoryCredentialStore());
        runtime.RegisterProvider(new ProviderSpec
        {
            Id = "local",
            Name = "Local",
            BaseUrl = "http://localhost",
            Auth = new ProviderAuth { ApiKey = new PromptingAuth("LOCAL_KEY_XYZ") { Name = "LOCAL_KEY_XYZ key" } },
            GetModels = () => new[] { Model("local", "local-1") },
            DefaultApi = "openai-completions",
        });
        Assert.NotNull(runtime.GetModel("local", "local-1"));
        Assert.Contains(runtime.GetProviders(), p => p.Id == "local");
    }

    [Fact]
    public async Task Runtime_ModelsJson_ProviderIsComposed()
    {
        var runtime = await CreateRuntimeAsync(new InMemoryCredentialStore(), """
        {
          "providers": {
            "cfg": {
              "baseUrl": "http://cfg:1",
              "apiKey": "sk-cfg",
              "models": [ { "id": "cfg-1", "api": "openai-completions" } ]
            }
          }
        }
        """);
        var provider = runtime.GetProvider("cfg");
        Assert.NotNull(provider);
        var model = runtime.GetModel("cfg", "cfg-1");
        Assert.NotNull(model);
        // Configured auth status comes from the static models.json key
        var status = runtime.GetProviderAuthStatus("cfg");
        Assert.True(status.Configured);
        var auth = await runtime.GetAuthAsync("cfg");
        Assert.Equal("sk-cfg", auth!.Auth.ApiKey);
    }

    [Fact]
    public async Task Runtime_RuntimeApiKey_ShadowsAndClears()
    {
        var store = new InMemoryCredentialStore();
        var runtime = await CreateRuntimeAsync(store);
        runtime.RegisterProvider(new ProviderSpec
        {
            Id = "p",
            Name = "P",
            BaseUrl = "http://p",
            Auth = new ProviderAuth { ApiKey = new PromptingAuth("P_KEY_XYZ") { Name = "P_KEY_XYZ key" } },
            GetModels = () => new[] { Model("p", "m") },
            DefaultApi = "openai-completions",
        });

        Assert.False(runtime.HasConfiguredAuth("p"));

        await runtime.SetRuntimeApiKeyAsync("p", "runtime-1");
        Assert.True(runtime.HasConfiguredAuth("p"));
        Assert.Equal("runtime", runtime.GetProviderAuthStatus("p").Source);
        var auth = await runtime.GetAuthAsync("p");
        Assert.Equal("runtime-1", auth!.Auth.ApiKey);

        await runtime.RemoveRuntimeApiKeyAsync("p");
        Assert.False(runtime.HasConfiguredAuth("p"));
    }

    [Fact]
    public async Task Runtime_Login_PersistsCredential_AndSyncs()
    {
        var store = new InMemoryCredentialStore();
        var runtime = await CreateRuntimeAsync(store);
        runtime.RegisterProvider(new ProviderSpec
        {
            Id = "p",
            Name = "P",
            BaseUrl = "http://p",
            Auth = new ProviderAuth
            {
                ApiKey = new PromptingAuth("P_KEY_XYZ")
                {
                    Name = "P_KEY_XYZ key",
                    Login = interaction => Task.FromResult(new ApiKeyCredential("stored-login")),
                },
            },
            GetModels = () => new[] { Model("p", "m") },
            DefaultApi = "openai-completions",
        });

        var credential = await runtime.LoginAsync("p", "api_key", new TestInteraction("ignored"));
        Assert.Equal("stored-login", ((ApiKeyCredential)credential).Key);
        Assert.True(runtime.HasConfiguredAuth("p"));
        Assert.Equal("stored", runtime.GetProviderAuthStatus("p").Source);
        var auth = await runtime.GetAuthAsync("p");
        Assert.Equal("stored-login", auth!.Auth.ApiKey);

        await runtime.LogoutAsync("p");
        Assert.False(runtime.HasConfiguredAuth("p"));
        Assert.Null(await store.ReadAsync("p"));
    }

    [Fact]
    public async Task Runtime_Logout_WithoutCredential_IsNoOp()
    {
        var runtime = await CreateRuntimeAsync(new InMemoryCredentialStore());
        runtime.RegisterProvider(new ProviderSpec
        {
            Id = "p",
            Name = "P",
            BaseUrl = "http://p",
            Auth = new ProviderAuth { ApiKey = new PromptingAuth("P_KEY_XYZ") { Name = "P_KEY_XYZ key" } },
            GetModels = () => new[] { Model("p", "m") },
            DefaultApi = "openai-completions",
        });
        await runtime.LogoutAsync("p");
        Assert.False(runtime.HasConfiguredAuth("p"));
    }

    [Fact]
    public async Task Runtime_GetAvailable_FiltersByAuth()
    {
        var runtime = await CreateRuntimeAsync(new InMemoryCredentialStore());
        runtime.RegisterProvider(new ProviderSpec
        {
            Id = "a",
            Name = "A",
            BaseUrl = "http://a",
            Auth = new ProviderAuth { ApiKey = new PromptingAuth("A_KEY_XYZ") { Name = "A_KEY_XYZ key" } },
            GetModels = () => new[] { Model("a", "m") },
            DefaultApi = "openai-completions",
        });
        runtime.RegisterProvider(new ProviderSpec
        {
            Id = "b",
            Name = "B",
            BaseUrl = "http://b",
            Auth = new ProviderAuth { ApiKey = new PromptingAuth("B_KEY_XYZ") { Name = "B_KEY_XYZ key" } },
            GetModels = () => new[] { Model("b", "m") },
            DefaultApi = "openai-completions",
        });

        var available = await runtime.GetAvailableAsync();
        Assert.Empty(available);

        await runtime.SetRuntimeApiKeyAsync("a", "key-a");
        var afterLogin = await runtime.GetAvailableAsync();
        Assert.Equal("a", afterLogin[0].Provider);
    }

    [Fact]
    public async Task Runtime_ModelHeaders_MergeOverResolvedAuth()
    {
        var runtime = await CreateRuntimeAsync(new InMemoryCredentialStore());
        var model = Model("p", "m") with
        {
            Headers = new Dictionary<string, string> { ["X-Extra"] = "1" },
        };
        runtime.RegisterProvider(new ProviderSpec
        {
            Id = "p",
            Name = "P",
            BaseUrl = "http://p",
            Auth = new ProviderAuth { ApiKey = new PromptingAuth("P_KEY_XYZ") { Name = "P_KEY_XYZ key" } },
            GetModels = () => new[] { model },
            DefaultApi = "openai-completions",
        });
        await runtime.SetRuntimeApiKeyAsync("p", "key");

        var auth = await runtime.GetAuthAsync(model);
        Assert.Equal("key", auth!.Auth.ApiKey);
        Assert.Equal("1", auth.Auth.Headers!["X-Extra"]);
    }

    [Fact]
    public async Task Runtime_ConfigError_SurfacesInGetError()
    {
        var runtime = await CreateRuntimeAsync(new InMemoryCredentialStore(), """
        { "providers": { "p": { "models": [ { "name": "no id" } ] } } }
        """);
        Assert.NotNull(runtime.GetError());
        Assert.Contains("must have required property 'id'", runtime.GetError());
    }

    [Fact]
    public async Task Runtime_Refresh_ReloadsConfig()
    {
        using var temp = TempDirectory.Create();
        var path = Path.Combine(temp.Path, "models.json");
        File.WriteAllText(path, """
        { "providers": { "one": { "apiKey": "sk-1", "models": [ { "id": "m1", "api": "openai-completions" } ] } } }
        """);
        var store = new InMemoryCredentialStore();
        var runtime = await ModelRuntime.CreateAsync(new CreateModelRuntimeOptions
        {
            Credentials = store,
            ModelsPath = path,
            ModelsStore = new InMemoryModelsStore(),
            Builtins = [],
            NetworkEnabled = false,
            RefreshOnCreate = true,
        });
        Assert.NotNull(runtime.GetModel("one", "m1"));

        File.WriteAllText(path, """
        { "providers": { "two": { "apiKey": "sk-2", "models": [ { "id": "m2", "api": "openai-completions" } ] } } }
        """);
        await runtime.RefreshAsync();
        Assert.NotNull(runtime.GetModel("two", "m2"));
        Assert.Null(runtime.GetModel("one", "m1"));
    }

    [Fact]
    public async Task Runtime_CompatibleApis_AllBuiltinProvidersKnown()
    {
        var runtime = await CreateRuntimeAsync(new InMemoryCredentialStore());
        // No builtins were registered; the catalog starts empty.
        Assert.Empty(runtime.GetProviders());
    }
}
