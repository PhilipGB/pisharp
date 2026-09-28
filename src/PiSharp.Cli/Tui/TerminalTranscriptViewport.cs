using System.Globalization;
using System.Text;

namespace PiSharp.Cli.Tui;

/// <summary>Maps transcript text into terminal-cell rows and viewport windows.</summary>
internal static class TerminalTranscriptViewport
{
    private const int MaximumScrollOffset = 60_000;

    public static List<string> WrapWindow(string text, int width, int maxRows, int scrollOffset,
        out int actualScrollOffset, out int firstVisualRow)
    {
        return WrapWindow(TerminalTextLayout.Create(text, width), maxRows, scrollOffset,
            out actualScrollOffset, out firstVisualRow);
    }

    public static List<string> WrapWindow(TerminalTextLayout.LayoutResult layout, int maxRows, int scrollOffset,
        out int actualScrollOffset, out int firstVisualRow)
    {
        ArgumentNullException.ThrowIfNull(layout);
        var totalRows = layout.RowCount;
        actualScrollOffset = Math.Min(Math.Clamp(scrollOffset, 0, MaximumScrollOffset), Math.Max(0, totalRows - maxRows));
        var end = totalRows - actualScrollOffset;
        var start = Math.Max(0, end - maxRows);
        firstVisualRow = start;
        return layout.Rows.Skip(start).Take(Math.Max(0, end - start)).ToList();
    }

    public static int CountVisualRows(string text, int width) => TerminalTextLayout.Create(text, width).RowCount;

    public static int VisualRowAt(string text, int index, int width) =>
        TerminalTextLayout.Create(text, width).VisualRowAt(index);

    public static int ScrollOffsetToShow(int totalRows, int selectedRow, int viewportHeight)
    {
        var startRow = Math.Clamp(selectedRow - viewportHeight / 2, 0, Math.Max(0, totalRows - viewportHeight));
        return Math.Clamp(totalRows - Math.Min(totalRows, startRow + viewportHeight), 0, MaximumScrollOffset);
    }

    public static string Clip(string value, int width)
    {
        var result = new StringBuilder();
        var used = 0;
        var elements = StringInfo.GetTextElementEnumerator(value);
        while (elements.MoveNext())
        {
            var element = (string)elements.Current;
            var cells = Math.Max(0, TerminalCells.Width(element));
            if (used + cells > width) break;
            result.Append(element);
            used += cells;
        }
        return result.ToString();
    }
}
