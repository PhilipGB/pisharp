using System.Globalization;
using System.Numerics;
using System.Text.Json;

namespace PiSharp.Runtime.Tools;

/// <summary>The primitive conversions used by the pinned built-in TypeBox schemas.</summary>
internal static class BuiltinScalarConversions
{
    private const long MaximumSafeInteger = 9_007_199_254_740_991;
    private static readonly char[] s_numberWhitespace =
        ['\t', '\n', '\v', '\f', '\r', ' ', '\u00a0', '\u1680', '\u2000', '\u2001', '\u2002',
         '\u2003', '\u2004', '\u2005', '\u2006', '\u2007', '\u2008', '\u2009', '\u200a',
         '\u2028', '\u2029', '\u202f', '\u205f', '\u3000', '\ufeff'];

    public static object? Convert(object? value, string type)
    {
        var primitive = value is JsonElement json ? json.ValueKind switch
        {
            JsonValueKind.String => json.GetString(),
            JsonValueKind.Number when json.TryGetDouble(out var number) => (object)number,
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Null => null,
            _ => value
        } : value;

        if (type == "string")
            return primitive switch
            {
                null => "null",
                string text => text,
                bool boolean => boolean ? "true" : "false",
                _ when TryNumber(primitive, out var number) => NumberText(number),
                _ => value
            };
        if (type == "number")
        {
            if (primitive is null) return 0d;
            if (primitive is bool boolean) return boolean ? 1d : 0d;
            if (TryNumber(primitive, out var number)) return number;
            if (primitive is string text && ParseNumber(text, out number)) return number;
        }
        if (type == "boolean")
        {
            if (primitive is null) return false;
            if (primitive is bool boolean) return boolean;
            if (TryNumber(primitive, out var number) && number is 0 or 1) return number == 1;
            if (primitive is string text)
            {
                if (text.Equals("true", StringComparison.OrdinalIgnoreCase) || text == "1") return true;
                if (text.Equals("false", StringComparison.OrdinalIgnoreCase) || text == "0") return false;
            }
        }
        return value;
    }

    private static bool TryNumber(object? value, out double number)
    {
        if (value is byte or sbyte or short or ushort or int or uint or long or ulong or float or double or decimal)
        {
            number = System.Convert.ToDouble(value, CultureInfo.InvariantCulture);
            return double.IsFinite(number);
        }
        number = 0;
        return false;
    }

    private static bool ParseNumber(string text, out double number)
    {
        var trimmed = text.Trim(s_numberWhitespace);
        number = 0;
        if (trimmed.Length == 0) return true;
        if (double.TryParse(trimmed, NumberStyles.Float, CultureInfo.InvariantCulture, out number) && double.IsFinite(number))
            return true;
        if (trimmed.Length > 2 && trimmed[0] == '0')
        {
            var radix = char.ToLowerInvariant(trimmed[1]) switch { 'x' => 16, 'o' => 8, 'b' => 2, _ => 0 };
            if (radix != 0)
            {
                number = 0;
                foreach (var character in trimmed.AsSpan(2))
                {
                    var digit = character is >= '0' and <= '9' ? character - '0' :
                        character is >= 'a' and <= 'f' ? character - 'a' + 10 :
                        character is >= 'A' and <= 'F' ? character - 'A' + 10 : -1;
                    if (digit < 0 || digit >= radix) return false;
                    number = number * radix + digit;
                    if (!double.IsFinite(number)) return false;
                }
                return true;
            }
        }
        if (text.Equals("true", StringComparison.OrdinalIgnoreCase)) { number = 1; return true; }
        if (text.Equals("false", StringComparison.OrdinalIgnoreCase)) { number = 0; return true; }
        if (text.EndsWith('n'))
        {
            var digits = text.AsSpan(0, text.Length - 1);
            var unsigned = digits.StartsWith("-", StringComparison.Ordinal) ? digits[1..] : digits;
            if (unsigned.Length is 0 or > 16 || (unsigned.Length > 1 && unsigned[0] == '0') ||
                unsigned.ContainsAnyExceptInRange('0', '9')) return false;
            if (BigInteger.TryParse(digits, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var integer) &&
                integer >= -MaximumSafeInteger && integer <= MaximumSafeInteger)
            {
                number = (double)integer;
                return true;
            }
        }
        return false;
    }

    private static string NumberText(double number)
    {
        if (number == 0) return "0";
        var negative = number < 0;
        var text = Math.Abs(number).ToString("R", CultureInfo.InvariantCulture);
        var exponentIndex = text.IndexOf('E');
        var exponent = exponentIndex < 0 ? 0 : int.Parse(text.AsSpan(exponentIndex + 1), CultureInfo.InvariantCulture);
        var significand = exponentIndex < 0 ? text : text[..exponentIndex];
        var point = significand.IndexOf('.');
        var position = (point < 0 ? significand.Length : point) + exponent;
        var digits = significand.Replace(".", "", StringComparison.Ordinal);
        var scientificExponent = position - 1;
        var formatted = scientificExponent is >= -6 and < 21
            ? position <= 0 ? "0." + new string('0', -position) + digits : position >= digits.Length
                ? digits + new string('0', position - digits.Length) : digits[..position] + "." + digits[position..]
            : digits[0] + (digits.Length > 1 ? "." + digits[1..] : "") + "e" +
              (scientificExponent >= 0 ? "+" : "") + scientificExponent.ToString(CultureInfo.InvariantCulture);
        return negative ? "-" + formatted : formatted;
    }
}
