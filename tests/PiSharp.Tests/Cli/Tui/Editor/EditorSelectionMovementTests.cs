using PiSharp.Cli.Tui;

namespace PiSharp.Tests;

public sealed class EditorSelectionMovementTests
{
    [Fact]
    public void SelectionExtensionRespectsGraphemesWordsLinesAndVisualRows()
    {
        var editor = new EditorBuffer();
        editor.SetText("a😀e\u0301\nb界c");
        editor.SetCursor(0);
        for (var index = 0; index < 3; index++) editor.Handle(Key(ConsoleKey.RightArrow, shift: true));
        Assert.Equal("a😀e\u0301", editor.SelectedText);
        Assert.Equal("a😀e\u0301\nb界c", editor.Text);

        editor.Handle(Key(ConsoleKey.LeftArrow, shift: true));
        Assert.Equal("a😀", editor.SelectedText);
        editor.Handle(Key(ConsoleKey.LeftArrow, shift: true));
        Assert.Equal("a", editor.SelectedText);
        editor.Handle(Key(ConsoleKey.RightArrow, shift: true));
        Assert.Equal("a😀", editor.SelectedText);
        editor.Handle(Key(ConsoleKey.RightArrow));
        Assert.Null(editor.SelectedText);
        Assert.Equal(3, editor.Cursor);

        editor.SetCursor(0);
        editor.Handle(Key(ConsoleKey.RightArrow, shift: true, alt: true));
        Assert.NotNull(editor.SelectedText);
        var firstWordSelection = editor.SelectedText;
        editor.Handle(Key(ConsoleKey.RightArrow, shift: true, control: true));
        Assert.NotNull(editor.SelectedText);
        Assert.NotEqual(firstWordSelection, editor.SelectedText);
        Assert.Equal("a😀e\u0301\nb界c", editor.Text);

        editor.SetCursor(0);
        editor.Handle(Key(ConsoleKey.End, shift: true));
        Assert.Equal("a😀e\u0301", editor.SelectedText);
        editor.Handle(Key(ConsoleKey.Home, shift: true));
        Assert.Null(editor.SelectedText);

        editor.SetLayoutWidth(10);
        editor.SetText("one two three four five");
        editor.SetCursor(0);
        editor.Handle(Key(ConsoleKey.DownArrow, shift: true));
        Assert.NotNull(editor.SelectedText);
        Assert.Equal("one two three four five", editor.Text);
        editor.Handle(Key(ConsoleKey.UpArrow, shift: true));
        Assert.Null(editor.SelectedText);
    }

    [Fact]
    public void SelectionReplacementDeletionAndUndoOperateOnTheSelectedRange()
    {
        var editor = new EditorBuffer();
        editor.SetText("alpha beta gamma");
        editor.SetCursor(5);
        editor.Handle(Key(ConsoleKey.RightArrow, shift: true));
        editor.Handle(Key(ConsoleKey.RightArrow, shift: true));
        Assert.Equal(" b", editor.SelectedText);

        editor.Handle(Key(ConsoleKey.X, 'X'));
        Assert.Equal("alphaXeta gamma", editor.Text);
        Assert.Equal(6, editor.Cursor);
        Assert.Null(editor.SelectedText);
        editor.Handle(Key(ConsoleKey.OemMinus, '-', control: true));
        Assert.Equal("alpha beta gamma", editor.Text);
        Assert.Equal(" b", editor.SelectedText);

        editor.SetCursor(5);
        editor.Handle(Key(ConsoleKey.RightArrow, shift: true));
        editor.Handle(Key(ConsoleKey.RightArrow, shift: true));
        editor.Handle(Key(ConsoleKey.Backspace));
        Assert.Equal("alphaeta gamma", editor.Text);
        editor.Handle(Key(ConsoleKey.OemMinus, '-', control: true));
        Assert.Equal("alpha beta gamma", editor.Text);
    }

    [Fact]
    public void SubmissionUsesTheCompleteDraftAndMouseSelectionDoesNotMoveTheCursor()
    {
        var editor = new EditorBuffer();
        editor.SetText("alpha beta\ngamma delta");
        editor.SetCursor(6);
        editor.Handle(Key(ConsoleKey.RightArrow, shift: true));
        Assert.NotNull(editor.SelectedText);

        Assert.True(editor.TrySubmit(out var submitted));
        Assert.Equal("alpha beta\ngamma delta", submitted);

        var mouseBuffer = new EditorBuffer();
        mouseBuffer.SetText("alpha beta\ngamma delta");
        mouseBuffer.SetCursor(6);
        var frame = EditorViewport.Layout(mouseBuffer.Text, mouseBuffer.Cursor, 40, 8, showPrompt: false);
        var selection = new EditorSelectionController();
        selection.SetFrame(mouseBuffer.Text, frame);
        Assert.True(selection.Begin(0, 0));
        Assert.True(selection.Move(1, 4));
        var result = selection.End(1, 4);
        Assert.True(result.Copy);
        Assert.Equal("alpha beta\ngamma", selection.SelectedText);
        Assert.Null(result.CursorOffset);
        Assert.Equal(6, mouseBuffer.Cursor);
        Assert.Equal("alpha beta\ngamma delta", mouseBuffer.Text);
    }

    private static ConsoleKeyInfo Key(ConsoleKey key, char keyChar = '\0', bool shift = false, bool alt = false,
        bool control = false) => new(keyChar, key, shift, alt, control);
}
