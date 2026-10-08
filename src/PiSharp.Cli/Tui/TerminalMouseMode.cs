namespace PiSharp.Cli.Tui;

/// <summary>Owns the mouse and focus reporting modes used by the interactive screen.</summary>
internal static class TerminalMouseMode
{
    private const string ButtonMotion = "\u001b[?1000h\u001b[?1002h";
    private const string AllMotion = "\u001b[?1000h\u001b[?1002h\u001b[?1003h";

    public static string Enable => (IsMultiplexer ? ButtonMotion : AllMotion) + "\u001b[?1004h\u001b[?1006h";
    public const string Disable = "\u001b[?1006l\u001b[?1004l\u001b[?1003l\u001b[?1002l\u001b[?1000l";

    private static bool IsMultiplexer
    {
        get
        {
            var term = Environment.GetEnvironmentVariable("TERM")?.ToLowerInvariant() ?? "";
            return Environment.GetEnvironmentVariable("TMUX") is not null ||
                Environment.GetEnvironmentVariable("ZELLIJ") is not null ||
                Environment.GetEnvironmentVariable("STY") is not null ||
                term.StartsWith("tmux", StringComparison.Ordinal) ||
                term.StartsWith("screen", StringComparison.Ordinal);
        }
    }
}
