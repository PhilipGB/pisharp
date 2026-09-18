using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;
using PiSharp.Cli;
using PiSharp.Core.Models;
using PiSharp.Core.Models.Auth;
using PiSharp.Core.Models.Providers;
using PiSharp.Core.Settings;

namespace PiSharp.Core.Tests;

/// <summary>
/// Provider catalogue completeness (pinned builtinProviders set) and the models.json layer
/// through the request bridge: env/command/literal API keys, configured headers, authHeader,
/// and model overrides must reach the wire, and dynamic catalogs must cache through the
/// file store so offline startups restore the last known list.
/// </summary>
public sealed class ProviderCatalogTests : IDisposable
{
    private const string EnvKey = "PISHARP_SLICE5_MJ_KEY";

    [Fact]
    public void BuiltinsCoverThePinnedProviderSet()
    {
        var ids = BuiltinProviders.CreateBuiltins()
            .Select(provider => provider.Id)
            .ToHashSet(StringComparer.Ordinal);

        // Pinned builtinProviders() minus radius (account-scoped gateway; see PARITY.md).
        var expected = new[]
        {
            "amazon-bedrock", "ant-ling", "anthropic", "azure-openai-responses", "baseten",
            "cerebras", "cloudflare-ai-gateway", "cloudflare-workers-ai", "deepseek", "fireworks",
            "github-copilot", "google", "google-vertex", "groq", "huggingface", "kimi-coding",
            "minimax", "minimax-cn", "mistral", "moonshotai", "moonshotai-cn", "nvidia", "openai",
            "openai-codex", "opencode", "opencode-go", "openrouter", "qwen-token-plan",
            "qwen-token-plan-cn", "qwen-token-plan-individual", "together", "vercel-ai-gateway",
            "xai", "xiaomi", "xiaomi-token-plan-ams", "xiaomi-token-plan-cn", "xiaomi-token-plan-sgp",
            "zai", "zai-coding-cn",
        };
        Assert.Equal(expected.ToHashSet(StringComparer.Ordinal), ids);
        Assert.DoesNotContain("radius", ids);
    }

    [Fact]
    public void EveryBuiltinOffersItsPinnedDefaultModel()
    {
        foreach (var provider in BuiltinProviders.CreateBuiltins())
        {
            if (!ModelResolver.DefaultModelPerProvider.TryGetValue(provider.Id, out var defaultId))
            {
                continue;
            }

            var models = provider.GetModels();
            Assert.True(models.Any(model => model.Id == defaultId),
                $"provider {provider.Id} is missing its pinned default model {defaultId}");
        }
    }

    [Fact]
    public void BuiltinProviderMetadataMatchesPinned()
    {
        var builtins = BuiltinProviders.CreateBuiltins().ToDictionary(p => p.Id, StringComparer.Ordinal);
        Assert.Equal("https://api.deepseek.com", builtins["deepseek"].BaseUrl);
        Assert.Equal("https://api.groq.com/openai/v1", builtins["groq"].BaseUrl);
        Assert.Equal("https://api.x.ai/v1", builtins["xai"].BaseUrl);
        Assert.Equal("https://api.z.ai/api/coding/paas/v4", builtins["zai"].BaseUrl);
        Assert.Equal("https://open.bigmodel.cn/api/coding/paas/v4", builtins["zai-coding-cn"].BaseUrl);
        Assert.Equal("https://api.moonshot.cn/v1", builtins["moonshotai-cn"].BaseUrl);
        Assert.Equal("https://api.xiaomimimo.com/v1", builtins["xiaomi"].BaseUrl);
        Assert.Equal("https://token-plan.cn-beijing.maas.aliyuncs.com/compatible-mode/v1", builtins["qwen-token-plan-cn"].BaseUrl);
        Assert.Null(builtins["amazon-bedrock"].BaseUrl);
        Assert.Null(builtins["opencode"].BaseUrl);
        Assert.Equal(ModelApi.BedrockConverseStream, builtins["amazon-bedrock"].DefaultApi);
        Assert.Equal(ModelApi.MistralConversations, builtins["mistral"].DefaultApi);
        Assert.Equal(ModelApi.OpenAiResponses, builtins["xai"].DefaultApi);
        Assert.Equal(ModelApi.AnthropicMessages, builtins["kimi-coding"].DefaultApi);
        Assert.NotNull(builtins["xai"].Auth.OAuth);
        Assert.NotNull(builtins["github-copilot"].Auth.OAuth);
        Assert.NotNull(builtins["openai-codex"].Auth.OAuth);
        Assert.Null(builtins["openai-codex"].Auth.ApiKey); // pinned openai-codex is OAuth-only
    }

