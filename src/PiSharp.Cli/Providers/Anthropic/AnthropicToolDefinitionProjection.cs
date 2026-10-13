using System.Text.Json;
using System.Text.Json.Nodes;
using PiSharp.Core;

namespace PiSharp.Cli;

internal sealed class AnthropicToolDefinitionProjection(bool supportsStrict, bool eager, Func<string, string> wireName,
    bool disablePreferredStrict)
{
    private static readonly HashSet<string> s_unsupported = new(StringComparer.Ordinal)
    {
        "minimum", "maximum", "exclusiveMinimum", "exclusiveMaximum", "multipleOf", "maxItems", "uniqueItems",
        "minContains", "maxContains", "minProperties", "maxProperties"
    };
    private static readonly HashSet<string> s_formats = new(StringComparer.Ordinal)
    {
        "date-time", "time", "date", "duration", "email", "hostname", "uri", "ipv4", "ipv6", "uuid"
    };

    internal bool HasPreferredStrictTools { get; private set; }

    internal JsonObject Create(JsonElement tool, bool cache = false)
    {
        var parameters = tool.GetProperty("parameters");
        var name = tool.GetProperty("name").GetString()!;
        var preferred = false;
        var required = false;
        if (tool.TryGetProperty("constrainedSampling", out var config) && config.ValueKind == JsonValueKind.Object &&
            config.TryGetProperty("type", out var type) && type.GetString() == "json_schema")
        {
            required = config.TryGetProperty("strict", out var policy) && policy.GetString() == "require";
            preferred = config.TryGetProperty("strict", out policy) && policy.GetString() == "prefer";
        }
        var strict = false;
        if (required || preferred && !disablePreferredStrict)
        {
            if (!supportsStrict && required)
                throw new InvalidOperationException($"Tool \"{name}\" requires JSON-schema constrained sampling, but strict tools are unsupported.");
            if (supportsStrict)
            {
                try
                {
                    parameters = StrictToolJsonSchema.Create(parameters, IsUnsupported);
                    strict = true;
                }
                catch (ArgumentException error)
                {
                    if (required)
                        throw new InvalidOperationException($"Tool \"{name}\" requires JSON-schema constrained sampling, but {error.Message}.", error);
                }
            }
        }
        HasPreferredStrictTools |= strict && preferred;
        var schema = strict ? JsonNode.Parse(parameters.GetRawText())!.AsObject() : new JsonObject();
        schema["type"] = "object";
        schema["properties"] = parameters.ValueKind == JsonValueKind.Object && parameters.TryGetProperty("properties", out var properties)
            ? JsonNode.Parse(properties.GetRawText()) : new JsonObject();
        schema["required"] = parameters.ValueKind == JsonValueKind.Object && parameters.TryGetProperty("required", out var keys)
            ? JsonNode.Parse(keys.GetRawText()) : new JsonArray();
        var definition = new JsonObject
        {
            ["name"] = wireName(name),
            ["description"] = tool.GetProperty("description").GetString(),
            ["input_schema"] = schema
        };
        if (strict) definition["strict"] = true;
        if (eager) definition["eager_input_streaming"] = true;
        if (cache) definition["cache_control"] = new JsonObject { ["type"] = "ephemeral" };
        return definition;
    }

    private static bool IsUnsupported(string key, JsonElement value) => s_unsupported.Contains(key) ||
        key == "minItems" && (value.ValueKind != JsonValueKind.Number || !value.TryGetDouble(out var count) || count is not (0 or 1)) ||
        key == "format" && (value.ValueKind != JsonValueKind.String || !s_formats.Contains(value.GetString()!));
}
