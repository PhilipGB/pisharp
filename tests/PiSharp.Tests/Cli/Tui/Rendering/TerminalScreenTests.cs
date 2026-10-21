using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.AI;
using PiSharp.Cli;
using PiSharp.Cli.Tui;
using PiSharp.Runtime.Extensions;
using PiSharp.Runtime.Sessions;
using SkiaSharp;

namespace PiSharp.Tests;

public sealed class TerminalScreenTests
{
    [Fact]
    public void TrustSelectorUsesMutedDetailsAndSeparatelyStyledCurrentAndSelectedRows()
    {
        var theme = TerminalThemeCatalog.LoadBuiltIn("dark", TerminalColorMode.TrueColor);
        const string cwd = "/workspace/project";
        const string parent = "/workspace";
        var options = new[]
        {
            new TerminalSelectionOption<string>("trust", "trust", "Trust"),
            new TerminalSelectionOption<string>("parent", "parent", $"Trust parent folder ({parent})"),
            new TerminalSelectionOption<string>("deny", "deny", "Do not trust", IsCurrent: true)
        };
        var header = new[] { cwd, "", "Saved decision: untrusted", "Current session: untrusted", "" };

        var currentSelection = TerminalDialogPanels.RenderInlinePanel(theme, 100, "\nProject trust", options,
            selectedIndex: 2, header, "↑↓ navigate  enter save  escape/ctrl+c cancel", optionIndent: 1,
            bottomSpacerLines: 1, showCurrentMarker: true, headerStyle: "muted");
        var currentText = string.Join('\n', currentSelection);

        Assert.Contains(" " + theme.Style("muted", cwd), currentText);
        Assert.Contains(" " + theme.Style("muted", "Saved decision: untrusted"), currentText);
        Assert.Contains(" " + theme.Style("muted", "Current session: untrusted"), currentText);
        Assert.Contains(theme.Style("accent", "→ ") + theme.Style("accent", "✓ ") +
            theme.Style("accent", "Do not trust"), currentText);
        Assert.Contains("     " + theme.Style("text", "Trust"), currentText);

        var parentSelection = TerminalDialogPanels.RenderInlinePanel(theme, 100, "\nProject trust", options,
            selectedIndex: 1, header, "↑↓ navigate  enter save  escape/ctrl+c cancel", optionIndent: 1,
            bottomSpacerLines: 1, showCurrentMarker: true, headerStyle: "muted");
        Assert.Contains(" " + theme.Style("accent", "→ ") + "  " +
            theme.Style("accent", $"Trust parent folder ({parent})"), string.Join('\n', parentSelection));
    }

    [Fact]
    public void DeferredInitialRenderIncludesStartupOutputAndConfiguredFooter()
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        using var screen = new TerminalScreen(output, error, () => 40, () => 9,
            new TerminalImageRenderer(), TerminalTheme.Default, queryTerminalColors: false,
            deferInitialRender: true);

        screen.SetFooter("configured model status");
        screen.Output.WriteLine("startup context");

        Assert.DoesNotContain("\u001b[?2026h", output.ToString());
        screen.RenderInitial();

