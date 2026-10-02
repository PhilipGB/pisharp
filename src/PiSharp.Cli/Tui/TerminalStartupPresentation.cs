namespace PiSharp.Cli.Tui;

internal sealed class TerminalStartupPresentation(QuietStartupMode? quietStartup, bool verbose)
{
    public bool ShowHeader => verbose || quietStartup != QuietStartupMode.Silent;
    public bool ShowDetails => verbose || quietStartup is null or QuietStartupMode.Full;

    public void Write(TextWriter output, string provider, string model, string thinking, string workingDirectory)
    {
        if (ShowHeader)
        {
            var version = typeof(CliArguments).Assembly.GetName().Version?.ToString(3) ?? "unknown";
            output.WriteLine($"PiSharp v{version}\n/model · /settings · /thinking · /scoped-models · /login · /logout · /tree · /fork · /new · /session · /hotkeys · /quit · Escape interrupts; Enter steers; Alt+Enter follows up\n");
        }
        if (ShowDetails)
            output.WriteLine($"PiSharp · {provider}/{model} · thinking {thinking} · {workingDirectory}\n");
    }
}
