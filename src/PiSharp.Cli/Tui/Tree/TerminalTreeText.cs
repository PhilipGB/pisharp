using System.Globalization;
using System.Text;

namespace PiSharp.Cli.Tui;

/// <summary>Keeps session-controlled tree text from emitting terminal control sequences.</summary>
internal static class TerminalTreeText
{
    /// <summary>Normalizes untrusted text to one rendered line.</summary>
    public static string Line(string value) =>
        TerminalSafeText.Normalize(value).Replace('\n', ' ').Replace('\t', ' ').Trim();

    /// <summary>Replaces control characters with spaces without changing the character count.</summary>
    public static string Display(string value)
    {
        var builder = new StringBuilder(value.Length);
        foreach (var rune in value.EnumerateRunes())
            builder.Append(rune.Value is '\n' or '\t' || Rune.IsControl(rune) ? " " : rune.ToString());
        return builder.ToString();
    }
}
