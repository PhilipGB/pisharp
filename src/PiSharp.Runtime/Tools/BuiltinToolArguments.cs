using System.Collections;
using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.Extensions.AI;
using PiSharp.Core;

namespace PiSharp.Runtime.Tools;

/// <summary>Prepares built-in arguments before binding them to the existing typed tool methods.</summary>
internal static class BuiltinToolArguments
{
    private static readonly JsonSerializerOptions s_errorJson = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };
    private static readonly JsonSerializerOptions s_typedJson = new(JsonSerializerDefaults.Web);

    public static void PrepareAndValidate(string name, JsonElement schema, AIFunctionArguments arguments)
    {
        if (name == "edit") PrepareEdits(arguments);
        var received = arguments.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
        var required = Required(schema);
        foreach (var property in schema.GetProperty("properties").EnumerateObject())
            if (!required.Contains(property.Name, StringComparer.Ordinal) &&
                arguments.TryGetValue(property.Name, out var value) && Kind(value) == JsonValueKind.Null)
                arguments.Remove(property.Name);

        var converted = (Dictionary<string, object?>)Convert(arguments, schema)!;
        arguments.Clear();
        foreach (var pair in converted) arguments[pair.Key] = pair.Value;

        var errors = new List<string>();
        Validate(arguments, schema, "", errors);
        if (errors.Count > 0)
            throw new ToolFailureException($"Validation failed for tool \"{name}\":\n{string.Join('\n', errors)}" +
                $"\n\nReceived arguments:\n{JsonSerializer.Serialize(received, s_errorJson)}");
    }

    private static void PrepareEdits(AIFunctionArguments arguments)
    {
        arguments.TryGetValue("edits", out var edits);
        if (Kind(edits) == JsonValueKind.String)
        {
            try
            {
                using var document = JsonDocument.Parse(edits is JsonElement json ? json.GetString()! : (string)edits!);
                var parsed = document.RootElement.Clone();
                if (parsed.ValueKind == JsonValueKind.Array) arguments["edits"] = edits = parsed;
                else if (IsEdit(parsed)) arguments["edits"] = edits = new object?[] { parsed };
            }
            catch (JsonException) { }
        }
        else if (IsEdit(edits)) arguments["edits"] = edits = new object?[] { edits };

        if (!arguments.TryGetValue("oldText", out var oldText) || Kind(oldText) != JsonValueKind.String ||
            !arguments.TryGetValue("newText", out var newText) || Kind(newText) != JsonValueKind.String)
            return;

        var replacements = Kind(edits) == JsonValueKind.Array ? Items(edits!).ToList() : [];
        replacements.Add(new Dictionary<string, object?> { ["oldText"] = oldText, ["newText"] = newText });
        arguments["edits"] = replacements;
        arguments.Remove("oldText");
        arguments.Remove("newText");
    }

    private static bool IsEdit(object? value) => Kind(value) == JsonValueKind.Object &&
        TryGet(value!, "oldText", out var oldText) && Kind(oldText) == JsonValueKind.String &&
        TryGet(value!, "newText", out var newText) && Kind(newText) == JsonValueKind.String;

    private static string[] Required(JsonElement schema) => schema.TryGetProperty("required", out var required)
        ? required.EnumerateArray().Select(item => item.GetString()!).ToArray() : [];

    private static object? Convert(object? value, JsonElement schema)
    {
        var type = schema.GetProperty("type").GetString()!;
        if (value is not (null or JsonElement or string or bool or byte or sbyte or short or ushort or int or uint or
            long or ulong or float or double or decimal or IDictionary<string, object?> or TextEdit) &&
            !(type == "array" && value is IList))
            value = JsonSerializer.SerializeToElement(value, s_typedJson);
        if (type == "array")
        {
            var items = Kind(value) == JsonValueKind.Array ? Items(value!) : [value];
            return items.Select(item => Convert(item, schema.GetProperty("items"))).ToList();
        }
        if (type != "object") return BuiltinScalarConversions.Convert(value, type);
        if (Kind(value) != JsonValueKind.Object) return value;
        var properties = value switch
        {
            JsonElement json => json.EnumerateObject().ToDictionary(property => property.Name, property => (object?)property.Value),
            TextEdit edit => new Dictionary<string, object?> { ["oldText"] = edit.OldText, ["newText"] = edit.NewText },
            _ => ((IDictionary<string, object?>)value!).ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal)
        };
        foreach (var property in schema.GetProperty("properties").EnumerateObject())
            if (properties.TryGetValue(property.Name, out var child))
                properties[property.Name] = Convert(child, property.Value);
        return properties;
    }

    private static void Validate(object? value, JsonElement schema, string path, List<string> errors)
    {
        var expected = schema.GetProperty("type").GetString();
        var kind = Kind(value);
        var valid = expected switch
        {
            "object" => kind == JsonValueKind.Object,
            "array" => kind == JsonValueKind.Array,
            "string" => kind == JsonValueKind.String,
            "number" => kind == JsonValueKind.Number,
            "boolean" => kind is JsonValueKind.True or JsonValueKind.False,
            _ => false
        };
        if (!valid)
        {
            errors.Add($"  - {path}: must be {expected}");
            return;
        }
        if (kind == JsonValueKind.Object)
        {
            var missing = Required(schema).Where(property => !TryGet(value!, property, out _)).ToArray();
            if (missing.Length > 0)
                errors.Add($"  - {Join(path, missing[0])}: must have required properties {string.Join(", ", missing)}");
            foreach (var property in schema.GetProperty("properties").EnumerateObject())
                if (TryGet(value!, property.Name, out var child))
                    Validate(child, property.Value, Join(path, property.Name), errors);
        }
        else if (kind == JsonValueKind.Array)
        {
            var index = 0;
            foreach (var child in Items(value!))
                Validate(child, schema.GetProperty("items"), Join(path, (index++).ToString(System.Globalization.CultureInfo.InvariantCulture)), errors);
        }
    }

    private static string Join(string path, string property) => path.Length == 0 ? property : path + "." + property;

    private static bool TryGet(object value, string property, out object? result)
    {
        if (value is IDictionary<string, object?> dictionary) return dictionary.TryGetValue(property, out result);
        if (value is JsonElement json && json.ValueKind == JsonValueKind.Object && json.TryGetProperty(property, out var child))
        {
            result = child;
            return true;
        }
        if (value is TextEdit edit && property is "oldText" or "newText")
        {
            result = property == "oldText" ? edit.OldText : edit.NewText;
            return true;
        }
        result = null;
        return false;
    }

    private static IEnumerable<object?> Items(object value) => value is JsonElement json
        ? json.EnumerateArray().Select(item => (object?)item) : ((IEnumerable)value).Cast<object?>();

    private static JsonValueKind Kind(object? value) => value switch
    {
        null => JsonValueKind.Null,
        JsonElement json when json.ValueKind == JsonValueKind.Number &&
            (!json.TryGetDouble(out var number) || !double.IsFinite(number)) => JsonValueKind.Undefined,
        JsonElement json => json.ValueKind,
        string => JsonValueKind.String,
        bool boolean => boolean ? JsonValueKind.True : JsonValueKind.False,
        double number when !double.IsFinite(number) => JsonValueKind.Undefined,
        float number when !float.IsFinite(number) => JsonValueKind.Undefined,
        byte or sbyte or short or ushort or int or uint or long or ulong or float or double or decimal => JsonValueKind.Number,
        IDictionary<string, object?> or TextEdit => JsonValueKind.Object,
        IEnumerable => JsonValueKind.Array,
        _ => JsonValueKind.Undefined
    };
}
