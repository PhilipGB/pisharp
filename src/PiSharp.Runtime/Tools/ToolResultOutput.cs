using System.Text.Json;
using PiSharp.Core;

namespace PiSharp.Runtime.Tools;

internal static class ToolResultOutput
{
    public static bool TryRead(object? value, out string text, out object? details)
    {
        if (ReadToolOutput.TryRead(value, out var readOutput))
        {
            text = readOutput.Text;
            details = null;
            return true;
        }
        if (value is IStructuredToolOutput structured)
        {
            text = structured.Text;
            details = structured.EventDetails;
            return true;
        }
        if (value is JsonElement json && TryRead(json, out text, out details)) return true;
        if (value is string serialized)
        {
            try
            {
                using var document = JsonDocument.Parse(serialized);
                if (TryRead(document.RootElement, out text, out details)) return true;
            }
            catch (JsonException) { }
        }
        text = "";
        details = null;
        return false;
    }

    private static bool TryRead(JsonElement value, out string text, out object? details)
    {
        text = "";
        details = null;
        if (value.ValueKind != JsonValueKind.Object || !TryGetString(value, "Text", out text)) return false;
        if (TryGetProperty(value, "Details", out var nestedDetails))
        {
            if (nestedDetails.ValueKind is not JsonValueKind.Null and not JsonValueKind.Undefined)
                details = nestedDetails.Clone();
            return true;
        }
        if (TryGetString(value, "Diff", out var diff) && TryGetString(value, "Patch", out var patch))
        {
            int? firstChangedLine = TryGetInt32(value, "FirstChangedLine", out var line) ? line : null;
            details = new FileEditDetails(diff, patch, firstChangedLine);
            return true;
        }
        text = "";
        return false;
    }

    private static bool TryGetString(JsonElement value, string property, out string text)
    {
        if (TryGetProperty(value, property, out var item) && item.ValueKind == JsonValueKind.String)
        {
            text = item.GetString()!;
            return true;
        }
        text = "";
        return false;
    }

    private static bool TryGetInt32(JsonElement value, string property, out int number)
    {
        if (TryGetProperty(value, property, out var item) && item.ValueKind == JsonValueKind.Number && item.TryGetInt32(out number))
            return true;
        number = 0;
        return false;
    }

    private static bool TryGetProperty(JsonElement value, string property, out JsonElement result)
    {
        foreach (var item in value.EnumerateObject())
            if (item.Name.Equals(property, StringComparison.OrdinalIgnoreCase))
            {
                result = item.Value;
                return true;
            }
        result = default;
        return false;
    }
}
