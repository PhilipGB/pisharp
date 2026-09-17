namespace PiSharp.Core.Models;

/// <summary>
/// Provider APIs known to pinned pi-ai (packages/ai/src/types.ts KnownApi). Custom
/// models.json providers may declare any api string; unknown APIs resolve to
/// <see cref="ModelErrors.UnsupportedCapability"/> at request time.
/// </summary>
public static class ModelApi
{
    public const string OpenAiCompletions = "openai-completions";
    public const string MistralConversations = "mistral-conversations";
    public const string OpenAiResponses = "openai-responses";
    public const string AzureOpenAiResponses = "azure-openai-responses";
    public const string OpenAiCodexResponses = "openai-codex-responses";
    public const string AnthropicMessages = "anthropic-messages";
    public const string BedrockConverseStream = "bedrock-converse-stream";
    public const string GoogleGenerativeAi = "google-generative-ai";
    public const string GoogleVertex = "google-vertex";
    public const string PiMessages = "pi-messages";
}

/// <summary>
/// A model definition owned by the application (pinned pi-ai: Model). The model domain is
/// framework-neutral: provider SDK objects belong in adapters, never here.
/// </summary>
public sealed record ModelInfo
{
    /// <summary>Model id within the provider (e.g. "gpt-4o").</summary>
    public required string Id { get; init; }

    /// <summary>Display name.</summary>
    public required string Name { get; init; }

    /// <summary>API/transport type (see <see cref="ModelApi"/>).</summary>
    public required string Api { get; init; }

    /// <summary>Provider id this model belongs to.</summary>
    public required string Provider { get; init; }

    /// <summary>Provider base URL for requests.</summary>
    public required string BaseUrl { get; init; }

    /// <summary>Whether the model supports reasoning/thinking levels.</summary>
    public bool Reasoning { get; init; }

    /// <summary>
    /// Maps Pi thinking levels to provider-specific values. Missing keys use provider
    /// defaults; a null value marks the level unsupported.
    /// </summary>
    public IReadOnlyDictionary<string, string?>? ThinkingLevelMap { get; init; }

    /// <summary>Input modalities: text and/or image.</summary>
    public IReadOnlyList<string> Input { get; init; } = ["text"];

    /// <summary>Per-million-token pricing.</summary>
    public ModelCost Cost { get; init; } = new ModelCost
    {
        Input = 0, Output = 0, CacheRead = 0, CacheWrite = 0,
    };

    /// <summary>Context window in tokens.</summary>
    public int ContextWindow { get; init; } = 128_000;

    /// <summary>Maximum output tokens.</summary>
    public int MaxTokens { get; init; } = 16_384;

    /// <summary>Default sampling parameters merged under per-request overrides.</summary>
    public IReadOnlyDictionary<string, object?>? SamplingParams { get; init; }

    /// <summary>Extra HTTP headers sent with requests for this model.</summary>
    public IReadOnlyDictionary<string, string>? Headers { get; init; }

    /// <summary>
    /// Raw compatibility overrides (pinned pi-ai: Model.compat). The canonical
    /// composition currency: models.json and built-in definitions both project into
    /// this string-keyed map; adapters read it via <see cref="ModelCompatReader"/>.
    /// </summary>
    public IReadOnlyDictionary<string, object?>? Compat { get; init; }

    /// <summary>Canonical provider/model reference, e.g. "openai/gpt-4o".</summary>
    public string Reference => $"{Provider}/{Id}";

    /// <summary>
    /// Returns true when both models identify the same provider/model pair
    /// (pinned pi-ai: modelsAreEqual).
    /// </summary>
    public static bool AreEqual(ModelInfo? a, ModelInfo? b) =>
        a is not null && b is not null &&
        a.Id == b.Id &&
        a.Provider == b.Provider;
}
