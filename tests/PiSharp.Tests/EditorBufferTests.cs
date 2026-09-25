using PiSharp.Cli.Tui;

namespace PiSharp.Tests;

public sealed class EditorBufferTests
{
    private static ConsoleKeyInfo Key(char c, ConsoleKey key, ConsoleModifiers modifiers = 0) =>
        new(c, key, modifiers.HasFlag(ConsoleModifiers.Shift), modifiers.HasFlag(ConsoleModifiers.Alt), modifiers.HasFlag(ConsoleModifiers.Control));

    [Fact]
    public void MultilineNavigationAndHistoryArePreservedAcrossSubmissions()
    {
        var editor = new EditorBuffer();
        foreach (var c in "first") editor.Handle(Key(c, ConsoleKey.F));
        Assert.Equal(EditorAction.Render, editor.Handle(Key('\n', ConsoleKey.J, ConsoleModifiers.Control)));
        foreach (var c in "second") editor.Handle(Key(c, ConsoleKey.S));
        Assert.Equal(EditorAction.Submit, editor.Handle(Key('\r', ConsoleKey.Enter)));
        Assert.Equal("first\nsecond", editor.Text);
        editor.Clear();
        editor.Handle(Key('d', ConsoleKey.D));
        editor.Handle(Key('\0', ConsoleKey.UpArrow));
        Assert.Equal("d", editor.Text);
        Assert.Equal(0, editor.Cursor);
        editor.Handle(Key('\0', ConsoleKey.UpArrow));
        Assert.Equal("first\nsecond", editor.Text);
        editor.Handle(Key('\0', ConsoleKey.DownArrow));
        Assert.Equal("first\nsecond", editor.Text);
        editor.Handle(Key('\0', ConsoleKey.DownArrow));
        Assert.Equal("d", editor.Text);
        editor.Handle(Key('\0', ConsoleKey.C, ConsoleModifiers.Control));
        Assert.Equal("", editor.Text);
        Assert.Equal(EditorAction.Exit, editor.Handle(Key('\0', ConsoleKey.D, ConsoleModifiers.Control)));
    }

    [Fact]
    public void EditingUsesGraphemeBoundariesAndDoesNotSplitEmoji()
    {
        var editor = new EditorBuffer();
        editor.SetText("a😀e\u0301b");
        editor.Handle(Key('\0', ConsoleKey.LeftArrow));
        editor.Handle(Key('\0', ConsoleKey.Backspace));
        Assert.Equal("a😀b", editor.Text);
        editor.Handle(Key('\0', ConsoleKey.Backspace));
        Assert.Equal("ab", editor.Text);
    }

    [Fact]
    public void KeyboardSelectionMovesByGraphemeAndPlainMovementCollapsesIt()
    {
        var editor = new EditorBuffer();
        editor.SetText("a😀e\u0301b");

        Assert.Equal(EditorAction.Render, editor.Handle(Key('\0', ConsoleKey.LeftArrow, ConsoleModifiers.Shift)));
        Assert.Equal("b", editor.SelectedText);
        editor.Handle(Key('\0', ConsoleKey.LeftArrow, ConsoleModifiers.Shift));
        editor.Handle(Key('\0', ConsoleKey.LeftArrow, ConsoleModifiers.Shift));
        Assert.Equal("😀e\u0301b", editor.SelectedText);
        Assert.Equal(EditorAction.Render, editor.Handle(Key('\0', ConsoleKey.LeftArrow)));
        Assert.Null(editor.SelectedText);
        Assert.Equal(1, editor.Cursor);
    }

    [Fact]
    public void TypingAndDeletingReplaceKeyboardSelectionAndUndoRestoresIt()
    {
        var editor = new EditorBuffer();
        editor.SetText("hello", 2);
        editor.Handle(Key('\0', ConsoleKey.RightArrow, ConsoleModifiers.Shift));
        editor.Handle(Key('\0', ConsoleKey.RightArrow, ConsoleModifiers.Shift));

        Assert.Equal("ll", editor.SelectedText);
        Assert.Equal(EditorAction.Render, editor.Handle(Key('X', ConsoleKey.X)));
        Assert.Equal("heXo", editor.Text);
        Assert.Null(editor.SelectedText);
        Assert.Equal(EditorAction.Render, editor.Handle(Key('-', ConsoleKey.OemMinus, ConsoleModifiers.Control)));
        Assert.Equal("hello", editor.Text);
        Assert.Equal(4, editor.Cursor);
        Assert.Equal("ll", editor.SelectedText);

        editor.SetCursor(2);
        editor.Handle(Key('\0', ConsoleKey.RightArrow, ConsoleModifiers.Shift));
        editor.Handle(Key('\0', ConsoleKey.RightArrow, ConsoleModifiers.Shift));
        Assert.Equal(EditorAction.Render, editor.Handle(Key('\0', ConsoleKey.Backspace)));
        Assert.Equal("heo", editor.Text);
        editor.Handle(Key('-', ConsoleKey.OemMinus, ConsoleModifiers.Control));
        Assert.Equal("hello", editor.Text);
    }

