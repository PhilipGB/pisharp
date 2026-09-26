using System.Text.Json;
using System.Text.Json.Nodes;

namespace PiSharp.Runtime.Providers;

public static class ModelMetadataJson
{
    private static readonly string[] CompatibilityObjectProperties =
        ["openRouterRouting", "vercelGatewayRouting", "chatTemplateKwargs", "chatTemplateArgs"];

    public static JsonElement? ReadObject(JsonElement parent, string property)
    {
        return parent.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.Object
            ? value.Clone()
            : null;
    }

    public static JsonElement? ReadObject(JsonElement parent, string property, string source, bool strict)
    {
        if (!parent.TryGetProperty(property, out var value) || value.ValueKind == JsonValueKind.Null) return null;
        if (value.ValueKind == JsonValueKind.Object) return value.Clone();
        if (strict) throw new InvalidDataException($"{source} {property} must be an object.");
        return null;
    }

    public static JsonElement? MergeObjects(JsonElement? preferred, JsonElement? fallback)
    {
        if (preferred is null) return fallback?.Clone();
        if (fallback is null) return preferred.Value.Clone();
        if (preferred.Value.ValueKind != JsonValueKind.Object || fallback.Value.ValueKind != JsonValueKind.Object)
            return preferred.Value.Clone();

        var merged = JsonNode.Parse(fallback.Value.GetRawText())!.AsObject();
        foreach (var property in preferred.Value.EnumerateObject())
            merged[property.Name] = JsonNode.Parse(property.Value.GetRawText());
        return JsonSerializer.SerializeToElement(merged);
    }

    public static JsonElement? MergeCompatibility(JsonElement? preferred, JsonElement? fallback)
    {
        var merged = MergeObjects(preferred, fallback);
        if (merged is null || preferred is null || fallback is null ||
            preferred.Value.ValueKind != JsonValueKind.Object || fallback.Value.ValueKind != JsonValueKind.Object)
            return merged;

        var result = JsonNode.Parse(merged.Value.GetRawText())!.AsObject();
        foreach (var property in CompatibilityObjectProperties)
        {
            if (!preferred.Value.TryGetProperty(property, out var preferredValue) || preferredValue.ValueKind != JsonValueKind.Object ||
                !fallback.Value.TryGetProperty(property, out var fallbackValue) || fallbackValue.ValueKind != JsonValueKind.Object)
                continue;

            var nested = JsonNode.Parse(fallbackValue.GetRawText())!.AsObject();
            foreach (var item in preferredValue.EnumerateObject())
                nested[item.Name] = JsonNode.Parse(item.Value.GetRawText());
            result[property] = nested;
        }
        return JsonSerializer.SerializeToElement(result);
    }
}
