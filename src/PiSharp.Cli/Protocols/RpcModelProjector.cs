using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using PiSharp.Cli;
using PiSharp.Runtime.Providers;
using PiSharp.Runtime.Sessions;
using PiSharp.Runtime.VirtualModels;

namespace PiSharp.Cli.Protocols;

internal static class RpcModelProjector
{
    public static JsonElement Project(ModelDescriptor model) => Project(model, null);

    public static JsonElement Project(ModelSelection selection) => Project(selection.Model, selection.Provider);

    public static JsonElement Project(ModelDescriptor model, ProviderProfile? provider)
    {
        var providerId = model.Provider ?? provider?.Id;
        var snapshot = new RpcModelSnapshot(
            model.Id,
            model.Name ?? model.Id,
            model.Api ?? provider?.Api ?? DefaultApi(providerId),
            providerId,
            (model.BaseUrl ?? provider?.Endpoint.ToString())?.TrimEnd('/'),
            model.Reasoning == true,
            SafeMetadata(model.ThinkingLevelMap),
            model.Input ?? ["text"],
            RpcModelInputLimits.From(model.InputLimits),
            RpcModelCostSnapshot.From(model.Pricing ?? new ModelPricing(0, 0, 0, CachedWrite: 0)),
            SafeMetadata(model.PromptCache),
            model.ContextLength ?? (model.Api == VirtualModelContract.Api ? 0 : 128000),
            model.MaxOutputTokens ?? (model.Api == VirtualModelContract.Api ? 0 : 16384),
            model.SamplingParameters is { } sampling ? SafeMetadata(sampling) : null,
            SafeMetadata(model.Compatibility ?? provider?.Compatibility));
        return JsonSerializer.SerializeToElement(snapshot);
    }

    private static JsonElement? SafeMetadata(JsonElement? metadata)
    {
        if (metadata is not JsonElement value || value.ValueKind != JsonValueKind.Object) return null;
        var node = JsonNode.Parse(value.GetRawText())!.AsObject();
        // Model request metadata crosses the RPC boundary, while credentials and headers stay local.
        RemoveCredentials(node);
        return JsonSerializer.SerializeToElement(node);
    }

    private static void RemoveCredentials(JsonNode? value)
    {
        if (value is JsonObject objectValue)
        {
            foreach (var property in objectValue.ToArray())
            {
                if (IsCredentialProperty(property.Key))
                {
                    objectValue.Remove(property.Key);
                    continue;
                }
                RemoveCredentials(property.Value);
            }
        }
        else if (value is JsonArray arrayValue)
            foreach (var item in arrayValue) RemoveCredentials(item);
    }

    private static bool IsCredentialProperty(string property)
    {
        var name = new string(property.Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();
        return name.Contains("apikey", StringComparison.Ordinal) ||
               name.Contains("authorization", StringComparison.Ordinal) ||
               name.Contains("credential", StringComparison.Ordinal) ||
               name.Contains("password", StringComparison.Ordinal) ||
               name.Contains("secret", StringComparison.Ordinal) ||
               name is "headers" or "accesstoken" or "refreshtoken" or "bearertoken";
    }

    private static string DefaultApi(string? provider) => provider switch
    {
        "openai" or "xai" => "openai-responses",
        "anthropic" => "anthropic-messages",
        _ => "openai-completions"
    };

    private sealed record RpcModelSnapshot(
        [property: JsonPropertyName("id")] string Id,
        [property: JsonPropertyName("name")] string Name,
        [property: JsonPropertyName("api")] string Api,
        [property: JsonPropertyName("provider"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Provider,
        [property: JsonPropertyName("baseUrl"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? BaseUrl,
        [property: JsonPropertyName("reasoning")] bool Reasoning,
        [property: JsonPropertyName("thinkingLevelMap"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] JsonElement? ThinkingLevelMap,
        [property: JsonPropertyName("input")] IReadOnlyList<string> Input,
        [property: JsonPropertyName("inputLimits"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] RpcModelInputLimits? InputLimits,
        [property: JsonPropertyName("cost")] RpcModelCostSnapshot Cost,
        [property: JsonPropertyName("promptCache"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] JsonElement? PromptCache,
        [property: JsonPropertyName("contextWindow")] int ContextWindow,
        [property: JsonPropertyName("maxTokens")] int MaxTokens,
        [property: JsonPropertyName("samplingParams"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] JsonElement? SamplingParameters,
        [property: JsonPropertyName("compat"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] JsonElement? Compatibility);

    private sealed record RpcModelCostSnapshot(
        [property: JsonPropertyName("input")] decimal Input,
        [property: JsonPropertyName("output")] decimal Output,
        [property: JsonPropertyName("cacheRead")] decimal CacheRead,
        [property: JsonPropertyName("cacheWrite")] decimal CacheWrite,
        [property: JsonPropertyName("tiers"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<RpcModelCostTier>? Tiers)
    {
        public static RpcModelCostSnapshot From(ModelPricing pricing) => new(
            pricing.Input,
            pricing.Output,
            pricing.CachedInput ?? 0,
            pricing.CachedWrite ?? 0,
            pricing.Tiers?.Select(RpcModelCostTier.From).ToArray());
    }

    private sealed record RpcModelCostTier(
        [property: JsonPropertyName("inputTokensAbove")] int InputTokensAbove,
        [property: JsonPropertyName("input")] decimal Input,
        [property: JsonPropertyName("output")] decimal Output,
        [property: JsonPropertyName("cacheRead")] decimal CacheRead,
        [property: JsonPropertyName("cacheWrite")] decimal CacheWrite)
    {
        public static RpcModelCostTier From(ModelPricingTier tier) =>
            new(tier.InputTokensAbove, tier.Input, tier.Output, tier.CachedInput ?? 0, tier.CachedWrite ?? 0);
    }

    private sealed record RpcModelInputLimits(
        [property: JsonPropertyName("maxRequestBytes"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? MaxRequestBytes,
        [property: JsonPropertyName("images"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] RpcModelImageInputLimits? Images)
    {
        public static RpcModelInputLimits? From(ModelInputLimits? limits) => limits is null ? null :
            new RpcModelInputLimits(limits.MaxRequestBytes,
                limits.Images is { } images ? RpcModelImageInputLimits.From(images) : null);
    }

    private sealed record RpcModelImageInputLimits(
        [property: JsonPropertyName("maxPerMessage"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? MaxPerMessage,
        [property: JsonPropertyName("maxPerRequest"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? MaxPerRequest,
        [property: JsonPropertyName("resize"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] RpcModelImageResizeOptions? Resize)
    {
        public static RpcModelImageInputLimits From(ModelImageInputLimits limits) =>
            new(limits.MaxPerMessage, limits.MaxPerRequest,
                limits.Resize is { } resize ? RpcModelImageResizeOptions.From(resize) : null);
    }

    private sealed record RpcModelImageResizeOptions(
        [property: JsonPropertyName("maxWidth"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? MaxWidth,
        [property: JsonPropertyName("maxHeight"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? MaxHeight,
        [property: JsonPropertyName("maxBytes"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? MaxBytes,
        [property: JsonPropertyName("jpegQuality"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? JpegQuality)
    {
        public static RpcModelImageResizeOptions From(ModelImageResizeOptions options) =>
            new(options.MaxWidth, options.MaxHeight, options.MaxBytes, options.JpegQuality);
    }
}
