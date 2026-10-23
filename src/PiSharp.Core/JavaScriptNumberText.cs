using System.Globalization;
using System.Numerics;

namespace PiSharp.Core;

/// <summary>Finite JavaScript number text used in pinned tool arguments and notices.</summary>
public static class JavaScriptNumberText
{
    public static string Fixed(double number, int digits)
    {
        if (!double.IsFinite(number)) throw new ArgumentOutOfRangeException(nameof(number), "Number must be finite.");
        ArgumentOutOfRangeException.ThrowIfNegative(digits);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(digits, 100);
        if (Math.Abs(number) >= 1e21) return Format(number);
        var bits = BitConverter.DoubleToUInt64Bits(Math.Abs(number));
        var exponent = (int)((bits >> 52) & 0x7ff);
        var mantissa = bits & 0x000f_ffff_ffff_ffff;
        if (exponent != 0) mantissa |= 1UL << 52;
        var power = (exponent == 0 ? 1 - 1023 : exponent - 1023) - 52;
        var scaled = (BigInteger)mantissa * BigInteger.Pow(10, digits);
        if (power >= 0) scaled <<= power;
        else
        {
            // Round the original binary value, preserving JS's ties and values just below a tie.
            var denominator = BigInteger.One << -power;
            scaled = BigInteger.DivRem(scaled, denominator, out var remainder);
            if (remainder * 2 >= denominator) scaled++;
        }
        var text = scaled.ToString(CultureInfo.InvariantCulture).PadLeft(digits + 1, '0');
        if (digits > 0) text = text[..^digits] + "." + text[^digits..];
        return number < 0 ? "-" + text : text;
    }

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
