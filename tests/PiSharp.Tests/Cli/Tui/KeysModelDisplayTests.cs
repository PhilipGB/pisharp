using System.Net;
using System.Text;
using Microsoft.Extensions.AI;
using PiSharp.Cli;
using PiSharp.Cli.Tui;
using PiSharp.Runtime.Providers;
using PiSharp.Runtime.Sessions;
using Xunit;

namespace PiSharp.Tests;

[Collection("Console writers")]
public sealed class KeysModelDisplayTests
{
    [Fact]
    public void DefaultBindingsMatchPinnedPiModelThinkingToolAndCopyActions()
    {
        var keymap = new EditorKeymap();
        var windows = OperatingSystem.IsWindows() || OperatingSystem.IsLinux() &&
            (Environment.GetEnvironmentVariable("WSL_DISTRO_NAME") is not null ||
             Environment.GetEnvironmentVariable("WSL_INTEROP") is not null);

        Assert.True(keymap.Matches("tui.input.tab", Key(ConsoleKey.Tab)));
        Assert.False(keymap.Matches("tui.input.tab", Key(ConsoleKey.Tab, ConsoleModifiers.Shift)));
        Assert.True(keymap.Matches("app.thinking.cycle", Key(ConsoleKey.Tab, ConsoleModifiers.Shift)));
        Assert.True(keymap.Matches("app.model.cycleForward", Key(ConsoleKey.P, ConsoleModifiers.Control)));
        Assert.True(keymap.Matches("app.model.select", Key(ConsoleKey.L, ConsoleModifiers.Control)));
        Assert.True(keymap.Matches("app.tools.expand", Key(ConsoleKey.O, ConsoleModifiers.Control)));
        Assert.True(keymap.Matches("app.thinking.toggle", Key(ConsoleKey.T, ConsoleModifiers.Control)));
        Assert.True(keymap.Matches("app.message.copy", Key(ConsoleKey.X, ConsoleModifiers.Control)));
        Assert.True(keymap.Matches("app.thinking.save", Key(ConsoleKey.S, ConsoleModifiers.Control)));
        Assert.True(keymap.Matches("tui.select.confirm", Key(ConsoleKey.Enter)));
        Assert.True(keymap.Matches("tui.select.cancel", Key(ConsoleKey.Escape)));
        Assert.True(keymap.Matches("tui.select.cancel", Key(ConsoleKey.C, ConsoleModifiers.Control)));
        Assert.True(keymap.Matches("tui.select.up", Key(ConsoleKey.UpArrow)));
        Assert.True(keymap.Matches("tui.select.down", Key(ConsoleKey.DownArrow)));
        Assert.True(keymap.Matches("tui.select.pageUp", Key(ConsoleKey.PageUp)));
        Assert.True(keymap.Matches("tui.select.pageDown", Key(ConsoleKey.PageDown)));

        if (windows)
        {
            Assert.True(keymap.Matches("app.model.cycleBackward", Key(ConsoleKey.P, ConsoleModifiers.Alt)));
            Assert.False(keymap.Matches("app.model.cycleBackward",
                Key(ConsoleKey.P, ConsoleModifiers.Control | ConsoleModifiers.Shift)));
        }
        else
        {
            Assert.True(keymap.Matches("app.model.cycleBackward",
                Key(ConsoleKey.P, ConsoleModifiers.Control | ConsoleModifiers.Shift)));
            Assert.False(keymap.Matches("app.model.cycleBackward", Key(ConsoleKey.P, ConsoleModifiers.Alt)));
        }
    }

    [Fact]
    public void CycleStatusMatchesPinnedPiNamingAndThinkingDisclosure()
    {
        var plain = new ModelDescriptor("plain", "fixture", null, null, Provider: "fixture", Name: "Plain Model");
        var reasoner = new ModelDescriptor("reasoner", "fixture", null, null, Reasoning: true,
            Provider: "fixture", Name: "Reasoning Model");

        Assert.Equal("", TerminalModelSelectionCommands.FormatThinkingSuffix(plain, "off"));
        Assert.Equal("", TerminalModelSelectionCommands.FormatThinkingSuffix(reasoner, "off"));
        Assert.Equal(" (thinking: medium)", TerminalModelSelectionCommands.FormatThinkingSuffix(reasoner, "medium"));
    }

