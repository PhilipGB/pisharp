using PiSharp.Cli;
using PiSharp.Cli.Tui;

namespace PiSharp.Tests;

public sealed class FullscreenWheelScrollAcceleratorTests
{
    [Fact]
    public void AutoModeAcceleratesRepeatedEventsAndResetsWhenGestureChanges()
    {
        var clock = new ManualTimeProvider();
        var accelerator = new FullscreenWheelScrollAccelerator(clock, accelerate: true);

        Assert.Equal(1, accelerator.Next(-1));
        clock.Advance(TimeSpan.FromMilliseconds(50));
        Assert.Equal(2, accelerator.Next(-1));
        clock.Advance(TimeSpan.FromMilliseconds(50));
        Assert.Equal(2, accelerator.Next(-1));

        clock.Advance(TimeSpan.FromMilliseconds(10));
        Assert.Equal(1, accelerator.Next(1));
        clock.Advance(TimeSpan.FromMilliseconds(20));
        Assert.Equal(5, accelerator.Next(1));

        clock.Advance(TimeSpan.FromMilliseconds(201));
        Assert.Equal(1, accelerator.Next(1));
    }

    [Fact]
    public void AutoModeLeavesSubNotchBurstsAtOneLineAndCanDisablePlatformAcceleration()
    {
        var clock = new ManualTimeProvider();
        var accelerator = new FullscreenWheelScrollAccelerator(clock, accelerate: true);

        Assert.Equal(1, accelerator.Next(-1));
        clock.Advance(TimeSpan.FromMilliseconds(4));
        Assert.Equal(1, accelerator.Next(-1));
        clock.Advance(TimeSpan.FromMilliseconds(50));
        Assert.Equal(2, accelerator.Next(-1));

        var terminalAccelerates = new FullscreenWheelScrollAccelerator(clock, accelerate: false);
        Assert.Equal(1, terminalAccelerates.Next(-1));
        clock.Advance(TimeSpan.FromMilliseconds(10));
        Assert.Equal(1, terminalAccelerates.Next(-1));
    }

    [Fact]
    public void FixedModeUsesConfiguredLineCountAndChangingSettingResetsAutoHistory()
    {
        var clock = new ManualTimeProvider();
        var accelerator = new FullscreenWheelScrollAccelerator(clock, accelerate: true);

        accelerator.SetSetting(FullscreenWheelScrollSetting.Fixed(7));
        Assert.Equal(7, accelerator.Next(-1));
        clock.Advance(TimeSpan.FromMilliseconds(10));
        Assert.Equal(7, accelerator.Next(-1));

        accelerator.SetSetting(FullscreenWheelScrollSetting.Automatic);
        Assert.Equal(1, accelerator.Next(-1));
    }
}