    [Fact]
    public async Task ModelsJsonProviderSendsEnvKeyHeadersAndAuthHeader()
    {
        using var temp = TempDirectory.Create();
        var modelsPath = WriteModelsJson(temp.Path, """
        {
            "providers": {
                "custom": {
                    "baseUrl": "http://custom.test/v1",
                    "apiKey": "$PISHARP_SLICE5_MJ_KEY",
                    "authHeader": true,
                    "headers": { "X-App": "pi" },
                    "models": [
                        { "id": "m1", "api": "openai-completions", "maxTokens": 2048 }
                    ]
                }
            }
        }
        """);

        Environment.SetEnvironmentVariable(EnvKey, "mj-key-123");
        try
        {
            var (runtime, state) = await CreateRuntimeAsync(modelsPath);
            await SelectModelAsync(runtime, state, "custom/m1");

            using var recorder = new RecordingHandler();
            using var bridge = new ModelRuntimeChatClient(runtime, () => state.Current, () => 0, recorder);
            await bridge.GetResponseAsync([new ChatMessage(ChatRole.User, "hi")]);

            var (headers, body) = recorder.Last();
            var json = JsonDocument.Parse(body);
            Assert.Equal("m1", json.RootElement.GetProperty("model").GetString());
            Assert.Equal(2_048, json.RootElement.GetProperty("max_completion_tokens").GetInt32());
            Assert.Equal("Bearer mj-key-123", headers["Authorization"]);
            Assert.Equal("pi", headers["X-App"]);
        }
        finally
        {
            Environment.SetEnvironmentVariable(EnvKey, null);
        }
    }

    [Fact]
    public async Task ModelsJsonCommandApiKeyResolvesAtRequestTime()
    {
        using var temp = TempDirectory.Create();
        var modelsPath = WriteModelsJson(temp.Path, """
        {
            "providers": {
                "cmdprov": {
                    "baseUrl": "http://cmd.test/v1",
                    "apiKey": "!echo cfg-123",
                    "models": [ { "id": "cm1", "api": "openai-completions" } ]
                }
            }
        }
        """);

        var (runtime, state) = await CreateRuntimeAsync(modelsPath);
        await SelectModelAsync(runtime, state, "cmdprov/cm1");

        using var recorder = new RecordingHandler();
        using var bridge = new ModelRuntimeChatClient(runtime, () => state.Current, () => 0, recorder);
        await bridge.GetResponseAsync([new ChatMessage(ChatRole.User, "hi")]);

        var (headers, _) = recorder.Last();
        Assert.Equal("Bearer cfg-123", headers["Authorization"]);
    }

    [Fact]
    public async Task ModelsJsonModelOverridesApplyToBuiltins()
    {
        using var temp = TempDirectory.Create();
        var modelsPath = WriteModelsJson(temp.Path, """
        {
            "providers": {
                "openai": {
                    "modelOverrides": {
                        "gpt-4o": { "maxTokens": 512 }
                    }
                }
            }
        }
        """);

        var (runtime, state) = await CreateRuntimeAsync(modelsPath);
        await runtime.SetRuntimeApiKeyAsync("openai", "oai-key");
        await SelectModelAsync(runtime, state, "openai/gpt-4o");

        using var recorder = new RecordingHandler();
        using var bridge = new ModelRuntimeChatClient(runtime, () => state.Current, () => 0, recorder);
        await bridge.GetResponseAsync([new ChatMessage(ChatRole.User, "hi")]);

        var (_, body) = recorder.Last();
        var json = JsonDocument.Parse(body);
        Assert.Equal(512, json.RootElement.GetProperty("max_completion_tokens").GetInt32());
    }

