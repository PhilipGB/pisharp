using System.Diagnostics;

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

    public static bool SupportsHyperlinks(Func<string, string?> environment, Func<bool>? tmuxProbe = null)
    {
        ArgumentNullException.ThrowIfNull(environment);
        var forced = environment("PI_HYPERLINKS");
        if (forced == "1") return true;
        if (forced == "0") return false;

        var termProgram = environment("TERM_PROGRAM")?.ToLowerInvariant() ?? "";
        var terminalEmulator = environment("TERMINAL_EMULATOR")?.ToLowerInvariant() ?? "";
        var term = environment("TERM")?.ToLowerInvariant() ?? "";
        if (Present(environment, "TMUX") || term.StartsWith("tmux", StringComparison.Ordinal))
            return (tmuxProbe ?? ProbeTmuxHyperlinks)();
        if (term.StartsWith("screen", StringComparison.Ordinal)) return false;

        if (termProgram is "herdr" or "kitty" or "ghostty" or "wezterm" or "warpterminal" or "iterm.app" or
            "alacritty" or "vscode" or "zed")
            return true;
        if (term.Contains("ghostty", StringComparison.Ordinal) || Present(environment, "KITTY_WINDOW_ID") ||
            Present(environment, "GHOSTTY_RESOURCES_DIR") || Present(environment, "WEZTERM_PANE") ||
            Present(environment, "WARP_SESSION_ID") || Present(environment, "WARP_TERMINAL_SESSION_UUID") ||
            Present(environment, "ITERM_SESSION_ID") || Present(environment, "WT_SESSION"))
            return true;
        if (terminalEmulator == "jetbrains-jediterm" || OperatingSystem.IsWindows()) return false;
        return false;
    }

    private static bool Present(Func<string, string?> environment, string name) =>
        !string.IsNullOrWhiteSpace(environment(name));

    private static bool ProbeTmuxHyperlinks()
    {
        try
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo("tmux")
                {
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                }
            };
            process.StartInfo.ArgumentList.Add("display-message");
            process.StartInfo.ArgumentList.Add("-p");
            process.StartInfo.ArgumentList.Add("#{client_termfeatures}");
            if (!process.Start()) return false;
            if (!process.WaitForExit(250))
            {
                process.Kill(entireProcessTree: true);
                return false;
            }
            if (process.ExitCode != 0) return false;
            return process.StandardOutput.ReadToEnd().Split(',').Any(feature =>
                feature.Trim().Equals("hyperlinks", StringComparison.Ordinal));
        }
        catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception or
            IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