    [Fact]
    public async Task ThinkingCycleUsesPinnedPiMessagesWithoutExtraPeriods()
    {
        using var fixture = await ModelFixture.CreateAsync("""
            {"providers":{"fixture":{"baseUrl":"https://fixture.test/v1","apiKeyEnv":"FIXTURE_KEY","models":[
              {"id":"plain","name":"Plain"},
              {"id":"reasoner","name":"Reasoner","reasoning":true}
            ]}}}
            """);
        var plain = await fixture.Runtime.ResolveAsync("fixture", "plain");
        var output = new StringWriter();
        var navigation = new TerminalModelNavigation(fixture.Runtime, () => output);

        Assert.Null(navigation.NextThinkingLevel(plain, "off"));
        Assert.Equal("Current model does not support thinking" + Environment.NewLine, output.ToString());

        var reasoner = await fixture.Runtime.ResolveAsync("fixture", "reasoner");
        output.GetStringBuilder().Clear();
        Assert.Equal("minimal", navigation.NextThinkingLevel(reasoner, "off"));
        Assert.Equal("", output.ToString());
    }

    [Fact]
    public async Task ModelCycleUsesTheCachedAvailableSnapshotAndScopeOrder()
    {
        using var fixture = await ModelFixture.CreateAsync("""
            {"providers":{"fixture":{"baseUrl":"https://fixture.test/v1","apiKeyEnv":"FIXTURE_KEY","models":[
              {"id":"first","name":"First"},
              {"id":"second","name":"Second"},
              {"id":"third","name":"Third"}
            ]}}}
            """);
        var selection = await fixture.Runtime.ResolveAsync("fixture", "first");
        var output = new StringWriter();
        var navigation = new TerminalModelNavigation(fixture.Runtime, () => output);

        Assert.Equal("second", (await navigation.NextModelAsync(() => selection, null, 1))?.Model.Id);
        var second = await fixture.Runtime.ResolveAsync("fixture", "second");
        Assert.Equal("third", (await navigation.NextModelAsync(() => second, null, 1))?.Model.Id);
        var third = await fixture.Runtime.ResolveAsync("fixture", "third");
        Assert.Equal("second", (await navigation.NextModelAsync(() => third, null, -1))?.Model.Id);
        Assert.Equal("first", (await navigation.NextModelAsync(() => second, null, -1))?.Model.Id);
        Assert.Equal("third", (await navigation.NextModelAsync(() => selection, null, -1))?.Model.Id);
        Assert.Empty(output.ToString());
        Assert.Equal(0, fixture.Handler.RequestCount);

        fixture.Runtime.SetScope(["fixture/third", "fixture/first"]);
        await fixture.Runtime.ListModelsAsync("fixture");
        Assert.Equal("third", (await navigation.NextModelAsync(() => selection, null, 1))?.Model.Id);
        Assert.Equal("first", (await navigation.NextModelAsync(() => third, null, 1))?.Model.Id);
        Assert.Equal(0, fixture.Handler.RequestCount);
    }

    [Fact]
    public async Task ModelCycleMergesConfiguredModelsMissingFromAPartialSnapshot()
    {
        using var fixture = await ModelFixture.CreateAsync("""
            {"providers":{"fixture":{"baseUrl":"https://fixture.test/v1","apiKey":"literal-key","models":[
              {"id":"first","name":"First"},
              {"id":"second","name":"Second"},
              {"id":"third","name":"Third"}
            ]}}}
            """);
        var selection = await fixture.Runtime.ResolveAsync("fixture", "first");
        var output = new StringWriter();
        var navigation = new TerminalModelNavigation(fixture.Runtime, () => output);

        Assert.Equal("second", (await navigation.NextModelAsync(() => selection, null, 1))?.Model.Id);
        var second = await fixture.Runtime.ResolveAsync("fixture", "second");
        var third = await fixture.Runtime.ResolveAsync("fixture", "third");
        Assert.Equal("third", (await navigation.NextModelAsync(() => second, null, 1))?.Model.Id);
        Assert.Equal("second", (await navigation.NextModelAsync(() => third, null, -1))?.Model.Id);
        Assert.Equal(0, fixture.Handler.RequestCount);
    }

