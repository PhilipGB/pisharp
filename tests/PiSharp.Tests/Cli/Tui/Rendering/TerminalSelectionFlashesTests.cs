using PiSharp.Cli.Tui;

namespace PiSharp.Tests;

public sealed class TerminalSelectionFlashesTests
{
    [Fact]
    public void CopyMessagesExpireIndependentlyAndPreserveTheirOrder()
    {
        var clock = new ManualTimeProvider();
        var flashes = new TerminalSelectionFlashes(clock);
        flashes.Add("first", TimeSpan.FromSeconds(1));
        clock.Advance(TimeSpan.FromMilliseconds(300));
        flashes.Add("second", TimeSpan.FromSeconds(1));
        Assert.Equal(["first", "second"], flashes.Messages);
        Assert.Equal(TimeSpan.FromMilliseconds(700), flashes.NextExpiry);
        clock.Advance(TimeSpan.FromMilliseconds(700));
        Assert.True(flashes.Expire());
        Assert.Equal(["second"], flashes.Messages);
        Assert.Equal(TimeSpan.FromMilliseconds(300), flashes.NextExpiry);
        clock.Advance(TimeSpan.FromMilliseconds(300));
        Assert.True(flashes.Expire());
        Assert.Empty(flashes.Messages);
        Assert.Equal(Timeout.InfiniteTimeSpan, flashes.NextExpiry);
    }

    [Fact]
    public void ErrorMessagesKeepTheirLongerLifetimeAndClearReleasesEveryMessage()
    {
        var clock = new ManualTimeProvider();
        var flashes = new TerminalSelectionFlashes(clock);
        flashes.Add("failed", TimeSpan.FromSeconds(5));
        flashes.Add("copied", TimeSpan.FromSeconds(1));
        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.True(flashes.Expire());
        Assert.Equal(["failed"], flashes.Messages);
        Assert.Equal(TimeSpan.FromSeconds(4), flashes.NextExpiry);
        flashes.Clear();
        Assert.Empty(flashes.Messages);
        Assert.Equal(Timeout.InfiniteTimeSpan, flashes.NextExpiry);
    }

    [Fact]
    public void FlashRetentionIsBoundedDuringRepeatedCopyEvents()
    {
        var flashes = new TerminalSelectionFlashes(new ManualTimeProvider());
        for (var index = 0; index < 100; index++) flashes.Add(index.ToString(), TimeSpan.FromSeconds(1));
        Assert.Equal(64, flashes.Messages.Count);
        Assert.Equal("36", flashes.Messages[0]);
        Assert.Equal("99", flashes.Messages[^1]);
    }

    [Fact]
    public void CompositorStacksMessagesAtTheRightAndKeepsTheLatestVisibleRows()
    {
        var compositor = new TerminalScreenCompositor(new StringWriter(), new TerminalImageRenderer());
        var frame = compositor.Compose("", 0, null, null, "transcript", "fixture-model",
            overlay: null, scrollOffset: 0, columns: 40, height: 3,
            new TranscriptSearchController(), new TerminalMouseRouter(), TerminalTheme.Default,
            transientFlashes: ["first", "second", "third", "fourth"]);
        Assert.EndsWith("\u001b[7m second \u001b[27m", frame.Rows[0]);
        Assert.EndsWith("\u001b[7m third \u001b[27m", frame.Rows[1]);
        Assert.EndsWith("\u001b[7m fourth \u001b[27m", frame.Rows[2]);
        Assert.All(frame.Rows, row => Assert.Equal(40, TerminalTextLayout.Width(row)));
    }
}
