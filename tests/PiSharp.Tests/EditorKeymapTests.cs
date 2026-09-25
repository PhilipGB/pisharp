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
                  "tui.editor.cursorLeft": ["ctrl+h", "alt+left"],
                  "app.tools.expand": "ctrl+y"
                }
                """);
            var keymap = new EditorKeymap(directory);

            Assert.False(keymap.Matches("tui.input.submit", Key(ConsoleKey.Enter)));
            Assert.True(keymap.Matches("tui.input.submit", Key(ConsoleKey.X, ConsoleModifiers.Control)));
            Assert.False(keymap.Matches("tui.input.newLine", Key(ConsoleKey.J, ConsoleModifiers.Control)));
            Assert.True(keymap.Matches("tui.editor.cursorLeft", Key(ConsoleKey.H, ConsoleModifiers.Control)));
            Assert.True(keymap.Matches("tui.editor.cursorLeft", Key(ConsoleKey.LeftArrow, ConsoleModifiers.Alt)));
            Assert.False(keymap.Matches("app.tools.expand", Key(ConsoleKey.O, ConsoleModifiers.Control)));
            Assert.True(keymap.Matches("app.tools.expand", Key(ConsoleKey.Y, ConsoleModifiers.Control)));
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

    [Fact]
    public void ActiveScreenSearchAndPagingBindingsDecodeFromTerminalInput()
    {
        var keymap = new EditorKeymap();
        var searchInput = new TerminalInput(new MemoryStream([6]));
        var toolOutputInput = new TerminalInput(new MemoryStream([15]));
        var pageInput = new TerminalInput(new MemoryStream("\u001b[5~"u8.ToArray()));

        Assert.True(searchInput.TryRead(0, out var search));
        Assert.True(toolOutputInput.TryRead(0, out var toolOutput));
        Assert.True(pageInput.TryRead(0, out var page));
        Assert.True(keymap.Matches("tui.altScreen.search", search.Key!.Value));
        Assert.True(keymap.Matches("app.tools.expand", toolOutput.Key!.Value));
        Assert.True(keymap.Matches("tui.altScreen.pageUp", page.Key!.Value));
    }

    [Fact]
    public async Task ApplicationCycleShortcutsDispatchAndPreserveTheEditorDraft()
    {
        var editor = new TerminalEditor();
        editor.Prefill("keep this draft");
        var actions = new List<string>();

        Assert.True(await editor.HandleApplicationShortcutAsync(Key(ConsoleKey.P, ConsoleModifiers.Control), action =>
        {
            actions.Add(action);
            return Task.CompletedTask;
        }));
        Assert.True(await editor.HandleApplicationShortcutAsync(Key(ConsoleKey.Tab, ConsoleModifiers.Shift), action =>
        {
            actions.Add(action);
            return Task.CompletedTask;
        }));
        var windowsBindings = OperatingSystem.IsWindows() || OperatingSystem.IsLinux() &&
            (Environment.GetEnvironmentVariable("WSL_DISTRO_NAME") is not null ||
             Environment.GetEnvironmentVariable("WSL_INTEROP") is not null);
        var backwardKey = windowsBindings
            ? Key(ConsoleKey.P, ConsoleModifiers.Alt)
            : Key(ConsoleKey.P, ConsoleModifiers.Control | ConsoleModifiers.Shift);
        Assert.True(await editor.HandleApplicationShortcutAsync(backwardKey, action =>
        {
            actions.Add(action);
            return Task.CompletedTask;
        }));

        Assert.Equal(["app.model.cycleForward", "app.thinking.cycle", "app.model.cycleBackward"], actions);
        Assert.Equal("keep this draft", editor.Draft);
        Assert.Equal("app.model.cycleBackward", new EditorKeymap().MatchIdleApplicationAction(
            Key(ConsoleKey.P, ConsoleModifiers.Alt)));
    }

    [Fact]
    public async Task ModelSelectorShortcutIsNamedAndPreservesTheEditorDraft()
    {
        var editor = new TerminalEditor();
        editor.Prefill("keep this draft");
        string? dispatched = null;

        Assert.True(await editor.HandleApplicationShortcutAsync(Key(ConsoleKey.L, ConsoleModifiers.Control), action =>
        {
            dispatched = action;
            return Task.CompletedTask;
        }));

        Assert.Equal("app.model.select", dispatched);
        Assert.Equal("keep this draft", editor.Draft);
        Assert.Contains("Open model selector (app.model.select)", editor.Hotkeys);
    }

    [Fact]
    public async Task SettingsPickerShortcutCanBeConfiguredAndPreservesTheEditorDraft()
    {
        var directory = Path.Combine(Path.GetTempPath(), "pisharp-keymap-settings-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            File.WriteAllText(Path.Combine(directory, "keybindings.json"), "{ \"app.settings.open\": \"ctrl+shift+s\" }");
            var editor = new TerminalEditor(agentDirectory: directory);
            editor.Prefill("keep this draft");
            string? dispatched = null;

            Assert.True(await editor.HandleApplicationShortcutAsync(Key(ConsoleKey.S, ConsoleModifiers.Control | ConsoleModifiers.Shift), action =>
            {
                dispatched = action;
                return Task.CompletedTask;
            }));

            Assert.Equal("app.settings.open", dispatched);
            Assert.Equal("keep this draft", editor.Draft);
            Assert.Contains("Open settings (app.settings.open)", editor.Hotkeys);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public async Task ExternalEditorShortcutCanBeConfiguredAndPreservesTheEditorDraft()
    {
        var directory = Path.Combine(Path.GetTempPath(), "pisharp-keymap-external-editor-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            File.WriteAllText(Path.Combine(directory, "keybindings.json"), "{ \"app.editor.external\": \"ctrl+shift+e\" }");
            var editor = new TerminalEditor(agentDirectory: directory);
            editor.Prefill("keep this draft");
            string? dispatched = null;

            Assert.True(await editor.HandleApplicationShortcutAsync(Key(ConsoleKey.E, ConsoleModifiers.Control | ConsoleModifiers.Shift), action =>
            {
                dispatched = action;
                return Task.CompletedTask;
            }));

            Assert.Equal("app.editor.external", dispatched);
            Assert.Equal("keep this draft", editor.Draft);
            Assert.Contains("Open external editor (app.editor.external)", editor.Hotkeys);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public async Task SessionSelectorShortcutCanBeConfiguredAndPreservesTheEditorDraft()
    {
        var directory = Path.Combine(Path.GetTempPath(), "pisharp-keymap-session-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            File.WriteAllText(Path.Combine(directory, "keybindings.json"),
                "{ \"app.session.resume\": \"ctrl+r\", \"app.session.fork\": \"ctrl+shift+g\" }");
            var editor = new TerminalEditor(agentDirectory: directory);
            editor.Prefill("keep this draft");
            var dispatched = new List<string>();

            Assert.True(await editor.HandleApplicationShortcutAsync(Key(ConsoleKey.R, ConsoleModifiers.Control), action =>
            {
                dispatched.Add(action);
                return Task.CompletedTask;
            }));
            Assert.True(await editor.HandleApplicationShortcutAsync(Key(ConsoleKey.G, ConsoleModifiers.Control | ConsoleModifiers.Shift), action =>
            {
                dispatched.Add(action);
                return Task.CompletedTask;
            }));

            Assert.Equal(["app.session.resume", "app.session.fork"], dispatched);
            Assert.Equal("keep this draft", editor.Draft);
            Assert.Contains("Open session selector (app.session.resume)", editor.Hotkeys);
            Assert.Contains("Open fork selector (app.session.fork)", editor.Hotkeys);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    private static ConsoleKeyInfo Key(ConsoleKey key, ConsoleModifiers modifiers = 0) => new('\0', key,
        modifiers.HasFlag(ConsoleModifiers.Shift), modifiers.HasFlag(ConsoleModifiers.Alt),
        modifiers.HasFlag(ConsoleModifiers.Control));
}
