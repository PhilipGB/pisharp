using System.Text.Json;

namespace PiSharp.Core.Models;

/// <summary>
/// models.json schema validation mirroring the pinned TypeBox schemas (model-config.ts).
/// Diagnostics use TypeBox-style messages ("must be string", "must have required property
/// 'id'") with dot-separated JSON paths, so PiSharp validation output matches the shape of
/// Pi's "Invalid models.json schema" diagnostics.
/// </summary>
public static class ModelConfigValidator
{
    private static readonly string[] ThinkingLevelKeys = { "off", "minimal", "low", "medium", "high", "xhigh", "max" };
    private static readonly string[] InputLiterals = { "text", "image" };
    private static readonly string[] MaxTokensFieldLiterals = { "max_completion_tokens", "max_tokens" };
    private static readonly string[] ThinkingFormatLiterals =
    {
        "openai", "openrouter", "together", "baseten", "deepseek", "zai", "qwen",
        "chat-template", "qwen-chat-template", "string-thinking", "ant-ling",
    };
    private static readonly string[] SessionAffinityFormats = { "openai", "openai-nosession", "openrouter" };
    private static readonly string[] KwargVariables = { "thinking.enabled", "thinking.effort" };

    /// <summary>Validates the parsed models.json document and returns all schema errors.</summary>
    public static List<(string Path, string Message)> Validate(JsonElement root)
    {
        var errors = new List<(string, string)>();
        if (root.ValueKind != JsonValueKind.Object)
        {
            errors.Add(("root", "must be object"));
            return errors;
        }

        if (!root.TryGetProperty("providers", out var providers) || providers.ValueKind != JsonValueKind.Object)
        {
            errors.Add(("providers", "must be object"));
            return errors;
        }

        foreach (var entry in providers.EnumerateObject())
        {
            ValidateProvider(entry.Name, entry.Value, errors);
        }

        return errors;
    }

    private static void ValidateProvider(string providerId, JsonElement provider, List<(string, string)> errors)
    {
        var prefix = $"providers.{providerId}";
        if (provider.ValueKind != JsonValueKind.Object)
        {
            errors.Add((prefix, "must be object"));
            return;
        }

        ValidateOptionalStringMin1(provider, "name", prefix, errors);
        ValidateOptionalStringMin1(provider, "baseUrl", prefix, errors);
        ValidateOptionalStringMin1(provider, "apiKey", prefix, errors);
        ValidateOptionalStringMin1(provider, "api", prefix, errors);
        ValidateOptionalLiteral(provider, "oauth", ["radius"], prefix, errors);
        ValidateStringRecord(provider, "headers", prefix, errors);
        ValidateCompat(provider, $"{prefix}.compat", errors);
        ValidateOptionalBoolean(provider, "authHeader", prefix, errors);

        if (provider.TryGetProperty("models", out var models))
        {
            if (models.ValueKind != JsonValueKind.Array)
            {
                errors.Add(($"{prefix}.models", "must be array"));
            }
            else
            {
                for (var index = 0; index < models.GetArrayLength(); index++)
                {
                    ValidateModelDefinition(models[index], $"{prefix}.models.{index}", errors);
                }
            }
        }

        if (provider.TryGetProperty("modelOverrides", out var overrides))
        {
            if (overrides.ValueKind != JsonValueKind.Object)
            {
                errors.Add(($"{prefix}.modelOverrides", "must be object"));
            }
            else
            {
                foreach (var entry in overrides.EnumerateObject())
                {
                    ValidateModelOverride(entry.Name, entry.Value, prefix, errors);
                }
            }
        }
    }

    private static void ValidateModelDefinition(JsonElement model, string prefix, List<(string, string)> errors)
    {
        if (model.ValueKind != JsonValueKind.Object)
        {
            errors.Add((prefix, "must be object"));
            return;
        }

        if (!model.TryGetProperty("id", out var id) || id.ValueKind != JsonValueKind.String || id.GetString()!.Length < 1)
        {
            errors.Add(($"{prefix}.id", id.ValueKind == JsonValueKind.Undefined
                ? "must have required property 'id'"
                : "must NOT have fewer than 1 characters"));
        }

        ValidateOptionalStringMin1(model, "name", prefix, errors);
        ValidateOptionalStringMin1(model, "api", prefix, errors);
        ValidateOptionalStringMin1(model, "baseUrl", prefix, errors);
        ValidateModelSharedFields(model, prefix, errors);
    }

