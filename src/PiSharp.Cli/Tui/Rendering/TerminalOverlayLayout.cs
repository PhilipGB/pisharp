namespace PiSharp.Cli.Tui;

using PiSharp.Extensions.Abstractions;

/// <summary>Places modal content over the idle or active terminal compositor.</summary>
internal static class TerminalOverlayLayout
{
    public static void ApplyTranscriptSearch(string[] screenRows, IReadOnlyList<string> panel, int columns)
    {
        if (screenRows.Length == 0 || panel.Count == 0 || columns < 3) return;
        var panelWidth = Math.Min(columns - 2, Math.Max(32, (int)(columns * 0.4)));
        var left = Math.Max(0, columns - panelWidth - 1);
        var top = Math.Min(1, Math.Max(0, screenRows.Length - panel.Count));
        for (var index = 0; index < panel.Count && top + index < screenRows.Length; index++)
        {
            var row = top + index;
            var styledScrollGlyph = TerminalTextLayout.SliceStyledCells(screenRows[row], columns - 1, columns);
            var beforePanel = TerminalTranscriptViewport.Clip(screenRows[row], left);
            beforePanel += new string(' ', Math.Max(0, left - TerminalTextLayout.Width(beforePanel)));
            screenRows[row] = beforePanel + "\u001b[0m" + TerminalTranscriptViewport.Clip(panel[index], panelWidth) +
                "\u001b[0m" + styledScrollGlyph;
        }
    }

    public static void Apply(string[] screenRows, IReadOnlyList<string> content, int columns, TerminalTheme? theme = null)
    {
        if (screenRows.Length == 0 || content.Count == 0) return;
        theme ??= TerminalTheme.Default;
        var (innerWidth, horizontalOffset, verticalOffset, skip, visibleFrameCount) =
            Geometry(content, columns, screenRows.Length);
        var cardWidth = innerWidth + 4;
        var frame = new List<string>(content.Count + 2) { "+" + new string('-', innerWidth + 2) + "+" };
        foreach (var line in content)
        {
            var clipped = TerminalTranscriptViewport.Clip(line, innerWidth);
            var padding = Math.Max(0, innerWidth - TerminalTextLayout.Width(clipped));
            frame.Add("| " + clipped + new string(' ', padding) + " |");
        }
        frame.Add("+" + new string('-', innerWidth + 2) + "+");

        var firstFrameLine = skip;
        if (skip > 0 || frame.Count > visibleFrameCount)
            frame = frame.Skip(skip).Take(visibleFrameCount).ToList();
        for (var index = 0; index < frame.Count; index++)
        {
            var line = frame[index];
            var absoluteFrameLine = firstFrameLine + index;
            var isOuterBorder = absoluteFrameLine == 0 || absoluteFrameLine == content.Count + 1;
            var inner = isOuterBorder ? line : line[2..^2];
            if (absoluteFrameLine == 1) inner = theme.Style("accent", inner, bold: true);
            else if (absoluteFrameLine > 0 && absoluteFrameLine <= content.Count &&
                content[absoluteFrameLine - 1].StartsWith("> ", StringComparison.Ordinal))
                inner = theme.Style("selectedBg", inner, background: true);
            var rendered = isOuterBorder
                ? theme.Style("borderAccent", inner)
                : theme.Fg("border") + "| " + "\u001b[0m" + inner +
                  "\u001b[0m" + theme.Fg("border") + " |\u001b[0m";
            screenRows[verticalOffset + index] = new string(' ', horizontalOffset) + rendered;
        }
    }

    public static void ApplyExtension(string[] screenRows, IReadOnlyList<string> content,
        int columns, int rows, TerminalTheme theme, PiSharpExtensionUiOverlayOptions? options)
    {
        if (screenRows.Length == 0 || content.Count == 0) return;
        if (columns < 5 || rows < 3)
        {
            var count = Math.Min(content.Count, screenRows.Length);
            var start = Math.Max(0, (screenRows.Length - count) / 2);
            for (var index = 0; index < count; index++)
                screenRows[start + index] = TerminalTranscriptViewport.Clip(content[index], columns);
            return;
        }

        var maximumCardWidth = columns;
        var naturalWidth = Math.Max(1, content.Max(line => TerminalTextLayout.Width(line)) + 4);
        var cardWidth = Math.Clamp(options?.Width ?? naturalWidth, 5, maximumCardWidth);
        var innerWidth = Math.Max(1, cardWidth - 4);
        var maxFrameHeight = Math.Clamp(options?.Height ?? rows, 1, rows);
        var maxContentLines = Math.Max(0, maxFrameHeight - 2);
        var visibleContent = content.Count <= maxContentLines
            ? content
            : content.Skip(Math.Max(0, (content.Count - maxContentLines) / 2)).Take(maxContentLines).ToArray();
        var frame = new List<string>(visibleContent.Count + 2)
        {
            theme.Style("borderAccent", "+" + new string('-', innerWidth + 2) + "+")
        };
        foreach (var line in visibleContent)
        {
            var clipped = TerminalTranscriptViewport.Clip(line, innerWidth);
            var padding = Math.Max(0, innerWidth - TerminalTextLayout.Width(clipped));
            frame.Add(theme.Fg("border") + "| " + "\u001b[0m" + clipped +
                new string(' ', padding) + theme.Fg("border") + " |\u001b[0m");
        }
        if (maxFrameHeight >= 2)
            frame.Add(theme.Style("borderAccent", "+" + new string('-', innerWidth + 2) + "+"));

        var left = Math.Clamp(options?.Left ?? (columns - (innerWidth + 4)) / 2,
            0, Math.Max(0, columns - (innerWidth + 4)));
        var top = Math.Clamp(options?.Top ?? (rows - frame.Count) / 2,
            0, Math.Max(0, rows - Math.Min(rows, frame.Count)));
        for (var index = 0; index < frame.Count && top + index < screenRows.Length; index++)
            screenRows[top + index] = TerminalTranscriptViewport.Clip(new string(' ', left) + frame[index], columns);
    }

    public static int? ContentLineAt(IReadOnlyList<string> content, int columns, int rows, int column, int row)
    {
        if (content.Count == 0) return null;
        var (innerWidth, horizontalOffset, verticalOffset, skip, visibleFrameCount) = Geometry(content, columns, rows);
        var screenColumn = column - 1;
        var screenRow = row - 1;
        if (screenColumn < horizontalOffset || screenColumn >= horizontalOffset + innerWidth + 4 ||
            screenRow < verticalOffset || screenRow >= verticalOffset + visibleFrameCount)
            return null;
        var frameLine = screenRow - verticalOffset + skip;
        var contentLine = frameLine - 1;
        return contentLine >= 0 && contentLine < content.Count ? contentLine : null;
    }

    private static (int InnerWidth, int HorizontalOffset, int VerticalOffset, int Skip, int VisibleFrameCount)
        Geometry(IReadOnlyList<string> content, int columns, int rows)
    {
        var availableInnerWidth = Math.Max(1, columns - 4);
        var innerWidth = Math.Min(availableInnerWidth,
            Math.Max(1, content.Max(line => TerminalTextLayout.Width(line))));
        var horizontalOffset = Math.Max(0, (columns - (innerWidth + 4)) / 2);
        var frameCount = content.Count + 2;
        var visibleFrameCount = Math.Min(frameCount, Math.Max(1, rows));
        var skip = frameCount > rows ? (frameCount - rows) / 2 : 0;
        var verticalOffset = Math.Max(0, (rows - visibleFrameCount) / 2);
        return (innerWidth, horizontalOffset, verticalOffset, skip, visibleFrameCount);
    }
}
