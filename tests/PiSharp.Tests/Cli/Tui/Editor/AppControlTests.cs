using System.Text.Json;
using PiSharp.Cli;
using PiSharp.Cli.Tui;

namespace PiSharp.Tests;

public sealed class AppControlTests
{
    private static ConsoleKeyInfo Key(ConsoleKey key, char keyChar = '\0', bool shift = false, bool alt = false,
        bool control = false) => new(keyChar, key, shift, alt, control);

    private static TerminalInputEvent Event(ConsoleKeyInfo key) => new(key, null);

    [Theory]
    [InlineData("draft")]
    [InlineData("  needs work  ")]
    public async Task IdleEscapeLeavesNonemptyDraftAndDispatchesNothing(string draft)
    {
        var editor = new TerminalEditor();
        editor.Prefill(draft);
        var actions = new List<string>();

        Assert.True(await editor.HandleIdleEscapeAsync(action =>
        {
            actions.Add(action);
            return Task.CompletedTask;
        }));

        Assert.Equal(draft, editor.Draft);
        Assert.Empty(actions);
    }

    [Theory]
    [InlineData("!ls -la")]
    [InlineData("  !echo hi")]
    [InlineData("!!private")]
    public async Task IdleEscapeExitsBashEntryModeByClearingTheDraft(string draft)
    {
        var editor = new TerminalEditor();
        editor.Prefill(draft);
        var actions = new List<string>();

        Assert.True(await editor.HandleIdleEscapeAsync(action =>
        {
            actions.Add(action);
            return Task.CompletedTask;
        }));

        Assert.Equal("", editor.Draft);
        Assert.Empty(actions);
    }

    [Theory]
    [InlineData("tree", "app.session.tree")]
    [InlineData("fork", "app.session.fork")]
    public async Task DoubleEscapeOpensTheConfiguredSelectorWithinTheWindow(string setting, string expectedAction)
    {
        var editor = new TerminalEditor();
        editor.SetDoubleEscapeActionProvider(() => setting);
        var now = 10_000L;
        editor.SetDoublePressClock(() => now);
        var actions = new List<string>();

        await editor.HandleIdleEscapeAsync(Dispatch);
        Assert.Empty(actions);
        now += TerminalEditor.DoublePressWindowMilliseconds - 1;
        await editor.HandleIdleEscapeAsync(Dispatch);

        Assert.Equal([expectedAction], actions);

        Task Dispatch(string action)
        {
            actions.Add(action);
            return Task.CompletedTask;
        }
    }

    [Fact]
    public async Task DoubleEscapeOutsideTheWindowDoesNotOpenASelector()
    {
        var editor = new TerminalEditor();
        editor.SetDoubleEscapeActionProvider(() => "tree");
        var now = 1_000L;
        editor.SetDoublePressClock(() => now);
        var actions = new List<string>();

        await editor.HandleIdleEscapeAsync(Dispatch);
        now += TerminalEditor.DoublePressWindowMilliseconds;
        await editor.HandleIdleEscapeAsync(Dispatch);
        Assert.Empty(actions);
        now += 1;
        await editor.HandleIdleEscapeAsync(Dispatch);

        Assert.Equal(["app.session.tree"], actions);

        Task Dispatch(string action)
        {
            actions.Add(action);
            return Task.CompletedTask;
        }
    }

    [Fact]
    public void LegacyDoubleEscapeDecodesAsTwoInterrupts()
    {
        var input = new TerminalInput(new MemoryStream("\u001b\u001b"u8.ToArray()));
        Assert.True(input.TryRead(0, out var first));
        Assert.Equal(ConsoleKey.Escape, first.Key?.Key);
        Assert.False(first.Key?.Modifiers.HasFlag(ConsoleModifiers.Alt));
        Assert.True(input.TryRead(0, out var second));
        Assert.Equal(ConsoleKey.Escape, second.Key?.Key);
        Assert.False(second.Key?.Modifiers.HasFlag(ConsoleModifiers.Alt));
        Assert.False(input.TryRead(0, out _));
    }

    [Fact]
    public void EscapeFollowedByAnArrowSequenceDecodesAsEscapeThenArrow()
    {
        var input = new TerminalInput(new MemoryStream("\u001b\u001b[D"u8.ToArray()));
        Assert.True(input.TryRead(0, out var first));
        Assert.Equal(ConsoleKey.Escape, first.Key?.Key);
        Assert.False(first.Key?.Modifiers.HasFlag(ConsoleModifiers.Alt));
        Assert.True(input.TryRead(0, out var second));
        Assert.Equal(ConsoleKey.LeftArrow, second.Key?.Key);
    }