    private static void ValidateModelOverride(string modelId, JsonElement overrideElement, string providerPrefix, List<(string, string)> errors)
    {
        var prefix = $"{providerPrefix}.modelOverrides.{modelId}";
        if (overrideElement.ValueKind != JsonValueKind.Object)
        {
            errors.Add((prefix, "must be object"));
            return;
        }

        ValidateOptionalStringMin1(overrideElement, "name", prefix, errors);
        ValidateModelSharedFields(overrideElement, prefix, errors);
    }

    private static void ValidateModelSharedFields(JsonElement model, string prefix, List<(string, string)> errors)
    {
        ValidateOptionalBoolean(model, "reasoning", prefix, errors);
        ValidateThinkingLevelMap(model, prefix, errors);

        if (model.TryGetProperty("input", out var input))
        {
            if (input.ValueKind != JsonValueKind.Array)
            {
                errors.Add(($"{prefix}.input", "must be array"));
            }
            else
            {
                for (var index = 0; index < input.GetArrayLength(); index++)
                {
                    var item = input[index];
                    if (item.ValueKind != JsonValueKind.String || !InputLiterals.Contains(item.GetString()!))
                    {
                        errors.Add(($"{prefix}.input.{index}", "must be equal to one of the allowed values"));
                    }
                }
            }
        }

        ValidateCost(model, prefix, errors);
        ValidateOptionalNumber(model, "contextWindow", prefix, errors);
        ValidateOptionalNumber(model, "maxTokens", prefix, errors);
        ValidateAnyRecord(model, "samplingParams", prefix, errors);
        ValidateStringRecord(model, "headers", prefix, errors);
        ValidateCompat(model, $"{prefix}.compat", errors);
    }

    private static void ValidateThinkingLevelMap(JsonElement model, string prefix, List<(string, string)> errors)
    {
        if (!model.TryGetProperty("thinkingLevelMap", out var map))
        {
            return;
        }

        if (map.ValueKind != JsonValueKind.Object)
        {
            errors.Add(($"{prefix}.thinkingLevelMap", "must be object"));
            return;
        }

        foreach (var entry in map.EnumerateObject())
        {
            if (!ThinkingLevelKeys.Contains(entry.Name))
            {
                errors.Add(($"{prefix}.thinkingLevelMap", $"must NOT have additional properties: {entry.Name}"));
                continue;
            }

            if (entry.Value.ValueKind != JsonValueKind.String && entry.Value.ValueKind != JsonValueKind.Null)
            {
                errors.Add(($"{prefix}.thinkingLevelMap.{entry.Name}", "must be string or null"));
            }
        }
    }

    private static void ValidateCost(JsonElement model, string prefix, List<(string, string)> errors)
    {
        if (!model.TryGetProperty("cost", out var cost))
        {
            return;
        }

        if (cost.ValueKind != JsonValueKind.Object)
        {
            errors.Add(($"{prefix}.cost", "must be object"));
            return;
        }

        ValidateRequiredNumber(cost, "input", $"{prefix}.cost", errors);
        ValidateRequiredNumber(cost, "output", $"{prefix}.cost", errors);
        ValidateRequiredNumber(cost, "cacheRead", $"{prefix}.cost", errors);
        ValidateRequiredNumber(cost, "cacheWrite", $"{prefix}.cost", errors);

        if (!cost.TryGetProperty("tiers", out var tiers))
        {
            return;
        }

        if (tiers.ValueKind != JsonValueKind.Array)
        {
            errors.Add(($"{prefix}.cost.tiers", "must be array"));
            return;
        }

        for (var index = 0; index < tiers.GetArrayLength(); index++)
        {
            var tier = tiers[index];
            if (tier.ValueKind != JsonValueKind.Object)
            {
                errors.Add(($"{prefix}.cost.tiers.{index}", "must be object"));
                continue;
            }

            ValidateRequiredNumber(tier, "inputTokensAbove", $"{prefix}.cost.tiers.{index}", errors);
            ValidateRequiredNumber(tier, "input", $"{prefix}.cost.tiers.{index}", errors);
            ValidateRequiredNumber(tier, "output", $"{prefix}.cost.tiers.{index}", errors);
            ValidateRequiredNumber(tier, "cacheRead", $"{prefix}.cost.tiers.{index}", errors);
            ValidateRequiredNumber(tier, "cacheWrite", $"{prefix}.cost.tiers.{index}", errors);
        }
    }

