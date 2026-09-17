using System.Text.Json;

namespace PiSharp.Core.Models;

/// <summary>
/// Parses validated models.json JSON into typed records (companion to
/// <see cref="ModelConfigValidator"/>). Called only after validation passes, so missing
/// required fields cannot occur.
/// </summary>
public static class ModelConfigParser
{
    /// <summary>Parses the validated root object into the immutable provider map.</summary>
    public static Dictionary<string, ModelsJsonProvider> ParseProviders(JsonElement root)
    {
        var providers = new Dictionary<string, ModelsJsonProvider>();
        if (!root.TryGetProperty("providers", out var providersElement) ||
            providersElement.ValueKind != JsonValueKind.Object)
        {
            return providers;
        }

        foreach (var entry in providersElement.EnumerateObject())
        {
            providers[entry.Name] = ParseProvider(entry.Value);
        }

        return providers;
    }

    private static ModelsJsonProvider ParseProvider(JsonElement element) => new()
    {
        Name = ReadString(element, "name"),
        BaseUrl = ReadString(element, "baseUrl"),
        ApiKey = ReadString(element, "apiKey"),
        Api = ReadString(element, "api"),
        OAuth = ReadString(element, "oauth"),
        Headers = ReadStringRecord(element, "headers"),
        Compat = ReadCompat(element),
        AuthHeader = ReadBoolean(element, "authHeader"),
        Models = element.TryGetProperty("models", out var models) && models.ValueKind == JsonValueKind.Array
            ? models.EnumerateArray().Select(ParseModel).ToArray()
            : null,
        ModelOverrides = element.TryGetProperty("modelOverrides", out var overrides) &&
            overrides.ValueKind == JsonValueKind.Object
            ? overrides.EnumerateObject()
                .ToDictionary(
                    entry => entry.Name,
                    entry => ParseOverride(entry.Value))
            : null,
    };

    private static ModelsJsonModel ParseModel(JsonElement element) => new()
    {
        Id = element.GetProperty("id").GetString()!,
        Name = ReadString(element, "name"),
        Api = ReadString(element, "api"),
        BaseUrl = ReadString(element, "baseUrl"),
        Reasoning = ReadBoolean(element, "reasoning"),
        ThinkingLevelMap = ReadThinkingLevelMap(element),
        Input = ReadStringArray(element, "input"),
        Cost = ReadCost(element),
        ContextWindow = ReadNumber(element, "contextWindow"),
        MaxTokens = ReadNumber(element, "maxTokens"),
        SamplingParams = ReadAnyRecord(element, "samplingParams"),
        Headers = ReadStringRecord(element, "headers"),
        Compat = ReadCompat(element),
    };

    private static ModelsJsonModelOverride ParseOverride(JsonElement element) => new()
    {
        Name = ReadString(element, "name"),
        Reasoning = ReadBoolean(element, "reasoning"),
        ThinkingLevelMap = ReadThinkingLevelMap(element),
        Input = ReadStringArray(element, "input"),
        Cost = ReadCost(element),
        ContextWindow = ReadNumber(element, "contextWindow"),
        MaxTokens = ReadNumber(element, "maxTokens"),
        SamplingParams = ReadAnyRecord(element, "samplingParams"),
        Headers = ReadStringRecord(element, "headers"),
        Compat = ReadCompat(element),
    };

    private static string? ReadString(JsonElement element, string property)
    {
        return element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
    }

    private static bool? ReadBoolean(JsonElement element, string property)
    {
        return element.TryGetProperty(property, out var value) &&
        value.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? value.GetBoolean()
            : null;
    }

    private static double? ReadNumber(JsonElement element, string property)
    {
        return element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.Number
            ? value.GetDouble()
            : null;
    }

    private static IReadOnlyDictionary<string, string>? ReadStringRecord(JsonElement element, string property)
    {
        if (!element.TryGetProperty(property, out var value) || value.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var result = new Dictionary<string, string>();
        foreach (var entry in value.EnumerateObject())
        {
            result[entry.Name] = entry.Value.ValueKind == JsonValueKind.String ? entry.Value.GetString()! : string.Empty;
        }

        return result;
    }

    private static IReadOnlyList<string>? ReadStringArray(JsonElement element, string property)
    {
        if (!element.TryGetProperty(property, out var value) || value.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        return value.EnumerateArray().Where(v => v.ValueKind == JsonValueKind.String).Select(v => v.GetString()!).ToArray();
    }

    private static IReadOnlyDictionary<string, object?>? ReadAnyRecord(JsonElement element, string property)
    {
        if (!element.TryGetProperty(property, out var value) || value.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        return value.EnumerateObject()
            .ToDictionary(
                entry => entry.Name,
                entry => JsonValueToCSharp(entry.Value));
    }

    private static IReadOnlyDictionary<string, string?>? ReadThinkingLevelMap(JsonElement element)
    {
        if (!element.TryGetProperty("thinkingLevelMap", out var value) || value.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var result = new Dictionary<string, string?>();
        foreach (var entry in value.EnumerateObject())
        {
            result[entry.Name] = entry.Value.ValueKind == JsonValueKind.String
                ? entry.Value.GetString()
                : null;
        }

        return result;
    }

    private static ModelCostValue? ReadCost(JsonElement element)
    {
        if (!element.TryGetProperty("cost", out var cost) || cost.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var result = new ModelCostValue
        {
            Input = cost.GetProperty("input").GetDouble(),
            Output = cost.GetProperty("output").GetDouble(),
            CacheRead = cost.GetProperty("cacheRead").GetDouble(),
            CacheWrite = cost.GetProperty("cacheWrite").GetDouble(),
        };

        if (cost.TryGetProperty("tiers", out var tiers) && tiers.ValueKind == JsonValueKind.Array)
        {
            result = result with
            {
                Tiers = tiers.EnumerateArray().Select(tier => new ModelCostTierValue
                {
                    InputTokensAbove = tier.GetProperty("inputTokensAbove").GetDouble(),
                    Input = tier.GetProperty("input").GetDouble(),
                    Output = tier.GetProperty("output").GetDouble(),
                    CacheRead = tier.GetProperty("cacheRead").GetDouble(),
                    CacheWrite = tier.GetProperty("cacheWrite").GetDouble(),
                }).ToArray(),
            };
        }

        return result;
    }

    private static ModelCompatValue? ReadCompat(JsonElement element)
    {
        if (!element.TryGetProperty("compat", out var value) || value.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var result = new Dictionary<string, object?>();
        foreach (var entry in value.EnumerateObject())
        {
            result[entry.Name] = JsonValueToCSharp(entry.Value);
        }

        return new ModelCompatValue { Values = result };
    }

    /// <summary>Converts a JSON value to plain C# objects (strings, doubles, bools, null, lists, maps).</summary>
    public static object? JsonValueToCSharp(JsonElement value)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.String:
                return value.GetString();
            case JsonValueKind.Number:
                return value.GetDouble();
            case JsonValueKind.True:
                return true;
            case JsonValueKind.False:
                return false;
            case JsonValueKind.Null or JsonValueKind.Undefined:
                return null;
            case JsonValueKind.Array:
                return value.EnumerateArray().Select(JsonValueToCSharp).ToArray();
            case JsonValueKind.Object:
                return value.EnumerateObject()
                    .ToDictionary(
                        entry => entry.Name,
                        entry => JsonValueToCSharp(entry.Value));
            default:
                return null;
        }
    }
}
