using System.Globalization;
using System.Numerics;

namespace PiSharp.Cli.Tui;

internal static class ToolDurationText
{
    internal static string Format(double milliseconds)
    {
        var seconds = milliseconds / 1000;
        if (seconds < 60) return FixedOneDecimal(seconds) + "s";
        var totalSeconds = Math.Floor(seconds);
        var minutes = Math.Floor(totalSeconds / 60);
        var remainder = totalSeconds % 60;
        return minutes < 60
            ? FormattableString.Invariant($"{minutes}m {remainder}s")
            : FormattableString.Invariant($"{Math.Floor(minutes / 60)}h {minutes % 60}m {remainder}s");
    }

    private static string FixedOneDecimal(double value)
    {
        // JavaScript toFixed rounds the represented binary value; scaling a double first changes ties.
        var negative = value < 0;
        var bits = BitConverter.DoubleToUInt64Bits(Math.Abs(value));
        var exponent = (int)((bits >> 52) & 0x7ff);
        var significand = new BigInteger(bits & 0x000fffffffffffffUL);
        if (exponent != 0) significand += BigInteger.One << 52;
        var shift = (exponent == 0 ? -1022 : exponent - 1023) - 52;
        var numerator = significand * 10;
        var denominator = BigInteger.One;
        if (shift >= 0) numerator <<= shift;
        else denominator <<= -shift;
        var tenths = BigInteger.DivRem(numerator, denominator, out var remainder);
        if (remainder * 2 >= denominator) tenths++;
        return (negative ? "-" : "") + (tenths / 10).ToString(CultureInfo.InvariantCulture) + "." +
            (tenths % 10).ToString(CultureInfo.InvariantCulture);
    }
}