    [Fact]
    public void ConsecutiveWordAndLineKillsAccumulateForYank()
    {
        var editor = new EditorBuffer();
        editor.SetText("one two three");
        editor.Handle(Key('\0', ConsoleKey.W, ConsoleModifiers.Control));
        editor.Handle(Key('\0', ConsoleKey.W, ConsoleModifiers.Control));
        editor.Handle(Key('\0', ConsoleKey.W, ConsoleModifiers.Control));
        Assert.Equal("", editor.Text);
        Assert.Equal(EditorAction.Render, editor.Handle(Key('\0', ConsoleKey.Y, ConsoleModifiers.Control)));
        Assert.Equal("one two three", editor.Text);

        editor.SetText("hello world");
        editor.Handle(Key('\0', ConsoleKey.A, ConsoleModifiers.Control));
        editor.Handle(Key('\0', ConsoleKey.K, ConsoleModifiers.Control));
        Assert.Equal("", editor.Text);
        editor.Handle(Key('\0', ConsoleKey.Y, ConsoleModifiers.Control));
        Assert.Equal("hello world", editor.Text);
    }

    [Fact]
    public void YankPopRotatesKillRingAndStopsAfterAnotherEditorAction()
    {
        var editor = new EditorBuffer();
        foreach (var text in new[] { "first", "second", "third" })
        {
            editor.SetText(text);
            editor.Handle(Key('\0', ConsoleKey.W, ConsoleModifiers.Control));
        }
        editor.SetText("");

        editor.Handle(Key('\0', ConsoleKey.Y, ConsoleModifiers.Control));
        Assert.Equal("third", editor.Text);
        editor.Handle(Key('y', ConsoleKey.Y, ConsoleModifiers.Alt));
        Assert.Equal("second", editor.Text);
        editor.Handle(Key('y', ConsoleKey.Y, ConsoleModifiers.Alt));
        Assert.Equal("first", editor.Text);
        editor.Handle(Key('y', ConsoleKey.Y, ConsoleModifiers.Alt));
        Assert.Equal("third", editor.Text);

        editor.Handle(Key('x', ConsoleKey.X));
        Assert.Equal(EditorAction.None, editor.Handle(Key('y', ConsoleKey.Y, ConsoleModifiers.Alt)));
        Assert.Equal("thirdx", editor.Text);
    }

    [Fact]
    public void BackwardLineKillsAccumulateNewlinesAndWordMovementStopsAtWordBoundaries()
    {
        var editor = new EditorBuffer();
        editor.SetText("A\nB\nC");
        for (var index = 0; index < 5; index++) editor.Handle(Key('\0', ConsoleKey.U, ConsoleModifiers.Control));
        Assert.Equal("", editor.Text);
        editor.Handle(Key('\0', ConsoleKey.Y, ConsoleModifiers.Control));
        Assert.Equal("A\nB\nC", editor.Text);

        editor.SetText("hello world");
        editor.Handle(Key('\0', ConsoleKey.A, ConsoleModifiers.Control));
        editor.Handle(Key('\0', ConsoleKey.RightArrow, ConsoleModifiers.Control));
        Assert.Equal(5, editor.Cursor);
        editor.Handle(Key('\0', ConsoleKey.RightArrow, ConsoleModifiers.Control));
        Assert.Equal("hello world".Length, editor.Cursor);
    }

    [Fact]
    public void ForwardWordKillsAccumulateAndUndoRevertsAYank()
    {
        var editor = new EditorBuffer();
        editor.SetText("hello world test");
        editor.Handle(Key('\0', ConsoleKey.A, ConsoleModifiers.Control));
        editor.Handle(Key('d', ConsoleKey.D, ConsoleModifiers.Alt));
        editor.Handle(Key('d', ConsoleKey.D, ConsoleModifiers.Alt));
        editor.Handle(Key('d', ConsoleKey.D, ConsoleModifiers.Alt));
        Assert.Equal("", editor.Text);
        editor.Handle(Key('\0', ConsoleKey.Y, ConsoleModifiers.Control));
        Assert.Equal("hello world test", editor.Text);
        Assert.Equal(EditorAction.Render, editor.Handle(Key('-', ConsoleKey.OemMinus, ConsoleModifiers.Control)));
        Assert.Equal("", editor.Text);
    }

