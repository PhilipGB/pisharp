namespace PiSharp.Core.Models;

/// <summary>
/// Compatibility overrides for OpenAI-compatible chat-completions APIs (pinned pi-ai:
/// OpenAICompletionsCompat). Null fields mean "auto-detect / API default".
/// </summary>
public sealed record OpenAiCompletionsCompat
{
    /// <summary>Whether the provider supports the store field.</summary>
    public bool? SupportsStore { get; init; }

    /// <summary>Whether the provider supports the developer role (vs system).</summary>
    public bool? SupportsDeveloperRole { get; init; }

    /// <summary>Whether the provider supports reasoning_effort.</summary>
    public bool? SupportsReasoningEffort { get; init; }

    /// <summary>Whether streamed responses include stream_options usage. Default: true.</summary>
    public bool? SupportsUsageInStreaming { get; init; }

    /// <summary>Whether streamed responses include finish_reason. Default: true.</summary>
    public bool? SupportsFinishReason { get; init; }

    /// <summary>Which field carries the output cap: "max_completion_tokens" or "max_tokens".</summary>
    public string? MaxTokensField { get; init; }

    /// <summary>Whether tool results require the name field.</summary>
    public bool? RequiresToolResultName { get; init; }

    /// <summary>Whether a user message after tool results requires an assistant message in between.</summary>
    public bool? RequiresAssistantAfterToolResult { get; init; }

    /// <summary>Whether thinking blocks must be converted to text with <thinking> delimiters.</summary>
    public bool? RequiresThinkingAsText { get; init; }

    /// <summary>Whether replayed assistant messages must include an empty reasoning_content field.</summary>
    public bool? RequiresReasoningContentOnAssistantMessages { get; init; }

    /// <summary>
    /// Reasoning parameter format: openai, openrouter, deepseek, together, baseten, zai,
    /// qwen, chat-template, qwen-chat-template, string-thinking, ant-ling. Default: openai.
    /// </summary>
    public string? ThinkingFormat { get; init; }

    /// <summary>Kwargs sent as chat_template_kwargs for the chat-template thinking format.</summary>
    public IReadOnlyDictionary<string, ChatTemplateKwargValue>? ChatTemplateKwargs { get; init; }

    /// <summary>Arguments sent as chat_template_args for the baseten thinking format.</summary>
    public IReadOnlyDictionary<string, ChatTemplateKwargValue>? ChatTemplateArgs { get; init; }

    /// <summary>Cache-control convention: "anthropic" applies cache_control markers.</summary>
    public string? CacheControlFormat { get; init; }

    /// <summary>OpenRouter routing preferences sent as the provider request field.</summary>
    public OpenRouterRouting? OpenRouterRouting { get; init; }

    /// <summary>Vercel AI Gateway routing preferences.</summary>
    public VercelGatewayRouting? VercelGatewayRouting { get; init; }

    /// <summary>Whether z.ai supports top-level tool_stream: true.</summary>
    public bool? ZaiToolStream { get; init; }

    /// <summary>Top-level field capping reasoning tokens: thinking_token_budget, thinking_budget, thinking_budget_tokens.</summary>
    public string? ThinkingTokenBudgetField { get; init; }

    /// <summary>Alias for thinkingTokenBudgetField: "thinking_token_budget" (vLLM).</summary>
    public bool? SupportsThinkingTokenBudget { get; init; }

    /// <summary>Whether the provider supports OpenAI grammar (Lark/regex) constrained tools.</summary>
    public bool? SupportsOpenAIGrammarTools { get; init; }

    /// <summary>Whether the provider supports the strict field in tool definitions. Default: true.</summary>
    public bool? SupportsStrictMode { get; init; }

    /// <summary>Whether to send session-affinity data from the session id. Default: OpenRouter only.</summary>
    public bool? SendSessionAffinityHeaders { get; init; }

    /// <summary>Provider-specific deferred tool serialization mode: "kimi".</summary>
    public string? DeferredToolsMode { get; init; }

    /// <summary>Session-affinity header format: openai, openai-nosession, openrouter.</summary>
    public string? SessionAffinityFormat { get; init; }

    /// <summary>Whether the provider supports long prompt-cache retention. Default: true.</summary>
    public bool? SupportsLongCacheRetention { get; init; }

    /// <summary>vLLM scheduler priority sent as the top-level priority field.</summary>
    public int? VllmPriority { get; init; }
}

