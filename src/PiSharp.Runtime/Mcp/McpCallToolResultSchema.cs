using System.Text.Json;
using System.Text.Json.Nodes;

namespace PiSharp.Runtime.Mcp;

/// <summary>Describes the complete MCP result that Codemode receives for a nested call.</summary>
internal static class McpCallToolResultSchema
{
    private const string s_structuredContentPointer = "#/properties/structuredContent";
    private static readonly HashSet<string> s_dataKeywords =
        ["const", "enum", "default", "examples"];
    private static readonly HashSet<string> s_schemaMapKeywords =
        ["properties", "patternProperties", "dependentSchemas", "dependencies", "$defs", "definitions"];

    public static JsonElement Create(JsonElement? structuredContentSchema)
    {
        var result = JsonNode.Parse("""
            {
              "type": "object",
              "properties": {
                "content": {
                  "type": "array",
                  "items": {
                    "type": "object",
                    "properties": {
                      "type": { "enum": ["text", "image", "audio", "resource_link", "resource"] },
                      "text": { "type": "string" },
                      "data": { "type": "string" },
                      "mimeType": { "type": "string" }
                    },
                    "required": ["type"],
                    "additionalProperties": true
                  }
                },
                "structuredContent": {},
                "isError": { "type": "boolean" }
              },
              "required": ["content"],
              "additionalProperties": true
            }
            """)!.AsObject();
        if (structuredContentSchema is { } schema)
            result["properties"]!["structuredContent"] = Relocate(JsonNode.Parse(schema.GetRawText()));
        return JsonSerializer.SerializeToElement(result);
    }

    private static JsonNode? Relocate(JsonNode? node)
    {
        if (node is JsonArray array)
        {
            foreach (var item in array) Relocate(item);
            return array;
        }
        if (node is not JsonObject obj) return node;

        foreach (var (key, value) in obj.ToArray())
        {
            if (key == "$ref" && value is JsonValue referenceValue &&
                referenceValue.TryGetValue<string>(out var reference) &&
                (reference == "#" || reference.StartsWith("#/", StringComparison.Ordinal)))
            {
                obj[key] = s_structuredContentPointer + reference[1..];
            }
            else if (s_dataKeywords.Contains(key))
            {
                continue;
            }
            else if (s_schemaMapKeywords.Contains(key) && value is JsonObject schemaMap)
            {
                foreach (var (_, schema) in schemaMap.ToArray()) Relocate(schema);
            }
            else
            {
                Relocate(value);
            }
        }
        return obj;
    }
}
