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
    public void AltEnterInsertsLineRatherThanSubmitting()
    {
        var editor = new EditorBuffer();
        editor.SetText("one");
        Assert.Equal(EditorAction.Render, editor.Handle(Key('\n', ConsoleKey.Enter, ConsoleModifiers.Alt)));
        Assert.Equal("one\n", editor.Text);
        Assert.Empty(editor.History);
    }
}