/// <summary>
/// A chat_template_kwargs value (pinned pi-ai: ChatTemplateKwargValue). Either a literal
/// scalar or a Pi-controlled variable reference.
/// </summary>
public sealed record ChatTemplateKwargValue
{
    /// <summary>Literal scalar value (string, number, boolean, or null).</summary>
    public object? Value { get; init; }

    /// <summary>
    /// When set, the value is a Pi-controlled variable: "thinking.enabled", "thinking.effort",
    /// or "thinking.budget".
    /// </summary>
    public string? Variable { get; init; }

    /// <summary>For variables: omit the kwarg entirely when thinking is off.</summary>
    public bool? OmitWhenOff { get; init; }
}

/// <summary>OpenRouter provider routing preferences (pinned pi-ai: OpenRouterRouting).</summary>
public sealed record OpenRouterRouting
{
    public bool? AllowFallbacks { get; init; }
    public bool? RequireParameters { get; init; }
    public string? DataCollection { get; init; }
    public bool? Zdr { get; init; }
    public bool? EnforceDistillableText { get; init; }
    public IReadOnlyList<string>? Order { get; init; }
    public IReadOnlyList<string>? Only { get; init; }
    public IReadOnlyList<string>? Ignore { get; init; }
    public IReadOnlyList<string>? Quantizations { get; init; }

    /// <summary>Sort strategy: a metric name or an explicit by/partition object.</summary>
    public string? Sort { get; init; }
    public string? SortBy { get; init; }
    public string? SortPartition { get; init; }

    public double? MaxPricePrompt { get; init; }
    public double? MaxPriceCompletion { get; init; }
    public double? MaxPriceImage { get; init; }
    public double? MaxPriceAudio { get; init; }
    public double? MaxPriceRequest { get; init; }

    public double? PreferredMinThroughput { get; init; }
    public double? PreferredMaxLatency { get; init; }
}

/// <summary>Vercel AI Gateway routing preferences (pinned pi-ai: VercelGatewayRouting).</summary>
public sealed record VercelGatewayRouting
{
    public IReadOnlyList<string>? Only { get; init; }
    public IReadOnlyList<string>? Order { get; init; }
}

/// <summary>Compatibility overrides for OpenAI Responses APIs (pinned pi-ai: OpenAIResponsesCompat).</summary>
public sealed record OpenAiResponsesCompat
{
    /// <summary>Whether the provider supports the developer role. Default: true.</summary>
    public bool? SupportsDeveloperRole { get; init; }

    /// <summary>Session-affinity header format: openai, openai-nosession, openrouter.</summary>
    public string? SessionAffinityFormat { get; init; }

    /// <summary>Whether the provider supports long prompt-cache retention. Default: true.</summary>
    public bool? SupportsLongCacheRetention { get; init; }

    /// <summary>Whether the provider supports strict JSON-schema function tools.</summary>
    public bool? SupportsStrictMode { get; init; }

    /// <summary>Whether to emit OpenAI custom tools with grammar formats.</summary>
    public bool? SupportsOpenAIGrammarTools { get; init; }

    /// <summary>Whether the model supports message-anchored additional_tools input items.</summary>
    public bool? SupportsAdditionalTools { get; init; }

    /// <summary>Whether the model supports client-executed tool search for deferred tools.</summary>
    public bool? SupportsToolSearch { get; init; }

    /// <summary>Whether the model accepts prompt_cache_options (OpenAI GPT-5.6+).</summary>
    public bool? SupportsExplicitPromptCacheMode { get; init; }

    /// <summary>Whether the provider accepts max_output_tokens. Default: true.</summary>
    public bool? SupportsMaxOutputTokens { get; init; }
}

/// <summary>
/// Compatibility overrides for Anthropic Messages-compatible APIs (pinned pi-ai:
/// AnthropicMessagesCompat).
/// </summary>
public sealed record AnthropicMessagesCompat
{
    /// <summary>Whether the provider accepts per-tool eager_input_streaming. Default: true.</summary>
    public bool? SupportsEagerToolInputStreaming { get; init; }

    /// <summary>Whether the provider supports long cache retention (1h). Default: true.</summary>
    public bool? SupportsLongCacheRetention { get; init; }

    /// <summary>Whether to send x-session-affinity headers from the session id. Default: false.</summary>
    public bool? SendSessionAffinityHeaders { get; init; }

