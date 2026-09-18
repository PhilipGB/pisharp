using PiSharp.Core.Models.Auth;
using PiSharp.Core.Models.OAuth;

namespace PiSharp.Core.Models.Providers;

/// <summary>
/// Built-in provider catalogue (pinned Pi: packages/ai/providers/all.ts). The full
/// pinned built-in provider set is registered with exact id/name/baseUrl/env/auth/api
/// metadata.
///
/// Intentional differences from pinned Pi:
/// - The pinned generated model data (providers/data/*.json) is produced at npm build
///   time and not committed to the source repository, so static model lists ship the
///   curated models plus each provider's pinned default model with conservative
///   baseline metadata (zero cost). The pi.dev remote-catalog overlay
///   (RemoteCatalogProvider) and models.json custom providers fill in the full model
///   list at runtime, exactly as in pinned Pi.
/// - radius is not registered: its gateway/baseUrl and models are account-scoped and
///   only discoverable after OAuth, which the static built-in layer cannot express.
/// - Special credential paths (AWS profile/chain for bedrock, ADC/service-account for
///   vertex, Cloudflare account/gateway ids, per-credential copilot model filtering)
///   are not portable to this runtime; the env-based paths are registered instead.
/// - The request bridge (Phase 2) serves openai-completions endpoints; built-in
///   models on other APIs are catalogued and selectable but throw
///   UnsupportedCapability at request time until those transports land.
/// </summary>
public static class BuiltinProviders
{
    /// <summary>Provider id for the OpenAI-compatible local server (llama.cpp) workflow.</summary>
    public const string LlamaCppProviderId = "llama.cpp";

    /// <summary>
    /// Creates the built-in provider set in pinned catalogue order (pinned
    /// builtinProviders(), minus radius which needs an account-scoped gateway config).
    /// </summary>
    public static IReadOnlyList<ProviderSpec> CreateBuiltins() =>
    [
        AmazonBedrock(),
        AntLing(),
        Anthropic(),
        AzureOpenAiResponses(),
        Baseten(),
        Cerebras(),
        CloudflareAIGateway(),
        CloudflareWorkersAI(),
        DeepSeek(),
        Fireworks(),
        GitHubCopilot(),
        Google(),
        GoogleVertex(),
        Groq(),
        HuggingFace(),
        KimiCoding(),
        CreateLlamaCppProvider(),
        MiniMax(),
        MiniMaxCn(),
        Mistral(),
        MoonshotAi(),
        MoonshotAiCn(),
        Nvidia(),
        OpenAi(),
        OpenAiCodex(),
        OpenCode(),
        OpenCodeGo(),
        OpenRouter(),
        QwenTokenPlan(),
        QwenTokenPlanCn(),
        QwenTokenPlanIndividual(),
        Together(),
        VercelAiGateway(),
        Xai(),
        Xiaomi(),
        XiaomiTokenPlanAms(),
        XiaomiTokenPlanCn(),
        XiaomiTokenPlanSgp(),
        Zai(),
        ZaiCodingCn(),
    ];

    /// <summary>
    /// Conservative offline baseline for synthesized default models: the pinned generated
    /// catalog carries the real metadata, but it is build-time data not present in the
    /// source repository; the pi.dev remote-catalog overlay replaces these at runtime.
    /// </summary>
    private const int BaselineContextWindow = 128_000;
    private const int BaselineMaxTokens = 32_768;

    /// <summary>
    /// Standard env-key built-in provider (pinned envApiKeyAuth + openAICompletionsApi
    /// pattern): exact pinned id/name/baseUrl/env/api metadata plus the pinned default
    /// model as the offline baseline.
    /// </summary>
    private static ProviderSpec SimpleProvider(
        string id,
        string name,
        string? baseUrl,
        string apiKeyName,
        string[] envNames,
        string api,
        OAuthAuth? oauth = null,
        string? modelBaseUrl = null)
    {
        return new ProviderSpec
        {
            Id = id,
            Name = name,
            BaseUrl = baseUrl,
            Auth = new ProviderAuth(
                new EnvApiKeyAuth
                {
                    Name = apiKeyName,
                    EnvironmentVariableNames = envNames,
                    LoginMessage = $"Enter {apiKeyName}",
                },
                oauth),
            GetModels = () => [DefaultModelModel(id, modelBaseUrl ?? baseUrl ?? FallbackBaseUrl(id), api)],
            DefaultApi = api,
        };
    }