        var text = output.ToString();
        Assert.Equal(1, Count(text, "\u001b[?2026h"));
        Assert.Contains("startup context", text);
        Assert.Contains("configured model status", text);
    }

    [Fact]
    public void TerminalProgressSettingEmitsAndClearsOscProgressControls()
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        using var screen = new TerminalScreen(output, error, () => 40, () => 9);
        output.GetStringBuilder().Clear();

        screen.SetShowTerminalProgress(true);
        screen.ReportTerminalProgress(0.42);
        screen.ClearTerminalProgress();
        screen.SetShowTerminalProgress(false);

        Assert.Contains("\u001b]9;4;1;42\u0007", output.ToString());
        Assert.Contains("\u001b]9;4;0\u0007", output.ToString());
    }

    [Fact]
    public void AlternateScreenShutdownReleasesModesInPiOrder()
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        var screen = new TerminalScreen(output, error, useAlternateScreen: true);
        output.GetStringBuilder().Clear();
        output.Write("\u001b[?2004h");

        Assert.True(screen.PrepareTerminalModeShutdown());
        screen.Dispose();

        var text = output.ToString();
        var keyboardOff = text.IndexOf("\u001b[<u", StringComparison.Ordinal);
        var beginRedraw = text.IndexOf("\u001b[?2026h", StringComparison.Ordinal);
        var endRedraw = text.IndexOf("\u001b[?2026l", beginRedraw, StringComparison.Ordinal);
        var beginCleanup = text.IndexOf("\u001b[?2026h", endRedraw, StringComparison.Ordinal);
        var mouseOff = text.IndexOf("\u001b[?1006l", StringComparison.Ordinal);
        var endCleanup = text.IndexOf("\u001b[?2026l", beginCleanup, StringComparison.Ordinal);
        var beginLeave = text.IndexOf("\u001b[?2026h", endCleanup, StringComparison.Ordinal);
        var pasteOff = text.IndexOf("\u001b[?2004l", StringComparison.Ordinal);
        var alternateScreenOff = text.IndexOf("\u001b[?1049l", StringComparison.Ordinal);
        Assert.True(keyboardOff >= 0 && keyboardOff < beginRedraw && beginRedraw < endRedraw && endRedraw < mouseOff);
        Assert.True(beginCleanup < mouseOff && mouseOff < endCleanup && endCleanup < beginLeave);
        Assert.True(beginLeave < pasteOff && pasteOff < alternateScreenOff);
        Assert.Equal(1, Count(text, "\u001b[<u"));
    }

    [Fact]
    public void TranscriptAndDraftShareAUnicodeAwareFullscreenFrame()
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        var screen = new TerminalScreen(output, error, () => 32, () => 9);
        screen.SetEditor("draft 界", 7);
        screen.Output.Write("answer \u001b[2J🙂\nnext line");
        screen.Error.Write("tool status");
        screen.SetFooter("running");
        screen.Dispose();

        var screenText = TerminalOutputFrameReader.Read(output.ToString(), rows: 9, columns: 32)
            .Last(frame => frame.Screen.Contains("draft 界", StringComparison.Ordinal)).Screen;
        Assert.Contains("draft 界", screenText);
        Assert.Contains("answer [2J🙂", screenText);
        Assert.Contains("next line", screenText);
        Assert.Contains("tool status", screenText);
        Assert.DoesNotContain("tool status", error.ToString());
        Assert.Contains("running", screenText);
    }

    [Fact]
    public void TerminalResizeRedrawsTheActiveViewport()
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        var columns = 40;
        var rows = 8;
        var screen = new TerminalScreen(output, error, () => columns, () => rows);
        var widthChanges = new List<int>();
        screen.TerminalWidthChanged += widthChanges.Add;
        screen.SetEditor("long draft text that wraps across the input viewport", 17);
        var previousLength = output.GetStringBuilder().Length;

        columns = 24;
        rows = 12;
        screen.RefreshIfResized();
        screen.Dispose();

        Assert.True(output.GetStringBuilder().Length > previousLength);
        Assert.Contains("long draft text", output.ToString());
        Assert.Equal([24], widthChanges);
    }

    [Fact]
    public void StreamingOutputNotifiesThePresentationWhenTheTerminalWidthChanges()
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        var columns = 40;
        var screen = new TerminalScreen(output, error, () => columns, () => 8);
        var widthChanges = new List<int>();
        screen.TerminalWidthChanged += widthChanges.Add;
        screen.SetEditor("streaming", 9);

        columns = 24;
        screen.Output.Write("next stream chunk");

        screen.Dispose();

        Assert.Equal([24], widthChanges);
    }

    [Fact]
    public void RenderSchedulingCoalescesOutputAndEditorUpdatesButOpensModalsImmediately()
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        var clock = new ManualTimeProvider();
        using var screen = new TerminalScreen(output, error, () => 40, () => 9,
            new TerminalImageRenderer(), TerminalTheme.Default, queryTerminalColors: false,
            minimumRenderInterval: TimeSpan.FromMilliseconds(16), timeProvider: clock);
        output.GetStringBuilder().Clear();

        screen.Output.Write("first");
        screen.Output.Write("second");
        Assert.Equal(0, Count(output.ToString(), "\u001b[?2026h"));

        clock.Advance(TimeSpan.FromMilliseconds(15));
        Assert.Equal(0, Count(output.ToString(), "\u001b[?2026h"));
        clock.Advance(TimeSpan.FromMilliseconds(1));
        Assert.Equal(1, Count(output.ToString(), "\u001b[?2026h"));

        output.GetStringBuilder().Clear();
        screen.Output.Write("pending output");
        screen.SetEditor("p", 1);
        screen.SetEditor("prompt", 6);
        Assert.Equal(0, Count(output.ToString(), "\u001b[?2026h"));

        clock.Advance(TimeSpan.FromMilliseconds(16));
        Assert.Equal(1, Count(output.ToString(), "\u001b[?2026h"));
        Assert.Contains("prompt", output.ToString());

        output.GetStringBuilder().Clear();
        screen.SetStatusNotification("message before modal");
        screen.SetEditorPanel(["modal panel"], 0, 1);
        Assert.Equal(2, Count(output.ToString(), "\u001b[?2026h"));
        Assert.Contains("message before modal", output.ToString());
        Assert.Contains("modal panel", output.ToString());
        clock.Advance(TimeSpan.FromMilliseconds(16));
        Assert.Equal(2, Count(output.ToString(), "\u001b[?2026h"));

        screen.SetEditorPanel(null);
        clock.Advance(TimeSpan.FromMilliseconds(16));
        output.GetStringBuilder().Clear();
        screen.SetOverlay(["modal overlay"]);
        Assert.Equal(1, Count(output.ToString(), "\u001b[?2026h"));
        Assert.Contains("modal overlay", output.ToString());
        clock.Advance(TimeSpan.FromMilliseconds(16));
        Assert.Equal(1, Count(output.ToString(), "\u001b[?2026h"));
    }

    [Fact]
    public void ModelPickerScreenKeepsWarningColorAcrossWrappedHintPadding()
    {
        const int width = 44;
        const string hint = "Only showing models from configured providers. Use /login to add providers.";
        using var output = new StringWriter();
        using var error = new StringWriter();
        using var screen = new TerminalScreen(output, error, () => width, () => 14);
        var selection = new TerminalSelectionList<string>("Select model",
            [new TerminalSelectionOption<string>("model", "model", "model")]);
        var panel = selection.RenderModelPicker(width, TerminalTheme.Default, hint, "Refreshing model catalogs…",
            refreshSuccess: false, model => model, out var cursorRow, out var cursorColumn);
        output.GetStringBuilder().Clear();

        screen.SetEditorPanel(panel, cursorRow, cursorColumn, cursorVisible: false, bottomMargin: 2);

        var firstHintLine = TerminalTextLayout.StripFormatting(TerminalTextLayout.Wrap(hint, width)[0]);
        var expected = TerminalTheme.Default.Style("warning",
            firstHintLine + new string(' ', width - TerminalTextLayout.Width(firstHintLine)));
        Assert.Contains(expected, output.ToString());
    }

    [Fact]
    public void SuspendCancelsPendingRenderAndResumeShowsUpdatesMadeWhileSuspended()
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        var clock = new ManualTimeProvider();
        using var screen = new TerminalScreen(output, error, () => 40, () => 9,
            new TerminalImageRenderer(), TerminalTheme.Default, queryTerminalColors: false,
            minimumRenderInterval: TimeSpan.FromMilliseconds(16), timeProvider: clock);
        screen.Output.Write("before suspend");
        screen.Suspend();
        output.GetStringBuilder().Clear();

        screen.Output.Write("while suspended");
        screen.SetEditor("draft", 5);
        clock.Advance(TimeSpan.FromMilliseconds(100));
        Assert.Empty(output.ToString());

        screen.Resume();
        Assert.Equal(1, Count(output.ToString(), "\u001b[?2026h"));
        Assert.Contains("before suspendwhile suspended", output.ToString());
        Assert.Contains("draft", output.ToString());
        output.GetStringBuilder().Clear();
        screen.SetEditor("resumed draft", 13);
        clock.Advance(TimeSpan.FromMilliseconds(15));
        Assert.Empty(output.ToString());
        clock.Advance(TimeSpan.FromMilliseconds(1));
        Assert.Equal(1, Count(output.ToString(), "\u001b[?2026h"));
        Assert.Contains("resumed draft", output.ToString());
    }

    [Fact]
    public void AuthorizationOwnershipPreservesNewerSignInAndRequiresAPanelForCopyStatus()
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        using var screen = new TerminalScreen(output, error, () => 80, () => 24);
        var mcpUrl = new Uri("https://mcp.example/authorize?state=first");
        var providerUrl = new Uri("https://provider.example/authorize?state=second");
        screen.AppendMcpAuthorizationNotification("server", mcpUrl, "Ctrl+Y", "Escape");
        Assert.Equal(mcpUrl.AbsoluteUri, screen.ActiveAuthorizationUrl);
        Assert.False(screen.SetAuthorizationUrlCopyStatus("Copied", TerminalStatusNotificationKind.Info));

        screen.SetEditorPanel(["login panel"]);
        Assert.True(screen.SetAuthorizationUrlCopyStatus("Copied", TerminalStatusNotificationKind.Info));
        Assert.True(screen.AppendProviderAuthorizationNotification("provider", "Provider", "Open browser",
            providerUrl, "Ctrl+Y", "Escape"));
        screen.ClearMcpAuthorizationNotification("server");
        Assert.Equal(providerUrl.AbsoluteUri, screen.ActiveAuthorizationUrl);

        screen.ClearAuthorizationUrl("provider:provider");
        Assert.Null(screen.ActiveAuthorizationUrl);
        Assert.False(screen.SetAuthorizationUrlCopyStatus("Copied", TerminalStatusNotificationKind.Info));
        Assert.Throws<ArgumentException>(() => screen.AppendMcpAuthorizationNotification("server",
            new Uri("file:///tmp/authorization")));
        Assert.Null(screen.ActiveAuthorizationUrl);
    }

    [Fact]
    public void LlamaLoadingPanelReplacesPendingCommandEditorFrame()
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        var clock = new ManualTimeProvider();
        using var screen = new TerminalScreen(output, error, () => 100, () => 32,
            new TerminalImageRenderer(), TerminalTheme.Default, queryTerminalColors: false,
            minimumRenderInterval: TimeSpan.FromMilliseconds(16), timeProvider: clock);
        output.GetStringBuilder().Clear();

        screen.SetEditor("/llama", 6);
        screen.SetEditor("", 0);
        screen.SetEditorPanel(["llama.cpp models", "Loading…"], 1, 1,
            flushPendingRenderOnOpen: false);

        var outputText = output.ToString();
        Assert.Equal(1, Count(outputText, "\u001b[?2026h"));
        Assert.DoesNotContain("/llama", outputText);
        Assert.Contains("llama.cpp models", outputText);
        Assert.Contains("Loading…", outputText);
    }

    [Fact]
    public void ResourceReloadProgressPanelReplacesAndRestoresTheEditor()
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        using var screen = new TerminalScreen(output, error, () => 100, () => 32,
            new TerminalImageRenderer(), TerminalTheme.Default, queryTerminalColors: false);
        screen.SetEditor("draft", 5);
        output.GetStringBuilder().Clear();

        screen.ShowReloadProgressPanel("Reloading resources...");

        var progress = output.ToString();
        Assert.Contains("\u001b[26;1H\u001b[2K", progress);
        Assert.Contains("Reloading resources...", progress);
        Assert.DoesNotContain("draft", progress);

        output.GetStringBuilder().Clear();
        screen.ReplaceTranscript(target => target.AppendUserMessage("reloaded transcript"));
        Assert.Empty(output.ToString());

        screen.DismissEditorPanelAndAppendThemedStatusLine("Reloaded resources.");

        var restored = output.ToString();
        Assert.Equal(1, Count(restored, "\u001b[?2026h"));
        Assert.Contains("reloaded transcript", restored);
        Assert.Contains("Reloaded resources.", restored);
        Assert.DoesNotContain("Reloading resources...", restored);
    }

    [Fact]
    public void LlamaCatalogLoadingUsesPiExtensionEditorSpacing()
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        using var screen = new TerminalScreen(output, error, () => 100, () => 32);
        var editor = new TerminalEditor();
        editor.AttachScreen(screen);
        output.GetStringBuilder().Clear();

        editor.ShowLlamaCatalogLoading();

        var frame = output.ToString();
        Assert.Contains("\u001b[25;1H\u001b[2K", frame);
        Assert.Contains("\u001b[26;1H\u001b[2K", frame);
        Assert.Contains("\u001b[27;1H\u001b[2K" + new string(' ', 100), frame);
        Assert.Contains("\u001b[28;1H\u001b[2K", frame);
        Assert.Contains("\u001b[29;1H\u001b[2K" + new string(' ', 100), frame);
        Assert.Contains("llama.cpp models", frame);
        Assert.Contains("Loading…", frame);
    }

    [Fact]
    public void DisposeRestoresTheLatestEditorStateWhenItsRenderIsCoalesced()
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        var clock = new ManualTimeProvider();
        var screen = new TerminalScreen(output, error, () => 40, () => 9,
            new TerminalImageRenderer(), TerminalTheme.Default, queryTerminalColors: false,
            deferInitialRender: true, minimumRenderInterval: TimeSpan.FromMilliseconds(16), timeProvider: clock);
        screen.SetEditor("/qui", 4);
        screen.RenderInitial();
        output.GetStringBuilder().Clear();

        screen.SetEditor("", 0);
        Assert.Equal(0, Count(output.ToString(), "\u001b[?2026h"));

        screen.Dispose();

        var restoreIndex = output.ToString().LastIndexOf("\u001b[?1049l", StringComparison.Ordinal);
        Assert.True(restoreIndex >= 0);
        Assert.DoesNotContain("/qui", output.ToString()[(restoreIndex + "\u001b[?1049l".Length)..]);
    }

    [Fact]
    public void ReplacingTranscriptFlushesPendingOutputBeforeDiscardingThePreviousSession()
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        var clock = new ManualTimeProvider();
        using var screen = new TerminalScreen(output, error, () => 40, () => 9,
            new TerminalImageRenderer(), TerminalTheme.Default, queryTerminalColors: false,
            minimumRenderInterval: TimeSpan.FromMilliseconds(16), timeProvider: clock);
        output.GetStringBuilder().Clear();

        screen.Output.Write("previous session notice");
        Assert.Equal(0, Count(output.ToString(), "\u001b[?2026h"));

        screen.ReplaceTranscript(_ => { });
        Assert.Equal(1, Count(output.ToString(), "\u001b[?2026h"));
        Assert.Contains("previous session notice", output.ToString());

        clock.Advance(TimeSpan.FromMilliseconds(16));
        Assert.Equal(2, Count(output.ToString(), "\u001b[?2026h"));
    }

    [Fact]
    public void DisposeRestoresConsoleWritersAfterAnActiveRun()
    {
        var previousOut = Console.Out;
        var previousError = Console.Error;
        using var output = new StringWriter();
        using var error = new StringWriter();
        try
        {
            var screen = new TerminalScreen(output, error, () => 32, () => 9);
            screen.Activate();
            Console.Write("captured output");
            Console.Error.Write("captured error");
            screen.Dispose();
            Console.Write("after output");
            Console.Error.Write("after error");
        }
        finally
        {
            Console.SetOut(previousOut);
            Console.SetError(previousError);
        }

        Assert.Contains("captured output", output.ToString());
        Assert.Contains("captured error", output.ToString());
        Assert.Contains("after output", output.ToString());
        Assert.DoesNotContain("captured error", error.ToString());
        Assert.Contains("after error", error.ToString());
    }

    [Fact]
    public async Task ActiveEditorScrollsTheTranscriptAndReturnsToLiveOutput()
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        var screen = new TerminalScreen(output, error, () => 40, () => 9);
        var editor = new TerminalEditor();
        editor.AttachScreen(screen);
        for (var index = 0; index < 30; index++) screen.Output.WriteLine($"message {index:D2}");

        var topStart = output.GetStringBuilder().Length;
        await editor.HandleActiveInputAsync(
            new(new ConsoleKeyInfo('\0', ConsoleKey.Home, false, false, true), null),
            (_, _, _) => Task.FromResult(true), () => [], () => { });
        var topFrame = output.ToString()[topStart..];

        var bottomStart = output.GetStringBuilder().Length;
        await editor.HandleActiveInputAsync(
            new(new ConsoleKeyInfo('\0', ConsoleKey.End, false, false, true), null),
            (_, _, _) => Task.FromResult(true), () => [], () => { });
        var bottomFrame = output.ToString()[bottomStart..];
        screen.Dispose();

        Assert.Contains("message 00", topFrame);
        Assert.DoesNotContain("message 29", topFrame);
        Assert.Contains("message 29", bottomFrame);
        Assert.DoesNotContain("message 00", bottomFrame);
    }

    [Fact]
    public void PageScrollUsesTheTranscriptViewportAndPiOverlap()
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        using var screen = new TerminalScreen(output, error, () => 80, () => 40);
        for (var index = 0; index < 120; index++) screen.Output.WriteLine($"message {index:D3}");

        screen.ScrollPage(up: true);

        Assert.Equal(30, screen.TranscriptScrollOffset);
    }

    [Fact]
    public void IdleEditorRoutesKittyTranscriptKeysToScrollAndSearch()
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        using var inputBytes = new MemoryStream(Encoding.UTF8.GetBytes("\u001b[57421u\u001b[102;6u"));
        var screen = new TerminalScreen(output, error, () => 40, () => 9);
        var editor = new TerminalEditor();
        editor.AttachScreen(screen);
        editor.Prefill("draft");
        for (var index = 0; index < 30; index++) screen.Output.WriteLine($"message {index:D2}");
        var input = new TerminalInput(inputBytes);

        Assert.True(editor.HandleIdleTranscriptInput(input.Read()));
        var scrollOffset = screen.TranscriptScrollOffset;
        Assert.Contains("Jump to latest message · Ctrl+End", output.ToString());
        Assert.DoesNotContain("live output follows at bottom", screen.FooterText);
        Assert.True(editor.HandleIdleTranscriptInput(input.Read()));
        Assert.Contains("Find in transcript", TerminalTextLayout.StripFormatting(output.ToString()));
        Assert.True(editor.HandleIdleTranscriptInput(new(new ConsoleKeyInfo('\u001b', ConsoleKey.Escape,
            false, false, false), null)));
        var restoredDraft = editor.Draft;
        screen.Dispose();

        Assert.True(scrollOffset > 0);
        Assert.Equal("draft", restoredDraft);
    }

    [Fact]
    public async Task ActiveEditorUsesMouseWheelAndCopiesUnicodeTranscriptSelection()
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        var screen = new TerminalScreen(output, error, () => 40, () => 9);
        for (var index = 0; index < 30; index++) screen.Output.WriteLine($"message {index:D2}");
        var editor = new TerminalEditor();
        editor.AttachScreen(screen);
        screen.SetFullscreenWheelScrollLines(FullscreenWheelScrollSetting.Fixed(6));

        for (var index = 0; index < 5; index++)
            await editor.HandleActiveInputAsync(new(null, null, new(64, 1, 1, false)), Queue, Clear, Abort);
        var topFrame = TerminalOutputFrameReader.Read(output.ToString(), rows: 9, columns: 40)[^1].Screen;
        Assert.Contains("message 00", topFrame);
        Assert.DoesNotContain("message 29", topFrame);

        for (var index = 0; index < 5; index++)
            await editor.HandleActiveInputAsync(new(null, null, new(65, 1, 1, false)), Queue, Clear, Abort);
        var bottomWheelFrame = TerminalOutputFrameReader.Read(output.ToString(), rows: 9, columns: 40)[^1].Screen;
        Assert.Contains("message 29", bottomWheelFrame);
        Assert.DoesNotContain("message 00", bottomWheelFrame);

        screen.ScrollToBottom();
        screen.Output.Write("alpha\nbe界a\nother");
        var copiedActions = new List<string>();
        Task Dispatch(string action) { copiedActions.Add(action); return Task.CompletedTask; }
        await editor.HandleActiveInputAsync(new(null, null, new(0, 2, 1, false)), Queue, Clear, Abort,
            dispatchApplicationAction: Dispatch);
        await editor.HandleActiveInputAsync(new(null, null, new(32, 6, 2, false)), Queue, Clear, Abort,
            dispatchApplicationAction: Dispatch);
        await editor.HandleActiveInputAsync(new(null, null, new(3, 6, 2, true)), Queue, Clear, Abort,
            dispatchApplicationAction: Dispatch);
        screen.Dispose();

        Assert.Equal("alpha\nbe界a", screen.SelectedText);
        Assert.Equal(["app.message.copy"], copiedActions);
        Assert.Contains("\u001b[7m", output.ToString());

        static Task<bool> Queue(string text, bool followUp, CancellationToken token) => Task.FromResult(true);
        static IReadOnlyList<string> Clear() => [];
        static void Abort() { }
    }

    [Fact]
    public async Task MouseTranscriptSelectionUsesClipboardAndShowsTransientFlash()
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        var commandRunner = new ClipboardCopyCommandRunner();
        using var screen = new TerminalScreen(output, error, () => 40, () => 9);
        screen.SetSelectionClipboard(new TerminalClipboard(commandRunner, platform: ClipboardPlatform.MacOS));
        screen.Output.Write("alpha\nbe界a\nother");
        var editor = new TerminalEditor();
        editor.AttachScreen(screen);

        await editor.HandleActiveInputAsync(new(null, null, new(0, 2, 1, false)), Queue, Clear, Abort);
        await editor.HandleActiveInputAsync(new(null, null, new(32, 6, 2, false)), Queue, Clear, Abort);
        await editor.HandleActiveInputAsync(new(null, null, new(3, 6, 2, true)), Queue, Clear, Abort);

        Assert.Equal("alpha\nbe界a", screen.SelectedText);
        Assert.Equal("alpha\nbe界a", commandRunner.CopiedText);
        Assert.Contains("\u001b[7m Copied! \u001b[27m", output.ToString());
        Assert.DoesNotContain("Copied selected text to clipboard", output.ToString());

        static Task<bool> Queue(string text, bool followUp, CancellationToken token) => Task.FromResult(true);
        static IReadOnlyList<string> Clear() => [];
        static void Abort() { }
    }

    [Fact]
    public void FullscreenWheelSettingControlsTranscriptRowsPerEvent()
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        using var screen = new TerminalScreen(output, error, () => 40, () => 9);
        for (var index = 0; index < 30; index++) screen.Output.WriteLine($"message {index:D2}");
        screen.SetFullscreenWheelScrollLines(FullscreenWheelScrollSetting.Fixed(3));

        screen.HandleMouse(new(64, 1, 1, false));

        var frame = TerminalOutputFrameReader.Read(output.ToString(), rows: 9, columns: 40)[^1].Screen;
        Assert.Contains("message 25", frame);
        Assert.DoesNotContain("message 29", frame);
    }

    [Fact]
    public async Task EditorMouseClickPlacesCursorAtGraphemeBoundary()
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        using var screen = new TerminalScreen(output, error, () => 40, () => 9);
        var editor = new TerminalEditor();
        editor.Prefill("hello");
        editor.AttachScreen(screen);

        await editor.HandleActiveInputAsync(Mouse(0, 3, 6), Queue, Clear, Abort);
        await editor.HandleActiveInputAsync(Mouse(0, 3, 6, release: true), Queue, Clear, Abort);
        await editor.HandleActiveInputAsync(new(new ConsoleKeyInfo('X', ConsoleKey.X, false, false, false), null), Queue, Clear, Abort);

        Assert.Equal("heXllo", editor.Draft);

        static TerminalInputEvent Mouse(int button, int column, int row, bool release = false) =>
            new(null, null, new(button, column, row, release));
        static Task<bool> Queue(string text, bool followUp, CancellationToken token) => Task.FromResult(true);
        static IReadOnlyList<string> Clear() => [];
        static void Abort() { }
    }

    [Fact]
    public async Task EditorMouseDragCopiesOriginalTextAcrossWrappedRowsWithoutMovingCursor()
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        using var screen = new TerminalScreen(output, error, () => 20, () => 9);
        var editor = new TerminalEditor();
        const string draft = "hello🙂worldabcdefg1234567890123456789";
        editor.Prefill(draft);
        editor.AttachScreen(screen);
        var frame = EditorViewport.Layout(draft, draft.Length, 20, 3, showPrompt: false);
        Assert.Equal(2, frame.Rows.Count);
        var editorStart = 7 - frame.Rows.Count;
        var copiedActions = new List<string>();
        Task Dispatch(string action) { copiedActions.Add(action); return Task.CompletedTask; }

        await editor.HandleActiveInputAsync(Mouse(0, 1, editorStart), Queue, Clear, Abort);
        await editor.HandleActiveInputAsync(Mouse(32, 20, editorStart + 1), Queue, Clear, Abort);
        await editor.HandleActiveInputAsync(Mouse(0, 20, editorStart + 1, release: true), Queue, Clear, Abort,
            dispatchApplicationAction: Dispatch);

        Assert.Equal(draft, screen.SelectedText);
        Assert.Equal(["app.message.copy"], copiedActions);
        Assert.Equal(draft, editor.Draft);
        await editor.HandleActiveInputAsync(new(new ConsoleKeyInfo('X', ConsoleKey.X, false, false, false), null), Queue, Clear, Abort);
        Assert.Equal(draft + "X", editor.Draft);

        static TerminalInputEvent Mouse(int button, int column, int row, bool release = false) =>
            new(null, null, new(button, column, row, release));
        static Task<bool> Queue(string text, bool followUp, CancellationToken token) => Task.FromResult(true);
        static IReadOnlyList<string> Clear() => [];
        static void Abort() { }
    }

    [Fact]
    public async Task KeyboardSelectionHighlightsPromptAndIsAvailableToCopyAction()
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        using var screen = new TerminalScreen(output, error, () => 40, () => 9);
        var editor = new TerminalEditor();
        editor.Prefill("select me");
        editor.AttachScreen(screen);

        await editor.HandleActiveInputAsync(Key(ConsoleKey.LeftArrow, ConsoleModifiers.Shift), Queue, Clear, Abort);
        await editor.HandleActiveInputAsync(Key(ConsoleKey.LeftArrow, ConsoleModifiers.Shift), Queue, Clear, Abort);
        editor.AttachScreen(screen);

        Assert.Equal("me", screen.SelectedText);
        Assert.Contains("\u001b[7m", output.ToString());
        var copied = new List<string?>();
        await editor.HandleActiveInputAsync(Key(ConsoleKey.X, ConsoleModifiers.Control), Queue, Clear, Abort,
            dispatchApplicationAction: action =>
            {
                Assert.Equal("app.message.copy", action);
                copied.Add(screen.SelectedText);
                return Task.CompletedTask;
            });
        Assert.Equal(["me"], copied);

        static TerminalInputEvent Key(ConsoleKey key, ConsoleModifiers modifiers) =>
            new(new ConsoleKeyInfo('\0', key, modifiers.HasFlag(ConsoleModifiers.Shift),
                modifiers.HasFlag(ConsoleModifiers.Alt), modifiers.HasFlag(ConsoleModifiers.Control)), null);
        static Task<bool> Queue(string text, bool followUp, CancellationToken token) => Task.FromResult(true);
        static IReadOnlyList<string> Clear() => [];
        static void Abort() { }
    }

    [Fact]
    public void MouseTrackingIsDisabledBeforeSuspendingAndLeavingTheAlternateScreen()
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        var screen = new TerminalScreen(output, error, () => 32, () => 9);
        screen.Suspend();
        screen.Resume();
        screen.Dispose();

        var text = output.ToString();
        var enable = TerminalMouseMode.Enable;
        var disable = TerminalMouseMode.Disable;
        Assert.Contains(TerminalKeyboardMode.Enable +
            "\u001b[?1049h\u001b[?7l\u001b[?25l" + enable, text);
        var enabled = text.IndexOf("\u001b[?1049h\u001b[?7l\u001b[?25l" + enable, StringComparison.Ordinal);
        var disabledForSuspend = text.IndexOf(disable + "\u001b[?7h", StringComparison.Ordinal);
        var reenabled = text.IndexOf("\u001b[?1049h\u001b[?7l\u001b[?25l" + enable, enabled + 1, StringComparison.Ordinal);
        var disabledForDispose = text.LastIndexOf(disable + "\u001b[?7h", StringComparison.Ordinal);
        var suspendAltExit = text.IndexOf("\u001b[?25h\u001b[?1049l", disabledForSuspend, StringComparison.Ordinal);
        var disposeCleanupEnd = text.IndexOf("\u001b[?2026l", disabledForDispose, StringComparison.Ordinal);
        var disposeRestore = text.IndexOf("\u001b[?2026h", disposeCleanupEnd, StringComparison.Ordinal);
        var disposeAltExit = text.IndexOf("\u001b[?1049l", disposeRestore, StringComparison.Ordinal);

        Assert.True(enabled >= 0 && enabled < disabledForSuspend);
        Assert.True(disabledForSuspend < suspendAltExit && suspendAltExit < reenabled);
        var keyboardDisabled = text.LastIndexOf(TerminalKeyboardMode.Disable, StringComparison.Ordinal);
        Assert.True(reenabled < disabledForDispose && disabledForDispose < disposeCleanupEnd &&
            disposeCleanupEnd < keyboardDisabled && keyboardDisabled < disposeRestore &&
            disposeRestore < disposeAltExit);
        Assert.DoesNotContain("\u001b[?1l", text);
    }

    [Fact]
    public void PresentTranscriptOnNormalScreenRendersBeforeLeavingTheMainScreen()
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        var screen = new TerminalScreen(output, error, () => 48, () => 12);
        screen.Output.Write("ordinary transcript line");

        Assert.True(screen.PrepareTerminalModeShutdown());
        screen.PresentTranscriptOnNormalScreen();
        screen.Dispose();

        var text = output.ToString();
        var alternateExit = text.IndexOf("\u001b[?1049l", StringComparison.Ordinal);
        var transcript = text.IndexOf("ordinary transcript line", alternateExit, StringComparison.Ordinal);
        Assert.True(alternateExit >= 0 && transcript > alternateExit);
        Assert.Equal(1, text.Split("\u001b[?1049l").Length - 1);
        Assert.Contains("\u001b[?2004l\u001b[?1049l", text);
        Assert.EndsWith("\u001b[?25h", text);
    }

    [Fact]
    public async Task ActiveEditorSearchHighlightsMatchesAndRestoresItsDraftOnEscape()
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        var screen = new TerminalScreen(output, error, () => 100, () => 12);
        screen.Output.Write("needle first result\nother output\nneedle second result\n");
        var editor = new TerminalEditor();
        editor.Prefill("draft to keep");
        editor.AttachScreen(screen);
        var aborted = false;
        Task<bool> Queue(string text, bool followUp, CancellationToken token) => Task.FromResult(true);
        IReadOnlyList<string> ClearQueue() => [];
        void Abort() => aborted = true;

        await editor.HandleActiveInputAsync(
            new(new ConsoleKeyInfo('\0', ConsoleKey.F, shift: true, alt: false, control: true), null), Queue, ClearQueue, Abort);
        editor.AttachScreen(screen);
        var searchStart = output.GetStringBuilder().Length;
        await editor.HandleActiveInputAsync(new(null, "needle"), Queue, ClearQueue, Abort);
        editor.AttachScreen(screen);
        var searchFrame = output.ToString()[searchStart..];
        var nextStart = output.GetStringBuilder().Length;
        await editor.HandleActiveInputAsync(new(new ConsoleKeyInfo('\n', ConsoleKey.Enter, false, false, false), null), Queue, ClearQueue, Abort);
        editor.AttachScreen(screen);
        var nextFrame = output.ToString()[nextStart..];
        var closeStart = output.GetStringBuilder().Length;
        await editor.HandleActiveInputAsync(new(new ConsoleKeyInfo('\0', ConsoleKey.Escape, false, false, false), null), Queue, ClearQueue, Abort);
        editor.AttachScreen(screen);
        var closeFrame = output.ToString()[closeStart..];
        screen.Dispose();

        Assert.Contains("1/2", searchFrame);
        Assert.Contains("\u001b[7mneedle", searchFrame);
        Assert.Contains("2/2", nextFrame);
        Assert.Contains("\u001b[7mneedle", nextFrame);
        Assert.DoesNotContain("\u001b[7mneedle", closeFrame);
        Assert.Equal("draft to keep", editor.Draft);
        Assert.False(aborted);
    }

    [Fact]
    public void BashToolUpdatesStayInOnePendingThenSuccessfulPiStylePanel()
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        var theme = TerminalThemeCatalog.LoadBuiltIn("dark", TerminalColorMode.TrueColor);
        var screen = new TerminalScreen(output, error, () => 80, () => 24, new TerminalImageRenderer(),
            theme, queryTerminalColors: false);
        var transcript = new InteractiveTranscript(screen.Output, screen.Error, screen: screen);
        transcript.Render(new AgentLifecycleEvent("tool_execution_started", Tool: "bash", OperationId: "bash-1")
        {
            ToolArguments = new Dictionary<string, object?> { ["command"] = "printf 'tool output'" }
        });
        transcript.Render(new("tool_execution_update", Tool: "bash", OperationId: "bash-1", Text: "first line\n"));
        var partial = CurrentFrame(output);

        transcript.Render(new("tool_execution_finished", Tool: "bash", OperationId: "bash-1",
            Text: "first line\nsecond line\n")
        { DurationMs = 100, IsError = false });
        transcript.Render(new("model_text_delta", Text: "Tool follow-up."));
        transcript.FinishTurn();
        var completed = CurrentFrame(output);
        screen.Dispose();

        Assert.Contains("$ printf 'tool output'", partial);
        Assert.Contains("first line", partial);
        Assert.Contains("Elapsed 0.0s", partial);
        Assert.DoesNotContain("Took", partial);
        Assert.Contains("$ printf 'tool output'", completed);
        Assert.Contains("first line", completed);
        Assert.Contains("second line", completed);
        Assert.Contains("Took 0.1s", completed);
        Assert.Equal(1, completed.Split("Took 0.1s", StringSplitOptions.None).Length - 1);
        var lines = completed.Split('\n');
        var timingLine = Array.FindIndex(lines, line => line.Contains("Took 0.1s", StringComparison.Ordinal));
        var followUpLine = Array.FindIndex(lines, line => line.Contains("Tool follow-up.", StringComparison.Ordinal));
        Assert.True(followUpLine > timingLine + 1);
        Assert.True(string.IsNullOrWhiteSpace(lines[timingLine + 1]));
        Assert.DoesNotContain("→ bash", completed);
        Assert.DoesNotContain("← bash completed", completed);
        Assert.Contains(theme.Bg("toolPendingBg"), output.ToString());
        Assert.Contains(theme.Bg("toolSuccessBg"), output.ToString());

        string CurrentFrame(StringWriter writer) =>
            TerminalOutputFrameReader.Read(writer.ToString(), rows: 24, columns: 80)[^1].Screen;
    }

    [Fact]
    public async Task ActiveToolResultsCollapseAfterTenLinesAndExpandWithCtrlO()
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        var screen = new TerminalScreen(output, error, () => 100, () => 30);
        var transcript = new InteractiveTranscript(screen.Output, screen.Error, screen: screen);
        var result = string.Join('\n', Enumerable.Range(1, 12).Select(line => $"line {line}"));
        transcript.Render(new("tool_execution_finished", Tool: "grep", Text: result));
        var collapsedFrame = output.ToString();
        var editor = new TerminalEditor();
        editor.AttachScreen(screen);
        var expandedFrameStart = output.GetStringBuilder().Length;

        await editor.HandleActiveInputAsync(
            new(new ConsoleKeyInfo('\0', ConsoleKey.O, shift: false, alt: false, control: true), null),
            (_, _, _) => Task.FromResult(true), () => [], () => { });
        var expandedFrame = output.ToString()[expandedFrameStart..];
        var collapsedFrameStart = output.GetStringBuilder().Length;
        await editor.HandleActiveInputAsync(
            new(new ConsoleKeyInfo('\0', ConsoleKey.O, shift: false, alt: false, control: true), null),
            (_, _, _) => Task.FromResult(true), () => [], () => { });
        var recollapsedFrame = output.ToString()[collapsedFrameStart..];
        var recollapsedScreen = TerminalOutputFrameReader.Read(output.ToString(), rows: 30, columns: 100)[^1].Screen;
        screen.Dispose();

        Assert.Contains("line 1", collapsedFrame);
        Assert.Contains("line 10", collapsedFrame);
        Assert.Contains("3 more visual rows; tool output collapsed", collapsedFrame);
        Assert.DoesNotContain("line 11", collapsedFrame);
        Assert.Contains("line 11", expandedFrame);
        Assert.Contains("line 12", expandedFrame);
        Assert.Contains("Tool output: expanded", expandedFrame);
        Assert.Contains("Tool output: collapsed", recollapsedFrame);
        Assert.DoesNotContain("Tool output: expanded", recollapsedScreen);
        Assert.Empty(error.ToString());
    }

    [Fact]
    public void ActiveToolResultPreviewsCollapseByWrappedRowsForBashCodemodeAndMcp()
    {
        foreach (var tool in new[] { "bash", "codemode", "mcp__fixture" })
        {
            using var output = new StringWriter();
            using var error = new StringWriter();
            var columns = 20;
            var screen = new TerminalScreen(output, error, () => columns, () => 80);
            var renderer = new PiSharpToolRenderer(
                renderResult: (result, _) => new([new(result.Text ?? "")]));
            var transcript = new InteractiveTranscript(output, error, screen: screen,
                toolRenderer: name => name == tool ? renderer : null);
            var results = new[]
            {
                new string('x', 300),
                string.Join('\n', Enumerable.Range(1, 6).Select(line => $"row{line}:{new string('y', 35)}"))
            };

            foreach (var result in results)
            {
                transcript.Render(new("tool_execution_finished", Tool: tool, Text: result));
                var collapsedFrame = CurrentFrame(output, columns);
                Assert.Contains("more rows", collapsedFrame);
                if (result.StartsWith('x'))
                    Assert.True(collapsedFrame.Count(character => character == 'x') < result.Length);
                else
                    Assert.DoesNotContain("row6:", collapsedFrame);

                if (tool == "bash" && result.StartsWith('x'))
                {
                    columns = 80;
                    screen.RefreshIfResized();
                    var widerFrame = CurrentFrame(output, columns);
                    Assert.DoesNotContain("more rows", widerFrame);
                    Assert.Equal(result.Length, widerFrame.Count(character => character == 'x'));
                    columns = 20;
                    screen.RefreshIfResized();
                }

                screen.SetToolResultsExpanded(true);
                var expandedFrame = CurrentFrame(output, columns);
                Assert.Contains(result[^10..], expandedFrame);
                screen.SetToolResultsExpanded(false);
            }

            screen.Dispose();
            Assert.Empty(error.ToString());
        }

        static string CurrentFrame(StringWriter output, int columns)
        {
            var text = output.ToString();
            var frames = TerminalOutputFrameReader.Read(text, rows: 80, columns: columns);
            Assert.NotEmpty(frames);
            return frames[^1].Screen;
        }
    }

    [Fact]
    public void ToolImageRendersAsKittyRowsOnlyWhenExpandedAndStaysOutOfCapturedText()
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        var renderer = new TerminalImageRenderer(name => name == "KITTY_WINDOW_ID" ? "2" : null);
        var screen = new TerminalScreen(output, error, () => 80, () => 24, renderer);
        var transcript = new InteractiveTranscript(screen.Output, screen.Error, screen: screen);
        var imageBytes = CreateImage(10, 40);
        var update = new AgentLifecycleEvent("tool_execution_finished", Tool: "read", Text: "Read image file [image/png]")
        {
            Images = [new DataContent(imageBytes, "image/png")]
        };
        var serialized = JsonSerializer.Serialize(update);

        transcript.Render(update);
        var collapsed = output.ToString();
        screen.ToggleToolResultsExpanded();
        var expanded = output.ToString();
        screen.SetFooter("next frame");
        var redrawn = output.ToString();
        var suspendStart = output.GetStringBuilder().Length;
        screen.Suspend();
        var suspended = output.ToString()[suspendStart..];
        var resumeStart = output.GetStringBuilder().Length;
        screen.Resume();
        var resumed = output.ToString()[resumeStart..];
        screen.Dispose();

        Assert.DoesNotContain("Images", serialized);
        Assert.DoesNotContain("\u001b_Ga=T", collapsed);
        Assert.Contains("[Image: [image/png] 10x40]", collapsed);
        Assert.Contains("\u001b_Ga=T", expanded);
        Assert.DoesNotContain("psimg-", expanded);
        Assert.Contains("\u001b_Ga=p", redrawn);
        Assert.Contains("\u001b_Ga=d,d=a", suspended);
        Assert.True(suspended.IndexOf("\u001b_Ga=d,d=a", StringComparison.Ordinal) <
            suspended.IndexOf("\u001b[?25h\u001b[?1049l", StringComparison.Ordinal));
        Assert.Contains("\u001b_Ga=p", resumed);
        Assert.Contains("\u001b_Ga=d,d=A", output.ToString());
        Assert.Empty(error.ToString());
        Assert.DoesNotContain(Convert.ToBase64String(imageBytes), error.ToString());
    }

    [Fact]
    public void AcceptedPromptRendersUserTextAndImageInTranscriptWithoutSerializingImage()
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        var screen = new TerminalScreen(output, error, () => 80, () => 40,
            new TerminalImageRenderer(name => name == "KITTY_WINDOW_ID" ? "2" : null));
        var transcript = new InteractiveTranscript(screen.Output, screen.Error, screen: screen);
        var imageBytes = CreateImage(12, 7);
        var update = new AgentLifecycleEvent("prompt_accepted", Text: "inspect this")
        {
            Images = [new DataContent(imageBytes, "image/png")]
        };

        transcript.Render(update);
        var rendered = output.ToString();
        var serialized = JsonSerializer.Serialize(update);
        screen.Dispose();

        Assert.DoesNotContain("› inspect this", rendered);
        Assert.Contains("inspect this", rendered);
        Assert.Contains(screen.CurrentTheme.Bg("userMessageBg"), rendered);
        Assert.Contains("\u001b_Ga=T", rendered);
        Assert.DoesNotContain("psimg-", rendered);
        Assert.DoesNotContain("Images", serialized);
        Assert.DoesNotContain(Convert.ToBase64String(imageBytes), serialized);
        Assert.Contains("[Image: [image/png] 12x7]", output.ToString());

        using var fallbackOutput = new StringWriter();
        using var fallbackError = new StringWriter();
        using var fallbackScreen = new TerminalScreen(fallbackOutput, fallbackError, () => 80, () => 40,
            new TerminalImageRenderer(_ => null));
        new InteractiveTranscript(fallbackScreen.Output, fallbackScreen.Error, screen: fallbackScreen).Render(update);
        var fallbackViewport = TerminalOutputFrameReader.Read(fallbackOutput.ToString(), rows: 40, columns: 80)[^1].Screen;
        Assert.Contains("[Image: [image/png] 12x7]", fallbackViewport);
    }

    [Fact]
    public void ThemeChangeRendersPreviouslyCommittedMarkdownWithTheNewPalette()
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        var light = TerminalThemeCatalog.LoadBuiltIn("light", TerminalColorMode.TrueColor);
        var dark = TerminalThemeCatalog.LoadBuiltIn("dark", TerminalColorMode.TrueColor);
        using var screen = new TerminalScreen(output, error, () => 80, () => 24, new TerminalImageRenderer(), light);

        screen.CommitAssistantText("# Themed heading");
        screen.SetTheme(dark);

        var outputText = output.ToString();
        var lastFrame = outputText.LastIndexOf("\u001b[?2026h", StringComparison.Ordinal);
        Assert.True(lastFrame >= 0);
        var currentFrame = outputText[lastFrame..];
        Assert.Contains("\u001b[38;2;205;154;34m", currentFrame);
        Assert.DoesNotContain("\u001b[38;2;143;104;2m", currentFrame);
    }

    [Fact]
    public void LiveAssistantReflowsAcrossThemeAndThinkingChangesThenCommitsWithoutOldDrafts()
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        var light = TerminalThemeCatalog.LoadBuiltIn("light", TerminalColorMode.TrueColor);
        var dark = TerminalThemeCatalog.LoadBuiltIn("dark", TerminalColorMode.TrueColor);
        using var screen = new TerminalScreen(output, error, () => 80, () => 24,
            new TerminalImageRenderer(), light);
        screen.SetAssistantThinkingText("live reasoning");
        screen.SetAssistantText("# Streaming heading");
        Assert.Contains("live reasoning", VisibleFrame(output.ToString()));

        screen.SetThinkingBlocksHidden(true);
        screen.SetTheme(dark);
        var live = VisibleFrame(output.ToString());
        Assert.Contains("Thinking...", live);
        Assert.Contains("Streaming heading", live);
        Assert.DoesNotContain("live reasoning", live);
        var lastFrame = output.ToString().LastIndexOf("\u001b[?2026h", StringComparison.Ordinal);
        Assert.Contains(dark.Fg("mdHeading"), output.ToString()[lastFrame..]);

        screen.CommitAssistantThinkingText("committed reasoning");
        screen.CommitAssistantText("# Final heading");
        screen.SetThinkingBlocksHidden(false);
        var committed = VisibleFrame(output.ToString());
        Assert.Contains("committed reasoning", committed);
        Assert.Contains("Final heading", committed);
        Assert.DoesNotContain("live reasoning", committed);
        Assert.DoesNotContain("Streaming heading", committed);
    }

    [Fact]
    public void ThinkingVisibilityToggleRendersRetainedReasoningAsPiDoes()
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        using var screen = new TerminalScreen(output, error, () => 80, () => 24);
        var transcript = new InteractiveTranscript(screen.Output, screen.Error, screen: screen);

        transcript.Render(new AgentLifecycleEvent("reasoning_delta", Text: "private reasoning"));
        transcript.Render(new AgentLifecycleEvent("model_text_delta", Text: "answer"));
        transcript.FinishTurn();

        var visibleFrame = VisibleFrame(output.ToString());
        Assert.Contains("private reasoning", visibleFrame);
        screen.SetThinkingBlocksHidden(true);
        var hiddenFrame = VisibleFrame(output.ToString());
        Assert.Contains("Thinking...", hiddenFrame);
        Assert.DoesNotContain("private reasoning", hiddenFrame);

        screen.SetThinkingBlocksHidden(false);
        var shownFrame = VisibleFrame(output.ToString());
        Assert.Contains("private reasoning", shownFrame);
        Assert.Contains("answer", shownFrame);
    }

    [Fact]
    public void HiddenThinkingLabelCanBeOverriddenAndRestoredWithoutChangingVisibility()
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        using var screen = new TerminalScreen(output, error, () => 80, () => 24);
        screen.AppendAssistantThinkingBlock("private reasoning");
        screen.SetThinkingBlocksHidden(true);

        screen.SetHiddenThinkingLabel("Reasoning hidden by policy");
        var overridden = VisibleFrame(output.ToString());
        Assert.Contains("Reasoning hidden by policy", overridden);
        Assert.DoesNotContain("private reasoning", overridden);
        Assert.True(screen.ThinkingBlocksHidden);

        screen.SetHiddenThinkingLabel(null);
        var restored = VisibleFrame(output.ToString());
        Assert.Contains("Thinking...", restored);
        Assert.DoesNotContain("private reasoning", restored);
        Assert.True(screen.ThinkingBlocksHidden);
    }

    private static string VisibleFrame(string output)
    {
        var frames = TerminalOutputFrameReader.Read(output, rows: 24, columns: 80);
        return frames.Count == 0 ? output : frames[^1].Screen;
    }

    [Fact]
    public void CodeBlockIndentChangeRendersPreviouslyCommittedMarkdownAgain()
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        using var screen = new TerminalScreen(output, error, () => 80, () => 24);

        screen.CommitAssistantText("```text\nline\n```");
        screen.SetMarkdownCodeBlockIndent(">>");

        var outputText = output.ToString();
        var lastFrame = outputText.LastIndexOf("\u001b[?2026h", StringComparison.Ordinal);
        Assert.True(lastFrame >= 0);
        var visibleFrame = Regex.Replace(outputText[lastFrame..], "\\u001b\\[[0-9;]*m", "");
        Assert.Contains(" >>line", visibleFrame);
    }

    [Fact]
    public void ThemeChangeRendersStoredToolViewsWithTheNewPalette()
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        var dark = TerminalThemeCatalog.LoadBuiltIn("dark", TerminalColorMode.TrueColor);
        var light = TerminalThemeCatalog.LoadBuiltIn("light", TerminalColorMode.TrueColor);
        using var screen = new TerminalScreen(output, error, () => 80, () => 24,
            new TerminalImageRenderer(), dark);
        var call = new PiSharpToolRenderView([new("custom tool", PiSharpToolTextStyle.Title, Bold: true)]);
        var result = new PiSharpToolRenderView([new("custom result", PiSharpToolTextStyle.Accent)]);
        var darkCall = TerminalToolPresentation.Render(call, dark);
        var lightCall = TerminalToolPresentation.Render(call, light);
        var lightResult = TerminalToolPresentation.Render(result, light);

        screen.AppendToolCall(call);
        screen.AppendToolResult(result);
        var initial = output.ToString();
        Assert.Contains(darkCall, initial);
        Assert.DoesNotContain(lightCall, initial);

        screen.SetTheme(light);

        var outputText = output.ToString();
        var lastFrame = outputText.LastIndexOf("\u001b[?2026h", StringComparison.Ordinal);
        Assert.True(lastFrame >= 0);
        var currentFrame = outputText[lastFrame..];
        Assert.Contains(lightCall, currentFrame);
        Assert.Contains(lightResult, currentFrame);
        Assert.DoesNotContain(darkCall, currentFrame);
    }

    [Fact]
    public void ReportedTerminalBackgroundResolvesAutomaticThemePair()
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        var root = Path.Combine(Path.GetTempPath(), "pisharp-theme-report-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var catalog = new TerminalThemeCatalog(root, environment: key => key == "COLORTERM" ? "truecolor" : null);
            using var screen = new TerminalScreen(output, error, () => 80, () => 24, new TerminalImageRenderer(),
                catalog.Resolve("light/dark"), queryTerminalColors: true);
            screen.SetThemeResolver(colors => catalog.Resolve("light/dark", colors));
            screen.CommitAssistantText("# Theme follows OSC 11");

            screen.HandleTerminalColorResponse(new(11, new TerminalTheme.Rgb(255, 255, 255)));
            screen.HandleTerminalDeviceAttributes();

            var outputText = output.ToString();
            var lastFrame = outputText.LastIndexOf("\u001b[?2026h", StringComparison.Ordinal);
            Assert.Contains("\u001b[38;2;143;104;2m", outputText[lastFrame..]);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void SystemThemeRequeriesAppearanceAndRendersCommittedMarkdownAgain()
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        var root = Path.Combine(Path.GetTempPath(), "pisharp-system-theme-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var catalog = new TerminalThemeCatalog(root, environment: _ => null, trueColorOverride: true);
            using var screen = new TerminalScreen(output, error, () => 80, () => 24, new TerminalImageRenderer(),
                catalog.Resolve(null), queryTerminalColors: true, followTerminalAppearance: true);
            screen.SetThemeResolver(colors => catalog.Resolve(null, colors));
            screen.CommitAssistantText("# Terminal appearance");

            SendColor(screen, "10;#f8f8f2");
            SendColor(screen, "11;#282a36");
            for (var index = 0; index < 16; index++) SendColor(screen, $"4;{index};#{index:x2}{index:x2}{index:x2}");
            screen.HandleTerminalDeviceAttributes();
            var darkHeading = screen.CurrentTheme.GetConcreteColor("mdHeading");
            Assert.Equal("dark", screen.CurrentTheme.Appearance);

            screen.HandleTerminalColorScheme("light");
            SendColor(screen, "10;#1e1e1e");
            SendColor(screen, "11;#fdf6e3");
            screen.HandleTerminalDeviceAttributes();

            Assert.Equal("light", screen.CurrentTheme.Appearance);
            Assert.NotEqual(darkHeading, screen.CurrentTheme.GetConcreteColor("mdHeading"));
            var lightHeadingAnsi = screen.CurrentTheme.Fg("mdHeading");
            var lastFrame = output.ToString().LastIndexOf("\u001b[?2026h", StringComparison.Ordinal);
            Assert.True(lastFrame >= 0);
            Assert.Contains(lightHeadingAnsi, output.ToString()[lastFrame..]);
            Assert.Equal(2, Count(output.ToString(), "\u001b]10;?"));

            screen.Dispose();
            Assert.Contains("\u001b[?2031l", output.ToString());
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static void SendColor(TerminalScreen screen, string payload)
    {
        Assert.True(TerminalColorResponse.TryParse(payload, out var response));
        screen.HandleTerminalColorResponse(response);
    }

    private static int Count(string text, string value)
    {
        var count = 0;
        for (var index = 0; (index = text.IndexOf(value, index, StringComparison.Ordinal)) >= 0; index += value.Length)
            count++;
        return count;
    }

    private static byte[] CreateImage(int width, int height)
    {
        using var bitmap = new SKBitmap(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul));
        bitmap.Erase(SKColors.CornflowerBlue);
        using var image = SKImage.FromBitmap(bitmap);
        using var encoded = image.Encode(SKEncodedImageFormat.Png, 100);
        return encoded.ToArray();
    }

    private sealed class ClipboardCopyCommandRunner : IClipboardCommandRunner
    {
        public string? CopiedText { get; private set; }

        public Task<ClipboardCommandResult?> RunAsync(string command, IReadOnlyList<string> arguments,
            string? input, int maximumOutputBytes, TimeSpan timeout, CancellationToken cancellationToken = default)
        {
            Assert.Equal("pbcopy", command);
            CopiedText = input;
            return Task.FromResult<ClipboardCommandResult?>(new(0, []));
        }
    }

}