    [Fact]
    public async Task DynamicCatalogCachesThroughTheFileStoreForOfflineStartups()
    {
        List<ModelInfo> dynamicModels = [];
        using var temp = TempDirectory.Create();
        var modelsPath = WriteModelsJson(temp.Path, """{ "providers": {} }""");
        var storePath = Path.Combine(temp.Path, "models-store.json");

        var spec = new ProviderSpec
        {
            Id = "dyn",
            Name = "Dyn",
            BaseUrl = "http://dyn.test/v1",
            Auth = new ProviderAuth(
                new EnvApiKeyAuth { Name = "d", EnvironmentVariableNames = ["DYN_TEST_KEY"] }),
            GetModels = () => dynamicModels,
            RefreshModelsAsync = context => Task.Run(async () =>
            {
                if (context.AllowNetwork == true)
                {
                    var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                    var online = Model("online-1");
                    await context.Publish(new ModelsPublication
                    {
                        Update = () => dynamicModels = [online],
                        Persist = new ModelsStoreEntry
                        {
                            Models = [online],
                            CheckedAt = now,
                            LastModified = now,
                        },
                    });
                }
                else if (context.Stored is { Models: { Count: > 0 } } stored)
                {
                    await context.Publish(new ModelsPublication
                    {
                        Update = () => dynamicModels = stored.Models.ToList(),
                    });
                }
            }, context.CancellationToken),
            DefaultApi = ModelApi.OpenAiCompletions,
        };

        // Online startup: the refresh publishes the list and persists it next to models.json.
        var online = await CreateRuntimeCoreAsync(modelsPath, networkEnabled: true);
        online.RegisterProvider(spec);
        await online.SetRuntimeApiKeyAsync("dyn", "k");
        await online.RefreshAsync(new ModelsRefreshOptions { AllowNetwork = true });
        Assert.Contains(online.GetAvailableSnapshot(), model => model.Id == "online-1");
        Assert.True(File.Exists(storePath));
        Assert.Contains("online-1", await File.ReadAllTextAsync(storePath));

        // Offline startup with the same file: the cached list is restored without network.
        var offline = await CreateRuntimeCoreAsync(modelsPath, networkEnabled: false);
        offline.RegisterProvider(spec);
        await offline.SetRuntimeApiKeyAsync("dyn", "k");
        await offline.RefreshAsync(new ModelsRefreshOptions { AllowNetwork = false });
        Assert.Contains(offline.GetAvailableSnapshot(), model => model.Id == "online-1");
    }

    public void Dispose() => Environment.SetEnvironmentVariable(EnvKey, null);

    private static ModelInfo Model(string id) => new()
    {
        Id = id,
        Name = id,
        Api = ModelApi.OpenAiCompletions,
        Provider = "dyn",
        BaseUrl = "http://dyn.test/v1",
        Reasoning = false,
        Input = ["text"],
        Cost = new ModelCost { Input = 0, Output = 0, CacheRead = 0, CacheWrite = 0 },
        ContextWindow = 8_000,
        MaxTokens = 1_000,
    };

    private static async Task<(ModelRuntime Runtime, ModelSessionState State)> CreateRuntimeAsync(string modelsPath)
    {
        var runtime = await CreateRuntimeCoreAsync(modelsPath, networkEnabled: false);
        var settings = await SettingsManager.CreateFromStorageAsync(new InMemorySettingsStorage());
        var state = new ModelSessionState(runtime, settings);
        return (runtime, state);
    }

    private static async Task<ModelRuntime> CreateRuntimeCoreAsync(string modelsPath, bool networkEnabled)
    {
        var runtime = await ModelRuntime.CreateAsync(new CreateModelRuntimeOptions
        {
            Credentials = new InMemoryCredentialStore(),
            Builtins = BuiltinProviders.CreateBuiltins(),
            ModelsPath = modelsPath,
            NetworkEnabled = networkEnabled,
            RefreshOnCreate = false,
        });
        return runtime;
    }

    private static async Task SelectModelAsync(ModelRuntime runtime, ModelSessionState state, string reference)
    {
        var slash = reference.IndexOf('/');
        var model = runtime.GetModel(reference[..slash], reference[(slash + 1)..])
            ?? throw new InvalidOperationException($"model not found: {reference}");
        await state.SetModelAsync(model, new ModelMutationOptions());
    }

    private static string WriteModelsJson(string dir, string json)
    {
        var path = Path.Combine(dir, "models.json");
        File.WriteAllText(path, json);
        return path;
    }

    /// <summary>Records every request and answers with a canned chat completion.</summary>
    private sealed class RecordingHandler : HttpMessageHandler
    {
        private readonly List<(Dictionary<string, string> Headers, string Body)> _requests = [];

        public IReadOnlyList<(Dictionary<string, string> Headers, string Body)> Requests => _requests;

        public (Dictionary<string, string> Headers, string Body) Last() => _requests[^1];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var body = request.Content is null
                ? string.Empty
                : await request.Content.ReadAsStringAsync(cancellationToken);
            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var header in request.Headers)
            {
                headers[header.Key] = string.Join(",", header.Value);
            }
            _requests.Add((headers, body));

            var model = JsonDocument.Parse(body).RootElement.GetProperty("model").GetString()!;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    """{"id":"c1","object":"chat.completion","created":1,"model":"__M__","choices":[{"index":0,"message":{"role":"assistant","content":"ok"},"finish_reason":"stop"}],"usage":{"prompt_tokens":1,"completion_tokens":1,"total_tokens":2}}"""
                        .Replace("__M__", model),
                    Encoding.UTF8,
                    "application/json"),
            };
        }
    }
}