    [Fact]
    public async Task ModelCycleReportsSingleModelWithoutExtraPunctuation()
    {
        using var fixture = await ModelFixture.CreateAsync("""
            {"providers":{"fixture":{"baseUrl":"https://fixture.test/v1","apiKeyEnv":"FIXTURE_KEY","models":[
              {"id":"only","name":"Only"}
            ]}}}
            """);
        var selection = await fixture.Runtime.ResolveAsync("fixture", "only");
        var output = new StringWriter();
        var navigation = new TerminalModelNavigation(fixture.Runtime, () => output);

        Assert.Null(await navigation.NextModelAsync(() => selection, null, 1));
        Assert.Equal("Only one model available" + Environment.NewLine, output.ToString());

        fixture.Runtime.SetScope(["fixture/only"]);
        output.GetStringBuilder().Clear();
        Assert.Null(await navigation.NextModelAsync(() => selection, null, 1));
        Assert.Equal("Only one model in scope" + Environment.NewLine, output.ToString());
    }

    [Fact]
    public async Task ActiveRunDispatchesModelThinkingToolAndCopyActionsWhilePreservingDraft()
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        using var screen = new TerminalScreen(output, error, () => 48, () => 14);
        screen.Output.Write("assistant transcript");
        var editor = new TerminalEditor();
        editor.AttachScreen(screen);
        editor.Prefill("draft survives");
        var actions = new List<string>();
        var startsExpanded = screen.ToolResultsExpanded;

        Assert.True(await SendAsync(editor, Key(ConsoleKey.P, ConsoleModifiers.Control)));
        Assert.True(await SendAsync(editor, Key(ConsoleKey.P, ConsoleModifiers.Control | ConsoleModifiers.Shift)));
        Assert.True(await SendAsync(editor, Key(ConsoleKey.L, ConsoleModifiers.Control)));
        Assert.True(await SendAsync(editor, Key(ConsoleKey.Tab, ConsoleModifiers.Shift)));
        Assert.True(await SendAsync(editor, Key(ConsoleKey.T, ConsoleModifiers.Control)));
        Assert.True(await SendAsync(editor, Key(ConsoleKey.O, ConsoleModifiers.Control)));
        Assert.True(await SendAsync(editor, Key(ConsoleKey.X, ConsoleModifiers.Control)));

        Assert.Equal(["app.model.cycleForward", "app.model.cycleBackward", "app.model.select",
            "app.thinking.cycle", "app.thinking.toggle", "app.message.copy"], actions);
        Assert.NotEqual(startsExpanded, screen.ToolResultsExpanded);
        Assert.Equal("draft survives", editor.Draft);

