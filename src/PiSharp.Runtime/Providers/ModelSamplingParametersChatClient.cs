using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.AI;

namespace PiSharp.Runtime.Providers;

public sealed class SamplingParametersRequestContext
{
    private readonly AsyncLocal<RequestState?> _current = new();

    public JsonElement? Current => _current.Value?.SamplingParameters;
    public string? ThinkingLevel => _current.Value?.ThinkingLevel;
    public ModelDescriptor? Model => _current.Value?.Model;

    public IDisposable Push(JsonElement? parameters, string thinkingLevel, ModelDescriptor model)
    {
        var previous = _current.Value;
        _current.Value = new(parameters, thinkingLevel, model);
        return new Scope(this, previous);
    }

    private sealed record RequestState(JsonElement? SamplingParameters, string ThinkingLevel, ModelDescriptor Model);

    private sealed class Scope(SamplingParametersRequestContext owner, RequestState? previous) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0) owner._current.Value = previous;
        }
    }
}

/// <summary>Applies Pi model and effective thinking-level sampling metadata before a provider request is serialized.</summary>
public sealed class ModelSamplingParametersChatClient(IChatClient inner, ModelDescriptor model,
    SamplingParametersRequestContext requestContext, bool providerSupportsSampling = true) : DelegatingChatClient(inner)
{
    public const string ThinkingLevelProperty = "pisharp.thinkingLevel";
    public const string RequestSamplingParametersProperty = "samplingParams";

    public override async Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages,
        ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        var (requestOptions, parameters) = Prepare(options);
        var thinkingLevel = GetString(options?.AdditionalProperties, ThinkingLevelProperty) ?? GetThinkingLevel(options?.Reasoning);
        using var scope = requestContext.Push(parameters, thinkingLevel, model);
        return await base.GetResponseAsync(messages, requestOptions, cancellationToken).ConfigureAwait(false);
    }

    public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var (requestOptions, parameters) = Prepare(options);
        var thinkingLevel = GetString(options?.AdditionalProperties, ThinkingLevelProperty) ?? GetThinkingLevel(options?.Reasoning);
        using var scope = requestContext.Push(parameters, thinkingLevel, model);
        await foreach (var update in base.GetStreamingResponseAsync(messages, requestOptions, cancellationToken)
            .WithCancellation(cancellationToken).ConfigureAwait(false))
            yield return update;
    }

    private (ChatOptions? Options, JsonElement? Parameters) Prepare(ChatOptions? options)
    {
        var requestProperties = options?.AdditionalProperties;
        var thinkingLevel = GetString(requestProperties, ThinkingLevelProperty) ?? GetThinkingLevel(options?.Reasoning);
        var requestParameters = GetObject(requestProperties, RequestSamplingParametersProperty);
        var modelParameters = providerSupportsSampling ? Resolve(model, thinkingLevel, null) : null;
        var parameters = providerSupportsSampling ? ModelMetadataJson.MergeObjects(requestParameters, modelParameters) : null;
        var thinkingBudgets = ThinkingBudgetPolicy.Read(requestProperties);
        if (model.Api == "openai-completions" && options?.Reasoning is not null && model.Reasoning == true &&
            ThinkingBudgetPolicy.ReadThinkingBudgetField(model.Compatibility) is { } budgetField)
        {
            var ceiling = options.MaxOutputTokens ?? model.MaxOutputTokens ?? 0;
            var budget = ThinkingBudgetPolicy.ClampToAnswerRoom(
                ThinkingBudgetPolicy.ForLevel(thinkingLevel, thinkingBudgets), ceiling);
            if (budget > 0)
            {
                parameters = ThinkingBudgetPolicy.MergeBudgetParameter(modelParameters, budgetField, budget);
                if (providerSupportsSampling) parameters = ModelMetadataJson.MergeObjects(requestParameters, parameters);
            }
        }

        var keepThinkingBudgets = model.Api is "anthropic-messages" or "google-generative-ai" or "google-vertex" or
            "bedrock-converse-stream";
        var removeBudgetSettings = requestProperties?.ContainsKey(ThinkingBudgetPolicy.SettingsProperty) == true && !keepThinkingBudgets;
        if (requestProperties is null ||
            (!requestProperties.ContainsKey(ThinkingLevelProperty) &&
             !requestProperties.ContainsKey(RequestSamplingParametersProperty) && !removeBudgetSettings))
            return (options, parameters);

        var requestOptions = options!.Clone();
        var properties = requestProperties.Clone();
        properties.Remove(ThinkingLevelProperty);
        properties.Remove(RequestSamplingParametersProperty);
        if (removeBudgetSettings) properties.Remove(ThinkingBudgetPolicy.SettingsProperty);
        requestOptions.AdditionalProperties = properties.Count == 0 ? null : properties;
        return (requestOptions, parameters);
    }

    internal static JsonElement? Resolve(ModelDescriptor model, string thinkingLevel,
        JsonElement? requestParameters)
    {
        var effectiveLevel = ClampThinkingLevel(model, thinkingLevel);
        JsonElement? levelParameters = null;
        if (model.SamplingParametersByThinkingLevel is { ValueKind: JsonValueKind.Object } levels &&
            levels.TryGetProperty(effectiveLevel, out var selected) && selected.ValueKind == JsonValueKind.Object)
            levelParameters = selected;

        var merged = ModelMetadataJson.MergeObjects(levelParameters, model.SamplingParameters);
        merged = ModelMetadataJson.MergeObjects(requestParameters, merged);
        return merged;
    }

    private static string ClampThinkingLevel(ModelDescriptor model, string requested)
    {
        var levels = new[] { "off", "minimal", "low", "medium", "high", "xhigh", "max" };
        string[] available;
        if (model.Reasoning != true)
        {
            available = ["off"];
        }
        else
        {
            available = levels.Where(level =>
            {
                JsonElement? mapped = model.ThinkingLevelMap is { ValueKind: JsonValueKind.Object } map &&
                    map.TryGetProperty(level, out var value) ? value : null;
                if (mapped is { ValueKind: JsonValueKind.Null }) return false;
                return level is not ("xhigh" or "max") || mapped is not null;
            }).ToArray();
        }

        var normalized = requested.ToLowerInvariant();
        var selected = Array.IndexOf(levels, normalized);
        if (selected < 0) return available.FirstOrDefault() ?? "off";
        for (var index = selected; index < levels.Length; index++)
            if (available.Contains(levels[index], StringComparer.Ordinal)) return levels[index];
        for (var index = selected - 1; index >= 0; index--)
            if (available.Contains(levels[index], StringComparer.Ordinal)) return levels[index];
        return available.FirstOrDefault() ?? "off";
    }

    private static string? GetString(AdditionalPropertiesDictionary? properties, string key) =>
        properties?.TryGetValue(key, out var value) == true ? value as string : null;

    private static string GetThinkingLevel(ReasoningOptions? reasoning) => reasoning?.Effort switch
    {
        ReasoningEffort.Low => "low",
        ReasoningEffort.Medium => "medium",
        ReasoningEffort.High => "high",
        ReasoningEffort.ExtraHigh => "xhigh",
        _ => "off"
    };

    private static JsonElement? GetObject(AdditionalPropertiesDictionary? properties, string key)
    {
        if (properties?.TryGetValue(key, out var value) != true || value is null) return null;
        if (value is JsonElement element && element.ValueKind == JsonValueKind.Object) return element.Clone();
        if (value is JsonNode node && node is JsonObject) return JsonSerializer.SerializeToElement(node);
        if (value is IReadOnlyDictionary<string, object?> or IDictionary<string, object?>)
        {
            var parsed = JsonSerializer.SerializeToElement(value);
            return parsed.ValueKind == JsonValueKind.Object ? parsed : null;
        }
        return null;
    }
}