    /// <summary>
    /// Canonical endpoint for providers whose pinned models carry per-model base URLs
    /// (the generated data is not in the repository); cloudflare keeps the pinned
    /// env-template URLs verbatim.
    /// </summary>
    private static string FallbackBaseUrl(string providerId) => providerId switch
    {
        "amazon-bedrock" => "https://bedrock-runtime.us-east-1.amazonaws.com",
        "azure-openai-responses" => "https://openai.azure.com",
        "cloudflare-ai-gateway" => "https://gateway.ai.cloudflare.com/v1/{CLOUDFLARE_ACCOUNT_ID}/{CLOUDFLARE_GATEWAY_ID}/compat",
        "cloudflare-workers-ai" => "https://api.cloudflare.com/client/v4/accounts/{CLOUDFLARE_ACCOUNT_ID}/ai/v1",
        "google-vertex" => "https://aiplatform.googleapis.com",
        "opencode" => "https://opencode.ai/zen/v1",
        "opencode-go" => "https://opencode.ai/zen/v1",
        _ => throw new InvalidOperationException($"No fallback base URL for provider {providerId}"),
    };

    private static ModelInfo DefaultModelModel(string providerId, string baseUrl, string api) => new()
    {
        Id = ModelResolver.DefaultModelPerProvider[providerId],
        Name = ModelResolver.DefaultModelPerProvider[providerId],
        Api = api,
        Provider = providerId,
        BaseUrl = baseUrl,
        Reasoning = false,
        Input = ["text"],
        Cost = new ModelCost { Input = 0, Output = 0, CacheRead = 0, CacheWrite = 0 },
        ContextWindow = BaselineContextWindow,
        MaxTokens = BaselineMaxTokens,
    };

    private static ProviderSpec AmazonBedrock() => SimpleProvider(
        "amazon-bedrock", "Amazon Bedrock", null,
        "AWS bearer token", ["AWS_BEARER_TOKEN_BEDROCK"],
        ModelApi.BedrockConverseStream);

    private static ProviderSpec AntLing() => SimpleProvider(
        "ant-ling", "Ant Ling", "https://api.ant-ling.com/v1",
        "Ant Ling API key", ["ANT_LING_API_KEY"],
        ModelApi.OpenAiCompletions);

    private static ProviderSpec AzureOpenAiResponses() => SimpleProvider(
        "azure-openai-responses", "Azure OpenAI", null,
        "Azure OpenAI API key", ["AZURE_OPENAI_API_KEY"],
        ModelApi.AzureOpenAiResponses);

    private static ProviderSpec Baseten() => SimpleProvider(
        "baseten", "Baseten", "https://inference.baseten.co/v1",
        "Baseten API key", ["BASETEN_API_KEY"],
        ModelApi.OpenAiCompletions);

    private static ProviderSpec Cerebras() => SimpleProvider(
        "cerebras", "Cerebras", "https://api.cerebras.ai/v1",
        "Cerebras API key", ["CEREBRAS_API_KEY"],
        ModelApi.OpenAiCompletions);

    private static ProviderSpec CloudflareAIGateway() => SimpleProvider(
        "cloudflare-ai-gateway", "Cloudflare AI Gateway", null,
        "Cloudflare AI Gateway API key", ["CLOUDFLARE_API_KEY"],
        ModelApi.OpenAiCompletions);

    private static ProviderSpec CloudflareWorkersAI() => SimpleProvider(
        "cloudflare-workers-ai", "Cloudflare Workers AI", null,
        "Cloudflare Workers AI API key", ["CLOUDFLARE_API_KEY"],
        ModelApi.OpenAiCompletions);

    private static ProviderSpec DeepSeek() => SimpleProvider(
        "deepseek", "DeepSeek", "https://api.deepseek.com",
        "DeepSeek API key", ["DEEPSEEK_API_KEY"],
        ModelApi.OpenAiCompletions);

    private static ProviderSpec Fireworks() => SimpleProvider(
        "fireworks", "Fireworks", "https://api.fireworks.ai/inference",
        "Fireworks API key", ["FIREWORKS_API_KEY"],
        ModelApi.OpenAiCompletions);

    private static ProviderSpec GitHubCopilot() => SimpleProvider(
        "github-copilot", "GitHub Copilot", "https://api.individual.githubcopilot.com",
        "GitHub Copilot token", ["COPILOT_GITHUB_TOKEN"],
        ModelApi.OpenAiCompletions,
        oauth: new GitHubCopilotOAuth { Name = "GitHub Copilot", IsSubscription = true });

