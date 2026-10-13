using System.Text;

namespace PiSharp.Cli.Tui;

internal static class RadiusLoginShimmer
{
    private static readonly TerminalTheme.Rgb[] s_colors =
        [new(77, 154, 191), new(131, 204, 210), new(241, 190, 87), new(240, 144, 130)];

    public static string Paint(string text, double elapsedMilliseconds, TerminalColorMode mode)
    {
        if (mode == TerminalColorMode.None) return text;
        var output = new StringBuilder();
        var index = 0;
        foreach (var character in text.EnumerateRunes())
        {
            var position = ((index++ - elapsedMilliseconds / 1000 * 10) % 16 + 16) % 16;
            var band = (int)(position / 4);
            var fraction = position / 4 - band;
            var amount = fraction * fraction * (3 - 2 * fraction);
            var from = s_colors[band];
            var to = s_colors[(band + 1) % s_colors.Length];
            double Mix(byte first, byte second) => first + (second - first) * amount;
            var red = Mix(from.R, to.R);
            var green = Mix(from.G, to.G);
            var blue = Mix(from.B, to.B);
            output.Append(mode == TerminalColorMode.TrueColor
                ? $"\u001b[38;2;{Math.Floor(red + .5)};{Math.Floor(green + .5)};{Math.Floor(blue + .5)}m"
                : $"\u001b[38;5;{TerminalTheme.Rgb.ToAnsi256(red, green, blue)}m");
            output.Append(character);
        }
        return output.Append("\u001b[39m").ToString();
    }
}
