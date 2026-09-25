using PiSharp.Core;
using System.Text.Json;

namespace PiSharp.Runtime.Tools;

internal sealed record EditToolOutput(string Text, string Diff, string Patch, int? FirstChangedLine)
{
    public override string ToString() => Text;

    public FileEditDetails Details() => new(Diff, Patch, FirstChangedLine);

    public static bool TryRead(object? value, out EditToolOutput output)
    {
        if (value is EditToolOutput typed)
        {
            output = typed;
            return true;
        }
        if (value is JsonElement json && TryRead(json, out output)) return true;
        if (value is string serialized)
        {
            try
            {
                using var document = JsonDocument.Parse(serialized);
                if (TryRead(document.RootElement, out output)) return true;
            }
            catch (JsonException) { }
        }
        output = null!;
        return false;
    }

    private static bool TryRead(JsonElement value, out EditToolOutput output)
    {
        output = null!;
        if (value.ValueKind != JsonValueKind.Object || !TryGetString(value, nameof(Text), out var text) || text is null ||
            !TryGetString(value, nameof(Diff), out var diff) || diff is null ||
            !TryGetString(value, nameof(Patch), out var patch) || patch is null) return false;
        output = new EditToolOutput(text, diff, patch, TryGetInt32(value, nameof(FirstChangedLine)));
        return true;
    }

    private static bool TryGetString(JsonElement value, string property, out string? text)
    {
        foreach (var item in value.EnumerateObject())
            if (item.Name.Equals(property, StringComparison.OrdinalIgnoreCase) && item.Value.ValueKind == JsonValueKind.String)
            {
                text = item.Value.GetString();
                return true;
            }
        text = null;
        return false;
    }

    private static int? TryGetInt32(JsonElement value, string property)
    {
        foreach (var item in value.EnumerateObject())
            if (item.Name.Equals(property, StringComparison.OrdinalIgnoreCase) &&
                item.Value.ValueKind == JsonValueKind.Number && item.Value.TryGetInt32(out var number)) return number;
        return null;
    }
}
