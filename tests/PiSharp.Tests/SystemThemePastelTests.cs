using System.Globalization;
using PiSharp.Cli.Tui;

namespace PiSharp.Tests;

public sealed class SystemThemePastelTests
{
    [Theory]
    [InlineData(223, 223, 223, 253)]
    [InlineData(143, 143, 143, 245)]
    public void Ansi256UsesTheLowerGrayAtEqualDistance(byte red, byte green, byte blue, int index)
    {
        Assert.Equal(index, new TerminalTheme.Rgb(red, green, blue).ToAnsi256());
    }

    [Theory]
    [InlineData(false, "#cc92bd", "#283d73", "#5d2b52")]
    [InlineData(true, "#a23388", "#d9e0ed", "#eed9e7")]
    public void PastelPaletteMatchesCurrentPiAfterLightnessChanges(bool light, string accent, string userPanel,
        string customPanel)
    {
        var palette = (light
            ? new[]
            {
                "#5c5f77", "#d20f39", "#40a02b", "#df8e1d", "#1e66f5", "#ea76cb", "#179299", "#acb0be",
                "#6c6f85", "#e64553", "#40a02b", "#df8e1d", "#1e66f5", "#ea76cb", "#179299", "#bcc0cc"
            }
            : new[]
            {
                "#51576d", "#e78284", "#a6d189", "#e5c890", "#8caaee", "#f4b8e4", "#81c8be", "#b5bfe2",
                "#626880", "#e67172", "#8ec772", "#d9ba73", "#7b9ef0", "#f2a4db", "#5abfb5", "#a5adce"
            }).Select(Rgb).ToArray();
        var background = Rgb(light ? "#eff1f5" : "#303446");
        var foreground = Rgb(light ? "#4c4f69" : "#c6d0f5");

        var theme = TerminalSystemTheme.Create(TerminalColorMode.TrueColor,
            new TerminalColorState(foreground, background, palette), light ? "light" : "dark");

        Assert.Equal(light ? "light" : "dark", theme.Appearance);
        Assert.Equal(Rgb(accent), theme.GetConcreteColor("accent"));
        Assert.Equal(Rgb(userPanel), theme.GetConcreteColor("userMessageBg"));
        Assert.Equal(Rgb(customPanel), theme.GetConcreteColor("customMessageBg"));
        Assert.True(TerminalSystemTheme.Contrast(theme.GetConcreteColor("text"), background) >= 4.5);
        Assert.True(TerminalSystemTheme.Contrast(theme.GetConcreteColor("text"),
            theme.GetConcreteColor("selectedBg")) >= 4.5);
    }

    private static TerminalTheme.Rgb Rgb(string hex) => new(
        byte.Parse(hex[1..3], NumberStyles.HexNumber, CultureInfo.InvariantCulture),
        byte.Parse(hex[3..5], NumberStyles.HexNumber, CultureInfo.InvariantCulture),
        byte.Parse(hex[5..7], NumberStyles.HexNumber, CultureInfo.InvariantCulture));
}
