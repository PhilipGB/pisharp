using PiSharp.Runtime.Extensions;

namespace PiSharp.Cli.Tui;

/// <summary>
/// Pi's bash result presentation: when the output exceeds five visual rows,
/// the last five are shown under a "... (N earlier lines," expansion hint.
/// </summary>
internal static class TerminalBashResultPreview
{
    internal const int VisualLines = 5;

    /// <summary>Raw bash result inputs, captured before the tool's arguments are released.</summary>
    internal readonly record struct Source(string? Command, string Output, string? TimerText);

    internal static PiSharpToolRenderView Render(Source source, int width, string expandKeyLabel, bool expanded)
    {
        var command = source.Command;
        var displayedCommand = command is null ? "[invalid arg]" : command.Length == 0 ? "..." : command;
        var spans = new List<PiSharpToolTextSpan>
        {
            new("$ " + TerminalSafeText.Normalize(displayedCommand), PiSharpToolTextStyle.Title, Bold: true)
        };
        var output = source.Output.Trim();
        if (output.Length > 0)
        {
            var rows = VisualRows(output, width);
            var hidden = expanded ? 0 : Math.Max(0, rows.Count - VisualLines);
            spans.Add(new("\n\n", PiSharpToolTextStyle.Normal));
            if (hidden > 0)
            {
                AppendHint(spans, hidden, expandKeyLabel);
                spans.Add(new("\n", PiSharpToolTextStyle.Normal));
            }
            for (var index = hidden; index < rows.Count; index++)
            {
                if (index > hidden) spans.Add(new("\n", PiSharpToolTextStyle.Output));
                spans.Add(new(TerminalSafeText.Normalize(rows[index]), PiSharpToolTextStyle.Output));
            }
        }
        if (!string.IsNullOrEmpty(source.TimerText))
            spans.Add(new("\n\n" + TerminalSafeText.Normalize(source.TimerText), PiSharpToolTextStyle.Muted));
        return new(spans);
    }

    private static void AppendHint(List<PiSharpToolTextSpan> spans, int hidden, string expandKeyLabel)
    {
        var key = string.IsNullOrWhiteSpace(expandKeyLabel) ? "ctrl+o" : expandKeyLabel.ToLowerInvariant();
        spans.Add(new($"... ({hidden} earlier lines,", PiSharpToolTextStyle.Muted));
        spans.Add(new(" ", PiSharpToolTextStyle.Normal));
        spans.Add(new(key, PiSharpToolTextStyle.Dim));
        spans.Add(new(" to expand)", PiSharpToolTextStyle.Muted));
    }

    /// <summary>Visual rows after wrapping at <paramref name="width"/>; non-positive widths keep logical lines.</summary>
    private static IReadOnlyList<string> VisualRows(string text, int width)
    {
        if (width <= 0) return text.Split('\n');
        var rows = new List<string>();
        foreach (var line in text.Split('\n'))
        {
            foreach (var row in TerminalTextLayout.Create(line, width).Rows)
                rows.Add(row.EndsWith("\u001b[0m", StringComparison.Ordinal) ? row[..^4] : row);
        }
        return rows;
    }
}
