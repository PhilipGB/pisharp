using System.Globalization;

namespace PiSharp.Cli.Tui;

/// <summary>Pi-compatible logical-to-visual line mapping used by movement and rendering.</summary>
internal static class EditorVisualLines
{
    internal readonly record struct Chunk(int StartOffset, int EndOffset);

    internal readonly record struct VisualLine(int LogicalLine, int StartOffset, int EndOffset)
    {
        public int Length => EndOffset - StartOffset;
    }

    private readonly record struct Element(int Index, string Text, int Width, bool Whitespace, bool Cjk);

    public static IReadOnlyList<VisualLine> Build(string text, int width)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (width <= 0) width = 1;
        var result = new List<VisualLine>();
        var lineStart = 0;
        var logicalLine = 0;
        while (true)
        {
            var newline = text.IndexOf('\n', lineStart);
            var lineEnd = newline < 0 ? text.Length : newline;
            var line = text[lineStart..lineEnd];
            if (line.Length == 0)
            {
                result.Add(new(logicalLine, lineStart, lineStart));
            }
            else if (VisibleWidth(line) <= width)
            {
                result.Add(new(logicalLine, lineStart, lineEnd));
            }
            else
            {
                foreach (var chunk in WrapLine(line, width))
                    result.Add(new(logicalLine, lineStart + chunk.StartOffset, lineStart + chunk.EndOffset));
            }

            if (newline < 0) break;
            lineStart = newline + 1;
            logicalLine++;
        }
        return result;
    }

    public static IReadOnlyList<Chunk> WrapLine(string line, int maxWidth)
    {
        if (line.Length == 0 || maxWidth <= 0) return [new(0, 0)];
        if (VisibleWidth(line) <= maxWidth) return [new(0, line.Length)];

        var elements = Elements(line);
        var chunks = new List<Chunk>();
        var currentWidth = 0;
        var chunkStart = 0;
        var wrapOpportunity = -1;
        var wrapOpportunityWidth = 0;
        for (var index = 0; index < elements.Count; index++)
        {
            var element = elements[index];
            if (currentWidth + element.Width > maxWidth)
            {
                if (wrapOpportunity >= 0 && currentWidth - wrapOpportunityWidth + element.Width <= maxWidth)
                {
                    chunks.Add(new(chunkStart, wrapOpportunity));
                    chunkStart = wrapOpportunity;
                    currentWidth -= wrapOpportunityWidth;
                }
                else if (chunkStart < element.Index)
                {
                    chunks.Add(new(chunkStart, element.Index));
                    chunkStart = element.Index;
                    currentWidth = 0;
                }
                wrapOpportunity = -1;
            }

            if (element.Width > maxWidth)
            {
                if (Elements(element.Text).Count <= 1)
                {
                    currentWidth += element.Width;
                    continue;
                }
                var subChunks = WrapLine(element.Text, maxWidth);
                for (var sub = 0; sub < subChunks.Count - 1; sub++)
                    chunks.Add(new(element.Index + subChunks[sub].StartOffset, element.Index + subChunks[sub].EndOffset));
                var last = subChunks[^1];
                chunkStart = element.Index + last.StartOffset;
                currentWidth = VisibleWidth(element.Text[last.StartOffset..last.EndOffset]);
                wrapOpportunity = -1;
                continue;
            }

            currentWidth += element.Width;
            if (index + 1 >= elements.Count) continue;
            var next = elements[index + 1];
            if (element.Whitespace && !next.Whitespace)
            {
                wrapOpportunity = next.Index;
                wrapOpportunityWidth = currentWidth;
            }
            else if (!element.Whitespace && !next.Whitespace && (element.Cjk || next.Cjk))
            {
                wrapOpportunity = next.Index;
                wrapOpportunityWidth = currentWidth;
            }
        }

        chunks.Add(new(chunkStart, line.Length));
        return chunks;
    }

    public static int FindVisualLine(IReadOnlyList<VisualLine> lines, int logicalLine, int cursor, int fallback)
    {
        for (var index = 0; index < lines.Count; index++)
        {
            var line = lines[index];
            if (line.LogicalLine != logicalLine) continue;
            var offset = cursor - line.StartOffset;
            var lastOfLine = index == lines.Count - 1 || lines[index + 1].LogicalLine != line.LogicalLine;
            if (offset >= 0 && (offset < line.Length || lastOfLine && offset == line.Length)) return index;
        }
        return fallback;
    }

    private static int VisibleWidth(string text)
    {
        var width = 0;
        var enumerator = StringInfo.GetTextElementEnumerator(text);
        while (enumerator.MoveNext()) width += Math.Max(0, TerminalCells.Width((string)enumerator.Current));
        return width;
    }

    private static List<Element> Elements(string line)
    {
        var result = new List<Element>();
        var enumerator = StringInfo.GetTextElementEnumerator(line);
        while (enumerator.MoveNext())
        {
            var text = (string)enumerator.Current;
            result.Add(new(enumerator.ElementIndex, text, Math.Max(0, TerminalCells.Width(text)),
                text.Any(char.IsWhiteSpace), IsCjk(text)));
        }
        return result;
    }

    private static bool IsCjk(string element) => element.EnumerateRunes().Any(rune => rune.Value is
        >= 0x1100 and <= 0x11FF or >= 0x2E80 and <= 0x2FFF or >= 0x3005 and <= 0x3007 or
        >= 0x3021 and <= 0x3029 or >= 0x3038 and <= 0x303B or >= 0x3040 and <= 0x30FF or
        >= 0x3100 and <= 0x312F or >= 0x3130 and <= 0x318F or >= 0x31A0 and <= 0x31BF or
        >= 0x3400 and <= 0x4DBF or >= 0x4E00 and <= 0x9FFF or >= 0xA960 and <= 0xA97F or
        >= 0xAC00 and <= 0xD7AF or >= 0xD7B0 and <= 0xD7FF or >= 0xF900 and <= 0xFAFF or
        >= 0x20000 and <= 0x3FFFD);
}
