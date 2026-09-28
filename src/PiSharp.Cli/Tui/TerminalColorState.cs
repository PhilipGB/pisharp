namespace PiSharp.Cli.Tui;

internal sealed record TerminalColorState(
    TerminalTheme.Rgb? Foreground = null,
    TerminalTheme.Rgb? Background = null,
    IReadOnlyList<TerminalTheme.Rgb>? Palette = null,
    string? AppearanceReport = null)
{
    public bool SameAs(TerminalColorState other)
    {
        if (Foreground != other.Foreground || Background != other.Background || AppearanceReport != other.AppearanceReport)
            return false;
        if (ReferenceEquals(Palette, other.Palette)) return true;
        return Palette is not null && other.Palette is not null && Palette.SequenceEqual(other.Palette);
    }
}