    private static void ValidateCompat(JsonElement owner, string prefix, List<(string, string)> errors)
    {
        if (!owner.TryGetProperty("compat", out var compat) || compat.ValueKind == JsonValueKind.Undefined)
        {
            return;
        }

        if (compat.ValueKind != JsonValueKind.Object)
        {
            errors.Add((prefix, "must be object"));
            return;
        }

        foreach (var entry in compat.EnumerateObject())
        {
            ValidateCompatKey(entry.Name, entry.Value, prefix, errors);
        }
    }

    private static void ValidateCompatKey(string key, JsonElement value, string prefix, List<(string, string)> errors)
    {
        switch (key)
        {
            case "supportsStore" or "supportsDeveloperRole" or "supportsReasoningEffort"
                or "supportsUsageInStreaming" or "supportsFinishReason" or "requiresToolResultName"
                or "requiresAssistantAfterToolResult" or "requiresThinkingAsText"
                or "requiresReasoningContentOnAssistantMessages" or "supportsOpenAIGrammarTools"
                or "supportsStrictMode" or "sendSessionAffinityHeaders" or "supportsLongCacheRetention"
                or "supportsEagerToolInputStreaming" or "supportsCacheControlOnTools"
                or "supportsTemperature" or "forceAdaptiveThinking" or "allowEmptySignature"
                or "supportsStrictTools" or "supportsMidConvoEffort" or "supportsToolReferences"
                or "supportsAdditionalTools" or "supportsToolSearch" or "supportsExplicitPromptCacheMode"
                or "supportsMaxOutputTokens":
                CheckType(value, JsonValueKind.True, $"{prefix}.{key}", "must be boolean", errors);
                break;
            case "maxTokensField":
                CheckLiteral(value, MaxTokensFieldLiterals, $"{prefix}.{key}", errors);
                break;
            case "thinkingFormat":
                CheckLiteral(value, ThinkingFormatLiterals, $"{prefix}.{key}", errors);
                break;
            case "cacheControlFormat":
                CheckLiteral(value, ["anthropic"], $"{prefix}.{key}", errors);
                break;
            case "deferredToolsMode":
                CheckLiteral(value, ["kimi"], $"{prefix}.{key}", errors);
                break;
            case "sessionAffinityFormat":
                CheckLiteral(value, SessionAffinityFormats, $"{prefix}.{key}", errors);
                break;
            case "vllmPriority":
                CheckType(value, JsonValueKind.Number, $"{prefix}.{key}", "must be number", errors);
                break;
            case "chatTemplateKwargs" or "chatTemplateArgs":
                ValidateChatTemplateRecord(value, $"{prefix}.{key}", errors);
                break;
            case "openRouterRouting":
                ValidateOpenRouterRouting(value, $"{prefix}.{key}", errors);
                break;
            case "vercelGatewayRouting":
                ValidateVercelGatewayRouting(value, $"{prefix}.{key}", errors);
                break;
            default:
                // TypeBox object schemas tolerate unknown properties; unknown compat
                // keys pass validation (pinned behaviour) and are preserved as-is.
                break;
        }
    }