    private static ProviderSpec GoogleVertex() => SimpleProvider(
        "google-vertex", "Google Vertex", null,
        "Google Cloud API key", ["GOOGLE_CLOUD_API_KEY"],
        ModelApi.GoogleVertex);

    private static ProviderSpec Groq() => SimpleProvider(
        "groq", "Groq", "https://api.groq.com/openai/v1",
        "Groq API key", ["GROQ_API_KEY"],
        ModelApi.OpenAiCompletions);

    private static ProviderSpec HuggingFace() => SimpleProvider(
        "huggingface", "Hugging Face", "https://router.huggingface.co/v1",
        "Hugging Face token", ["HF_TOKEN"],
        ModelApi.OpenAiCompletions);

    private static ProviderSpec KimiCoding() => SimpleProvider(
        "kimi-coding", "Kimi For Coding", "https://api.kimi.com/coding",
        "Kimi API key", ["KIMI_API_KEY"],
        ModelApi.AnthropicMessages,
        oauth: new KimiCodingOAuth
        {
            Name = "Kimi Code (subscription)",
            IsSubscription = true,
            LoginLabel = "Sign in with Kimi Code",
        });

    private static ProviderSpec MiniMax() => SimpleProvider(
        "minimax", "MiniMax", "https://api.minimax.io/anthropic",
        "MiniMax API key", ["MINIMAX_API_KEY"],
        ModelApi.AnthropicMessages);

    private static ProviderSpec MiniMaxCn() => SimpleProvider(
        "minimax-cn", "MiniMax CN", "https://api.minimaxi.com/anthropic",
        "MiniMax CN API key", ["MINIMAX_CN_API_KEY"],
        ModelApi.AnthropicMessages);

    private static ProviderSpec Mistral() => SimpleProvider(
        "mistral", "Mistral", "https://api.mistral.ai",
        "Mistral API key", ["MISTRAL_API_KEY"],
        ModelApi.MistralConversations);

    private static ProviderSpec MoonshotAi() => SimpleProvider(
        "moonshotai", "Moonshot AI", "https://api.moonshot.ai/v1",
        "Moonshot AI API key", ["MOONSHOT_API_KEY"],
        ModelApi.OpenAiCompletions);

    private static ProviderSpec MoonshotAiCn() => SimpleProvider(
        "moonshotai-cn", "Moonshot AI CN", "https://api.moonshot.cn/v1",
        "Moonshot AI API key", ["MOONSHOT_API_KEY"],
        ModelApi.OpenAiCompletions);

    private static ProviderSpec Nvidia() => SimpleProvider(
        "nvidia", "NVIDIA", "https://integrate.api.nvidia.com/v1",
        "NVIDIA API key", ["NVIDIA_API_KEY"],
        ModelApi.OpenAiCompletions);

    private static ProviderSpec OpenAiCodex() => new()
    {
        Id = "openai-codex",
        Name = "OpenAI Codex",
        BaseUrl = "https://chatgpt.com/backend-api",
        // Pinned openai-codex is OAuth-only; no API-key method is fabricated.
        Auth = new ProviderAuth(null, new OpenAiCodexOAuth
        {
            Name = "OpenAI (ChatGPT Plus/Pro)",
            IsSubscription = true,
        }),
        GetModels = () => [DefaultModelModel("openai-codex", "https://chatgpt.com/backend-api", ModelApi.OpenAiCodexResponses)],
        DefaultApi = ModelApi.OpenAiCodexResponses,
    };

    private static ProviderSpec OpenCode() => SimpleProvider(
        "opencode", "OpenCode Zen", null,
        "OpenCode API key", ["OPENCODE_API_KEY"],
        ModelApi.OpenAiCompletions);

    private static ProviderSpec OpenCodeGo() => SimpleProvider(
        "opencode-go", "OpenCode Go", null,
        "OpenCode API key", ["OPENCODE_API_KEY"],
        ModelApi.OpenAiCompletions);

    private static ProviderSpec QwenTokenPlan() => SimpleProvider(
        "qwen-token-plan", "Qwen Token Plan",
        "https://token-plan.ap-southeast-1.maas.aliyuncs.com/compatible-mode/v1",
        "Qwen Token Plan API key", ["QWEN_TOKEN_PLAN_API_KEY"],
        ModelApi.OpenAiCompletions);

    private static ProviderSpec QwenTokenPlanCn() => SimpleProvider(
        "qwen-token-plan-cn", "Qwen Token Plan CN",
        "https://token-plan.cn-beijing.maas.aliyuncs.com/compatible-mode/v1",
        "Qwen Token Plan CN API key", ["QWEN_TOKEN_PLAN_CN_API_KEY"],
        ModelApi.OpenAiCompletions);

