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
    public void NewlinesAndControlCharactersCannotInjectTerminalEscapeSequences()
    {
        var frame = EditorViewport.Layout("a\u001b[2J\nb", 7, 80, 8);
        Assert.Equal(["❯ a [2J", "│ b"], frame.Rows);
        Assert.DoesNotContain('\u001b', string.Join("", frame.Rows));
        Assert.Throws<ArgumentOutOfRangeException>(() => EditorViewport.Layout("a", 2, 80, 8));
    }
}
