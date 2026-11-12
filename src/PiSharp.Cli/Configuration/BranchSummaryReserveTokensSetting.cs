using System.Globalization;
using System.Numerics;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace PiSharp.Cli;

/// <summary>A presence-carrying file value, coerced only when branch history is budgeted.</summary>
public readonly record struct BranchSummaryReserveTokensSetting(string Raw)
{
    public static BranchSummaryReserveTokensSetting From(JsonElement value) => new(value.GetRawText());

    public double Resolve()
    {
        using var document = JsonDocument.Parse(Raw);
        var value = document.RootElement;
        return value.ValueKind == JsonValueKind.Null ? BranchSummarySettings.DefaultReserveTokens : ToNumber(value);
    }

    internal BranchSummaryReserveTokensSetting Overlay(BranchSummaryReserveTokensSetting upper)
    {
        var lowerNode = JsonNode.Parse(Raw);
        var upperNode = JsonNode.Parse(upper.Raw);
        if (lowerNode is not JsonObject lowerObject || upperNode is not JsonObject upperObject) return upper;
        Merge(lowerObject, upperObject);
        return new(lowerObject.ToJsonString());
    }

    private static void Merge(JsonObject lower, JsonObject upper)
    {
        foreach (var (key, value) in upper)
        {
            if (lower[key] is JsonObject lowerObject && value is JsonObject upperObject)
                Merge(lowerObject, upperObject);
            else lower[key] = value?.DeepClone();
        }
    }

    // JSON's bounded subset of Number(value): arrays use their comma-joined primitive string.
    private static double ToNumber(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Number => value.GetDouble(),
        JsonValueKind.True => 1,
        JsonValueKind.False or JsonValueKind.Null => 0,
        JsonValueKind.String => ParseNumber(value.GetString()!),
        JsonValueKind.Array => ParseNumber(ArrayString(value)),
        _ => double.NaN
    };

    private static string ArrayString(JsonElement array) => string.Join(",", array.EnumerateArray().Select(value =>
        value.ValueKind switch
        {
            JsonValueKind.Null => "",
            JsonValueKind.String => value.GetString()!,
            JsonValueKind.Array => ArrayString(value),
            JsonValueKind.Object => "[object Object]",
            JsonValueKind.Number => value.GetDouble().ToString("G", CultureInfo.InvariantCulture),
            JsonValueKind.True => "true",
            _ => "false"
        }));

    private static double ParseNumber(string text)
    {
        // ECMAScript whitespace differs from .NET Trim (notably BOM and U+0085).
        text = text.Trim('\u0009', '\u000A', '\u000B', '\u000C', '\u000D', '\u0020', '\u00A0', '\u1680',
            '\u2000', '\u2001', '\u2002', '\u2003', '\u2004', '\u2005', '\u2006', '\u2007', '\u2008',
            '\u2009', '\u200A', '\u2028', '\u2029', '\u202F', '\u205F', '\u3000', '\uFEFF');
        if (text.Length == 0) return 0;
        if (text is "Infinity" or "+Infinity") return double.PositiveInfinity;
        if (text == "-Infinity") return double.NegativeInfinity;
        if (text.Length > 2 && text[0] == '0' && char.ToLowerInvariant(text[1]) is 'x' or 'b' or 'o')
        {
            var radix = char.ToLowerInvariant(text[1]) switch { 'x' => 16, 'b' => 2, _ => 8 };
            var number = BigInteger.Zero;
            foreach (var character in text.AsSpan(2))
            {
                var digit = character is >= '0' and <= '9' ? character - '0'
                    : char.ToLowerInvariant(character) - 'a' + 10;
                if (digit < 0 || digit >= radix) return double.NaN;
                number = number * radix + digit;
            }
            return (double)number;
        }
        return Regex.IsMatch(text, @"\A[+-]?(?:[0-9]+\.?[0-9]*|\.[0-9]+)(?:[eE][+-]?[0-9]+)?\z",
                   RegexOptions.CultureInvariant) &&
               double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
            ? parsed : double.NaN;
    }
}
