using System.Text;
using PiSharp.Cli.Tui;

namespace PiSharp.Tests;

public sealed class EditorMovementBindingTests
{
    [Fact]
    public void ConfiguredMovementBindingsReplaceDisableAndFallBackLikePi()
    {
        var directory = Path.Combine(Path.GetTempPath(), "pisharp-movement-keymap-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, "keybindings.json");
            File.WriteAllText(path, """
                {
                  "tui.editor.cursorLeft": "f6",
                  "tui.editor.cursorRight": [],
                  "tui.editor.cursorUp": 42,
                  "unknown.action": "ctrl+x"
                }
                """);
            var keymap = new EditorKeymap(directory);
            Assert.True(keymap.Matches("tui.editor.cursorLeft", Key(ConsoleKey.F6)));
            Assert.False(keymap.Matches("tui.editor.cursorLeft", Key(ConsoleKey.LeftArrow)));
            Assert.False(keymap.Matches("tui.editor.cursorRight", Key(ConsoleKey.RightArrow)));
            Assert.True(keymap.Matches("tui.editor.cursorUp", Key(ConsoleKey.UpArrow)));
            Assert.False(keymap.Matches("tui.editor.cursorUp", Key(ConsoleKey.F5)));

            var editor = new EditorBuffer(keymap);
            editor.SetText("abc");
            editor.SetCursor(2);
            editor.Handle(Key(ConsoleKey.LeftArrow));
            Assert.Equal(2, editor.Cursor);
            editor.Handle(Key(ConsoleKey.F6));
            Assert.Equal(1, editor.Cursor);
            editor.Handle(Key(ConsoleKey.RightArrow));
            Assert.Equal(1, editor.Cursor);

            File.WriteAllText(path, "{");
            var fallback = new EditorKeymap(directory);
            Assert.True(fallback.Matches("tui.editor.cursorLeft", Key(ConsoleKey.LeftArrow)));
            Assert.True(fallback.Matches("tui.editor.cursorRight", Key(ConsoleKey.RightArrow)));
            Assert.True(fallback.Matches("tui.editor.cursorUp", Key(ConsoleKey.UpArrow)));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public void CtrlShiftRightRemainsSelectionRatherThanWordMovement()
    {
        var keymap = new EditorKeymap();
        var key = new ConsoleKeyInfo('\0', ConsoleKey.RightArrow, true, false, true);
        Assert.False(keymap.Matches("tui.editor.cursorWordRight", key));
        Assert.True(keymap.Matches("tui.editor.selectWordRight", key));
    }

    [Fact]
    public void LegacyAndKittyMovementInputDecodesToPiKeys()
    {
        var reader = new TerminalInput(new MemoryStream(Encoding.UTF8.GetBytes(
            "\u001b[17~\u001b[15~\u001b[20~\u001d\u001b\u001d\u001b[5;5~\u001b[6;5~\u001b[106;3u\u001b[57421;5u")));
        Assert.Equal(ConsoleKey.F6, reader.Read().Key!.Value.Key);
        Assert.Equal(ConsoleKey.F5, reader.Read().Key!.Value.Key);
        Assert.Equal(ConsoleKey.F9, reader.Read().Key!.Value.Key);

        var ctrlBracket = reader.Read().Key!.Value;
        Assert.Equal((ConsoleKey.Oem6, ConsoleModifiers.Control), (ctrlBracket.Key, ctrlBracket.Modifiers));
        Assert.True(new EditorKeymap().Matches("tui.editor.jumpForward", ctrlBracket));
        var ctrlAltBracket = reader.Read().Key!.Value;
        Assert.Equal((ConsoleKey.Oem6, ConsoleModifiers.Control | ConsoleModifiers.Alt),
            (ctrlAltBracket.Key, ctrlAltBracket.Modifiers));
        Assert.True(new EditorKeymap().Matches("tui.editor.jumpBackward", ctrlAltBracket));

        var ctrlPageUp = reader.Read().Key!.Value;
        Assert.Equal((ConsoleKey.PageUp, ConsoleModifiers.Control), (ctrlPageUp.Key, ctrlPageUp.Modifiers));
        Assert.True(new EditorKeymap().Matches("tui.editor.pageUp", ctrlPageUp));
        var ctrlPageDown = reader.Read().Key!.Value;
        Assert.Equal((ConsoleKey.PageDown, ConsoleModifiers.Control), (ctrlPageDown.Key, ctrlPageDown.Modifiers));
        Assert.True(new EditorKeymap().Matches("tui.editor.pageDown", ctrlPageDown));

        var kittyAltJ = reader.Read().Key!.Value;
        Assert.Equal((ConsoleKey.J, ConsoleModifiers.Alt), (kittyAltJ.Key, kittyAltJ.Modifiers));
        var kittyCtrlPageUp = reader.Read().Key!.Value;
        Assert.Equal((ConsoleKey.PageUp, ConsoleModifiers.Control),
            (kittyCtrlPageUp.Key, kittyCtrlPageUp.Modifiers));
    }

    private static ConsoleKeyInfo Key(ConsoleKey key, ConsoleModifiers modifiers = 0) =>
        new('\0', key, modifiers.HasFlag(ConsoleModifiers.Shift), modifiers.HasFlag(ConsoleModifiers.Alt),
            modifiers.HasFlag(ConsoleModifiers.Control));
}