    /// <summary>Session-affinity format: "openrouter" sends x-session-id.</summary>
    public string? SessionAffinityFormat { get; init; }

    /// <summary>Whether tool definitions accept cache_control markers. Default: true.</summary>
    public bool? SupportsCacheControlOnTools { get; init; }

    /// <summary>Whether the model accepts the temperature field. Default: true.</summary>
    public bool? SupportsTemperature { get; init; }

    /// <summary>Force adaptive thinking regardless of the model id.</summary>
    public bool? ForceAdaptiveThinking { get; init; }

    /// <summary>Replay empty thinking signatures as signature: "" instead of text.</summary>
    public bool? AllowEmptySignature { get; init; }

    /// <summary>Whether the provider supports Anthropic strict tool schemas.</summary>
    public bool? SupportsStrictTools { get; init; }

    /// <summary>Whether the transport supports effort-only system messages and thinking binding.</summary>
    public bool? SupportsMidConvoEffort { get; init; }

    /// <summary>Whether the provider supports deferred tools loaded via tool_reference.</summary>
    public bool? SupportsToolReferences { get; init; }
}

/// <summary>
/// Raw compat map utilities: the canonical composition currency for Model.compat
/// (pinned pi-ai compat objects are raw key-value maps merged with nested-merge
/// semantics for routing/chat-template keys).
/// </summary>
public static class ModelCompatReader
{
    private static readonly string[] NestedMergeKeys =
    {
        "openRouterRouting", "vercelGatewayRouting", "chatTemplateKwargs", "chatTemplateArgs",
    };

    /// <summary>
    /// Merges an override compat map over a base map (pinned Pi: mergeCompat). Nested
    /// objects for the routing/chat-template keys merge one level deep.
    /// </summary>
    public static Dictionary<string, object?>? Merge(
        IReadOnlyDictionary<string, object?>? baseCompat,
        IReadOnlyDictionary<string, object?>? overrideCompat)
    {
        if (overrideCompat is null)
        {
            return baseCompat is null ? null : baseCompat.ToDictionary(
                e => e.Key, e => e.Value, StringComparer.Ordinal);
        }

        var merged = new Dictionary<string, object?>(baseCompat ?? new Dictionary<string, object?>(), StringComparer.Ordinal);
        foreach (var (key, value) in overrideCompat)
        {
            if (NestedMergeKeys.Contains(key) &&
                (IsPlainObject(merged.GetValueOrDefault(key)) || IsPlainObject(value)))
            {
                var baseObject = merged.GetValueOrDefault(key) as Dictionary<string, object?>;
                var overrideObject = value as Dictionary<string, object?>;
                var nested = new Dictionary<string, object?>(baseObject ?? new Dictionary<string, object?>(), StringComparer.Ordinal);
                if (overrideObject is not null)
                {
                    foreach (var (nestedKey, nestedValue) in overrideObject)
                    {
                        nested[nestedKey] = nestedValue;
                    }
                }

                merged[key] = nested;
                continue;
            }

            merged[key] = value;
        }

        return merged;
    }

    private static bool IsPlainObject(object? value) => value is Dictionary<string, object?>;

    /// <summary>Reads a boolean compat field; null when absent.</summary>
    public static bool? GetBool(IReadOnlyDictionary<string, object?>? compat, string key)
    {
        return compat is not null && compat.TryGetValue(key, out var value) && value is bool b ? b : null;
    }

    /// <summary>Reads a string compat field; null when absent.</summary>
    public static string? GetString(IReadOnlyDictionary<string, object?>? compat, string key)
    {
        return compat is not null && compat.TryGetValue(key, out var value) && value is string s ? s : null;
    }

    /// <summary>Reads a nested object compat field; null when absent.</summary>
    public static IReadOnlyDictionary<string, object?>? GetObject(
        IReadOnlyDictionary<string, object?>? compat, string key)
    {
        return compat is not null && compat.TryGetValue(key, out var value) && value is Dictionary<string, object?> obj
            ? obj
            : null;
    }

    /// <summary>Normalizes a parsed JSON value map for use as a compat map.</summary>
    public static Dictionary<string, object?> ToDictionary(IReadOnlyDictionary<string, object?> values)
        => values.ToDictionary(e => e.Key, e => e.Value, StringComparer.Ordinal);
}
