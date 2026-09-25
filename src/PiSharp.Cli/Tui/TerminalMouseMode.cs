namespace PiSharp.Cli.Tui;

/// <summary>Owns the SGR button-motion tracking modes used by the interactive screen.</summary>
internal static class TerminalMouseMode
{
    public const string Enable = "\u001b[?1000h\u001b[?1002h\u001b[?1006h";
    public const string Disable = "\u001b[?1006l\u001b[?1002l\u001b[?1000l";
}
