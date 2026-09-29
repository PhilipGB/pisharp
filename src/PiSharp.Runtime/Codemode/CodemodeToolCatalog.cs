using System.Text.Json;
using PiSharp.Runtime.Extensions;

namespace PiSharp.Runtime.Codemode;

/// <summary>Bounded script and model-facing descriptions of session-callable tools.</summary>
internal static class CodemodeToolCatalog
{
    public static string JavascriptIdentifier(string name)
    {
        var chars = name.Select((character, index) =>
            char.IsAsciiLetter(character) || character is '_' or '$' || index > 0 && char.IsAsciiDigit(character)
                ? character : '_').ToArray();
        return new(chars);
    }

    public static string RenderToolSample(PiSharpToolRegistration tool)
    {
        var name = JavascriptIdentifier(tool.Function.Name);
        var parameters = SchemaType(tool.Function.JsonSchema, 0);
        var result = tool.OutputSchema is { } output ? SchemaType(output, 0) : "string";
        var description = tool.Function.Description ?? "";
        if (description.Length > 1024) description = description[..1024];
        var sample = description.Trim() + "\n\ncodemode tool declaration:\n```ts\n" +
            $"declare const tools: {{ {name}(args: {parameters}): Promise<{result}>; }};\n```";
        return sample.Length > 4096 ? sample[..4096] : sample;
    }

    private static string SchemaType(JsonElement schema, int depth)
    {
        if (depth >= 4 || schema.ValueKind != JsonValueKind.Object) return "unknown";
        if (schema.TryGetProperty("type", out var type) && type.ValueKind == JsonValueKind.String)
            switch (type.GetString())
            {
                case "string": return "string";
                case "number" or "integer": return "number";
                case "boolean": return "boolean";
                case "null": return "null";
                case "array":
                    return schema.TryGetProperty("items", out var items)
                    ? SchemaType(items, depth + 1) + "[]" : "unknown[]";
            }
        if (!schema.TryGetProperty("properties", out var properties) ||
            properties.ValueKind != JsonValueKind.Object) return "unknown";
        var required = schema.TryGetProperty("required", out var requiredItems) &&
            requiredItems.ValueKind == JsonValueKind.Array
                ? requiredItems.EnumerateArray().Where(item => item.ValueKind == JsonValueKind.String)
                    .Select(item => item.GetString()!).ToHashSet(StringComparer.Ordinal)
                : new HashSet<string>(StringComparer.Ordinal);
        var fields = properties.EnumerateObject().Take(32).Select(property =>
            JsonSerializer.Serialize(property.Name) + (required.Contains(property.Name) ? ": " : "?: ") +
            SchemaType(property.Value, depth + 1));
        return "{ " + string.Join("; ", fields) + " }";
    }
}
