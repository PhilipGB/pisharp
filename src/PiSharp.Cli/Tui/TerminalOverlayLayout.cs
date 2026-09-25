namespace PiSharp.Cli.Tui;

/// <summary>Places modal content over the idle or active terminal compositor.</summary>
internal static class TerminalOverlayLayout
{
    public static void Apply(string[] screenRows, IReadOnlyList<string> content, int columns)
    {
        if (screenRows.Length == 0 || content.Count == 0) return;
        var availableInnerWidth = Math.Max(1, columns - 4);
        var innerWidth = Math.Min(availableInnerWidth,
            Math.Max(1, content.Max(line => TerminalTextLayout.Width(line))));
        var cardWidth = innerWidth + 4;
        var horizontalOffset = Math.Max(0, (columns - cardWidth) / 2);
        var frame = new List<string>(content.Count + 2)
        {
            "+" + new string('-', innerWidth + 2) + "+"
        };
        foreach (var line in content)
        {
            var clipped = TerminalTranscriptViewport.Clip(line, innerWidth);
            var padding = Math.Max(0, innerWidth - TerminalTextLayout.Width(clipped));
            frame.Add("| " + clipped + new string(' ', padding) + " |");
        }
        frame.Add("+" + new string('-', innerWidth + 2) + "+");

        if (frame.Count > screenRows.Length)
        {
            var skip = (frame.Count - screenRows.Length) / 2;
            frame = frame.Skip(skip).Take(screenRows.Length).ToList();
        }
        var verticalOffset = Math.Max(0, (screenRows.Length - frame.Count) / 2);
        for (var index = 0; index < frame.Count; index++)
            screenRows[verticalOffset + index] = new string(' ', horizontalOffset) + "\u001b[7m" + frame[index] + "\u001b[0m";
    }
}
