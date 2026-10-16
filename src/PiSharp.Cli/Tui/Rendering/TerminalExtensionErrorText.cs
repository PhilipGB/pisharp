namespace PiSharp.Cli.Tui;

internal static class TerminalExtensionErrorText
{
    internal static string Message(string path, string error) => $"Extension \"{path}\" error: {error}";

    internal static string Render(string message, int columns, TerminalTheme theme)
    {
        var padding = Math.Min(1, Math.Max(0, (columns - 1) / 2));
        var contentWidth = Math.Max(1, columns - 2 * padding);
        var text = TerminalSafeText.Normalize(message.Replace("\t", "   ", StringComparison.Ordinal));
        var styled = theme.Style("error", text);
        // Pi's foreground reset leaves a trailing YAML context newline as a visible blank row.
        var lines = TerminalTextLayout.Create(styled[..^4] + "\u001b[39m", contentWidth).Rows;
        var margin = new string(' ', padding);
        return string.Join('\n', lines.Select(line =>
        {
            if (line.EndsWith("\u001b[0m", StringComparison.Ordinal)) line = line[..^4];
            var paddingWidth = Math.Max(0, columns - padding - TerminalTextLayout.Width(line));
            var paddingText = new string(' ', paddingWidth);
            var styledPadding = line.Contains("\u001b[39m", StringComparison.Ordinal)
                ? "\u001b[39m" + paddingText : theme.Style("error", paddingText);
            return margin + line + styledPadding;
        })) + "\n";
    }
}