    private static ProviderSpec QwenTokenPlanIndividual() => SimpleProvider(
        "qwen-token-plan-individual", "Qwen Token Plan Individual",
        "https://token-plan.ap-southeast-1.maas.aliyuncs.com/compatible-mode/v1",
        "Qwen Token Plan Individual API key", ["QWEN_TOKEN_PLAN_API_KEY"],
        ModelApi.OpenAiCompletions);

    private static ProviderSpec Together() => SimpleProvider(
        "together", "Together", "https://api.together.ai/v1",
        "Together API key", ["TOGETHER_API_KEY"],
        ModelApi.OpenAiCompletions);

    private static ProviderSpec VercelAiGateway() => SimpleProvider(
        "vercel-ai-gateway", "Vercel AI Gateway", "https://ai-gateway.vercel.sh",
        "Vercel AI Gateway API key", ["AI_GATEWAY_API_KEY"],
        ModelApi.AnthropicMessages);

    private static ProviderSpec Xai() => SimpleProvider(
        "xai", "xAI", "https://api.x.ai/v1",
        "xAI API key", ["XAI_API_KEY"],
        ModelApi.OpenAiResponses,
        oauth: new XaiOAuth
        {
            Name = "xAI (Grok/X subscription)",
            IsSubscription = true,
            LoginLabel = "Sign in with SuperGrok or X Premium",
        });

    private static ProviderSpec Xiaomi() => SimpleProvider(
        "xiaomi", "Xiaomi", "https://api.xiaomimimo.com/v1",
        "Xiaomi API key", ["XIAOMI_API_KEY"],
        ModelApi.OpenAiCompletions);

    private static ProviderSpec XiaomiTokenPlanAms() => SimpleProvider(
        "xiaomi-token-plan-ams", "Xiaomi Token Plan AMS",
        "https://token-plan-ams.xiaomimimo.com/v1",
        "Xiaomi Token Plan AMS API key", ["XIAOMI_TOKEN_PLAN_AMS_API_KEY"],
        ModelApi.OpenAiCompletions);

    private static ProviderSpec XiaomiTokenPlanCn() => SimpleProvider(
        "xiaomi-token-plan-cn", "Xiaomi Token Plan CN",
        "https://token-plan-cn.xiaomimimo.com/v1",
        "Xiaomi Token Plan CN API key", ["XIAOMI_TOKEN_PLAN_CN_API_KEY"],
        ModelApi.OpenAiCompletions);

    private static ProviderSpec XiaomiTokenPlanSgp() => SimpleProvider(
        "xiaomi-token-plan-sgp", "Xiaomi Token Plan SGP",
        "https://token-plan-sgp.xiaomimimo.com/v1",
        "Xiaomi Token Plan SGP API key", ["XIAOMI_TOKEN_PLAN_SGP_API_KEY"],
        ModelApi.OpenAiCompletions);

    private static ProviderSpec Zai() => SimpleProvider(
        "zai", "Z.AI", "https://api.z.ai/api/coding/paas/v4",
        "Z.AI API key", ["ZAI_API_KEY"],
        ModelApi.OpenAiCompletions);

    private static ProviderSpec ZaiCodingCn() => SimpleProvider(
        "zai-coding-cn", "Z.AI Coding CN", "https://open.bigmodel.cn/api/coding/paas/v4",
        "Z.AI Coding CN API key", ["ZAI_CODING_CN_API_KEY"],
        ModelApi.OpenAiCompletions);

