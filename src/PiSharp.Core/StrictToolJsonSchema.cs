using System.Text.Json;
using System.Text.Json.Nodes;
using System.Globalization;

namespace PiSharp.Core;

/// <summary>Projects an independent tool schema into the provider strict JSON-schema subset.</summary>
public static class StrictToolJsonSchema
{
    private static readonly HashSet<string> s_unsupported = new(StringComparer.Ordinal)
    {
        "$ref", "$defs", "definitions", "allOf", "oneOf", "patternProperties", "dependentSchemas",
        "dependencies", "unevaluatedProperties", "propertyNames", "contains", "prefixItems", "not", "if", "then", "else"
    };

    public static JsonElement Create(JsonElement schema, Func<string, JsonElement, bool>? isUnsupported = null)
    {
        if (JsonNode.Parse(schema.GetRawText()) is not JsonObject root)
            throw new ArgumentException("root schema must have type object");
        ConvertNode(root, isUnsupported);
        if (root["type"]?.ToJsonString() != "\"object\"")
            throw new ArgumentException("root schema must have type object");
        return JsonSerializer.SerializeToElement(root);
    }

    private static void ConvertNode(JsonNode? node, Func<string, JsonElement, bool>? isUnsupported)
    {
        if (node is not JsonObject schema) throw new ArgumentException("boolean schemas are unsupported");
        foreach (var key in s_unsupported)
            if (schema.ContainsKey(key)) throw new ArgumentException($"{key} schemas are unsupported");
        if (isUnsupported is not null)
            foreach (var pair in schema)
                if (isUnsupported(pair.Key, JsonSerializer.SerializeToElement(pair.Value)))
                    throw new ArgumentException($"{pair.Key}: {pair.Value?.ToJsonString() ?? "null"} is unsupported");
        if (schema.ContainsKey("anyOf"))
        {
            if (schema["anyOf"] is not JsonArray { Count: > 0 } variants)
                throw new ArgumentException("anyOf must contain at least one schema");
            foreach (var variant in variants)
            {
                if (IsStructured(variant)) throw new ArgumentException("object and array unions are unsupported");
                ConvertNode(variant, isUnsupported);
            }
        }
        if (schema.ContainsKey("items"))
        {
            if (schema["items"] is JsonArray) throw new ArgumentException("tuple schemas are unsupported");
            ConvertNode(schema["items"], isUnsupported);
        }
        var isObject = schema["type"]?.ToJsonString() == "\"object\"";
        if (schema.ContainsKey("properties") && !isObject)
            throw new ArgumentException("properties require type object");
        if (!isObject) return;
        if (schema.ContainsKey("additionalProperties") && schema["additionalProperties"]?.ToJsonString() != "false")
            throw new ArgumentException("schema-valued or true additionalProperties is unsupported");
        if (schema.ContainsKey("properties") && schema["properties"] is not JsonObject)
            throw new ArgumentException("object properties must be a schema map");
        if (schema.ContainsKey("required") && (schema["required"] is not JsonArray requiredArray ||
            requiredArray.Any(value => value is not JsonValue text || !text.TryGetValue<string>(out _))))
            throw new ArgumentException("object required must be a string array");
        var properties = schema["properties"] as JsonObject ?? new JsonObject();
        var required = schema["required"] is JsonArray keys
            ? keys.Select(value => value!.GetValue<string>()).ToHashSet(StringComparer.Ordinal) : [];
        if (required.Any(key => !properties.ContainsKey(key)))
            throw new ArgumentException("required contains an unknown property");
        var ordered = properties.OrderBy(pair => PropertyOrder(pair.Key)).ToArray();
        foreach (var pair in ordered)
        {
            ConvertNode(pair.Value, isUnsupported);
            if (!required.Contains(pair.Key) && !AllowsNull(pair.Value))
                properties[pair.Key] = new JsonObject { ["anyOf"] = new JsonArray(pair.Value!.DeepClone(), new JsonObject { ["type"] = "null" }) };
        }
        schema["required"] = new JsonArray(ordered.Select(pair => (JsonNode?)JsonValue.Create(pair.Key)).ToArray());
        schema["additionalProperties"] = false;
    }

    // Pi's Object.keys visits canonical array-index names first, then other names in insertion order.
    private static ulong PropertyOrder(string key) => uint.TryParse(key, NumberStyles.None, CultureInfo.InvariantCulture, out var index) &&
        index != uint.MaxValue && index.ToString(CultureInfo.InvariantCulture) == key ? index : ulong.MaxValue;

    private static bool IsStructured(JsonNode? node) => node is JsonObject schema &&
        (HasType(schema, "object") || HasType(schema, "array") || schema.ContainsKey("properties") || schema.ContainsKey("items"));

    private static bool HasType(JsonObject schema, string type) => schema["type"] is JsonArray types
        ? types.Any(value => value?.ToJsonString() == JsonSerializer.Serialize(type))
        : schema["type"]?.ToJsonString() == JsonSerializer.Serialize(type);

    private static bool AllowsNull(JsonNode? node) => node is JsonObject schema &&
        (HasType(schema, "null") || schema.ContainsKey("const") && schema["const"] is null ||
            schema["enum"] is JsonArray values && values.Any(value => value is null) ||
            schema["anyOf"] is JsonArray variants && variants.Any(AllowsNull));
}
