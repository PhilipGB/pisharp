using PiSharp.Cli;
using PiSharp.Cli.Tui;

namespace PiSharp.Tests;

public sealed class TerminalThinkingSelectorTests
{
    [Fact]
    public void OptionsUseSupportedLevelOrderDescriptionsAndCurrentAndDefaultMarkers()
    {
        var levels = ThinkingLevels.AvailableForModel(true);

        var options = TerminalThinkingSelector.CreateOptions(levels, "high", "medium");

        Assert.Equal(["off", "minimal", "low", "medium", "high"], options.Select(option => option.Value));
        Assert.Equal("Deep reasoning (~16k tokens)", options.Single(option => option.Value == "high").Description);
        Assert.True(options.Single(option => option.Value == "high").IsCurrent);
        Assert.Contains("Moderate reasoning (~8k tokens) · default",
            options.Single(option => option.Value == "medium").Description);
        Assert.DoesNotContain(options, option => option.Value is "xhigh" or "max");
    }
}