    /// <summary>
    /// Creates the dynamic llama.cpp provider (pinned createLlamaProvider). The model
    /// list is empty until a refresh syncs the router catalog: models appear when a
    /// stored credential (or /login) records the server URL, or the /llama command
    /// forces a refresh. Requests keyless unless LLAMA_API_KEY or a stored key says so.
    /// </summary>
    public static ProviderSpec CreateLlamaCppProvider()
    {
        var models = new List<ModelInfo>();
        var gate = new object();

        return new ProviderSpec
        {
            Id = LlamaCppProviderId,
            Name = "llama.cpp",
            BaseUrl = LlamaUrls.InferenceUrl(LlamaUrls.DefaultServerUrl),
            Auth = new ProviderAuth(new LlamaServerApiKeyAuth
            {
                Name = "llama.cpp server",
                Check = async input =>
                {
                    var serverUrl = await LlamaServerApiKeyAuth.ResolveServerUrlAsync(input);
                    return serverUrl is null
                        ? null
                        : new AuthCheck(
                            input.Credential is not null ? "stored credential" : LlamaUrls.BaseUrlEnvironmentVariable,
                            "api_key");
                },
            }),
            GetModels = () =>
            {
                lock (gate)
                {
                    return models.ToArray();
                }
            },
            DefaultApi = ModelApi.OpenAiCompletions,
            RefreshModelsAsync = async context =>
            {
                // Offline/cache-only phase: restore the last synced catalog (pinned
                // refreshModels stored restore).
                var restored = context.Stored is { } stored
                    ? stored.Models
                        .Where(model => model.Provider == LlamaCppProviderId && model.Api == ModelApi.OpenAiCompletions)
                        .ToArray()
                    : Array.Empty<ModelInfo>();
                if (!await context.Publish(new ModelsPublication
                {
                    Update = () =>
                    {
                        lock (gate)
                        {
                            models = restored.ToList();
                        }
                    },
                }))
                {
                    return;
                }

                // Pinned refreshes live when the refresh credential resolves a server URL:
                // either the stored credential's env or an ambient LLAMA_BASE_URL (the
                // catalog synthesizes { key, env } from the ambient auth resolve).
                if (!context.AllowNetwork || context.CancellationToken.IsCancellationRequested)
                {
                    return;
                }

                if (context.Credential is not ApiKeyCredential apiCredential)
                {
                    return;
                }

                if (apiCredential.Env is not { } credentialEnv ||
                    !credentialEnv.TryGetValue(LlamaUrls.BaseUrlEnvironmentVariable, out var serverUrl))
                {
                    return;
                }

                var client = new LlamaClient(serverUrl, apiCredential.Key);
                var catalog = await client.ListAsync(cancellationToken: context.CancellationToken);
                if (context.CancellationToken.IsCancellationRequested)
                {
                    return;
                }

                var routerAutoload = await RouterAutoloadEnabledAsync(client, catalog, context.CancellationToken);
                var refreshed = catalog
                    .Where(model => LlamaModelIsSelectable(model, routerAutoload))
                    .Select(model => LlamaToPiModel(model, serverUrl))
                    .ToArray();
                await context.Publish(new ModelsPublication
                {
                    Persist = new ModelsStoreEntry
                    {
                        Models = refreshed,
                        CheckedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                        LastModified = 0,
                    },
                    Update = () =>
                    {
                        lock (gate)
                        {
                            models = refreshed.ToList();
                        }
                    },
                });
            },
        };
    }

