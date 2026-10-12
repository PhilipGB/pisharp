namespace PiSharp.Cli.Tui;

internal sealed record TerminalStartupDetail(string Name, IReadOnlyList<string> Items,
    IReadOnlyList<string>? ExpandedItems = null, bool ShowWhenQuiet = false,
    bool ShowEachItemWhenQuiet = false,
    IReadOnlyList<TerminalStartupDiagnosticLine>? DiagnosticLines = null,
    string HeaderStyle = "mdHeading",
    IReadOnlyList<TerminalStartupPresentationLine>? ExpandedLines = null);

internal sealed record TerminalStartupDiagnosticLine(string Text, int Indent, string Style,
    string? Marker = null, string? MarkerStyle = null);

internal sealed record TerminalStartupPresentationLine(string Text, int Indent, string Style);

internal sealed class TerminalStartupPresentation(QuietStartupMode? quietStartup, bool verbose,
    Func<string, string?>? getDisplayKeys = null)
{
    public bool ShowHeader => verbose || quietStartup != QuietStartupMode.Silent;
    public bool ShowDetails => verbose || quietStartup is null or QuietStartupMode.Full;

    public string BuildLogo(TerminalTheme theme)
    {
        if (!ShowHeader) return "";
        var version = typeof(CliArguments).Assembly.GetName().Version?.ToString(3) ?? "unknown";
        return $" {theme.Style("accent", "▀▀█")} {theme.Style("dim", $"PiSharp v{version}")}";
    }

    public string Build(TerminalTheme theme, bool expanded)
    {
        var rows = new List<string>();
        if (ShowHeader)
        {
            if (expanded)
                rows.AddRange(ExpandedHelp(theme));
            else
            {
                var expandKey = DisplayKey("app.tools.expand", "Ctrl+O");
                rows.Add($" {theme.Style("accent", "█▀ █")} {theme.Style("dim", $"{DisplayKey("app.interrupt", "Esc")} interrupt · {DisplayKey("app.clear", "Ctrl+C")}/{DisplayKey("app.exit", "Ctrl+D")} clear/exit · / commands · ! bash · {expandKey} more")}");
                rows.Add(" " + theme.Style("dim",
                    $"Press {expandKey} to show full startup help{(ShowDetails ? " and loaded resources" : "")}."));
            }
            rows.Add("");
            rows.Add(" " + theme.Style("dim", "PiSharp can explain its own features and look up its docs. Ask how to use or extend PiSharp."));
            rows.Add("");
        }
        return string.Join('\n', rows);
    }

    public string BuildDetails(TerminalTheme theme, int columns,
        IReadOnlyList<TerminalStartupDetail>? startupDetails, string? projectTrustNotice, bool expanded,
        bool includePersistentDetails = true, bool persistentOnly = false)
    {
        var rows = new List<string>();
        if (startupDetails is not null)
        {
            foreach (var detail in startupDetails.Where(detail =>
                         (persistentOnly ? detail.ShowWhenQuiet : includePersistentDetails || !detail.ShowWhenQuiet) &&
                         (ShowDetails || detail.ShowWhenQuiet)))
            {
                var sourceItems = expanded ? detail.ExpandedItems ?? detail.Items : detail.Items;
                var items = sourceItems.Select(TerminalTextLayout.Sanitize)
                    .Select(item => item.Trim()).Where(item => item.Length > 0).ToArray();
                if (items.Length == 0 && detail.DiagnosticLines is not { Count: > 0 } &&
                    !(expanded && detail.ExpandedLines is { Count: > 0 })) continue;
                if (detail.DiagnosticLines is { } diagnosticLines)
                {
                    rows.Add(theme.Style(detail.HeaderStyle, $"[{TerminalTextLayout.Sanitize(detail.Name)}]"));
                    rows.AddRange(diagnosticLines.Select(line => RenderDiagnosticLine(theme, line)));
                    rows.Add("");
                    continue;
                }

                rows.Add(theme.Style(detail.HeaderStyle, $"[{TerminalTextLayout.Sanitize(detail.Name)}]"));
                if (expanded && detail.ExpandedLines is { } expandedLines)
                {
                    rows.AddRange(expandedLines.Select(line => RenderPresentationLine(theme, line)));
                }
                else rows.AddRange(expanded || detail.ShowEachItemWhenQuiet
                    ? items.Select(item => theme.Style("dim", $"  {item}"))
                    : [theme.Style("dim", $"  {string.Join(", ", items)}")]);
                rows.Add("");
            }
        }
        if (!persistentOnly && !string.IsNullOrWhiteSpace(projectTrustNotice))
        {
            var notice = projectTrustNotice;
            var split = notice.LastIndexOf(" then ", StringComparison.Ordinal);
            if (split >= 0)
            {
                var firstLine = notice[..(split + " then".Length)];
                if (TerminalTextLayout.Width(" " + firstLine) <= Math.Max(1, columns - 1))
                    notice = firstLine + "\n " + notice[(split + " then ".Length)..];
            }
            rows.Add(" " + theme.Style("warning", notice));
            rows.Add("");
        }
        return string.Join('\n', rows);
    }

    private static string RenderDiagnosticLine(TerminalTheme theme, TerminalStartupDiagnosticLine line)
    {
        var indentation = new string(' ', Math.Max(0, line.Indent));
        var text = TerminalTextLayout.Sanitize(line.Text);
        if (line.Marker is null) return theme.Style(line.Style, indentation + text);
        var marker = theme.Style(line.MarkerStyle ?? line.Style, TerminalTextLayout.Sanitize(line.Marker));
        return theme.Style(line.Style, indentation) + marker + " " + text;
    }

    private static string RenderPresentationLine(TerminalTheme theme, TerminalStartupPresentationLine line) =>
        " " + theme.Style(line.Style,
            new string(' ', Math.Max(0, line.Indent)) + TerminalTextLayout.Sanitize(line.Text));

    private IEnumerable<string> ExpandedHelp(TerminalTheme theme)
    {
        var clearKey = DisplayKey("app.clear", "Ctrl+C");
        (string? Action, string Key, string Description)[] hints =
        [
            ("app.interrupt", "Esc", "to interrupt"),
            ("app.clear", "Ctrl+C", "to clear"),
            (null, $"{clearKey} twice", "to exit"),
            ("app.exit", "Ctrl+D", "to exit (empty)"),
            ("tui.editor.deleteToLineEnd", "Ctrl+K", "to delete to end"),
            (null,
                $"{DisplayKey("app.model.cycleForward", "Ctrl+P")}/{DisplayKey("app.model.cycleBackward", "Alt+P")}",
                "to cycle models"),
            ("app.model.select", "Ctrl+L", "to select a model"),
            ("app.thinking.cycle", "Shift+Tab", "to cycle thinking level"),
            ("app.tools.expand", "Ctrl+O", "to expand tools"),
            ("app.thinking.toggle", "Ctrl+T", "to expand thinking"),
            ("app.editor.external", "Ctrl+G", "for external editor"),
            (null, "/", "for commands"),
            (null, "!", "to run bash"),
            (null, "!!", "to run bash (no context)"),
            ("app.message.followUp", "Alt+Enter", "to queue follow-up"),
            ("app.message.dequeue", "Alt+Up", "to edit all queued messages"),
            ("app.clipboard.pasteImage", "Ctrl+V", "to paste files on macOS, images, or text"),
            (null, "drop files", "to attach")
        ];
        foreach (var hint in hints)
        {
            var key = hint.Action is null ? FormatDisplayKey(hint.Key) :
                getDisplayKeys?.Invoke(hint.Action) is { Length: > 0 } configuredKey
                    ? FormatDisplayKey(configuredKey) : null;
            if (string.IsNullOrWhiteSpace(key)) continue;
            yield return " " + theme.Style("dim", $"{key} {hint.Description}");
        }
    }

    private string DisplayKey(string action, string fallback) =>
        FormatDisplayKey(getDisplayKeys?.Invoke(action) is { Length: > 0 } key ? key : fallback);

    private static string FormatDisplayKey(string key) => string.Join("/", key.Split('/')
        .Select(binding => string.Join("+", binding.Split('+').Select(part =>
            part.Equals("esc", StringComparison.OrdinalIgnoreCase) ||
            part.Equals("escape", StringComparison.OrdinalIgnoreCase) ? "escape" : part.ToLowerInvariant()))));
}
