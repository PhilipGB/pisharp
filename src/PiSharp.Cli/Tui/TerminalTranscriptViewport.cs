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
        var capacity = Math.Max(1, maxRows + Math.Clamp(scrollOffset, 0, MaximumScrollOffset));
        var rows = new Queue<string>(capacity);
        var totalRows = 0;
        void Add(string value)
        {
            if (rows.Count == capacity) rows.Dequeue();
            rows.Enqueue(value);
            totalRows++;
        }

        foreach (var line in TerminalTextLayout.Wrap(text, width)) Add(line);
        actualScrollOffset = Math.Min(scrollOffset, Math.Max(0, totalRows - maxRows));
        var end = rows.Count - actualScrollOffset;
        var start = Math.Max(0, end - maxRows);
        firstVisualRow = totalRows - rows.Count + start;
        return rows.Skip(start).Take(end - start).ToList();
    }

    public static int CountVisualRows(string text, int width)
    {
        var rows = 1;
        var used = 0;
        for (var offset = 0; offset < text.Length;)
        {
            if (text[offset] == '\n')
            {
                rows++;
                used = 0;
                offset++;
                continue;
            }
            var element = StringInfo.GetNextTextElement(text, offset);
            var cells = Math.Max(0, TerminalCells.Width(element));
            if (used + cells > width && used > 0)
            {
                rows++;
                used = 0;
            }
            used += cells;
            offset += element.Length;
        }
        return rows;
    }

    public static int VisualRowAt(string text, int index, int width)
    {
        var row = 0;
        var used = 0;
        for (var offset = 0; offset < index;)
        {
            if (text[offset] == '\n')
            {
                row++;
                used = 0;
                offset++;
                continue;
            }
            var element = StringInfo.GetNextTextElement(text, offset);
            if (offset + element.Length > index) return row;
            var cells = Math.Max(0, TerminalCells.Width(element));
            if (used + cells > width && used > 0)
            {
                row++;
                used = 0;
            }
            used += cells;
            offset += element.Length;
        }
        return row;
    }

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
