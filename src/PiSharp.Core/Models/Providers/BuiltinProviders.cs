using PiSharp.Core.Models.Auth;
using PiSharp.Core.Models.OAuth;

namespace PiSharp.Core.Models.Providers;

/// <summary>
/// Built-in provider catalogue (pinned Pi: packages/ai/providers/all.ts).
///
/// Intentional difference from pinned Pi: the pinned generated model data
/// (providers/data/*.json) is produced at npm build time and not committed to the
/// source repository, so PiSharp ships a reduced, deterministic built-in catalogue.
/// The pi.dev remote-catalog overlay (RemoteCatalogProvider) and models.json custom
/// providers fill in the full model list at runtime, exactly as in pinned Pi.
/// </summary>
public static class BuiltinProviders
{
    /// <summary>Provider id for the OpenAI-compatible local server (llama.cpp) workflow.</summary>
    public const string LlamaCppProviderId = "llama.cpp";

    /// <summary>Creates the built-in provider set in pinned catalogue order.</summary>
    public static IReadOnlyList<ProviderSpec> CreateBuiltins() =>
    [
        OpenAi(),
        Anthropic(),
        OpenRouter(),
        Google(),
    ];

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
