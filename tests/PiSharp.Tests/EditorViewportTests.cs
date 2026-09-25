using PiSharp.Cli.Tui;

namespace PiSharp.Tests;

public sealed class EditorViewportTests
{
    [Fact]
    public void MultilineAndWrappedInputKeepsCursorWithinViewport()
    {
        var frame = EditorViewport.Layout("abcdefgh\nmore text", 11, 9, 5);
        Assert.Equal(["❯ abcdef", "│ gh", "│ more t", "│ ext"], frame.Rows);
        Assert.Equal(2, frame.CursorRow);
        Assert.Equal(5, frame.CursorColumn);
    }

    [Fact]
    public void TallInputScrollsToCursorWithoutIncludingHiddenRows()
    {
        var text = "one\ntwo\nthree\nfour";
        var end = EditorViewport.Layout(text, text.Length, 20, 2);
        Assert.Equal(["│ three", "│ four"], end.Rows);
        Assert.Equal(1, end.CursorRow);
        Assert.Equal(7, end.CursorColumn);
        var beginning = EditorViewport.Layout(text, 0, 20, 2);
        Assert.Equal(["❯ one", "│ two"], beginning.Rows);
        Assert.Equal(0, beginning.CursorRow);
    }

    [Fact]
    public void WideAndCombiningGraphemesUseTerminalCellsNotUtf16Length()
    {
        var text = "a界e\u0301🙂z";
        var frame = EditorViewport.Layout(text, text.IndexOf('z'), 8, 4);
        Assert.Equal(["❯ a界e\u0301", "│ 🙂z"], frame.Rows);
        Assert.Equal(1, frame.CursorRow);
        Assert.Equal(5, frame.CursorColumn);
        var withinCluster = text.IndexOf('\u0301');
        Assert.Throws<ArgumentException>(() => EditorViewport.Layout(text, withinCluster, 8, 4));
        var family = "👩‍👩‍👧‍👦";
        var emoji = EditorViewport.Layout("A" + family + "B", 1 + family.Length, 8, 4);
        Assert.Single(emoji.Rows);
        Assert.Equal(6, emoji.CursorColumn);
    }

    [Fact]
    public void LayoutChangesWhenTerminalWidthChanges()
    {
        var narrow = EditorViewport.Layout("abcdef", 5, 6, 4);
        var wide = EditorViewport.Layout("abcdef", 5, 15, 4);
        Assert.True(narrow.Rows.Count > wide.Rows.Count);
        Assert.Equal(0, wide.CursorRow);
        Assert.InRange(narrow.CursorColumn, 3, 6);
    }

    [Fact]
    public void NewlinesAndControlCharactersCannotInjectTerminalEscapeSequences()
    {
        var frame = EditorViewport.Layout("a\u001b[2J\nb", 7, 80, 8);
        Assert.Equal(["❯ a [2J", "│ b"], frame.Rows);
        Assert.DoesNotContain('\u001b', string.Join("", frame.Rows));
        Assert.Throws<ArgumentOutOfRangeException>(() => EditorViewport.Layout("a", 2, 80, 8));
    }

    [Fact]
    public void RowMapsConvertTerminalCellsToGraphemeSafeSourceOffsetsAcrossWrapping()
    {
        var text = "a界e\u0301🙂z";
        var frame = EditorViewport.Layout(text, text.Length, 8, 4);

        Assert.Equal(["a界e\u0301", "🙂z"], frame.ContentRows);
        Assert.Equal(0, frame.RowMaps[0].OffsetAt(0));
        Assert.Equal(1, frame.RowMaps[0].OffsetAt(1));
        Assert.Equal(2, frame.RowMaps[0].OffsetAt(3));
        Assert.Equal(4, frame.RowMaps[1].OffsetAt(0));
        Assert.Equal(text.Length, frame.RowMaps[1].OffsetAt(3));
        Assert.Equal((4, 6), (frame.RowMaps[1].Cells[0].StartOffset, frame.RowMaps[1].Cells[0].EndOffset));
    }
}
