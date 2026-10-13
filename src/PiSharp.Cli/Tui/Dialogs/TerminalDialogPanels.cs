namespace PiSharp.Cli.Tui;

internal static class TerminalDialogPanels
{
    public static IReadOnlyList<string> RenderInlinePanel<T>(TerminalTheme theme, int width, string title,
        IReadOnlyList<TerminalSelectionOption<T>> options, int selectedIndex, IReadOnlyList<string>? header,
        string footer, int optionIndent = 0, int bottomSpacerLines = 0, bool showCurrentMarker = false,
        string headerStyle = "dim")
    {
        var lines = new List<string> { PanelBorder(theme, width) };
        var titleRows = TerminalSafeText.Normalize(title).Split('\n');
        for (var index = 0; index < titleRows.Length; index++)
        {
            var row = " " + theme.Style("accent", titleRows[index], bold: true);
            if (titleRows.Length > 1 && index < titleRows.Length - 1) row += theme.Fg("accent");
            lines.Add(PadPanelLine(row, width) +
                (titleRows.Length > 1 && index < titleRows.Length - 1 ? "\u001b[39m" : ""));
        }
        if (header is not null)
        {
            foreach (var row in header)
                lines.Add(row.Length == 0 ? "" : PadPanelLine(" " + theme.Style(headerStyle, TerminalSafeText.Normalize(row)), width));
        }
        if (options.Count == 0)
            lines.Add(PadPanelLine(" " + theme.Style("warning", "No router models are available"), width));
        else
        {
            var labelWidth = Math.Clamp(Math.Max(34, options.Max(option => TerminalTextLayout.Width(option.Label))), 34, 54);
            foreach (var (option, index) in options.Select((option, index) => (option, index)))
            {
                var label = TerminalSafeText.Normalize(option.Label);
                var marker = index == selectedIndex ? "→ " : "  ";
                var selected = index == selectedIndex;
                var current = showCurrentMarker
                    ? option.IsCurrent ? theme.Style("accent", "✓ ") : "  "
                    : "";
                var description = string.IsNullOrWhiteSpace(option.Description)
                    ? "" : TerminalSafeText.Normalize(option.Description);
                var primary = new string(' ', Math.Max(0, optionIndent)) + marker + current + label;
                if (showCurrentMarker)
                {
                    var styledMarker = selected ? theme.Style("accent", marker) : marker;
                    var styledLabel = theme.Style(selected ? "accent" : "text", label);
                    var styledPrimary = new string(' ', Math.Max(0, optionIndent)) + styledMarker + current + styledLabel;
                    if (description.Length > 0)
                        styledPrimary += theme.Style("muted",
                            new string(' ', Math.Max(2, labelWidth - TerminalTextLayout.Width(label) + 2)) + description);
                    lines.Add(PadPanelLine(styledPrimary, width));
                }
                else if (description.Length == 0)
                    lines.Add(selected ? theme.Style("accent", primary) : primary);
                else if (selected)
                    lines.Add(theme.Style("accent", marker + label.PadRight(labelWidth) + "  " + description));
                else
                    lines.Add(primary + theme.Style("muted",
                        new string(' ', Math.Max(2, labelWidth - TerminalTextLayout.Width(label) + 2)) + description));
            }
        }
        lines.Add("");
        lines.Add(PadPanelLine(" " + RenderKeyHint(theme, TerminalSafeText.Normalize(footer)), width));
        lines.AddRange(Enumerable.Repeat("", Math.Max(0, bottomSpacerLines)));
        lines.Add(PanelBorder(theme, width));
        return lines;
    }

    public static IReadOnlyList<string> RenderExtensionSelectorPanel<T>(TerminalScreen screen, string title,
        IReadOnlyList<TerminalSelectionOption<T>> options, int selectedIndex)
    {
        var width = screen.TerminalWidth;
        var theme = screen.CurrentTheme;
        var border = theme.Fg("border") + new string('─', Math.Max(1, width)) + "\u001b[0m";
        var lines = new List<string> { border, "" };
        var titleRows = TerminalSafeText.Normalize(title).Split('\n');
        for (var index = 0; index < titleRows.Length; index++)
        {
            var row = " " + theme.Style("accent", titleRows[index], bold: true);
            if (index == 0 && titleRows.Length > 1)
            {
                var padding = Math.Max(0, width - TerminalTextLayout.Width(row));
                lines.Add(row + theme.Fg("accent") + new string(' ', padding) + "\u001b[39m");
            }
            else
            {
                lines.Add(PadPanelLine(row, width));
            }
        }
        lines.Add("");
        for (var index = 0; index < options.Count; index++)
        {
            var label = TerminalSafeText.Normalize(options[index].Label);
            lines.Add(index == selectedIndex
                ? PadPanelLine(" " + theme.Style("accent", "→ " + label), width)
                : PadPanelLine("   " + theme.Style("text", label), width));
        }
        lines.Add("");
        lines.Add(PadPanelLine(" " + RenderExtensionKeyHint(theme,
            ("↑↓", "navigate"), ("enter", "select"), ("escape/ctrl+c", "cancel")), width));
        lines.Add("");
        lines.Add(border);
        return lines;
    }

