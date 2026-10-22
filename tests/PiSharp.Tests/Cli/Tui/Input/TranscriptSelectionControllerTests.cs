using PiSharp.Cli.Tui;

namespace PiSharp.Tests;

public sealed class TranscriptSelectionControllerTests
{
    [Theory]
    [InlineData("zero alpha beta", 6, 10, "alpha")]
    [InlineData("extensions/starline/fixed-editor/compositor.ts", 13, 16,
        "extensions/starline/fixed-editor/compositor.ts")]
    [InlineData("earendil-works/pi-tui", 10, 12, "earendil-works/pi-tui")]
    [InlineData("foo  bar", 1, 3, "foo")]
    [InlineData("éclair 🙂 next", 1, 3, "éclair")]
    [InlineData("éclair 🙂 next", 8, 9, "🙂")]
    public void DoubleClickSelectsTheSameWordAtDifferentCells(string line, int first, int second, string expected)
    {
        var selection = new TranscriptSelectionController(new ManualTimeProvider());
        selection.SetVisibleRows(["\u001b[31m" + line + "\u001b[0m"], 0);
        Click(selection, first, 1);
        Click(selection, second, 1);
        Assert.Equal(expected, selection.SelectedText);
        Assert.Contains("\u001b[7m", selection.HighlightVisibleRows()[0]);
        Assert.DoesNotContain("\u001b", selection.SelectedText, StringComparison.Ordinal);
    }

    [Fact]
    public void TripleClickSelectsTheLineWithoutCopiedPaddingAndFourthClickClearsIt()
    {
        var selection = new TranscriptSelectionController(new ManualTimeProvider());
        selection.SetVisibleRows(["zero alpha beta   "], 0);
        Click(selection, 6, 1);
        Click(selection, 8, 1);
        Click(selection, 10, 1);
        Assert.Equal("zero alpha beta", selection.SelectedText);
        Click(selection, 7, 1);
        Assert.Null(selection.SelectedText);
    }

    [Theory]
    [InlineData(500, "alpha")]
    [InlineData(501, null)]
    public void ClickSequenceUsesThePinnedHalfSecondWindow(int milliseconds, string? expected)
    {
        var clock = new ManualTimeProvider();
        var selection = new TranscriptSelectionController(clock);
        selection.SetVisibleRows(["zero alpha beta"], 0);
        Click(selection, 6, 1);
        clock.Advance(TimeSpan.FromMilliseconds(milliseconds));
        Click(selection, 8, 1);
        Assert.Equal(expected, selection.SelectedText);
    }

    [Fact]
    public void TripleClickIncludesTheSelectedVisualLineMarginWithoutTrailingPadding()
    {
        var selection = new TranscriptSelectionController(new ManualTimeProvider());
        selection.SetVisibleRows(["zero alpha beta   "], 0, lineMargin: 1);
        Click(selection, 6, 1);
        Click(selection, 8, 1);
        Click(selection, 10, 1);
        Assert.Equal(" zero alpha beta", selection.SelectedText);
        Assert.True(selection.IsWholeLineSelected(0));
        Assert.False(selection.IsWholeLineSelected(1));
    }

    [Fact]
    public void WordDragIncludesWholeWordsAcrossRowsAndReversesFromItsOriginalRange()
    {
        var selection = new TranscriptSelectionController(new ManualTimeProvider());
        selection.SetVisibleRows(["zero alpha beta", "gamma delta"], 0);
        Click(selection, 12, 1);
        Send(selection, 0, 14, 1);
        Send(selection, 32, 3, 2);
        Send(selection, 0, 3, 2, release: true);
        Assert.Equal("beta\ngamma", selection.SelectedText);

        Click(selection, 8, 2);
        Send(selection, 0, 10, 2);
        Send(selection, 32, 7, 1);
        Send(selection, 0, 7, 1, release: true);
        Assert.Equal("alpha beta\ngamma delta", selection.SelectedText);
    }

    [Fact]
    public void WordDragIncludesTheWholeWhitespaceSegmentWithoutCopyingTrailingPadding()
    {
        var selection = new TranscriptSelectionController(new ManualTimeProvider());
        selection.SetVisibleRows(["foo  bar"], 0);
        Click(selection, 1, 1);
        Send(selection, 0, 2, 1);
        Send(selection, 32, 4, 1);
        var highlighted = selection.HighlightVisibleRows()[0];
        Assert.Contains("foo  \u001b[27m", highlighted);
        Send(selection, 0, 4, 1, release: true);
        Assert.Equal("foo", selection.SelectedText);
    }

    [Fact]
    public void TripleClickDragExtendsByWholeLines()
    {
        var selection = new TranscriptSelectionController(new ManualTimeProvider());
        selection.SetVisibleRows(["zero alpha beta", "gamma delta"], 0);
        Click(selection, 6, 1);
        Click(selection, 8, 1);
        Send(selection, 0, 10, 1);
        Send(selection, 32, 8, 2);
        Send(selection, 0, 8, 2, release: true);
        Assert.Equal("zero alpha beta\ngamma delta", selection.SelectedText);
    }

    [Fact]
    public void ClearAndDragBreakTheClickSequenceAndOrphanedMotionCannotSelect()
    {
        var selection = new TranscriptSelectionController(new ManualTimeProvider());
        selection.SetVisibleRows(["zero alpha beta"], 0);
        Click(selection, 6, 1);
        selection.Clear();
        Click(selection, 8, 1);
        Assert.Null(selection.SelectedText);
        Send(selection, 32, 12, 1);
        Send(selection, 0, 12, 1, release: true);
        Assert.Null(selection.SelectedText);
    }

    private static void Click(TranscriptSelectionController selection, int column, int row)
    {
        Send(selection, 0, column, row);
        Send(selection, 0, column, row, release: true);
    }

    private static void Send(TranscriptSelectionController selection, int button, int column, int row,
        bool release = false) => selection.HandleMouse(new(button, column, row, release), 0, 2, 0);
}