        async Task<bool> SendAsync(TerminalEditor current, ConsoleKeyInfo key) =>
            await current.HandleActiveInputAsync(new(key, null),
                (_, _, _) => Task.FromResult(true), () => [], () => { },
                dispatchApplicationAction: action =>
                {
                    actions.Add(action);
                    return Task.CompletedTask;
                });
    }

    [Fact]
    public async Task ShiftTabCyclesThinkingEvenWhenACompletionCycleIsActive()
    {
        var editor = new TerminalEditor();
        editor.Prefill("/mod");
        await editor.HandleActiveInputAsync(new(Key(ConsoleKey.Tab), null),
            (_, _, _) => Task.FromResult(true), () => [], () => { });
        string? action = null;

        Assert.True(await editor.HandleApplicationShortcutAsync(
            Key(ConsoleKey.Tab, ConsoleModifiers.Shift), dispatched =>
            {
                action = dispatched;
                return Task.CompletedTask;
            }));

        Assert.Equal("app.thinking.cycle", action);
        Assert.Equal("/model", editor.Draft);
    }

    [Fact]
    public void RegularModeEditorPanelRendersInsteadOfBeingTreatedAsHiddenDocument()
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        using var screen = new TerminalScreen(output, error, false);
        screen.SetEditorPanel(["Select model", "  beta [fixture]"], bottomMargin: 2);

        Assert.Contains("Select model", output.ToString());
        Assert.Contains("beta [fixture]", output.ToString());
    }

    [Fact]
    public async Task CopyPrefersTheLastAssistantMessageWhenCopyOnSelectIsEnabled()
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        using var screen = new TerminalScreen(output, error, true);
        var runner = new RecordingClipboard();
        var clipboard = new TerminalClipboard(runner, _ => null, platform: ClipboardPlatform.MacOS);
        screen.SetSelectionClipboard(clipboard);
        var conversation = Conversation("assistant text");
        SelectTestText(screen);

        Assert.False(string.IsNullOrEmpty(screen.SelectedText));
        await TerminalClipboardActions.CopyLastAssistantAsync(clipboard, conversation, screen, output);

        Assert.Equal("assistant text", runner.CopiedText);
        Assert.Contains("\u001b[7m Copied! \u001b[27m", output.ToString());
    }

    [Fact]
    public async Task CopyPrefersTheActiveSelectionWhenFullscreenCopyOnSelectIsDisabled()
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        using var screen = new TerminalScreen(output, error, true)
        {
            CopyOnSelect = false
        };
        var runner = new RecordingClipboard();
        var clipboard = new TerminalClipboard(runner, _ => null, platform: ClipboardPlatform.MacOS);
        screen.SetSelectionClipboard(clipboard);
        var conversation = Conversation("assistant text");
        SelectTestText(screen);
        var selected = screen.SelectedText;

        await TerminalClipboardActions.CopyLastAssistantAsync(clipboard, conversation, screen, output);

        Assert.Equal(selected, runner.CopiedText);
    }

    [Fact]
    public async Task CopyWithoutAgentMessagesUsesPinnedPiError()
    {
        using var output = new StringWriter();
        var runner = new RecordingClipboard();
        var clipboard = new TerminalClipboard(runner, _ => null, platform: ClipboardPlatform.MacOS);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            TerminalClipboardActions.CopyLastAssistantAsync(clipboard, Conversation(), null, output));

        Assert.Equal("No agent messages to copy yet.", error.Message);
        Assert.Null(runner.CopiedText);
    }

    [Fact]
    public void SelectionOverlayUsesConfigurableModelKeysAndRenderedHints()
    {
        var directory = Path.Combine(Path.GetTempPath(), "pisharp-model-keys-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            File.WriteAllText(Path.Combine(directory, "keybindings.json"), """
                {"tui.select.confirm":"f5","tui.select.cancel":"f4","tui.input.tab":"f6","app.models.save":"f7"}
                """);
            var keymap = new EditorKeymap(directory);
            var options = new[]
            {
                new TerminalSelectionOption<string>("first", "first", "first"),
                new TerminalSelectionOption<string>("second", "second", "second")
            };
            var list = new TerminalSelectionList<string>("Select model", options, "first",
                scopedOptions: options, keymap: keymap);

            Assert.Equal(TerminalSelectionAction.Continue, list.HandleInput(new(Key(ConsoleKey.Enter), null)));
            Assert.Equal(TerminalSelectionAction.Continue, list.HandleInput(new(Key(ConsoleKey.S, ConsoleModifiers.Control), null)));
            Assert.Equal(TerminalSelectionAction.Continue, list.HandleInput(new(Key(ConsoleKey.Tab), null)));
            Assert.Equal(TerminalSelectionAction.Accept, list.HandleInput(new(Key(ConsoleKey.F5), null)));
            Assert.Equal(TerminalSelectionAction.AcceptAsDefault, list.HandleInput(new(Key(ConsoleKey.F7), null)));
            Assert.Equal(TerminalSelectionAction.Cancel, list.HandleInput(new(Key(ConsoleKey.F4), null)));

            var scopedRendered = string.Join('\n', list.RenderModelPicker(80, TerminalTheme.Default, "hint", "",
                refreshSuccess: true, model => model, out _, out _));
            Assert.Contains("F5 select", scopedRendered, StringComparison.Ordinal);
            Assert.Contains("F6 scope", scopedRendered, StringComparison.Ordinal);

            var allList = new TerminalSelectionList<string>("Select model", options, "first", keymap: keymap);
            var allRendered = string.Join('\n', allList.RenderModelPicker(80, TerminalTheme.Default, "hint", "",
                refreshSuccess: true, model => model, out _, out _));
            Assert.Contains("F5 to select", allRendered, StringComparison.Ordinal);
            Assert.Contains("F7 to set as default", allRendered, StringComparison.Ordinal);
            Assert.Contains("F4 to cancel", allRendered, StringComparison.Ordinal);
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public void ThinkingSelectorUsesTheAppThinkingSaveBindingInsteadOfTheModelSaveBinding()
    {
        var directory = Path.Combine(Path.GetTempPath(), "pisharp-thinking-keys-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            File.WriteAllText(Path.Combine(directory, "keybindings.json"), """
                {"app.thinking.save":"f8","app.models.save":"f9"}
                """);
            var keymap = new EditorKeymap(directory);
            var options = new[] { new TerminalSelectionOption<string>("low", "low", "low") };
            var list = new TerminalSelectionList<string>("Thinking Level", options, "low",
                keymap: keymap, saveAction: "app.thinking.save");

            Assert.Equal(TerminalSelectionAction.Continue, list.HandleInput(new(Key(ConsoleKey.S, ConsoleModifiers.Control), null)));
            Assert.Equal(TerminalSelectionAction.Continue, list.HandleInput(new(Key(ConsoleKey.F9), null)));
            Assert.Equal(TerminalSelectionAction.AcceptAsDefault, list.HandleInput(new(Key(ConsoleKey.F8), null)));
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task FullscreenCopyOnSelectParsesAndMergesWithTrustedProjectPrecedence()
    {
        var directory = Path.Combine(Path.GetTempPath(), "pisharp-copy-setting-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            await File.WriteAllTextAsync(Path.Combine(directory, "settings.json"),
                "{\"fullscreenCopyOnSelect\":false}");
            var user = await UserSettings.LoadAsync(directory, _ => null);
            Assert.False(user.FullscreenCopyOnSelect);

            await File.WriteAllTextAsync(Path.Combine(directory, "settings.json"),
                "{\"fullscreenCopyOnSelect\":true}");
            var project = await UserSettings.LoadAsync(directory, _ => null);
            Assert.True(user.Overlay(project).FullscreenCopyOnSelect);
            Assert.False(project.Overlay(user).FullscreenCopyOnSelect);

            await File.WriteAllTextAsync(Path.Combine(directory, "settings.json"),
                "{\"fullscreenCopyOnSelect\":\"no\"}");
            await Assert.ThrowsAsync<InvalidDataException>(() => UserSettings.LoadAsync(directory, _ => null));
        }
        finally { Directory.Delete(directory, true); }
    }

    private static ConsoleKeyInfo Key(ConsoleKey key, ConsoleModifiers modifiers = ConsoleModifiers.None) =>
        new('\0', key, modifiers.HasFlag(ConsoleModifiers.Shift), modifiers.HasFlag(ConsoleModifiers.Alt),
            modifiers.HasFlag(ConsoleModifiers.Control));

    private static ConversationSession Conversation(params string[] assistantMessages)
    {
        var conversation = new ConversationSession(Path.GetTempPath(), "fixture", "https://fixture.test/v1");
        foreach (var message in assistantMessages)
            conversation.Append(new ChatMessage(ChatRole.Assistant, message));
        return conversation;
    }

    private static void SelectTestText(TerminalScreen screen)
    {
        screen.Output.Write("alpha\nbe界a\nother");
        screen.HandleMouse(new(0, 2, 1, false));
        screen.HandleMouse(new(32, 6, 2, false));
        screen.HandleMouse(new(3, 6, 2, true));
    }

    private sealed class RecordingClipboard : IClipboardCommandRunner
    {
        public string? CopiedText { get; private set; }

        public Task<ClipboardCommandResult?> RunAsync(string command, IReadOnlyList<string> arguments,
            string? input, int maximumOutputBytes, TimeSpan timeout, CancellationToken cancellationToken = default)
        {
            if (command == "pbcopy") CopiedText = input;
            return Task.FromResult<ClipboardCommandResult?>(new(0, []));
        }
    }

    private sealed class CountingHandler : HttpMessageHandler
    {
        private int _requestCount;
        public int RequestCount => Volatile.Read(ref _requestCount);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _requestCount);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError));
        }
    }

    private sealed class ModelFixture : IDisposable
    {
        private readonly HttpClient _http;

        private ModelFixture(string directory, ProviderModelRuntime runtime, CountingHandler handler, HttpClient http)
        {
            DirectoryPath = directory;
            Runtime = runtime;
            Handler = handler;
            _http = http;
        }

        public string DirectoryPath { get; }
        public ProviderModelRuntime Runtime { get; }
        public CountingHandler Handler { get; }

        public static async Task<ModelFixture> CreateAsync(string modelsJson)
        {
            var directory = Path.Combine(Path.GetTempPath(), "pisharp-model-display-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            await File.WriteAllTextAsync(Path.Combine(directory, "models.json"), modelsJson);
            var handler = new CountingHandler();
            var http = new HttpClient(handler);
            var runtime = await ProviderModelRuntime.CreateAsync(directory, false,
                name => name == "FIXTURE_KEY" ? "fixture-key" : null, http, offline: true);
            await runtime.ListModelsAsync("fixture");
            return new ModelFixture(directory, runtime, handler, http);
        }

        public void Dispose()
        {
            _http.Dispose();
            Directory.Delete(DirectoryPath, true);
        }
    }
}