    public static IReadOnlyList<string> RenderExtensionInputPanel(TerminalScreen screen, string title,
        string value, int cursor, out int cursorRow, out int cursorColumn)
    {
        var width = screen.TerminalWidth;
        var theme = screen.CurrentTheme;
        var border = theme.Fg("border") + new string('─', Math.Max(1, width)) + "\u001b[0m";
        var normalizedValue = TerminalSafeText.Normalize(value);
        var cursorOffset = Math.Clamp(cursor, 0, normalizedValue.Length);
        var cursorElementLength = cursorOffset < normalizedValue.Length
            ? System.Globalization.StringInfo.GetNextTextElement(normalizedValue, cursorOffset).Length
            : 0;
        var cursorText = cursorElementLength > 0
            ? normalizedValue.Substring(cursorOffset, cursorElementLength)
            : " ";
        var inputLine = "> " + normalizedValue[..cursorOffset] + "\u001b[7m" + cursorText + "\u001b[27m" +
            normalizedValue[(cursorOffset + cursorElementLength)..];
        var lines = new List<string>
        {
            border,
            "",
            PadPanelLine(" " + theme.Style("accent", TerminalSafeText.Normalize(title)), width),
            "",
            PadPanelLine(inputLine, width),
            "",
            PadPanelLine(" " + RenderExtensionKeyHint(theme,
                ("enter", "submit"), ("escape/ctrl+c", "cancel")), width),
            "",
            border
        };
        cursorRow = 4;
        cursorColumn = Math.Min(width, 3 + TerminalTextLayout.Width(normalizedValue[..cursorOffset]));
        return lines;
    }

    public static string RenderExtensionKeyHint(TerminalTheme theme,
        params (string Key, string Description)[] hints) =>
        string.Join("  ", hints.Select(hint =>
            theme.Style("dim", hint.Key) + theme.Style("muted", " " + hint.Description)));

    public static string RenderKeyHint(TerminalTheme theme, string value)
    {
        var groups = value.Split(" • ", StringSplitOptions.None);
        for (var index = 0; index < groups.Length; index++)
        {
            var separator = groups[index].IndexOf(' ');
            groups[index] = separator <= 0
                ? theme.Style("dim", groups[index])
                : theme.Style("dim", groups[index][..separator]) +
                  theme.Style("muted", groups[index][separator..]);
        }
        return string.Join(" • ", groups);
    }

    public static IReadOnlyList<string> RenderPromptPanel(TerminalScreen screen, string title,
        IReadOnlyList<(string Message, string? Placeholder)> prompts, IReadOnlyList<string> completed,
        int promptIndex, string value, int cursor, out int cursorRow, out int cursorColumn,
        bool submittedPrompt = false)
    {
        var width = screen.TerminalWidth;
        var theme = screen.CurrentTheme;
        var border = theme.Fg("border") + new string('─', Math.Max(1, width)) + "\u001b[0m";
        var lines = new List<string> { border,
            PadPanelLine(" " + theme.Style("accent", TerminalSafeText.Normalize(title), bold: true), width) };
        var currentInputRow = 0;
        for (var index = 0; index <= promptIndex; index++)
        {
            lines.Add("");
            lines.Add(PadPanelLine(" " + theme.Style("text", TerminalSafeText.Normalize(prompts[index].Message)), width));
            if (!string.IsNullOrWhiteSpace(prompts[index].Placeholder))
                lines.Add(PadPanelLine(" " + theme.Style("dim", "e.g., " + TerminalSafeText.Normalize(prompts[index].Placeholder!)), width));
            currentInputRow = lines.Count;
            var inputValue = TerminalSafeText.Normalize(index == promptIndex ? value : completed[index]);
            var inputLine = "> " + inputValue;
            if (index == promptIndex && !submittedPrompt)
            {
                var inputCursor = Math.Clamp(cursor, 0, inputValue.Length);
                var cursorCharacter = inputCursor < inputValue.Length ? inputValue[inputCursor].ToString() : " ";
                inputLine = "> " + inputValue[..inputCursor] + "\u001b[7m" + cursorCharacter + "\u001b[27m" +
                    inputValue[(inputCursor + (inputCursor < inputValue.Length ? 1 : 0))..];
            }
            lines.Add(PadPanelLine(inputLine, width));
            lines.Add(PadPanelLine(" (" + theme.Style("dim", "escape/ctrl+c") +
                theme.Style("muted", " to cancel,") + " " + theme.Style("dim", "enter") +
                theme.Style("muted", " to submit") + ")", width));
        }
        lines.Add(border);
        cursorRow = currentInputRow;
        var normalizedValue = TerminalSafeText.Normalize(value);
        cursorColumn = Math.Min(width, 3 + TerminalTextLayout.Width(normalizedValue[..Math.Clamp(cursor, 0, normalizedValue.Length)]));
        return lines;
    }

    public static string PanelBorder(TerminalTheme theme, int width) =>
        theme.Fg("accent") + new string('─', Math.Max(1, width)) + "\u001b[0m";

    public static string PadPanelLine(string line, int width) =>
        line + new string(' ', Math.Max(0, width - TerminalTextLayout.Width(line)));
}
