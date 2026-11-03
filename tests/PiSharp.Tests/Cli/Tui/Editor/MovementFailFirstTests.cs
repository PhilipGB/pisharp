using System.Text;
using PiSharp.Cli.Tui;

namespace PiSharp.Tests;

/// <summary>Regressions for movement/history defects confirmed against the pre-milestone baseline.</summary>
public sealed class MovementFailFirstTests
{
    [Fact]
    public void SetTextExpandsTabsLikePi()
    {
        var editor = new EditorBuffer();
        editor.SetText("a\tb");
        Assert.Equal("a    b", editor.Text);
        Assert.Equal(6, editor.Cursor);
    }

    [Fact]
    public void CtrlBracketAndCharacterEnterJumpMode()
    {
        var editor = new EditorBuffer();
        editor.SetText("a,b,c");
        editor.SetCursor(0);
        editor.Handle(Key(ConsoleKey.Oem6, control: true));
        editor.Handle(Key(ConsoleKey.OemComma, ','));
        Assert.Equal("a,b,c", editor.Text);
        Assert.Equal(1, editor.Cursor);
    }

    [Fact]
    public void PageDownMovesEditorCursorByPiFormula()
    {
        var editor = new EditorBuffer();
        editor.SetText(string.Join("\n", Enumerable.Range(0, 20).Select(index => $"line {index}")));
        editor.SetCursor(0);
        editor.Handle(Key(ConsoleKey.PageDown, control: true));
        Assert.Equal(7, editor.Text[..editor.Cursor].Count(character => character == '\n'));
    }

    [Fact]
    public void EditorWrapsAtWordBoundariesLikePi()
    {
        var frame = EditorViewport.Layout("more text", 9, 9, 5);
        Assert.Equal(["more ", "text"], frame.ContentRows);
    }

    [Fact]
    public void CtrlFRemainsCursorRightInFullscreen()
    {
        var keymap = new EditorKeymap();
        Assert.True(keymap.Matches("tui.editor.cursorRight", Key(ConsoleKey.F, control: true)));
        Assert.False(keymap.Matches("tui.altScreen.search", Key(ConsoleKey.F, control: true)));
    }

    [Fact]
    public void LegacyF9Decodes()
    {
        var reader = new TerminalInput(new MemoryStream(Encoding.UTF8.GetBytes("\u001b[20~")));
        Assert.Equal(ConsoleKey.F9, reader.Read().Key!.Value.Key);
    }

    [Fact]
    public void CtrlBracketKeymapEntersJumpMode()
    {
        var keymap = new EditorKeymap();
        Assert.True(keymap.Matches("tui.editor.jumpForward", Key(ConsoleKey.Oem6, control: true)));
        Assert.True(keymap.Matches("tui.editor.jumpBackward",
            Key(ConsoleKey.Oem6, control: true, alt: true)));
    }

    private static ConsoleKeyInfo Key(ConsoleKey key, char keyChar = '\0', bool alt = false, bool control = false) =>
        new(keyChar, key, false, alt, control);
}
