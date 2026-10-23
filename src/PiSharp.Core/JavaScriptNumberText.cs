using System.Globalization;

namespace PiSharp.Core;

/// <summary>Finite JavaScript number text used in pinned tool arguments and notices.</summary>
public static class JavaScriptNumberText
{
    public static string Format(double number)
    {
        if (!double.IsFinite(number)) throw new ArgumentOutOfRangeException(nameof(number), "Number must be finite.");
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
