using System.Globalization;

namespace PiSharp.Cli.Tui;

/// <summary>Validated reply to an OSC 10, OSC 11, or OSC 4 terminal color query.</summary>
internal readonly record struct TerminalColorResponse(int Slot, TerminalTheme.Rgb? Color, int? PaletteIndex = null)
{
    public static bool TryParse(string payload, out TerminalColorResponse response)
    {
        response = default;
        var separator = payload.IndexOf(';');
        if (separator <= 0 || !int.TryParse(payload.AsSpan(0, separator), NumberStyles.None,
                CultureInfo.InvariantCulture, out var slot) || slot is not (4 or 10 or 11))
            return false;

        int? paletteIndex = null;
        var valueStart = separator + 1;
        if (slot == 4)
        {
            var indexSeparator = payload.IndexOf(';', valueStart);
            if (indexSeparator <= valueStart || !int.TryParse(payload.AsSpan(valueStart, indexSeparator - valueStart),
                    NumberStyles.None, CultureInfo.InvariantCulture, out var index) || index is < 0 or > 15)
                return false;
            paletteIndex = index;
            valueStart = indexSeparator + 1;
        }

        var value = payload[valueStart..].Trim();
        if (value.StartsWith('#'))
        {
            var hex = value[1..];
            if (hex.Length == 6 && hex.All(Uri.IsHexDigit))
            {
                response = new(slot, new TerminalTheme.Rgb(byte.Parse(hex[..2], NumberStyles.HexNumber, CultureInfo.InvariantCulture),
                    byte.Parse(hex[2..4], NumberStyles.HexNumber, CultureInfo.InvariantCulture),
                    byte.Parse(hex[4..6], NumberStyles.HexNumber, CultureInfo.InvariantCulture)), paletteIndex);
                return true;
            }
            if (hex.Length == 12 && hex.All(Uri.IsHexDigit))
            {
                response = new(slot, new TerminalTheme.Rgb(ParseChannel(hex[..4]), ParseChannel(hex[4..8]), ParseChannel(hex[8..12])), paletteIndex);
                return true;
            }
            return false;
        }

        if (value.StartsWith("rgb:", StringComparison.OrdinalIgnoreCase) ||
            value.StartsWith("rgba:", StringComparison.OrdinalIgnoreCase))
            value = value[(value.IndexOf(':') + 1)..];
        var channels = value.Split('/');
        if (channels.Length != 3 || channels.Any(channel => channel.Length is < 1 or > 4 || !channel.All(Uri.IsHexDigit)))
            return false;
        response = new(slot, new TerminalTheme.Rgb(ParseChannel(channels[0]), ParseChannel(channels[1]), ParseChannel(channels[2])), paletteIndex);
        return true;
    }

    private static byte ParseChannel(string value)
    {
        var maximum = (1 << (value.Length * 4)) - 1;
        var parsed = int.Parse(value, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        return (byte)Math.Round(parsed * 255d / maximum);
    }
}
