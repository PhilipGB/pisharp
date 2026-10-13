using PiSharp.Cli.Tui;

namespace PiSharp.Tests;

public sealed class RadiusLoginShimmerTests
{
    [Fact]
    public void RadiusBandsBlendAndMoveAtTenCharactersPerSecond()
    {
        Assert.Equal("\u001b[38;2;77;154;191mS\u001b[38;2;85;162;194mi\u001b[38;2;104;179;201mg\u001b[38;2;123;196;207mn\u001b[39m",
            RadiusLoginShimmer.Paint("Sign", 0, TerminalColorMode.TrueColor));
        Assert.Equal("\u001b[38;2;188;147;149mS\u001b[39m",
            RadiusLoginShimmer.Paint("S", 250, TerminalColorMode.TrueColor));
    }

    [Fact]
    public void NoColorOutputPreservesPlainText()
    {
        Assert.Equal("Sign in with Radius", RadiusLoginShimmer.Paint("Sign in with Radius", 250, TerminalColorMode.None));
    }

    [Theory]
    [InlineData(123, "\u001b[38;5;139md")]
    [InlineData(777, "\u001b[38;5;186ma")]
    public void IndexedBandsQuantizeUnroundedInterpolatedChannels(double elapsedMilliseconds, string expected)
    {
        Assert.Contains(expected, RadiusLoginShimmer.Paint("Sign in with Radius", elapsedMilliseconds, TerminalColorMode.Ansi256));
    }
}
