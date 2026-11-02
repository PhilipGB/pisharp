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
        IReadOnlyList<RowMap> RowMaps, IReadOnlyList<string> ContentRows, int MouseCellOffset = 3,
        int ScrollOffset = 0);

    public static Frame Layout(string text, int cursor, int columns, int height, bool showPrompt = true,
        int? scrollOffset = null)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentOutOfRangeException.ThrowIfNegative(cursor);
        if (cursor > text.Length) throw new ArgumentOutOfRangeException(nameof(cursor));
        var cursorOffset = showPrompt ? 3 : 0;
        var capacity = Math.Max(1, columns - (showPrompt ? 3 : 1));
        var visualLines = EditorVisualLines.Build(text, capacity);
        var rows = new List<string>();
        var rowMaps = new List<RowMap>();
        var cursorRow = 0;
        var cursorColumn = cursorOffset;
        var cursorSeen = false;

        for (var visualIndex = 0; visualIndex < visualLines.Count; visualIndex++)
        {
            var visual = visualLines[visualIndex];
            var line = new StringBuilder();
            var cells = new List<CellSpan>();
            var used = 0;
            if (cursor == visual.StartOffset)
            {
                cursorRow = rows.Count;
                cursorColumn = used + cursorOffset;
                cursorSeen = true;
            }

            var enumerator = StringInfo.GetTextElementEnumerator(text[visual.StartOffset..visual.EndOffset]);
            while (enumerator.MoveNext())
            {
                var position = visual.StartOffset + enumerator.ElementIndex;
                var element = (string)enumerator.Current;
                var sanitized = Sanitize(element);
                var width = Math.Max(1, TerminalCells.Width(sanitized));
                if (width > capacity)
                {
                    sanitized = "?";
                    width = 1;
                }
                if (!cursorSeen && position == cursor)
                {
                    cursorRow = rows.Count;
                    cursorColumn = used + cursorOffset;
                    cursorSeen = true;
                }
                line.Append(sanitized);
                cells.Add(new(position, position + element.Length, used, used + width));
                used += width;
            }

            var lastOfLogicalLine = visualIndex == visualLines.Count - 1 ||
                visualLines[visualIndex + 1].LogicalLine != visual.LogicalLine;
            if (!cursorSeen && cursor == visual.EndOffset && lastOfLogicalLine)
            {
                cursorRow = rows.Count;
                cursorColumn = used + cursorOffset;
                cursorSeen = true;
            }
            rows.Add(line.ToString());
            rowMaps.Add(new(visual.StartOffset, visual.EndOffset, cells));
        }

        if (!cursorSeen) throw new ArgumentException("Cursor must be at a text-element boundary.", nameof(cursor));
        var visibleHeight = Math.Max(1, height);
        var first = Math.Clamp(scrollOffset ?? 0, 0, Math.Max(0, rows.Count - visibleHeight));
        if (cursorRow < first) first = cursorRow;
        else if (cursorRow >= first + visibleHeight) first = cursorRow - visibleHeight + 1;
        first = Math.Clamp(first, 0, Math.Max(0, rows.Count - visibleHeight));
        var visibleRows = rows.Skip(first).Take(visibleHeight).Select((row, offset) => showPrompt
            ? (first + offset == 0 ? "❯ " : "│ ") + row
            : row).ToArray();
        return new(visibleRows, cursorRow - first, cursorColumn, rowMaps.Skip(first).Take(visibleHeight).ToArray(),
            rows.Skip(first).Take(visibleHeight).ToArray(), showPrompt ? 3 : 1, first);
    }

    private static string Sanitize(string element) => string.Concat(element.EnumerateRunes().Select(rune =>
        rune.Value == '\t' ? "   " : Rune.IsControl(rune) ? " " : rune.ToString()));
}
