using PiSharp.Cli.Tui;

namespace PiSharp.Tests;

public sealed class EditorKeymapTests
{
    [Fact]
    public void UserBindingsReplaceDefaultsAndAnEmptyListDisablesAnAction()
    {
        var directory = Path.Combine(Path.GetTempPath(), "pisharp-keymap-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            File.WriteAllText(Path.Combine(directory, "keybindings.json"), """
                {
                  "tui.input.submit": "ctrl+x",
                  "tui.input.newLine": [],
                  "tui.editor.cursorLeft": ["ctrl+h", "alt+left"]
                }
                """);
            var keymap = new EditorKeymap(directory);

            Assert.False(keymap.Matches("tui.input.submit", Key(ConsoleKey.Enter)));
            Assert.True(keymap.Matches("tui.input.submit", Key(ConsoleKey.X, ConsoleModifiers.Control)));
            Assert.False(keymap.Matches("tui.input.newLine", Key(ConsoleKey.J, ConsoleModifiers.Control)));
            Assert.True(keymap.Matches("tui.editor.cursorLeft", Key(ConsoleKey.H, ConsoleModifiers.Control)));
            Assert.True(keymap.Matches("tui.editor.cursorLeft", Key(ConsoleKey.LeftArrow, ConsoleModifiers.Alt)));
            Assert.Contains("disabled", keymap.FormatHotkeys());
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public void EditorUsesConfiguredSubmitAndCursorActions()
    {
        var directory = Path.Combine(Path.GetTempPath(), "pisharp-keymap-editor-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            File.WriteAllText(Path.Combine(directory, "keybindings.json"), """
                { "tui.input.submit": "ctrl+x", "tui.editor.cursorLeft": "ctrl+h" }
                """);
            var editor = new EditorBuffer(new EditorKeymap(directory));
            editor.SetText("abc");

            Assert.Equal(EditorAction.Render, editor.Handle(Key(ConsoleKey.H, ConsoleModifiers.Control)));
            Assert.Equal(2, editor.Cursor);
            Assert.Equal(EditorAction.Submit, editor.Handle(Key(ConsoleKey.X, ConsoleModifiers.Control)));
            Assert.Equal("abc", Assert.Single(editor.History));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public void InvalidConfigFallsBackToDefaultBindings()
    {
        var directory = Path.Combine(Path.GetTempPath(), "pisharp-keymap-invalid-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            File.WriteAllText(Path.Combine(directory, "keybindings.json"), "not json");
            var keymap = new EditorKeymap(directory);
            Assert.True(keymap.Matches("tui.input.submit", Key(ConsoleKey.Enter)));
            Assert.True(keymap.Matches("tui.editor.cursorWordLeft", Key(ConsoleKey.B, ConsoleModifiers.Alt)));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public void ReloadAppliesChangedBindingsWithoutReplacingTheEditor()
    {
        var directory = Path.Combine(Path.GetTempPath(), "pisharp-keymap-reload-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, "keybindings.json");
            File.WriteAllText(path, "{ \"tui.input.submit\": \"ctrl+x\" }");
            var keymap = new EditorKeymap(directory);
            File.WriteAllText(path, "{ \"tui.input.submit\": \"ctrl+y\" }");

            keymap.Reload();

            Assert.False(keymap.Matches("tui.input.submit", Key(ConsoleKey.X, ConsoleModifiers.Control)));
            Assert.True(keymap.Matches("tui.input.submit", Key(ConsoleKey.Y, ConsoleModifiers.Control)));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public void ShiftedAsciiSymbolsMatchConfiguredKeyIds()
    {
        var directory = Path.Combine(Path.GetTempPath(), "pisharp-keymap-symbol-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            File.WriteAllText(Path.Combine(directory, "keybindings.json"),
                "{ \"tui.input.submit\": \"shift+?\", \"app.clear\": \"f12\" }");
            var keymap = new EditorKeymap(directory);
            var reader = new TerminalInput(new MemoryStream("?"u8.ToArray()));

            Assert.True(reader.TryRead(0, out var input));
            Assert.True(keymap.Matches("tui.input.submit", input.Key!.Value));
            Assert.True(keymap.Matches("app.clear", Key(ConsoleKey.F12)));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    private static ConsoleKeyInfo Key(ConsoleKey key, ConsoleModifiers modifiers = 0) => new('\0', key,
        modifiers.HasFlag(ConsoleModifiers.Shift), modifiers.HasFlag(ConsoleModifiers.Alt),
        modifiers.HasFlag(ConsoleModifiers.Control));
}