    [Fact]
    public void AltEnterInsertsLineRatherThanSubmitting()
    {
        var editor = new EditorBuffer();
        editor.SetText("one");
        Assert.Equal(EditorAction.Render, editor.Handle(Key('\n', ConsoleKey.Enter, ConsoleModifiers.Alt)));
        Assert.Equal("one\n", editor.Text);
        Assert.Empty(editor.History);
    }

    [Fact]
    public void PromptHistoryTrimsConsecutiveDuplicatesAndKeepsTheNewestHundredEntries()
    {
        var editor = new EditorBuffer();
        Submit(" same ");
        Submit("same");
        for (var index = 0; index < 105; index++) Submit($"prompt {index}");

        Assert.Equal(100, editor.History.Count);
        Assert.Equal("prompt 5", editor.History[0]);
        Assert.Equal("prompt 104", editor.History[^1]);

        void Submit(string text)
        {
            editor.SetText(text);
            Assert.True(editor.TrySubmit(out _));
            editor.Clear();
        }
    }

    [Fact]
    public void UpAndDownMoveBetweenLinesAndHistoryRestoresTheSavedCursor()
    {
        var editor = new EditorBuffer();
        editor.SetText("older prompt");
        Assert.True(editor.TrySubmit(out _));
        editor.Clear();
        editor.SetText("one\ntwo", 7);

        Assert.Equal(EditorAction.Render, editor.Handle(Key('\0', ConsoleKey.UpArrow)));
        Assert.Equal(3, editor.Cursor);
        Assert.Equal(EditorAction.Render, editor.Handle(Key('\0', ConsoleKey.DownArrow)));
        Assert.Equal(7, editor.Cursor);
        editor.Handle(Key('\0', ConsoleKey.Home));
        Assert.Equal(4, editor.Cursor);
        editor.Handle(Key('\0', ConsoleKey.UpArrow));
        Assert.Equal(0, editor.Cursor);
        editor.Handle(Key('\0', ConsoleKey.UpArrow));
        Assert.Equal("older prompt", editor.Text);
        Assert.Equal(0, editor.Cursor);
        editor.Handle(Key('\0', ConsoleKey.DownArrow));

        Assert.Equal("one\ntwo", editor.Text);
        Assert.Equal(0, editor.Cursor);
    }

    [Fact]
    public void UndoGroupsTypedWordsAndKeepsWhitespaceAndNewlinesAsBoundaries()
    {
        var editor = new EditorBuffer();
        Type("hello world");
        Assert.Equal(EditorAction.Render, editor.Handle(Key('-', ConsoleKey.OemMinus, ConsoleModifiers.Control)));
        Assert.Equal("hello", editor.Text);
        editor.Handle(Key('-', ConsoleKey.OemMinus, ConsoleModifiers.Control));
        Assert.Equal("", editor.Text);

        Type("hello  ");
        editor.Handle(Key('-', ConsoleKey.OemMinus, ConsoleModifiers.Control));
        Assert.Equal("hello ", editor.Text);
        editor.Handle(Key('-', ConsoleKey.OemMinus, ConsoleModifiers.Control));
        Assert.Equal("hello", editor.Text);
        editor.Handle(Key('-', ConsoleKey.OemMinus, ConsoleModifiers.Control));
        Assert.Equal("", editor.Text);

        Type("hello\nworld");
        editor.Handle(Key('-', ConsoleKey.OemMinus, ConsoleModifiers.Control));
        Assert.Equal("hello\n", editor.Text);
        editor.Handle(Key('-', ConsoleKey.OemMinus, ConsoleModifiers.Control));
        Assert.Equal("hello", editor.Text);
        editor.Handle(Key('-', ConsoleKey.OemMinus, ConsoleModifiers.Control));
        Assert.Equal("", editor.Text);

        void Type(string text)
        {
            foreach (var character in text)
                editor.Handle(character == '\n'
                    ? Key('\n', ConsoleKey.Enter, ConsoleModifiers.Alt)
                    : Key(character, ConsoleKey.NoName));
        }
    }

    [Fact]
    public void UndoRestoresDeletionAndSubmitClearsTheUndoStack()
    {
        var editor = new EditorBuffer();
        editor.SetText("hello world");
        editor.SetCursor(editor.Text.Length);
        Assert.Equal(EditorAction.Render, editor.Handle(Key('w', ConsoleKey.W, ConsoleModifiers.Control)));
        Assert.Equal("hello ", editor.Text);
        editor.Handle(Key('-', ConsoleKey.OemMinus, ConsoleModifiers.Control));
        Assert.Equal("hello world", editor.Text);
        Assert.Equal(editor.Text.Length, editor.Cursor);

        Assert.True(editor.TrySubmit(out _));
        editor.Clear();
        Assert.Equal(EditorAction.None, editor.Handle(Key('-', ConsoleKey.OemMinus, ConsoleModifiers.Control)));
        Assert.Equal("", editor.Text);
    }
}
