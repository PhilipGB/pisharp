using System.Text;

namespace PiSharp.Cli.Tui;

internal static class TerminalSkillInvocationRenderer
{
    private const string Reset = "\u001b[0m";

    public static string RenderSkill(TerminalSkillInvocation skill, int width, TerminalTheme theme,
        string expandKeyLabel, string markdownCodeBlockIndent, bool expanded)
    {
        ArgumentNullException.ThrowIfNull(skill);
        ArgumentNullException.ThrowIfNull(theme);
        width = Math.Max(1, width);

        var output = new StringBuilder();
        AppendPaddedRow(output, "", width, theme, "customMessageBg");
        output.Append('\n');

        var label = theme.Style("customMessageLabel", "[skill]", bold: true);
        if (!expanded)
        {
            var hint = string.IsNullOrWhiteSpace(expandKeyLabel)
                ? ""
                : theme.Style("dim", $" ({expandKeyLabel.ToLowerInvariant()} to expand)");
            var collapsedLabel = theme.Style("customMessageLabel", "[skill]", bold: true) +
                theme.Style("customMessageLabel", " ");
            AppendPaddedRow(output, " " + collapsedLabel + theme.Style("customMessageText", skill.Name) + hint,
                width, theme, "customMessageBg");
            output.Append('\n');
            AppendPaddedRow(output, "", width, theme, "customMessageBg");
            output.Append('\n');
            return output.ToString();
        }

        AppendPaddedRow(output, " " + label, width, theme, "customMessageBg");
        output.Append('\n');
        var markdown = TerminalMarkdownRenderer.Render($"**{skill.Name}**\n\n{skill.Content}",
            Math.Max(1, width - 2), theme, markdownCodeBlockIndent, "customMessageText");
        foreach (var (line, continuation) in WrapLines(markdown, Math.Max(1, width - 2)))
        {
            AppendPaddedRow(output, " " + line, width, theme, "customMessageBg",
                continuation ? "customMessageText" : null);
            output.Append('\n');
        }
        AppendPaddedRow(output, "", width, theme, "customMessageBg");
        output.Append('\n');
        return output.ToString();
    }

    public static string RenderUserMessage(string text, int width, TerminalTheme theme,
        string markdownCodeBlockIndent)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text);
        ArgumentNullException.ThrowIfNull(theme);
        width = Math.Max(1, width);

        var markdown = TerminalMarkdownRenderer.Render(text, Math.Max(1, width - 2), theme,
            markdownCodeBlockIndent, "userMessageText");
        var output = new StringBuilder();
        output.Append('\n');
        AppendPaddedRow(output, "", width, theme, "userMessageBg");
        output.Append('\n');
        foreach (var (line, continuation) in WrapLines(markdown, Math.Max(1, width - 2)))
        {
            AppendPaddedRow(output, " " + line, width, theme, "userMessageBg",
                continuation ? "userMessageText" : null);
            output.Append('\n');
        }
        AppendPaddedRow(output, "", width, theme, "userMessageBg");
        output.Append('\n');
        return output.ToString();
    }

    private static IEnumerable<(string Text, bool Continuation)> WrapLines(string markdown, int width)
    {
        foreach (var line in markdown.Split('\n'))
        {
            var wrappedLines = TerminalTextLayout.Wrap(line, width);
            for (var index = 0; index < wrappedLines.Count; index++)
                yield return (wrappedLines[index], index < wrappedLines.Count - 1);
        }
    }

    private static void AppendPaddedRow(StringBuilder output, string text, int width, TerminalTheme theme,
        string backgroundToken, string? rightPaddingForegroundToken = null)
    {
        if (TerminalTextLayout.Width(text) > width)
            text = TerminalTranscriptViewport.Clip(text, width);

        var background = theme.Bg(backgroundToken);
        AppendWithBackground(output, text, background);
        var padding = Math.Max(0, width - TerminalTextLayout.Width(text));
        output.Append(background);
        if (rightPaddingForegroundToken is not null) output.Append(theme.Fg(rightPaddingForegroundToken));
        output.Append(' ', padding).Append(Reset);
    }

    private static void AppendWithBackground(StringBuilder output, string text, string background)
    {
        output.Append(background);
        var offset = 0;
        while (offset < text.Length)
        {
            var reset = text.IndexOf(Reset, offset, StringComparison.Ordinal);
            if (reset < 0)
            {
                output.Append(text, offset, text.Length - offset);
                return;
            }

            output.Append(text, offset, reset - offset).Append(Reset).Append(background);
            offset = reset + Reset.Length;
        }
    }
}
