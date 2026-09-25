using System.Globalization;

namespace PiSharp.Cli.Tui;

/// <summary>Validated reply to an OSC 10 foreground or OSC 11 background query.</summary>
internal readonly record struct TerminalColorResponse(int Slot, TerminalTheme.Rgb Color)
{
    public static bool TryParse(string payload, out TerminalColorResponse response)
    {
        response = default;
        var separator = payload.IndexOf(';');
        if (separator <= 0 || !int.TryParse(payload.AsSpan(0, separator), NumberStyles.None,
                CultureInfo.InvariantCulture, out var slot) || slot is not (10 or 11))
            return false;

        var value = payload[(separator + 1)..].Trim();
        if (value.StartsWith('#'))
        {
            var hex = value[1..];
            if (hex.Length == 6 && hex.All(Uri.IsHexDigit))
            {
                response = new(slot, new(byte.Parse(hex[..2], NumberStyles.HexNumber, CultureInfo.InvariantCulture),
                    byte.Parse(hex[2..4], NumberStyles.HexNumber, CultureInfo.InvariantCulture),
                    byte.Parse(hex[4..6], NumberStyles.HexNumber, CultureInfo.InvariantCulture)));
                return true;
            }
            if (hex.Length == 12 && hex.All(Uri.IsHexDigit))
            {
                response = new(slot, new(ParseChannel(hex[..4]), ParseChannel(hex[4..8]), ParseChannel(hex[8..12])));
                return true;
            }
            return false;
        }

        if (value.StartsWith("rgb:", StringComparison.OrdinalIgnoreCase)) value = value[4..];
        var channels = value.Split('/');
        if (channels.Length != 3 || channels.Any(channel => channel.Length is < 1 or > 4 || !channel.All(Uri.IsHexDigit)))
            return false;
        response = new(slot, new(ParseChannel(channels[0]), ParseChannel(channels[1]), ParseChannel(channels[2])));
        return true;
    }

    private static byte ParseChannel(string value)
    {
        var maximum = (1 << (value.Length * 4)) - 1;
        var parsed = int.Parse(value, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        return (byte)Math.Round(parsed * 255d / maximum);
    }
}