    [Fact]
    public async Task DoubleEscapeDrivesTheConfiguredSelectorThroughTheEditorLoop()
    {
        var reader = new TerminalInput(new MemoryStream("\u001b\u001b"u8.ToArray()));
        var editor = new TerminalEditor();
        editor.SetDoubleEscapeActionProvider(() => "fork");
        var actions = new List<string>();
        long now = 0;
        editor.SetDoublePressClock(() => now);
        Assert.True(reader.TryRead(0, out var first));
        Assert.True(reader.TryRead(0, out var second));
        Assert.True(await editor.HandleIdleEscapeAsync(action =>
        {
            actions.Add(action);
            return Task.CompletedTask;
        }));
        now += 1;
        Assert.True(await editor.HandleIdleEscapeAsync(action =>
        {
            actions.Add(action);
            return Task.CompletedTask;
        }));
        Assert.Equal(["app.session.fork"], actions);
    }

    [Fact]
    public async Task ActiveStreamingDoubleCtrlCClearsThenRequestsShutdown()
    {
        var editor = new TerminalEditor();
        var now = 1000L;
        editor.SetDoublePressClock(() => now);
        var shutdowns = 0;
        Task<bool> Queue(string text, bool followUp, CancellationToken _) => Task.FromResult(true);
        IReadOnlyList<string> Clear() => [];
        var first = await editor.HandleActiveInputAsync(Event(Key(ConsoleKey.C, control: true)), Queue, Clear,
            () => { }, requestShutdown: () => shutdowns++);
        Assert.True(first);
        Assert.Equal(0, shutdowns);
        now += 499;
        var second = await editor.HandleActiveInputAsync(Event(Key(ConsoleKey.C, control: true)), Queue, Clear,
            () => { }, requestShutdown: () => shutdowns++);
        Assert.False(second);
        Assert.Equal(1, shutdowns);
    }

    [Fact]
    public async Task ActiveStreamingCtrlDExitsOnlyWhenTheEditorIsEmpty()
    {
        var editor = new TerminalEditor();
        var shutdowns = 0;
        Task<bool> Queue(string text, bool followUp, CancellationToken _) => Task.FromResult(true);
        IReadOnlyList<string> Clear() => [];
        editor.Prefill("draft");
        Assert.True(await editor.HandleActiveInputAsync(Event(Key(ConsoleKey.D, control: true)), Queue, Clear,
            () => { }, requestShutdown: () => shutdowns++));
        Assert.Equal("draft", editor.Draft);
        Assert.Equal(0, shutdowns);
        editor.SetDraft("");
        Assert.False(await editor.HandleActiveInputAsync(Event(Key(ConsoleKey.D, control: true)), Queue, Clear,
            () => { }, requestShutdown: () => shutdowns++));
        Assert.Equal(1, shutdowns);
    }

    [Fact]
    public void LegacyAndKittyEscapeDecodeToTheSameApplicationAction()
    {
        var legacy = new TerminalInput(new MemoryStream("\u001b"u8.ToArray()));
        var kitty = new TerminalInput(new MemoryStream("\u001b[27u"u8.ToArray()));
        Assert.True(legacy.TryRead(0, out var legacyEscape));
        Assert.True(kitty.TryRead(0, out var kittyEscape));
        Assert.Equal(ConsoleKey.Escape, legacyEscape.Key?.Key);
        Assert.Equal(ConsoleKey.Escape, kittyEscape.Key?.Key);
        Assert.False(legacyEscape.Key?.Modifiers.HasFlag(ConsoleModifiers.Alt));
        Assert.False(kittyEscape.Key?.Modifiers.HasFlag(ConsoleModifiers.Alt));
        var keymap = new EditorKeymap();
        Assert.True(keymap.Matches("app.interrupt", legacyEscape.Key!.Value));
        Assert.True(keymap.Matches("app.interrupt", kittyEscape.Key!.Value));
    }

