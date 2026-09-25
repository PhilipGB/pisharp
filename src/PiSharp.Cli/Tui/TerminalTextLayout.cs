using System.Globalization;
using System.Text;

namespace PiSharp.Cli.Tui;

/// <summary>Measures and wraps terminal text by display cells while preserving supported styling.</summary>
internal static class TerminalTextLayout
{
    private const string SgrReset = "\u001b[0m";

    public static int Width(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var maximum = 0;
        var current = 0;
        for (var offset = 0; offset < text.Length;)
        {
            if (TryReadEscape(text, offset, out var length, out _, out _))
            {
                offset += length;
                continue;
            }

            if (text[offset] is '\r' or '\n')
            {
                maximum = Math.Max(maximum, current);
                current = 0;
                offset += text[offset] == '\r' && offset + 1 < text.Length && text[offset + 1] == '\n' ? 2 : 1;
                continue;
            }

            if (text[offset] == '\t')
            {
                current += 4 - current % 4;
                offset++;
                continue;
            }

            var element = StringInfo.GetNextTextElement(text, offset);
            var safe = Sanitize(element);
            current += TerminalCells.Width(safe);
            offset += element.Length;
        }
        return Math.Max(maximum, current);
    }

    public static IReadOnlyList<string> Wrap(string text, int width)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentOutOfRangeException.ThrowIfLessThan(width, 1);
        if (text.Length == 0) return [""];

        var lines = new List<string>();
        var line = new StringBuilder();
        var activeSgr = new List<string>();
        string? activeHyperlinkOpen = null;
        string? activeHyperlinkClose = null;
        var used = 0;

        void StartLine()
        {
            foreach (var sequence in activeSgr) line.Append(sequence);
            if (activeHyperlinkOpen is not null) line.Append(activeHyperlinkOpen);
        }

        void FinishLine()
        {
            if (activeHyperlinkClose is not null) line.Append(activeHyperlinkClose);
            line.Append(SgrReset);
            lines.Add(line.ToString());
            line.Clear();
            used = 0;
            StartLine();
        }

        void AppendElement(string element)
        {
            var safe = Sanitize(element);
            var cells = TerminalCells.Width(safe);
            if (cells > width)
            {
                safe = "?";
                cells = 1;
            }
            if (used > 0 && used + cells > width) FinishLine();
            line.Append(safe);
            used += cells;
        }

        for (var offset = 0; offset < text.Length;)
        {
            if (TryReadEscape(text, offset, out var length, out var kind, out var payload))
            {
                var sequence = text.Substring(offset, length);
                if (kind == EscapeKind.Sgr)
                {
                    line.Append(sequence);
                    if (ResetsSgr(payload)) activeSgr.Clear();
                    if (payload.Length > 0 && payload != "0") activeSgr.Add(sequence);
                }
                else if (kind == EscapeKind.Hyperlink)
                {
                    line.Append(sequence);
                    if (HyperlinkTarget(payload).Length == 0)
                    {
                        activeHyperlinkOpen = null;
                        activeHyperlinkClose = null;
                    }
                    else
                    {
                        activeHyperlinkOpen = sequence;
                        var terminator = sequence.EndsWith('\a') ? "\a" : "\u001b\\";
                        activeHyperlinkClose = "\u001b]8;;" + terminator;
                    }
                }
                offset += length;
                continue;
            }

            if (text[offset] is '\r' or '\n')
            {
                FinishLine();
                offset += text[offset] == '\r' && offset + 1 < text.Length && text[offset + 1] == '\n' ? 2 : 1;
                continue;
            }

            if (text[offset] == '\t')
            {
                var spaces = 4 - used % 4;
                for (var index = 0; index < spaces; index++) AppendElement(" ");
                offset++;
                continue;
            }

            var element = StringInfo.GetNextTextElement(text, offset);
            AppendElement(element);
            offset += element.Length;
        }

        if (line.Length > 0 || lines.Count == 0 || text[^1] is '\r' or '\n')
        {
            if (activeHyperlinkClose is not null) line.Append(activeHyperlinkClose);
            line.Append(SgrReset);
            lines.Add(line.ToString());
        }
        return lines;
    }

    private static string Sanitize(string element)
    {
        var output = new StringBuilder(element.Length);
        foreach (var rune in element.EnumerateRunes())
            output.Append(Rune.IsControl(rune) ? " " : rune.ToString());
        return output.ToString();
    }

    private static bool TryReadEscape(string text, int offset, out int length, out EscapeKind kind, out string payload)
    {
        length = 0;
        kind = EscapeKind.Other;
        payload = "";
        if (offset + 1 >= text.Length || text[offset] != '\u001b') return false;

        if (text[offset + 1] == '[')
        {
            var end = offset + 2;
            while (end < text.Length && text[end] is >= '\u0020' and <= '\u003f') end++;
            if (end >= text.Length || text[end] is < '\u0040' or > '\u007e') return false;
            length = end - offset + 1;
            payload = text[(offset + 2)..end];
            if (text[end] == 'm' && payload.All(character => char.IsAsciiDigit(character) || character is ';' or ':'))
                kind = EscapeKind.Sgr;
            return true;
        }

        if (text[offset + 1] == ']')
        {
            var end = offset + 2;
            while (end < text.Length && text[end] != '\a' &&
                !(text[end] == '\u001b' && end + 1 < text.Length && text[end + 1] == '\\'))
                end++;
            if (end >= text.Length) return false;
            var terminatorLength = text[end] == '\a' ? 1 : 2;
            length = end - offset + terminatorLength;
            payload = text[(offset + 2)..end];
            if (payload.StartsWith("8;", StringComparison.Ordinal)) kind = EscapeKind.Hyperlink;
            return true;
        }

        if (text[offset + 1] is >= '\u0040' and <= '\u005f')
        {
            length = 2;
            return true;
        }
        return false;
    }

    private static bool ResetsSgr(string payload)
    {
        foreach (var parameter in payload.Split(';'))
            if (parameter.Length == 0 || parameter == "0") return true;
        return false;
    }

    private static string HyperlinkTarget(string payload)
    {
        var parameterSeparator = payload.IndexOf(';');
        if (parameterSeparator < 0) return "";
        var targetSeparator = payload.IndexOf(';', parameterSeparator + 1);
        return targetSeparator < 0 ? "" : payload[(targetSeparator + 1)..];
    }

    private enum EscapeKind { Other, Sgr, Hyperlink }
}
