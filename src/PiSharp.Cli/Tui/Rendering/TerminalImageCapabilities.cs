namespace PiSharp.Cli.Tui;

internal enum TerminalImageProtocol { None, Kitty, ITerm2 }

/// <summary>Detects image protocols conservatively from the attached terminal environment.</summary>
internal static class TerminalImageCapabilities
{
    public static TerminalImageProtocol Detect(Func<string, string?> environment)
    {
        ArgumentNullException.ThrowIfNull(environment);
        var termProgram = environment("TERM_PROGRAM")?.ToLowerInvariant() ?? "";
        var term = environment("TERM")?.ToLowerInvariant() ?? "";
        var forced = environment("PI_IMAGE_PROTOCOL")?.ToLowerInvariant();
        if (forced is "kitty") return TerminalImageProtocol.Kitty;
        if (forced is "iterm2") return TerminalImageProtocol.ITerm2;
        if (forced is "none" or "0") return TerminalImageProtocol.None;

        if (Present(environment, "TMUX") || term.StartsWith("tmux", StringComparison.Ordinal) ||
            term.StartsWith("screen", StringComparison.Ordinal))
            return TerminalImageProtocol.None;

        if (Present(environment, "KITTY_WINDOW_ID") || termProgram == "kitty" ||
            termProgram == "ghostty" || term.Contains("ghostty", StringComparison.Ordinal) ||
            Present(environment, "GHOSTTY_RESOURCES_DIR") || Present(environment, "WEZTERM_PANE") ||
            termProgram == "wezterm" || termProgram == "warpterminal" || Present(environment, "WARP_SESSION_ID") ||
            Present(environment, "WARP_TERMINAL_SESSION_UUID"))
            return TerminalImageProtocol.Kitty;

        if (Present(environment, "ITERM_SESSION_ID") || termProgram == "iterm.app")
            return TerminalImageProtocol.ITerm2;

        return TerminalImageProtocol.None;
    }

    private static bool Present(Func<string, string?> environment, string name) =>
        !string.IsNullOrWhiteSpace(environment(name));
}
