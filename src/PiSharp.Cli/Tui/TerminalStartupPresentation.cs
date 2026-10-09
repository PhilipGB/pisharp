namespace PiSharp.Cli.Tui;

internal sealed class TerminalStartupPresentation(QuietStartupMode? quietStartup, bool verbose)
{
    public bool ShowHeader => verbose || quietStartup != QuietStartupMode.Silent;

    public string Build(TerminalTheme theme, int columns, string? projectTrustNotice)
    {
        var rows = new List<string>();
        if (ShowHeader)
        {
            var version = typeof(CliArguments).Assembly.GetName().Version?.ToString(3) ?? "unknown";
            rows.Add("");
            rows.Add($" {theme.Style("accent", "▀▀█")} {theme.Style("dim", $"PiSharp v{version}")}");
            rows.Add($" {theme.Style("accent", "█▀ █")} {theme.Style("dim", "Esc interrupt · Ctrl+C/Ctrl+D clear/exit · / commands · ! Bash · Ctrl+O more")}");
            rows.Add(" " + theme.Style("dim", "Use /hotkeys for shortcut help and / for commands."));
            rows.Add("");
            rows.Add(" " + theme.Style("dim", "PiSharp can explain its own features and look up its docs. Ask how to use or extend PiSharp."));
            rows.Add("");
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
}
