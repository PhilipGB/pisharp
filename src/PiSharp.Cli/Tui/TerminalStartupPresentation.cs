namespace PiSharp.Cli.Tui;

internal sealed record TerminalStartupDetail(string Name, IReadOnlyList<string> Items,
    IReadOnlyList<string>? ExpandedItems = null);

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
                rows.Add($" {theme.Style("accent", "█▀ █")} {theme.Style("dim", $"{DisplayKey("app.interrupt", "Esc")} interrupt · {DisplayKey("app.clear", "Ctrl+C")}/{DisplayKey("app.exit", "Ctrl+D")} clear/exit · / commands · ! Bash · {expandKey} more")}");
                rows.Add(" " + theme.Style("dim",
                    $"Press {expandKey} to show full startup help{(ShowDetails ? " and loaded resources" : "")}."));
                rows.Add(" " + theme.Style("dim", "Use /hotkeys for shortcut help and / for commands."));
            }
            rows.Add("");
            rows.Add(" " + theme.Style("dim", "PiSharp can explain its own features and look up its docs. Ask how to use or extend PiSharp."));
            rows.Add("");
        }
        return string.Join('\n', rows);
    }

    public string BuildDetails(TerminalTheme theme, int columns,
        IReadOnlyList<TerminalStartupDetail>? startupDetails, string? projectTrustNotice, bool expanded)
    {
        var rows = new List<string>();
        if (ShowDetails && startupDetails is not null)
        {
            foreach (var detail in startupDetails)
            {
                var sourceItems = expanded ? detail.ExpandedItems ?? detail.Items : detail.Items;
                var items = sourceItems.Select(TerminalTextLayout.Sanitize)
                    .Select(item => item.Trim()).Where(item => item.Length > 0).ToArray();
                if (items.Length == 0) continue;
                rows.Add(" " + theme.Style("mdHeading", $"[{TerminalTextLayout.Sanitize(detail.Name)}]"));
                rows.AddRange(expanded
                    ? items.Select(item => " " + theme.Style("dim", $"  {item}"))
                    : [" " + theme.Style("dim", $"  {string.Join(", ", items)}")]);
                rows.Add("");
            }
        }
        if (!string.IsNullOrWhiteSpace(projectTrustNotice))
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

    private IEnumerable<string> ExpandedHelp(TerminalTheme theme)
    {
        (string? Action, string Key, string Description)[] hints =
        [
            ("app.interrupt", "Esc", "to interrupt"),
            ("app.clear", "Ctrl+C", "to clear"),
            ("app.exit", "Ctrl+D", "to exit when empty"),
            ("app.model.cycleForward", "Ctrl+P", "to cycle models forward"),
            ("app.model.cycleBackward", "Alt+P", "to cycle models backward"),
            ("app.model.select", "Ctrl+L", "to select a model"),
            ("app.thinking.cycle", "Shift+Tab", "to cycle thinking level"),
            ("app.tools.expand", "Ctrl+O", "to expand or collapse details and tool output"),
            ("app.editor.external", "Ctrl+G", "to open the external editor"),
            (null, "/", "for commands"),
            (null, "!", "to run bash"),
            ("app.message.followUp", "Alt+Enter", "to queue a follow-up"),
            ("app.message.dequeue", "Alt+Up", "to restore queued messages"),
            ("app.clipboard.pasteImage", "Ctrl+V", "to paste clipboard content"),
            (null, "drop files", "to attach")
        ];
        foreach (var hint in hints)
        {
            var key = hint.Action is null ? hint.Key : getDisplayKeys?.Invoke(hint.Action);
            if (string.IsNullOrWhiteSpace(key)) continue;
            yield return " " + theme.Style("dim", $"{key} {hint.Description}");
        }
    }

    private string DisplayKey(string action, string fallback) =>
        getDisplayKeys?.Invoke(action) is { Length: > 0 } key ? key : fallback;
}