    [Fact]
    public void RebindingSuspensionAndCancellationDoesNotDisableTheirSafeguards()
    {
        var directory = Path.Combine(Path.GetTempPath(), "pisharp-app-control-safeguards-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            File.WriteAllText(Path.Combine(directory, "keybindings.json"),
                "{\"app.interrupt\":\"f7\",\"app.suspend\":\"f8\",\"app.exit\":\"f9\",\"app.clear\":\"f10\"}");
            var keymap = new EditorKeymap(directory);
            Assert.True(keymap.Matches("app.interrupt", Key(ConsoleKey.F7)));
            Assert.Equal("app.suspend", keymap.MatchIdleApplicationAction(Key(ConsoleKey.F8)));
            Assert.False(keymap.Matches("app.suspend", Key(ConsoleKey.Z, control: true)));
            Assert.False(keymap.Matches("app.interrupt", Key(ConsoleKey.Escape)));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public void EmptyBindingsDisableTheirApplicationActions()
    {
        var directory = Path.Combine(Path.GetTempPath(), "pisharp-app-control-disabled-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            File.WriteAllText(Path.Combine(directory, "keybindings.json"),
                "{\"app.suspend\":[],\"app.interrupt\":[],\"app.clear\":[],\"app.exit\":[]}");
            var keymap = new EditorKeymap(directory);
            Assert.False(keymap.Matches("app.suspend", Key(ConsoleKey.Z, control: true)));
            Assert.False(keymap.Matches("app.interrupt", Key(ConsoleKey.Escape)));
            Assert.Null(keymap.MatchIdleApplicationAction(Key(ConsoleKey.Z, control: true)));
            Assert.False(keymap.Matches("app.exit", Key(ConsoleKey.D, control: true)));
            Assert.False(keymap.Matches("app.clear", Key(ConsoleKey.C, control: true)));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public void WindowsSuspensionReportsPinnedPiUnsupportedActionWithoutClaimingJobControl()
    {
        Assert.Equal("Suspend to background is not supported on Windows",
            TerminalInputActions.WindowsSuspendUnsupportedMessage);
    }

    [Fact]
    public async Task NoneDisablesDoubleEscapeNavigation()
    {
        var editor = new TerminalEditor();
        editor.SetDoubleEscapeActionProvider(() => "none");
        var now = 1_000L;
        editor.SetDoublePressClock(() => now);
        var actions = new List<string>();

        await editor.HandleIdleEscapeAsync(Dispatch);
        now += 100;
        await editor.HandleIdleEscapeAsync(Dispatch);

        Assert.Empty(actions);

        Task Dispatch(string action)
        {
            actions.Add(action);
            return Task.CompletedTask;
        }
    }

    [Fact]
    public async Task DoubleEscapeRequiresAnEmptyEditor()
    {
        var editor = new TerminalEditor();
        editor.SetDoubleEscapeActionProvider(() => "tree");
        var now = 1_000L;
        editor.SetDoublePressClock(() => now);
        editor.Prefill("keep me");
        var actions = new List<string>();

        await editor.HandleIdleEscapeAsync(Dispatch);
        now += 100;
        await editor.HandleIdleEscapeAsync(Dispatch);

        Assert.Equal("keep me", editor.Draft);
        Assert.Empty(actions);

        Task Dispatch(string action)
        {
            actions.Add(action);
            return Task.CompletedTask;
        }
    }

    [Theory]
    [InlineData(499, true)]
    [InlineData(500, false)]
    [InlineData(501, false)]
    public void CtrlCDoublePressUsesTheFiveHundredMillisecondWindow(long elapsed, bool exits)
    {
        var editor = new TerminalEditor();
        var now = 5_000L;
        editor.SetDoublePressClock(() => now);

        Assert.False(editor.RegisterClearPress());
        now += elapsed;
        Assert.Equal(exits, editor.RegisterClearPress());
    }

    [Fact]
    public void CtrlCClearsEditorAndResetsCursorAndSelection()
    {
        var buffer = new EditorBuffer();
        buffer.SetText("alpha beta");
        buffer.SetCursor(5);
        Assert.Equal(EditorAction.Cancel, buffer.Handle(Key(ConsoleKey.C, control: true)));
        Assert.Equal("", buffer.Text);
        Assert.Equal(0, buffer.Cursor);
        Assert.Null(buffer.SelectedText);
    }

    [Fact]
    public void CtrlDExitsOnlyWhenTheEditorIsEmpty()
    {
        var empty = new EditorBuffer();
        Assert.Equal(EditorAction.Exit, empty.Handle(Key(ConsoleKey.D, control: true)));

        var nonempty = new EditorBuffer();
        nonempty.SetText("a😀e\u0301b");
        nonempty.SetCursor(1);
        Assert.Equal(EditorAction.Render, nonempty.Handle(Key(ConsoleKey.D, control: true)));
        Assert.Equal("ae\u0301b", nonempty.Text);
        Assert.Equal(1, nonempty.Cursor);
    }

    [Fact]
    public void CtrlDForwardDeleteHonoursSelectionAndGraphemes()
    {
        var selected = new EditorBuffer();
        selected.SetText("alpha beta");
        selected.SetCursor(1);
        selected.Handle(Key(ConsoleKey.RightArrow, shift: true));
        selected.Handle(Key(ConsoleKey.RightArrow, shift: true));
        Assert.Equal(EditorAction.Render, selected.Handle(Key(ConsoleKey.D, control: true)));
        Assert.Equal("aha beta", selected.Text);
        Assert.Null(selected.SelectedText);

        var grapheme = new EditorBuffer();
        grapheme.SetText("😀x");
        grapheme.SetCursor(0);
        Assert.Equal(EditorAction.Render, grapheme.Handle(Key(ConsoleKey.D, control: true)));
        Assert.Equal("x", grapheme.Text);
    }

    [Fact]
    public void DeleteKeyRemainsForwardDeleteAndDoesNotExit()
    {
        var buffer = new EditorBuffer();
        buffer.SetText("abc");
        buffer.SetCursor(0);
        Assert.Equal(EditorAction.Render, buffer.Handle(Key(ConsoleKey.Delete)));
        Assert.Equal("bc", buffer.Text);
    }

    [Theory]
    [InlineData("tree", "tree")]
    [InlineData("fork", "fork")]
    [InlineData("none", "none")]
    public async Task DoubleEscapeSettingParsesAndProducesTheEffectiveValue(string value, string expected)
    {
        var directory = Path.Combine(Path.GetTempPath(), "pisharp-app-control-settings-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            await File.WriteAllTextAsync(Path.Combine(directory, "settings.json"),
                JsonSerializer.Serialize(new Dictionary<string, string> { ["doubleEscapeAction"] = value }));
            var settings = await UserSettings.LoadAsync(directory, _ => null);
            Assert.Equal(expected, settings.EffectiveDoubleEscapeAction);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public async Task DoubleEscapeSettingDefaultsAndRejectsInvalidValues()
    {
        Assert.Equal("tree", new UserSettings().EffectiveDoubleEscapeAction);
        Assert.Equal("fork", new UserSettings(DoubleEscapeAction: "fork").EffectiveDoubleEscapeAction);

        var directory = Path.Combine(Path.GetTempPath(), "pisharp-app-control-invalid-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            await File.WriteAllTextAsync(Path.Combine(directory, "settings.json"),
                "{\"doubleEscapeAction\":\"history\"}");
            await Assert.ThrowsAsync<InvalidDataException>(() => UserSettings.LoadAsync(directory, _ => null));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public void TrustedProjectDoubleEscapeActionOverridesTheUserValue()
    {
        var user = new UserSettings(DoubleEscapeAction: "tree");
        var project = new UserSettings(DoubleEscapeAction: "none");
        Assert.Equal("none", user.Overlay(project).EffectiveDoubleEscapeAction);
        Assert.Equal("tree", user.Overlay(new UserSettings()).EffectiveDoubleEscapeAction);
    }

    [Fact]
    public void ApplicationControlBindingsCanBeReboundAndDisabled()
    {
        var directory = Path.Combine(Path.GetTempPath(), "pisharp-app-control-bindings-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            File.WriteAllText(Path.Combine(directory, "keybindings.json"),
                JsonSerializer.Serialize(new Dictionary<string, string[]>
                {
                    ["app.interrupt"] = ["f7"],
                    ["app.clear"] = [],
                    ["app.exit"] = ["ctrl+q"],
                    ["app.suspend"] = ["f8"]
                }));
            var keymap = new EditorKeymap(directory);
            Assert.True(keymap.Matches("app.interrupt", Key(ConsoleKey.F7)));
            Assert.False(keymap.Matches("app.interrupt", Key(ConsoleKey.Escape)));
            Assert.False(keymap.Matches("app.clear", Key(ConsoleKey.C, control: true)));
            Assert.True(keymap.Matches("app.exit", Key(ConsoleKey.Q, control: true)));
            Assert.True(keymap.Matches("app.suspend", Key(ConsoleKey.F8)));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public void ApplicationSelectionTreeHasNoDefaultBindingButCanBeConfigured()
    {
        Assert.False(new EditorKeymap().Matches("app.session.tree", Key(ConsoleKey.T, control: true)));
        var directory = Path.Combine(Path.GetTempPath(), "pisharp-app-control-tree-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            File.WriteAllText(Path.Combine(directory, "keybindings.json"),
                "{\"app.session.tree\":\"f7\"}");
            var keymap = new EditorKeymap(directory);
            Assert.True(keymap.Matches("app.session.tree", Key(ConsoleKey.F7)));
            Assert.Equal("app.session.tree", keymap.MatchIdleApplicationAction(Key(ConsoleKey.F7)));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }
}
