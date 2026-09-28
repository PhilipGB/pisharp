using PiSharp.Cli;

namespace PiSharp.Tests;

public sealed class CliThemeArgumentsTests
{
    [Fact]
    public void ThemeFlagsPreserveRepeatedPathsAndInteractiveOverrides()
    {
        var arguments = CliArguments.Parse([
            "--theme", "themes/custom.json", "--theme", "themes/extra", "--use-theme", "ocean/light", "--no-themes"
        ]);

        Assert.Equal(["themes/custom.json", "themes/extra"], arguments.ThemePaths);
        Assert.Equal("ocean/light", arguments.UseTheme);
        Assert.True(arguments.NoThemes);
    }

    [Theory]
    [InlineData("--theme")]
    [InlineData("--theme", "")]
    [InlineData("--theme", "--no-themes")]
    [InlineData("--use-theme")]
    [InlineData("--use-theme", "--no-themes")]
    [InlineData("--use-theme", "")]
    public void ThemeFlagsRequireValues(params string[] arguments)
    {
        Assert.Throws<ArgumentException>(() => CliArguments.Parse(arguments));
    }
}