    private static async Task<bool> RouterAutoloadEnabledAsync(
        LlamaClient client,
        IReadOnlyList<LlamaModelInfo> catalog,
        CancellationToken cancellationToken)
    {
        if (catalog.All(model =>
                !(model.Status.Value == LlamaModelStatusValues.Unloaded &&
                  string.Equals(model.Source, "preset", StringComparison.Ordinal))))
        {
            return false;
        }

        try
        {
            return await client.GetModelsAutoloadAsync(cancellationToken);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Pinned modelIsSelectable: loaded and sleeping models are selectable (sleeping
    /// wake on request); unloaded presets are routable only when the router autoloads
    /// them on first use.
    /// </summary>
    internal static bool LlamaModelIsSelectable(LlamaModelInfo model, bool routerAutoload)
    {
        if (model.Status.Value is LlamaModelStatusValues.Loaded or LlamaModelStatusValues.Sleeping)
        {
            return true;
        }

        return routerAutoload &&
               model.Status.Value == LlamaModelStatusValues.Unloaded &&
               !model.Status.Failed &&
               string.Equals(model.Source, "preset", StringComparison.Ordinal);
    }

    /// <summary>
    /// Pinned toPiModel: context window from the GGUF metadata (fallback 128k), max
    /// output pinned to the context window, image modality when the architecture says so,
    /// zero cost, no reasoning.
    /// </summary>
    internal static ModelInfo LlamaToPiModel(LlamaModelInfo model, string serverUrl)
    {
        var reportedContext = model.Meta?.NCtx ?? model.Meta?.NCtxTrain;
        var contextWindow = reportedContext is > 0 ? reportedContext.Value : 128_000;
        var imageInput = model.Architecture?.InputModalities
            ?.Any(modality => string.Equals(modality, "image", StringComparison.OrdinalIgnoreCase)) == true;
        return new ModelInfo
        {
            Id = model.Id,
            Name = model.Id,
            Api = ModelApi.OpenAiCompletions,
            Provider = LlamaCppProviderId,
            BaseUrl = LlamaUrls.InferenceUrl(serverUrl),
            Reasoning = false,
            Input = imageInput ? ["text", "image"] : ["text"],
            Cost = new ModelCost { Input = 0, Output = 0, CacheRead = 0, CacheWrite = 0 },
            ContextWindow = contextWindow,
            MaxTokens = contextWindow,
        };
    }

    /// <summary>
    /// api-key auth for the llama.cpp router (pinned createLlamaProvider auth): the server
    /// URL comes from the stored credential's env or the LLAMA_BASE_URL variable, and the
    /// key falls back to LLAMA_API_KEY then the keyless "local" default.
    /// </summary>
    public sealed class LlamaServerApiKeyAuth : ApiKeyAuth
    {
        protected override Func<IAuthInteraction, Task<ApiKeyCredential>>? CreateDefaultLogin() =>
            interaction => LoginAsync(interaction);

        /// <summary>
        /// Pinned llama login: prompt for the server URL (placeholder from LLAMA_BASE_URL or
        /// the default), an optional API key, verify the router answers, and persist the URL
        /// in the credential's env.
        /// </summary>
        public static async Task<ApiKeyCredential> LoginAsync(IAuthInteraction interaction)
        {
            interaction.Signal.ThrowIfCancellationRequested();
            var placeholder = Environment.GetEnvironmentVariable(LlamaUrls.BaseUrlEnvironmentVariable);
            var enteredUrl = await interaction.PromptAsync(
                new TextPromptStep("llama.cpp server URL", string.IsNullOrWhiteSpace(placeholder) ? LlamaUrls.DefaultServerUrl : placeholder),
                interaction.Signal);
            var serverUrl = LlamaUrls.Normalize(
                string.IsNullOrWhiteSpace(enteredUrl) ? (placeholder ?? LlamaUrls.DefaultServerUrl) : enteredUrl);

            interaction.Signal.ThrowIfCancellationRequested();
            var apiKey = (await interaction.PromptAsync(
                new SecretPromptStep("API key (optional)"), interaction.Signal)).Trim();

            // Pinned verifies the server before persisting; a dead server aborts the login.
            var client = new LlamaClient(serverUrl, apiKey.Length > 0 ? apiKey : null);
            await client.ListAsync(cancellationToken: interaction.Signal);

            return new ApiKeyCredential(
                apiKey.Length > 0 ? apiKey : null,
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    [LlamaUrls.BaseUrlEnvironmentVariable] = serverUrl,
                });
        }

        public override async Task<AuthResult?> ResolveAsync(ApiKeyAuthInput input)
        {
            var serverUrl = await ResolveServerUrlAsync(input);
            if (serverUrl is null)
            {
                return null;
            }

            var apiKey = input.Credential?.Key;
            if (string.IsNullOrEmpty(apiKey))
            {
                apiKey = await input.Context.Env(LlamaUrls.ApiKeyEnvironmentVariable);
            }

            // Pinned resolve carries the provider env (credential env plus the normalized
            // server URL) so refresh-credential reconstruction keeps the server address.
            var resolvedEnv = new Dictionary<string, string>(StringComparer.Ordinal);
            if (input.Credential?.Env is { } credentialEnv)
            {
                foreach (var pair in credentialEnv)
                {
                    resolvedEnv[pair.Key] = pair.Value;
                }
            }

            resolvedEnv[LlamaUrls.BaseUrlEnvironmentVariable] = serverUrl;

            return new AuthResult
            {
                Auth = new ModelAuth
                {
                    ApiKey = string.IsNullOrEmpty(apiKey) ? "local" : apiKey,
                    BaseUrl = LlamaUrls.InferenceUrl(serverUrl),
                },
                Env = resolvedEnv,
                Source = input.Credential is not null ? "stored credential" : LlamaUrls.BaseUrlEnvironmentVariable,
            };
        }

        internal static async Task<string?> ResolveServerUrlAsync(ApiKeyAuthInput input)
        {
            var fromCredential = input.Credential?.Env is { } env
                && env.TryGetValue(LlamaUrls.BaseUrlEnvironmentVariable, out var value)
                && !string.IsNullOrWhiteSpace(value)
                ? LlamaUrls.Normalize(value)
                : null;
            if (fromCredential is not null)
            {
                return fromCredential;
            }

            var fromEnvironment = await input.Context.Env(LlamaUrls.BaseUrlEnvironmentVariable);
            input.CancellationToken.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(fromEnvironment))
            {
                return null;
            }

            return LlamaUrls.Normalize(fromEnvironment);
        }
    }

    /// <summary>
    /// Creates the keyless local-server provider used by the PISHARP_ENDPOINT
    /// workflow. The single model is synthesized from PISHARP_MODEL with context and
    /// output limits from the environment.
    /// </summary>
    public static ProviderSpec CreateLlamaCppProvider(
        string endpoint,
        string? modelId,
        int? contextWindow,
        int? maxOutputTokens,
        int? temperature)
    {
        var model = new ModelInfo
        {
            Id = string.IsNullOrWhiteSpace(modelId) ? "local-model" : modelId,
            Name = string.IsNullOrWhiteSpace(modelId) ? "Local Model" : modelId,
            Api = ModelApi.OpenAiCompletions,
            Provider = LlamaCppProviderId,
            BaseUrl = endpoint,
            Reasoning = false,
            Input = ["text"],
            Cost = new ModelCost { Input = 0, Output = 0, CacheRead = 0, CacheWrite = 0 },
            ContextWindow = contextWindow ?? 128_000,
            MaxTokens = maxOutputTokens ?? 8_192,
            SamplingParams = temperature is { } t
                ? new Dictionary<string, object?> { ["temperature"] = (double)t }
                : null,
        };

        return new ProviderSpec
        {
            Id = LlamaCppProviderId,
            Name = "llama.cpp",
            BaseUrl = endpoint,
            Auth = new ProviderAuth(new LocalServerApiKeyAuth { Name = "llama.cpp (local)" }),
            GetModels = () => [model],
            DefaultApi = ModelApi.OpenAiCompletions,
        };
    }

    private static ProviderSpec OpenAi() => new()
    {
        Id = "openai",
        Name = "OpenAI",
        BaseUrl = "https://api.openai.com/v1",
        Auth = new ProviderAuth(new EnvApiKeyAuth
        {
            Name = "OpenAI API key",
            EnvironmentVariableNames = ["OPENAI_API_KEY"],
            LoginMessage = "Enter OpenAI API key",
        }),
        GetModels = () =>
        [
            Model(
                "openai", "gpt-5.5", "GPT-5.5", ModelApi.OpenAiCompletions,
                reasoning: false, contextWindow: BaselineContextWindow, maxTokens: BaselineMaxTokens,
                input: 0, output: 0, cacheRead: 0, cacheWrite: 0),
            Model(
                "openai", "gpt-4o", "GPT-4o", ModelApi.OpenAiCompletions,
                reasoning: false, contextWindow: 128_000, maxTokens: 16_384,
                input: 2.5, output: 10, cacheRead: 1.25, cacheWrite: 0),
            Model(
                "openai", "gpt-4.1", "GPT-4.1", ModelApi.OpenAiCompletions,
                reasoning: false, contextWindow: 1_047_576, maxTokens: 32_768,
                input: 2, output: 8, cacheRead: 0.5, cacheWrite: 0),
        ],
        DefaultApi = ModelApi.OpenAiCompletions,
    };

    private static ProviderSpec Anthropic() => new()
    {
        Id = "anthropic",
        Name = "Anthropic",
        BaseUrl = "https://api.anthropic.com",
        Auth = new ProviderAuth(
            new EnvApiKeyAuth
            {
                Name = "Anthropic API key",
                EnvironmentVariableNames = ["ANTHROPIC_OAUTH_TOKEN", "ANTHROPIC_API_KEY"],
                BearerTokenEnvironmentVariable = "ANTHROPIC_AUTH_TOKEN",
                LoginMessage = "Enter Anthropic API key",
            },
            new AnthropicOAuth { Name = "Anthropic (Claude Pro/Max)", IsSubscription = true }),
        GetModels = () =>
        [
            Model(
                "anthropic", "claude-opus-4-8", "Claude Opus 4.8", ModelApi.AnthropicMessages,
                reasoning: true, contextWindow: BaselineContextWindow, maxTokens: BaselineMaxTokens,
                input: 0, output: 0, cacheRead: 0, cacheWrite: 0),
            Model(
                "anthropic", "claude-sonnet-4-5", "Claude Sonnet 4.5", ModelApi.AnthropicMessages,
                reasoning: true, contextWindow: 200_000, maxTokens: 64_000,
                input: 3, output: 15, cacheRead: 0.30, cacheWrite: 3.75),
            Model(
                "anthropic", "claude-opus-4-1", "Claude Opus 4.1", ModelApi.AnthropicMessages,
                reasoning: true, contextWindow: 200_000, maxTokens: 32_000,
                input: 15, output: 75, cacheRead: 1.5, cacheWrite: 18.75),
        ],
        DefaultApi = ModelApi.AnthropicMessages,
    };

    private static ProviderSpec OpenRouter() => new()
    {
        Id = "openrouter",
        Name = "OpenRouter",
        BaseUrl = "https://openrouter.ai/api/v1",
        Auth = new ProviderAuth(
            new EnvApiKeyAuth
            {
                Name = "OpenRouter API key",
                EnvironmentVariableNames = ["OPENROUTER_API_KEY"],
                LoginMessage = "Enter OpenRouter API key",
            },
            new OpenRouterOAuth { Name = "OpenRouter" }),
        GetModels = () =>
        [
            Model(
                "openrouter", "moonshotai/kimi-k2.6", "Kimi K2.6", ModelApi.OpenAiCompletions,
                reasoning: true, contextWindow: 262_144, maxTokens: 262_144,
                input: 0.6, output: 3, cacheRead: 0.1, cacheWrite: 0,
                headers: new Dictionary<string, string>
                {
                    ["HTTP-Referer"] = "https://github.com/badlogic/pi-mono",
                    ["X-Title"] = "PiSharp",
                }),
        ],
        DefaultApi = ModelApi.OpenAiCompletions,
    };

    private static ProviderSpec Google() => new()
    {
        Id = "google",
        Name = "Google",
        BaseUrl = "https://generativelanguage.googleapis.com/v1beta",
        Auth = new ProviderAuth(new EnvApiKeyAuth
        {
            Name = "Google API key",
            EnvironmentVariableNames = ["GEMINI_API_KEY"],
            LoginMessage = "Enter Google API key",
        }),
        GetModels = () =>
        [
            Model(
                "google", "gemini-3.1-pro-preview", "Gemini 3.1 Pro Preview", ModelApi.GoogleGenerativeAi,
                reasoning: true, contextWindow: BaselineContextWindow, maxTokens: BaselineMaxTokens,
                input: 0, output: 0, cacheRead: 0, cacheWrite: 0,
                headers: new Dictionary<string, string> { ["x-goog-api-key"] = "{key}" }),
            Model(
                "google", "gemini-2.5-pro", "Gemini 2.5 Pro", ModelApi.GoogleGenerativeAi,
                reasoning: true, contextWindow: 1_048_576, maxTokens: 65_536,
                input: 1.25, output: 10, cacheRead: 0.3125, cacheWrite: 0,
                headers: new Dictionary<string, string> { ["x-goog-api-key"] = "{key}" }),
        ],
        DefaultApi = ModelApi.GoogleGenerativeAi,
    };

    private static ModelInfo Model(
        string provider,
        string id,
        string name,
        string api,
        bool reasoning,
        int contextWindow,
        int maxTokens,
        double input,
        double output,
        double cacheRead,
        double cacheWrite,
        IReadOnlyDictionary<string, string>? headers = null) => new()
    {
        Id = id,
        Name = name,
        Api = api,
        Provider = provider,
        BaseUrl = provider == "openai"
            ? "https://api.openai.com/v1"
            : provider == "anthropic"
                ? "https://api.anthropic.com"
                : provider == "openrouter"
                    ? "https://openrouter.ai/api/v1"
                    : "https://generativelanguage.googleapis.com/v1beta",
        Reasoning = reasoning,
        Input = ["text"],
        Cost = new ModelCost { Input = input, Output = output, CacheRead = cacheRead, CacheWrite = cacheWrite },
        ContextWindow = contextWindow,
        MaxTokens = maxTokens,
        Headers = headers,
    };
}

/// <summary>
/// Keyless local server auth (pinned Pi pattern for local providers): resolution
/// always succeeds, reporting the provider as configured without any credential.
/// </summary>
public sealed class LocalServerApiKeyAuth : ApiKeyAuth
{
    public override Task<AuthResult?> ResolveAsync(ApiKeyAuthInput input)
    {
        input.CancellationToken.ThrowIfCancellationRequested();
        // Local endpoints need no auth, but a supplied key (--api-key runtime override or a
        // stored credential) wins over the keyless default (pinned local-server behavior).
        if (input.Credential is ApiKeyCredential { Key: { Length: > 0 } key })
        {
            return Task.FromResult<AuthResult?>(new AuthResult
            {
                Auth = new ModelAuth { ApiKey = key },
                Source = "llama.cpp (local)",
            });
        }

        return Task.FromResult<AuthResult?>(new AuthResult
        {
            Auth = new ModelAuth { ApiKey = string.Empty },
            Source = "llama.cpp (local)",
        });
    }
}
