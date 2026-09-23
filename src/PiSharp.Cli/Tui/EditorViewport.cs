using System.Globalization;
using System.Text;

namespace PiSharp.Cli.Tui;

/// <summary>Pure cell-based layout for the normal-screen terminal editor; scrollback stays above it.</summary>
public static class EditorViewport
{
    public sealed record Frame(IReadOnlyList<string> Rows, int CursorRow, int CursorColumn);

    public static Frame Layout(string text, int cursor, int columns, int height)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(cursor);
        if (cursor > text.Length) throw new ArgumentOutOfRangeException(nameof(cursor));
        var capacity = Math.Max(1, columns - 3);
        var rows = new List<string>();
        var line = new StringBuilder();
        var used = 0;
        var cursorRow = 0;
        var cursorColumn = 3;
        var cursorSeen = false;
        var elements = StringInfo.GetTextElementEnumerator(text);
        while (elements.MoveNext())
        {
            var position = elements.ElementIndex;
            var element = (string)elements.Current;
            if (element == "\n")
            {
                if (position == cursor) { cursorRow = rows.Count; cursorColumn = used + 3; cursorSeen = true; }
                rows.Add(line.ToString());
                line.Clear();
                used = 0;
                continue;
            }
            var sanitized = string.Concat(element.EnumerateRunes().Select(rune => Rune.IsControl(rune) ? " " : rune.ToString()));
            var width = Math.Max(1, TerminalCells.Width(sanitized));
            if (width > capacity) { sanitized = "?"; width = 1; }
            if (used + width > capacity)
            {
                rows.Add(line.ToString());
                line.Clear();
                used = 0;
            }
            if (position == cursor) { cursorRow = rows.Count; cursorColumn = used + 3; cursorSeen = true; }
            line.Append(sanitized);
            used += width;
        }
        if (cursor == text.Length) { cursorRow = rows.Count; cursorColumn = used + 3; cursorSeen = true; }
        if (!cursorSeen) throw new ArgumentException("Cursor must be at a text-element boundary.", nameof(cursor));
        rows.Add(line.ToString());
        var visibleHeight = Math.Max(1, height);
        var first = Math.Clamp(cursorRow - visibleHeight + 1, 0, Math.Max(0, rows.Count - visibleHeight));
        return new Frame(rows.Skip(first).Take(visibleHeight).Select((row, offset) =>
            (first + offset == 0 ? "❯ " : "│ ") + row).ToArray(), cursorRow - first, cursorColumn);
    }
}

/// <summary>Approximate POSIX terminal display width by grapheme cluster, including CJK and emoji joins.</summary>
internal static class TerminalCells
{
    public static int Width(string element)
    {
        var width = 0;
        foreach (var rune in element.EnumerateRunes())
        {
            var value = rune.Value;
            var category = Rune.GetUnicodeCategory(rune);
            if (value is 0x200D or 0xFE0E or 0xFE0F || category is UnicodeCategory.NonSpacingMark or
                UnicodeCategory.EnclosingMark or UnicodeCategory.SpacingCombiningMark or UnicodeCategory.Format)
                continue;
            var wide = value is >= 0x1100 and <= 0x115F or 0x2329 or 0x232A or
                >= 0x2E80 and <= 0xA4CF or >= 0xAC00 and <= 0xD7A3 or
                >= 0xF900 and <= 0xFAFF or >= 0xFE10 and <= 0xFE19 or
                >= 0xFE30 and <= 0xFE6F or >= 0xFF01 and <= 0xFF60 or
                >= 0xFFE0 and <= 0xFFE6 or >= 0x1F000 and <= 0x1FAFF or
                >= 0x20000 and <= 0x3FFFD;
            width = Math.Max(width, wide ? 2 : 1);
        }
        return element.Contains('\uFE0F') ? Math.Max(width, 2) : width;
    }
}
