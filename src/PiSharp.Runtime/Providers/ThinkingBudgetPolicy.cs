using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.AI;

namespace PiSharp.Runtime.Providers;

/// <summary>Shared token-budget defaults and bounds for providers that count reasoning against output tokens.</summary>
public static class ThinkingBudgetPolicy
{
    public const string SettingsProperty = "pisharp.thinkingBudgets";
    public const int MinimumAnswerTokens = 1024;

    public static IReadOnlyDictionary<string, int>? Read(AdditionalPropertiesDictionary? properties)
    {
        if (properties?.TryGetValue(SettingsProperty, out var value) != true || value is null) return null;
        try
        {
            var json = value is JsonElement element ? element : JsonSerializer.SerializeToElement(value);
            if (json.ValueKind != JsonValueKind.Object) return null;
            var budgets = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var property in json.EnumerateObject())
            {
                if (property.Name is not ("minimal" or "low" or "medium" or "high") ||
                    property.Value.ValueKind != JsonValueKind.Number || !property.Value.TryGetInt32(out var budget) || budget < 0)
                    return null;
                budgets[property.Name] = budget;
            }
            return budgets;
        }
        catch (NotSupportedException)
        {
            return null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public static int ForLevel(string level, IReadOnlyDictionary<string, int>? customBudgets = null)
    {
        var key = level is "xhigh" or "max" ? "high" : level;
        if (customBudgets?.TryGetValue(key, out var custom) == true) return custom;
        return key switch
        {
            "minimal" => 1024,
            "low" => 2048,
            "medium" => 8192,
            "high" => 16384,
            _ => 0
        };
    }

    public static int ClampToAnswerRoom(int budget, int outputCeiling) =>
        Math.Min(budget, Math.Max(0, outputCeiling - MinimumAnswerTokens));

    public static (int MaxTokens, int ThinkingBudget) AdjustMaxTokens(int? requestedMaxTokens, int modelMaxTokens,
        string level, IReadOnlyDictionary<string, int>? customBudgets = null)
    {
        var budget = ForLevel(level, customBudgets);
        var maxTokens = requestedMaxTokens is { } requested
            ? (int)Math.Min((long)requested + budget, modelMaxTokens)
            : modelMaxTokens;
        if (maxTokens <= budget) budget = ClampToAnswerRoom(budget, maxTokens);
        return (maxTokens, budget);
    }

    public static string? ReadThinkingBudgetField(JsonElement? compatibility)
    {
        if (compatibility is not { ValueKind: JsonValueKind.Object } value) return null;
        if (value.TryGetProperty("thinkingTokenBudgetField", out var field) && field.ValueKind == JsonValueKind.String)
            return field.GetString();
        return value.TryGetProperty("supportsThinkingTokenBudget", out var supported) &&
            supported.ValueKind == JsonValueKind.True ? "thinking_token_budget" : null;
    }

    public static JsonElement? MergeBudgetParameter(JsonElement? parameters, string field, int budget)
    {
        var result = parameters is { ValueKind: JsonValueKind.Object } json
            ? JsonNode.Parse(json.GetRawText()) as JsonObject ?? new JsonObject()
            : new JsonObject();
        result[field] = budget;
        return JsonSerializer.SerializeToElement(result);
    }
}
