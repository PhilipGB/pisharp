namespace PiSharp.Cli.Tui;

/// <summary>Places modal content over the idle or active terminal compositor.</summary>
internal static class TerminalOverlayLayout
{
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