    private static void ValidateChatTemplateRecord(JsonElement element, string prefix, List<(string, string)> errors)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            errors.Add((prefix, "must be object"));
            return;
        }

        foreach (var entry in element.EnumerateObject())
        {
            var value = entry.Value;
            if (value.ValueKind is JsonValueKind.String or JsonValueKind.Number
                or JsonValueKind.True or JsonValueKind.False or JsonValueKind.Null)
            {
                continue;
            }

            if (value.ValueKind == JsonValueKind.Object &&
                value.TryGetProperty("$var", out var variable) &&
                variable.ValueKind == JsonValueKind.String &&
                KwargVariables.Contains(variable.GetString()!))
            {
                if (value.TryGetProperty("omitWhenOff", out var omit) && omit.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                {
                    errors.Add(($"{prefix}.{entry.Name}.omitWhenOff", "must be boolean"));
                }

                continue;
            }

            errors.Add(($"{prefix}.{entry.Name}", "must be a scalar or a $var reference"));
        }
    }

    private static void ValidateOpenRouterRouting(JsonElement element, string prefix, List<(string, string)> errors)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            errors.Add((prefix, "must be object"));
            return;
        }

        foreach (var entry in element.EnumerateObject())
        {
            var value = entry.Value;
            switch (entry.Name)
            {
                case "allow_fallbacks" or "require_parameters" or "zdr" or "enforce_distillable_text":
                    CheckType(value, JsonValueKind.True, $"{prefix}.{entry.Name}", "must be boolean", errors);
                    break;
                case "data_collection":
                    CheckLiteral(value, ["deny", "allow"], $"{prefix}.{entry.Name}", errors);
                    break;
                case "order" or "only" or "ignore" or "quantizations":
                    ValidateStringArray(value, $"{prefix}.{entry.Name}", errors);
                    break;
                case "sort":
                    if (value.ValueKind != JsonValueKind.String &&
                        !(value.ValueKind == JsonValueKind.Object && IsRoutingSortObject(value)))
                    {
                        errors.Add(($"{prefix}.{entry.Name}", "must be a string or a by/partition object"));
                    }

                    break;
                case "max_price":
                    ValidateMaxPrice(value, $"{prefix}.{entry.Name}", errors);
                    break;
                case "preferred_min_throughput" or "preferred_max_latency":
                    if (value.ValueKind != JsonValueKind.Number &&
                        !(value.ValueKind == JsonValueKind.Object && IsPercentileCutoffs(value)))
                    {
                        errors.Add(($"{prefix}.{entry.Name}", "must be a number or percentile cutoffs"));
                    }

                    break;
                default:
                    break;
            }
        }
    }

    private static bool IsRoutingSortObject(JsonElement element)
    {
        foreach (var property in element.EnumerateObject())
        {
            if (property.Name == "by" && property.Value.ValueKind == JsonValueKind.String)
            {
                continue;
            }

            if (property.Name == "partition" &&
                property.Value.ValueKind is JsonValueKind.String or JsonValueKind.Null)
            {
                continue;
            }

            return false;
        }

        return true;
    }

    private static bool IsPercentileCutoffs(JsonElement element)
    {
        foreach (var property in element.EnumerateObject())
        {
            if (!property.Name.StartsWith("p", StringComparison.Ordinal) ||
                property.Value.ValueKind != JsonValueKind.Number)
            {
                return false;
            }
        }

        return true;
    }

    private static void ValidateMaxPrice(JsonElement element, string prefix, List<(string, string)> errors)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            errors.Add((prefix, "must be object"));
            return;
        }

        foreach (var entry in element.EnumerateObject())
        {
            if (entry.Value.ValueKind is not (JsonValueKind.Number or JsonValueKind.String))
            {
                errors.Add(($"{prefix}.{entry.Name}", "must be a number or string"));
            }
        }
    }

    private static void ValidateVercelGatewayRouting(JsonElement element, string prefix, List<(string, string)> errors)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            errors.Add((prefix, "must be object"));
            return;
        }

        ValidateStringArrayMaybe(element, "only", prefix, errors);
        ValidateStringArrayMaybe(element, "order", prefix, errors);
    }

    private static void ValidateStringArrayMaybe(JsonElement owner, string property, string prefix, List<(string, string)> errors)
    {
        if (owner.TryGetProperty(property, out var value))
        {
            ValidateStringArray(value, $"{prefix}.{property}", errors);
        }
    }

    private static void ValidateStringArray(JsonElement element, string prefix, List<(string, string)> errors)
    {
        if (element.ValueKind != JsonValueKind.Array)
        {
            errors.Add((prefix, "must be an array of strings"));
            return;
        }

        for (var index = 0; index < element.GetArrayLength(); index++)
        {
            if (element[index].ValueKind != JsonValueKind.String)
            {
                errors.Add(($"{prefix}.{index}", "must be string"));
            }
        }
    }

    private static void ValidateStringRecord(JsonElement owner, string property, string prefix, List<(string, string)> errors)
    {
        if (!owner.TryGetProperty(property, out var value))
        {
            return;
        }

        if (value.ValueKind != JsonValueKind.Object)
        {
            errors.Add(($"{prefix}.{property}", "must be an object of strings"));
            return;
        }

        foreach (var entry in value.EnumerateObject())
        {
            if (entry.Value.ValueKind != JsonValueKind.String)
            {
                errors.Add(($"{prefix}.{property}.{entry.Name}", "must be string"));
            }
        }
    }

    private static void ValidateAnyRecord(JsonElement owner, string property, string prefix, List<(string, string)> errors)
    {
        if (owner.TryGetProperty(property, out var value) && value.ValueKind != JsonValueKind.Object)
        {
            errors.Add(($"{prefix}.{property}", "must be object"));
        }
    }

    private static void ValidateOptionalStringMin1(JsonElement owner, string property, string prefix, List<(string, string)> errors)
    {
        if (!owner.TryGetProperty(property, out var value))
        {
            return;
        }

        if (value.ValueKind != JsonValueKind.String)
        {
            errors.Add(($"{prefix}.{property}", "must be string"));
            return;
        }

        if (value.GetString()!.Length < 1)
        {
            errors.Add(($"{prefix}.{property}", "must NOT have fewer than 1 characters"));
        }
    }

    private static void ValidateOptionalBoolean(JsonElement owner, string property, string prefix, List<(string, string)> errors)
    {
        if (owner.TryGetProperty(property, out var value) && value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
        {
            errors.Add(($"{prefix}.{property}", "must be boolean"));
        }
    }

    private static void ValidateOptionalNumber(JsonElement owner, string property, string prefix, List<(string, string)> errors)
    {
        if (owner.TryGetProperty(property, out var value) && value.ValueKind != JsonValueKind.Number)
        {
            errors.Add(($"{prefix}.{property}", "must be number"));
        }
    }

    private static void ValidateRequiredNumber(JsonElement owner, string property, string prefix, List<(string, string)> errors)
    {
        if (!owner.TryGetProperty(property, out var value))
        {
            errors.Add((prefix, $"must have required property '{property}'"));
            return;
        }

        if (value.ValueKind != JsonValueKind.Number)
        {
            errors.Add(($"{prefix}.{property}", "must be number"));
        }
    }

    private static void ValidateOptionalLiteral(
        JsonElement owner, string property, string[] literals, string prefix, List<(string, string)> errors)
    {
        if (owner.TryGetProperty(property, out var value))
        {
            CheckLiteral(value, literals, $"{prefix}.{property}", errors);
        }
    }

    private static void CheckLiteral(JsonElement value, string[] literals, string path, List<(string, string)> errors)
    {
        if (value.ValueKind != JsonValueKind.String || !literals.Contains(value.GetString()!))
        {
            errors.Add((path, literals.Length == 1 ? "must be equal to constant" : "must be equal to one of the allowed values"));
        }
    }

    private static void CheckType(JsonElement value, JsonValueKind expected, string path, string message, List<(string, string)> errors)
    {
        var matches = expected == JsonValueKind.True
            ? value.ValueKind is JsonValueKind.True or JsonValueKind.False
            : value.ValueKind == expected;
        if (!matches)
        {
            errors.Add((path, message));
        }
    }
}
