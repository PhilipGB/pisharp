using System.Globalization;
using System.Text;

namespace PiSharp.Cli.Tui;

/// <summary>Pure cell-based layout for the normal-screen terminal editor; scrollback stays above it.</summary>
public static class EditorViewport
{
    public sealed record CellSpan(int StartOffset, int EndOffset, int StartCell, int EndCell);

    public sealed record RowMap(int StartOffset, int EndOffset, IReadOnlyList<CellSpan> Cells)
    {
        public CellSpan? CellAt(int cell)
        {
            foreach (var span in Cells)
                if (cell < span.EndCell) return span;
            return null;
        }

        public int OffsetAt(int cell)
        {
            foreach (var span in Cells)
            {
                if (cell < span.StartCell) return span.StartOffset;
                if (cell < span.EndCell)
                    return cell - span.StartCell < (span.EndCell - span.StartCell + 1) / 2
                        ? span.StartOffset
                        : span.EndOffset;
            }
            return EndOffset;
        }
    }

    public sealed record Frame(IReadOnlyList<string> Rows, int CursorRow, int CursorColumn,
        IReadOnlyList<RowMap> RowMaps, IReadOnlyList<string> ContentRows);

    public static Frame Layout(string text, int cursor, int columns, int height)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(cursor);
        if (cursor > text.Length) throw new ArgumentOutOfRangeException(nameof(cursor));
        var capacity = Math.Max(1, columns - 3);
        var rows = new List<string>();
        var rowMaps = new List<RowMap>();
        var line = new StringBuilder();
        var cells = new List<CellSpan>();
        var used = 0;
        var lineStart = 0;
        var cursorRow = 0;
        var cursorColumn = 3;
        var cursorSeen = false;

        void FinishLine(int endOffset)
        {
            rows.Add(line.ToString());
            rowMaps.Add(new(lineStart, endOffset, cells.ToArray()));
            line.Clear();
            cells.Clear();
            used = 0;
            lineStart = endOffset;
        }

        var elements = StringInfo.GetTextElementEnumerator(text);
        while (elements.MoveNext())
        {
            var position = elements.ElementIndex;
            var element = (string)elements.Current;
            if (element == "\n")
            {
                if (position == cursor) { cursorRow = rows.Count; cursorColumn = used + 3; cursorSeen = true; }
                FinishLine(position);
                lineStart = position + element.Length;
                continue;
            }
            var sanitized = string.Concat(element.EnumerateRunes().Select(rune => Rune.IsControl(rune) ? " " : rune.ToString()));
            var width = Math.Max(1, TerminalCells.Width(sanitized));
            if (width > capacity) { sanitized = "?"; width = 1; }
            if (used + width > capacity)
            {
                FinishLine(position);
            }
            if (position == cursor) { cursorRow = rows.Count; cursorColumn = used + 3; cursorSeen = true; }
            line.Append(sanitized);
            cells.Add(new(position, position + element.Length, used, used + width));
            used += width;
        }
        if (cursor == text.Length) { cursorRow = rows.Count; cursorColumn = used + 3; cursorSeen = true; }
        if (!cursorSeen) throw new ArgumentException("Cursor must be at a text-element boundary.", nameof(cursor));
        FinishLine(text.Length);
        var visibleHeight = Math.Max(1, height);
        var first = Math.Clamp(cursorRow - visibleHeight + 1, 0, Math.Max(0, rows.Count - visibleHeight));
        var visibleRows = rows.Skip(first).Take(visibleHeight).Select((row, offset) =>
            (first + offset == 0 ? "❯ " : "│ ") + row).ToArray();
        return new(visibleRows, cursorRow - first, cursorColumn, rowMaps.Skip(first).Take(visibleHeight).ToArray(),
            rows.Skip(first).Take(visibleHeight).ToArray());
    }
}
