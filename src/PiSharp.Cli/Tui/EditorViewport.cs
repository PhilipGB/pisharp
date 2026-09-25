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
