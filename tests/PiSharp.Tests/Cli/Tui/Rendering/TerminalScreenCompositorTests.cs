using PiSharp.Cli;
using PiSharp.Cli.Tui;
using PiSharp.Extensions.Abstractions;

namespace PiSharp.Tests;

public sealed class TerminalScreenCompositorTests
{
    [Fact]
    public void RenderClearsOnlyChangedRowsAfterTheInitialFrame()
    {
        using var output = new StringWriter();
        var compositor = new TerminalScreenCompositor(output, new TerminalImageRenderer());
        var first = new TerminalScreenCompositor.Frame(["first", "second"], 1, 1, 0, 10, 2);
        compositor.Render(first);
        output.GetStringBuilder().Clear();

        compositor.Render(first with { Rows = ["first", "updated"] });

        var update = output.ToString();
        Assert.DoesNotContain("\u001b[2J", update);
        Assert.Equal(1, Count(update, "\u001b[2K"));
        Assert.Contains("\u001b[2;1H\u001b[2Kupdated", update);

        output.GetStringBuilder().Clear();
        compositor.Render(first with { Rows = ["first", "updated"] });
        var cursorOnly = output.ToString();
        Assert.DoesNotContain("\u001b[2K", cursorOnly);
        Assert.Contains("\u001b[?2026h", cursorOnly);
        Assert.Contains("\u001b[?2026l", cursorOnly);
    }

    [Fact]
    public void RegularScreenStreamsTheFullDocumentAndAppendsNewRowsWithoutClearingScrollback()
    {
        using var output = new StringWriter();
        var compositor = new TerminalScreenCompositor(output, new TerminalImageRenderer(), useAlternateScreen: false);
        var frame = new TerminalScreenCompositor.Frame(["viewport row"], 0, 1, 0, 20, 4,
            DocumentRows: ["first row", "second row"], DocumentCursorRow: 1, DocumentCursorColumn: 2);

        compositor.Render(frame);

        var initial = output.ToString();
        Assert.Contains("first row\r\nsecond row", initial);
        Assert.DoesNotContain("viewport row", initial);
        Assert.DoesNotContain("\u001b[2J", initial);
        output.GetStringBuilder().Clear();

        compositor.Render(frame with
        {
            DocumentRows = ["first row", "second row", "third row"],
            DocumentCursorRow = 2
        });

        var update = output.ToString();
        Assert.Contains("\r\n\u001b[2Kthird row", update);
        Assert.DoesNotContain("\u001b[2J", update);
    }

    [Fact]
    public void RegularScreenWidthChangeRedrawsTheDocumentAndClearsObsoleteScrollback()
    {
        using var output = new StringWriter();
        var compositor = new TerminalScreenCompositor(output, new TerminalImageRenderer(), useAlternateScreen: false);
        var frame = new TerminalScreenCompositor.Frame([], 0, 1, 0, 20, 4,
            DocumentRows: ["first row"], DocumentCursorRow: 0, DocumentCursorColumn: 1);
        compositor.Render(frame);
        output.GetStringBuilder().Clear();

        compositor.Render(frame with { Columns = 18, DocumentRows = ["reflowed row"] });

        Assert.Contains("\u001b[2J\u001b[H\u001b[3J", output.ToString());
        Assert.Contains("reflowed row", output.ToString());
    }

    [Fact]
    public void RegularScreenExitMovesToTheNextDocumentRowLikePinnedPi()
    {
        using var output = new StringWriter();
        var compositor = new TerminalScreenCompositor(output, new TerminalImageRenderer(), useAlternateScreen: false);
        compositor.Render(new TerminalScreenCompositor.Frame([], 0, 1, 0, 20, 4,
            DocumentRows: ["first row", "last row"], DocumentCursorRow: 0, DocumentCursorColumn: 1));
        output.GetStringBuilder().Clear();

        compositor.LeaveMainScreen();

        Assert.EndsWith("\r\n\u001b[?25h", output.ToString());
        Assert.Contains(" \u001b[2B\r\n", output.ToString());
        Assert.DoesNotContain("\u001b[?7h", output.ToString());
        Assert.DoesNotContain("\u001b[?2026h", output.ToString());
    }

    [Fact]
    public void TranscriptPaddingPreservesFullWidthUserMessageBackgroundRows()
    {
        var row = "\u001b[44m" + new string(' ', 40) + "\u001b[0m";

        Assert.Equal(row, TerminalScreenCompositor.PadTranscriptRow(row, transcriptWidth: 38, outputPadding: 1));
    }

    [Fact]
    public void TranscriptPaddingFillsBlankToolPanelRowsWithTheirBackground()
    {
        const string background = "\u001b[48;2;1;2;3m";
        var padded = TerminalScreenCompositor.PadTranscriptRow(
            background + "\u001b[0m", transcriptWidth: 8, outputPadding: 1);

        Assert.Equal(background + new string(' ', 10) + "\u001b[0m", padded);
    }

    [Fact]
    public void TranscriptPaddingFillsTheFullWidthOfUnstyledBlankRows()
    {
        Assert.Equal(new string(' ', 80),
            TerminalScreenCompositor.PadTranscriptRow("", transcriptWidth: 78, outputPadding: 1));
    }

    [Fact]
    public void TranscriptPaddingResetsInlineUnderlineBeforeTrailingCells()
    {
        const string reset = "\u001b[0m";
        var row = "\u001b[4mlink\u001b[24m\u001b[39m" + reset + reset;
        var padded = TerminalScreenCompositor.PadTranscriptRow(row, transcriptWidth: 12, outputPadding: 0);

        Assert.Equal(row + new string(' ', 8), padded);
        Assert.Equal("link" + new string(' ', 8), TerminalTextLayout.StripFormatting(padded));
    }

    [Fact]
    public void TranscriptPaddingKeepsToolPanelBackgroundAcrossTrailingCells()
    {
        const string background = "\u001b[48;2;1;2;3m";
        var padded = TerminalScreenCompositor.PadTranscriptRow(
            background + "result\u001b[0m", transcriptWidth: 8, outputPadding: 1);

        Assert.Equal(background + " result\u001b[0m" + background + "   \u001b[0m", padded);
    }

    [Fact]
    public void RestoreLastFrameStreamsTheRegularDocumentBelowTheAlternateScreen()
    {
        using var output = new StringWriter();
        var compositor = new TerminalScreenCompositor(output, new TerminalImageRenderer());
        compositor.Render(new TerminalScreenCompositor.Frame(
            ["fullscreen viewport"], 0, 1, 0, 10, 7,
            DocumentRows: ["transcript", "", "\u001b[2m────\u001b[0m", "   ", "\u001b[7mX\u001b[27m", "cwd", "footer"],
            DocumentCursorRow: 4, DocumentCursorColumn: 1));
        output.GetStringBuilder().Clear();

        compositor.RestoreLastFrameToNormalBuffer();

        var restored = output.ToString();
        Assert.StartsWith("\u001b[?2026h\u001b[?7l\r\u001b[2Ktranscript\r\n\r\u001b[2K\r\n\r\u001b[2K\u001b[2m────", restored);
        Assert.Contains("X", restored);
        Assert.Contains("\r\n\r\u001b[2K\u001b[2m────\u001b[0m\r\n\r\u001b[2K   \r\n\r\u001b[2K\u001b[7mX", restored);
        Assert.Contains("cwd\r\n\r\u001b[2Kfooter", restored);
        Assert.Contains("\u001b[0m\u001b[?7h\u001b[?25h\u001b[?2026l", restored);
        Assert.EndsWith("\u001b[?25h", restored);
        Assert.Contains("\u001b[?25l\u001b[2A\u001b[1G\u001b[0m \u001b[3B\r\n\u001b[?25h", restored);
        Assert.DoesNotContain("fullscreen viewport", restored);
        Assert.Contains("\u001b[7mX\u001b[27m", restored);
        Assert.DoesNotContain("\u001b[2J", restored);
        Assert.DoesNotContain("\u001b[3J", restored);
    }

    [Fact]
    public void LeaveAlternateScreenPreservesTheNormalBufferInItsOwnSynchronizedTransition()
    {
        using var output = new StringWriter();
        var compositor = new TerminalScreenCompositor(output, new TerminalImageRenderer());

        compositor.LeaveAlternateScreen("\u001b[?2004l");

        Assert.Equal("\u001b[?2026h\u001b[?2004l\u001b[?1049l\u001b[?25h\u001b[?2026l", output.ToString());
    }

    [Fact]
    public void RestoreLastFrameAdvancesBelowFullWidthFinalRowLikeThePinnedMainScreen()
    {
        using var output = new StringWriter();
        var compositor = new TerminalScreenCompositor(output, new TerminalImageRenderer());
        compositor.Render(new TerminalScreenCompositor.Frame(
            ["header", "editor", new string('x', 10)], 1, 1, 0, 10, 3));
        output.GetStringBuilder().Clear();

        compositor.RestoreLastFrameToNormalBuffer();

        var restored = output.ToString();
        Assert.Contains(new string('x', 10), restored);
        Assert.Contains("\u001b[0m\u001b[?7h\u001b[?25h\u001b[?2026l", restored);
        Assert.EndsWith("\u001b[?25h", restored);
    }

    [Fact]
    public void RestoreLastFrameReconstructsDocumentAndKeepsExtensionDashboardWithoutTransientOverlays()
    {
        using var output = new StringWriter();
        var compositor = new TerminalScreenCompositor(output, new TerminalImageRenderer());
        var transcript = string.Join('\n', Enumerable.Range(0, 32).Select(index => $"transcript row {index:D2}"));
        var frame = compositor.Compose("draft", 5, null, null, transcript, "fixture-model",
            overlay: ["modal overlay"], scrollOffset: 0, columns: 48, height: 18,
            new TranscriptSearchController(), new TerminalMouseRouter(), TerminalTheme.Default,
            editorPanel: ["focused panel"],
            extensionHeader: [new("extension header", "accent")],
            extensionWidgetsAbove: [new("extension widget", "success")],
            extensionFooter: [new("extension footer", "muted")]);
        compositor.Render(frame);
        output.GetStringBuilder().Clear();

        compositor.RestoreLastFrameToNormalBuffer();

        var restored = output.ToString();
        Assert.DoesNotContain("\u001b[2J", restored);
        Assert.DoesNotContain("\u001b[3J", restored);
        Assert.Contains("extension header", restored);
        Assert.Contains("transcript row 00", restored);
        Assert.Contains("transcript row 31", restored);
        Assert.Contains("extension widget", restored);
        Assert.Contains("draft", restored);
        Assert.Contains("\u001b[7m", restored);
        Assert.Contains("extension footer", restored);
        Assert.DoesNotContain("focused panel", restored);
        Assert.DoesNotContain("modal overlay", restored);
        Assert.True(restored.IndexOf("extension header", StringComparison.Ordinal) <
            restored.IndexOf("transcript row 00", StringComparison.Ordinal));
        Assert.True(restored.IndexOf("extension widget", StringComparison.Ordinal) <
            restored.IndexOf("draft", StringComparison.Ordinal));
        Assert.True(restored.IndexOf("draft", StringComparison.Ordinal) <
            restored.IndexOf("extension footer", StringComparison.Ordinal));
        Assert.Equal(1, restored.Split("\u001b[?2026h", StringSplitOptions.None).Length - 1);
        Assert.Equal(1, restored.Split("\u001b[?2026l", StringSplitOptions.None).Length - 1);
        Assert.Contains("\u001b[?25h\u001b[?2026l", restored);
        Assert.EndsWith("\u001b[?25h", restored);
    }

    [Fact]
    public void FullDocumentRestorationUsesThePiAboveWidgetSpacerAfterTheTranscript()
    {
        var compositor = new TerminalScreenCompositor(new StringWriter(), new TerminalImageRenderer());
        var frame = compositor.Compose("", 0, null, null, "answer\n\n\n", "fixture-model",
            overlay: null, scrollOffset: 0, columns: 48, height: 18,
            new TranscriptSearchController(), new TerminalMouseRouter(), TerminalTheme.Default);
        var rows = frame.DocumentRows!.Select(TerminalTextLayout.StripFormatting).ToArray();
        var answerRow = Array.FindIndex(rows, row => row.Trim() == "answer");
        var editorBorderRow = Array.FindIndex(rows, row => row.Contains('─'));

        Assert.True(answerRow >= 0);
        Assert.True(editorBorderRow > answerRow);
        Assert.Equal(2, editorBorderRow - answerRow);
    }

    [Fact]
    public void StatusNotificationRemainsVisibleAboveThePanel()
    {
        var compositor = new TerminalScreenCompositor(new StringWriter(), new TerminalImageRenderer());

        var frame = compositor.Compose("", 0, null, null, "", "fixture-model",
            overlay: null, scrollOffset: 0, columns: 100, height: 32,
            new TranscriptSearchController(), new TerminalMouseRouter(), TerminalTheme.Default,
            editorPanel: ["manager panel"], statusNotification: "Downloaded owner/model:Q4_K_M");

        Assert.Contains(" Downloaded owner/model:Q4_K_M", frame.Rows[1]);
        Assert.Contains("manager panel", frame.Rows[^3]);

        var idleFrame = compositor.Compose("", 0, null, null, "", "fixture-model",
            overlay: null, scrollOffset: 0, columns: 100, height: 32,
            new TranscriptSearchController(), new TerminalMouseRouter(), TerminalTheme.Default,
            statusNotification: "Downloaded owner/model:Q4_K_M");

        Assert.Contains(" Downloaded owner/model:Q4_K_M", idleFrame.Rows[1]);
    }

    [Fact]
    public void StatusNotificationUsesPiSeverityLabelsAndThemeColors()
    {
        var compositor = new TerminalScreenCompositor(new StringWriter(), new TerminalImageRenderer());
        var theme = TerminalTheme.Default;

        foreach (var (kind, style, prefix) in new[]
        {
            (TerminalStatusNotificationKind.Info, "dim", ""),
            (TerminalStatusNotificationKind.Warning, "warning", "Warning: "),
            (TerminalStatusNotificationKind.Error, "error", "Error: ")
        })
        {
            var frame = compositor.Compose("", 0, null, null, "", "fixture-model",
                overlay: null, scrollOffset: 0, columns: 100, height: 32,
                new TranscriptSearchController(), new TerminalMouseRouter(), theme,
                statusNotification: "fixture notification", statusNotificationKind: kind);

            Assert.Equal(theme.Style(style, " " + prefix + "fixture notification"), frame.Rows[1]);
        }
    }

    [Fact]
    public void SlashCompletionDescriptionColumnFollowsTheLongestCommand()
    {
        var compositor = new TerminalScreenCompositor(new StringWriter(), new TerminalImageRenderer());
        var theme = TerminalTheme.Default;
        var suggestions = new[]
        {
            new EditorCompletionSuggestion("/skill:project-only", "[p] Project-only skill"),
            new EditorCompletionSuggestion("/skill:unsafe&skill", "[t] Temporary-only skill")
        };

        var frame = compositor.Compose("/skill:", 7, null, null, "", "fixture-model",
            overlay: null, scrollOffset: 0, columns: 100, height: 32,
            new TranscriptSearchController(), new TerminalMouseRouter(), theme,
            editorSuggestions: suggestions);

        var selectedRow = Assert.Single(frame.Rows, row => row.StartsWith(
            theme.Style("accent", "→ skill:project-only  [p] Project-only skill"), StringComparison.Ordinal));
        var otherRow = Assert.Single(frame.Rows, row => row.StartsWith(
            "  skill:unsafe&skill" + theme.Style("muted", "  [t] Temporary-only skill"), StringComparison.Ordinal));
        Assert.Equal(100, TerminalTextLayout.Width(selectedRow));
        Assert.Equal(100, TerminalTextLayout.Width(otherRow));
    }

    [Fact]
    public void SettingsPresentationOptionsAffectTheComposedTerminalFrame()
    {
        var compositor = new TerminalScreenCompositor(new StringWriter(), new TerminalImageRenderer());
        EditorCompletionSuggestion[] suggestions =
        [
            new("/first", "first suggestion"),
            new("/second", "second suggestion"),
            new("/third", "third suggestion")
        ];

        var frame = compositor.Compose("abc", 2, null, null, "transcript-test", "fixture-model",
            overlay: null, scrollOffset: 0, columns: 44, height: 18,
            new TranscriptSearchController(), new TerminalMouseRouter(), TerminalTheme.Default,
            editorSuggestions: suggestions, editorPaddingX: 2, outputPad: 1,
            autocompleteMaxVisible: 2, showHardwareCursor: true);
        var rows = frame.Rows.Select(TerminalTextLayout.StripFormatting).ToArray();

        Assert.True(frame.CursorVisible);
        Assert.Contains(rows, row => row.Contains(" transcript-test ", StringComparison.Ordinal));
        Assert.Contains(rows, row => row.Contains("  abc", StringComparison.Ordinal));
        Assert.Contains(rows, row => row.Contains("→ first", StringComparison.Ordinal));
        Assert.Contains(rows, row => row.Contains("second", StringComparison.Ordinal));
        Assert.DoesNotContain(rows, row => row.Contains("third", StringComparison.Ordinal));
    }

    [Fact]
    public void ExtensionPresentationOccupiesIndependentRowsAroundTheEditorAndFooter()
    {
        var compositor = new TerminalScreenCompositor(new StringWriter(), new TerminalImageRenderer());
        var frame = compositor.Compose("draft", 5, null, null, "conversation", "model status",
            overlay: null, scrollOffset: 0, columns: 48, height: 18,
            new TranscriptSearchController(), new TerminalMouseRouter(), TerminalTheme.Default,
            extensionStatuses: [("build", "ready")],
            extensionHeader: [new("extension header", "accent")],
            extensionWidgetsAbove: [new("above editor", "success")],
            extensionWidgetsBelow: [new("below editor", "warning")],
            extensionFooter: [new("extension footer", "muted")]);
        var plain = frame.Rows.Select(TerminalTextLayout.StripFormatting).ToArray();
        var header = Array.FindIndex(plain, row => row.Contains("extension header", StringComparison.Ordinal));
        var conversation = Array.FindIndex(plain, row => row.Contains("conversation", StringComparison.Ordinal));
        var above = Array.FindIndex(plain, row => row.Contains("above editor", StringComparison.Ordinal));
        var editor = Array.FindIndex(plain, row => row.Contains("draft", StringComparison.Ordinal));
        var below = Array.FindIndex(plain, row => row.Contains("below editor", StringComparison.Ordinal));
        var extensionFooter = Array.FindIndex(plain, row => row.Contains("extension footer", StringComparison.Ordinal));

        Assert.True(header < conversation);
        Assert.True(conversation < above);
        Assert.True(above < editor);
        Assert.True(editor < below);
        Assert.True(below < extensionFooter);
        Assert.Contains(plain, row => row.Contains("build: ready", StringComparison.Ordinal));
        Assert.Contains(plain[^1], "model status");
        Assert.DoesNotContain("above editor", frame.Rows[^1]);
    }

    [Fact]
    public void ExtensionWidgetsAreSanitizedAndRemainInsideVeryShortScreens()
    {
        var compositor = new TerminalScreenCompositor(new StringWriter(), new TerminalImageRenderer());
        var frame = compositor.Compose("draft", 5, null, null, "conversation", "model status",
            overlay: null, scrollOffset: 0, columns: 20, height: 5,
            new TranscriptSearchController(), new TerminalMouseRouter(), TerminalTheme.Default,
            extensionStatuses: [("status", "live")],
            extensionHeader: [new("\u001b]0;injected\u0007 header")],
            extensionWidgetsAbove: Enumerable.Range(0, 20).Select(index => new PiSharpExtensionUiLine($"top {index}")).ToArray(),
            extensionWidgetsBelow: Enumerable.Range(0, 20).Select(index => new PiSharpExtensionUiLine($"bottom {index}")).ToArray());
        var plain = frame.Rows.Select(TerminalTextLayout.StripFormatting).ToArray();

        Assert.Equal(5, frame.Rows.Count);
        Assert.All(frame.Rows, row => Assert.True(TerminalTextLayout.Width(row) <= 20));
        Assert.Contains(plain[^1], "model status");
        Assert.DoesNotContain("\u001b]0;", string.Join("\n", frame.Rows));
    }

    [Fact]
    public void ExtensionPresentationClipsWideAndCombiningUnicodeByTerminalCells()
    {
        var compositor = new TerminalScreenCompositor(new StringWriter(), new TerminalImageRenderer());
        var frame = compositor.Compose("draft界e\u0301", 5, null, null, "conversation界", "model status",
            overlay: null, scrollOffset: 0, columns: 17, height: 14,
            new TranscriptSearchController(), new TerminalMouseRouter(), TerminalTheme.Default,
            extensionStatuses: [("build", "ready")],
            extensionHeader: [new("header 界界界 e\u0301")],
            extensionWidgetsAbove: [new("above 界界界界界界界界")],
            extensionWidgetsBelow: [new("below e\u0301 界界界界")],
            extensionFooter: [new("footer 界界界界界界界界")],
            extensionCustomOverlay: [new("overlay 界界界界界界")],
            extensionCustomOverlayOptions: new(Left: 3, Top: 2, Width: 11, Height: 5));

        Assert.Equal(14, frame.Rows.Count);
        Assert.All(frame.Rows, row => Assert.InRange(TerminalTextLayout.Width(row), 0, 17));
        Assert.DoesNotContain("\u001b]52;", string.Join("\n", frame.Rows));
        Assert.Contains(frame.Rows.Select(TerminalTextLayout.StripFormatting),
            row => row.Contains("overlay", StringComparison.Ordinal));
    }

    [Fact]
    public void ExtensionFullScreenLayoutsStayWithinBoundsAcrossTerminalSizes()
    {
        for (var columns = 1; columns <= 80; columns++)
            for (var height = 1; height <= 30; height++)
                foreach (var hostingMode in new[] { "focused", "overlay" })
                {
                    var compositor = new TerminalScreenCompositor(new StringWriter(), new TerminalImageRenderer());
                    var frame = compositor.Compose("draft with wrapped text 界", 14, null, null,
                        "conversation content 界", "model status", overlay: null, scrollOffset: 0,
                        columns, height, new TranscriptSearchController(), new TerminalMouseRouter(),
                        TerminalTheme.Default,
                        extensionStatuses: [("build", "ready")],
                        extensionHeader: [new("extension header")],
                        extensionWidgetsAbove: [new("above editor widget")],
                        extensionWidgetsBelow: [new("below editor widget")],
                        extensionFooter: [new("extension footer")],
                        extensionCustomOverlay: hostingMode == "overlay" ? [new("overlay panel")] : null,
                        extensionCustomOverlayOptions: new(Left: columns / 3, Top: height / 3,
                            Width: Math.Max(5, columns / 2), Height: Math.Max(1, height / 2)),
                        extensionCustomPanel: hostingMode == "focused" ? [new("focused custom panel")] : null);

                    Assert.Equal(height, frame.Rows.Count);
                    Assert.All(frame.Rows, row => Assert.InRange(TerminalTextLayout.Width(row), 0, columns));
                    Assert.InRange(frame.CursorRow, 0, height - 1);
                    Assert.InRange(frame.CursorColumn, 1, columns + 1);
                }
    }

    [Fact]
    public void ClearOnShrinkRedrawsWhenContentShrinksAndStillErasesNarrowerRows()
    {
        using var output = new StringWriter();
        var compositor = new TerminalScreenCompositor(output, new TerminalImageRenderer());
        compositor.SetClearOnShrink(true);
        var original = new TerminalScreenCompositor.Frame(
            ["longer content", "second row", "footer"], 0, 1, 0, 20, 3, ContentHeight: 3);
        compositor.Render(original);
        output.GetStringBuilder().Clear();

        compositor.Render(original with { Rows = ["short", "second row", "footer"] });
        var narrowerRowUpdate = output.ToString();
        Assert.DoesNotContain("\u001b[2J", narrowerRowUpdate);
        Assert.Contains("\u001b[2K", narrowerRowUpdate);
        output.GetStringBuilder().Clear();

        compositor.Render(original with { Rows = ["short", "", "footer"], ContentHeight = 2 });
        var contentShrinkUpdate = output.ToString();
        Assert.Equal(1, Count(contentShrinkUpdate, "\u001b[2J"));
        Assert.Contains("\u001b[2;1H\u001b[2K", contentShrinkUpdate);
        Assert.Contains("\u001b[3;1H\u001b[2Kfooter", contentShrinkUpdate);
    }

    [Fact]
    public void ClippedSlashCompletionResetsStyleBeforePaddingTheRow()
    {
        var compositor = new TerminalScreenCompositor(new StringWriter(), new TerminalImageRenderer());
        var theme = TerminalTheme.Default;
        var suggestions = new[]
        {
            new EditorCompletionSuggestion("/skill:long-description", new string('x', 200))
        };

        var frame = compositor.Compose("/skill:", 7, null, null, "", "fixture-model",
            overlay: null, scrollOffset: 0, columns: 120, height: 40,
            new TranscriptSearchController(), new TerminalMouseRouter(), theme,
            editorSuggestions: suggestions);

        var selectedRow = Assert.Single(frame.Rows, row => row.Contains("skill:long-description", StringComparison.Ordinal));
        Assert.Equal(120, TerminalTextLayout.Width(selectedRow));
        Assert.EndsWith("\u001b[0m  ", selectedRow);
    }

    [Theory]
    [InlineData(80)]
    [InlineData(120)]
    public void ShortSlashCompletionLabelsKeepTheMinimumDescriptionColumn(int columns)
    {
        var compositor = new TerminalScreenCompositor(new StringWriter(), new TerminalImageRenderer());
        var theme = TerminalTheme.Default;
        var frame = compositor.Compose("/gui", 4, null, null, "", "fixture-model", overlay: null,
            scrollOffset: 0, columns: columns, height: 32, new TranscriptSearchController(),
            new TerminalMouseRouter(), theme,
            editorSuggestions: [new EditorCompletionSuggestion("/guide", "<subject> — [t] Guide")]);

        var row = Assert.Single(frame.Rows, row => row.Contains("<subject>", StringComparison.Ordinal));
        Assert.StartsWith(theme.Style("accent", "→ guide       <subject> — [t] Guide"), row);
        Assert.Equal(columns, TerminalTextLayout.Width(row));
    }

    [Fact]
    public void MultilineArgumentHintsAreRenderedOnOneSuggestionRow()
    {
        var compositor = new TerminalScreenCompositor(new StringWriter(), new TerminalImageRenderer());
        var theme = TerminalTheme.Default;
        var frame = compositor.Compose("/mul", 4, null, null, "", "fixture-model", overlay: null,
            scrollOffset: 0, columns: 120, height: 32, new TranscriptSearchController(), new TerminalMouseRouter(), theme,
            editorSuggestions: [new EditorCompletionSuggestion("/multi", " <subject>\r\n\n[detail] — [t] Guide ")]);

        var row = Assert.Single(frame.Rows, row => row.Contains("<subject>", StringComparison.Ordinal));
        Assert.StartsWith(theme.Style("accent", "→ multi       <subject> [detail] — [t] Guide"), row);
        Assert.DoesNotContain('\n', row);
        Assert.DoesNotContain('\r', row);
    }

    [Fact]
    public void StartupHeaderStaysAboveTranscriptAndStatusNotifications()
    {
        var compositor = new TerminalScreenCompositor(new StringWriter(), new TerminalImageRenderer());

        var frame = compositor.Compose("", 0, null, null, "project history", "fixture-model",
            overlay: null, scrollOffset: 0, columns: 80, height: 24,
            new TranscriptSearchController(), new TerminalMouseRouter(), TerminalTheme.Default,
            statusNotification: "Loaded fixture-model", startupHeader: "brand",
            startupContent: "help\n\nstartup trust notice");

        Assert.Empty(frame.Rows[0]);
        Assert.Contains("brand", frame.Rows[1]);
        Assert.Contains("help", frame.Rows[2]);
        Assert.Contains("startup trust notice", frame.Rows[4]);
        Assert.Contains("Loaded fixture-model", frame.Rows[3]);
        Assert.Contains("project history", frame.Rows[5]);
    }

    [Fact]
    public void StartupPresentationKeepsItsOwnIndentationWhenTranscriptOutputPaddingChanges()
    {
        var compositor = new TerminalScreenCompositor(new StringWriter(), new TerminalImageRenderer());

        var frame = compositor.Compose("", 0, null, null, "history", "fixture-model",
            overlay: null, scrollOffset: 0, columns: 20, height: 12,
            new TranscriptSearchController(), new TerminalMouseRouter(), TerminalTheme.Default,
            startupHeader: "brand", startupContent: " help", outputPad: 2);

        Assert.StartsWith(" help", frame.Rows[2]);
        Assert.StartsWith("  history", frame.Rows[3]);
        Assert.StartsWith(" help", frame.DocumentRows![2]);
        Assert.StartsWith("  history", frame.DocumentRows[3]);
    }

    [Fact]
    public void WrappedStartupPresentationKeepsTheFixedPiHeaderInset()
    {
        var compositor = new TerminalScreenCompositor(new StringWriter(), new TerminalImageRenderer());

        var frame = compositor.Compose("", 0, null, null, "", "fixture-model",
            overlay: null, scrollOffset: 0, columns: 20, height: 12,
            new TranscriptSearchController(), new TerminalMouseRouter(), TerminalTheme.Default,
            startupHeader: "brand", startupContent: " first line wraps after these words");

        var startupRows = frame.Rows.Skip(2).TakeWhile(row =>
            !string.IsNullOrWhiteSpace(TerminalTextLayout.StripFormatting(row))).ToArray();

        Assert.True(startupRows.Length > 1);
        Assert.All(startupRows, row => Assert.StartsWith(" ", row));
        Assert.All(frame.DocumentRows!.Skip(2).Take(startupRows.Length), row => Assert.StartsWith(" ", row));
    }

    [Fact]
    public void StartupDetailsUseTheScrollableTranscriptBelowTheFixedHeader()
    {
        var compositor = new TerminalScreenCompositor(new StringWriter(), new TerminalImageRenderer());

        var frame = compositor.Compose("", 0, null, null, "project history", "fixture-model",
            overlay: null, scrollOffset: 0, columns: 80, height: 24,
            new TranscriptSearchController(), new TerminalMouseRouter(), TerminalTheme.Default,
            startupHeader: "brand", startupContent: "[Skills]\n  startup-skill\n\n[Prompts]\n  /review");

        Assert.Empty(frame.Rows[0]);
        Assert.Contains("brand", frame.Rows[1]);
        Assert.Contains(frame.Rows, row => row.Contains("[Skills]", StringComparison.Ordinal));
        Assert.Contains(frame.Rows, row => row.Contains("startup-skill", StringComparison.Ordinal));
    }

    [Fact]
    public void UnderfilledTranscriptStartsAtTopOfItsViewport()
    {
        var compositor = new TerminalScreenCompositor(new StringWriter(), new TerminalImageRenderer());

        var frame = compositor.Compose("", 0, null, null, "first message\nsecond message", "fixture-model",
            overlay: null, scrollOffset: 0, columns: 80, height: 24,
            new TranscriptSearchController(), new TerminalMouseRouter(), TerminalTheme.Default);

        Assert.Contains("first message", frame.Rows[0]);
        Assert.Contains("second message", frame.Rows[1]);
    }

    [Fact]
    public void DocumentRowsKeepTrailingZeroWidthImageMarkers()
    {
        const string marker = "\uE000psimg-00000000000000000000000000000000-0\uE001";
        var compositor = new TerminalScreenCompositor(new StringWriter(), new TerminalImageRenderer());

        var frame = compositor.Compose("", 0, null, null, "answer\n" + marker, "fixture-model",
            overlay: null, scrollOffset: 0, columns: 80, height: 24,
            new TranscriptSearchController(), new TerminalMouseRouter());

        Assert.Contains(frame.DocumentRows!, row => row.Contains(marker, StringComparison.Ordinal));
    }

    [Fact]
    public void ScrolledTranscriptKeepsItsVisibleRowsWhileStreamingAndLatestStillFollows()
    {
        var compositor = new TerminalScreenCompositor(new StringWriter(), new TerminalImageRenderer());
        var transcript = string.Join('\n', Enumerable.Range(0, 30).Select(index => $"message {index:D2}"));

        compositor.Compose("", 0, null, null, transcript, "fixture-model", overlay: null,
            scrollOffset: 0, columns: 80, height: 12, new TranscriptSearchController(), new TerminalMouseRouter());
        var scrolled = compositor.Compose("", 0, null, null, transcript, "fixture-model", overlay: null,
            scrollOffset: 8, columns: 80, height: 12, new TranscriptSearchController(), new TerminalMouseRouter());
        Assert.Contains("message 16", scrolled.Rows[0]);

        transcript += "\nmessage 30\nmessage 31\nmessage 32";
        var streaming = compositor.Compose("", 0, null, null, transcript, "fixture-model", overlay: null,
            scrollOffset: scrolled.ScrollOffset, columns: 80, height: 12,
            new TranscriptSearchController(), new TerminalMouseRouter());

        Assert.Contains("message 16", streaming.Rows[0]);
        Assert.DoesNotContain(streaming.Rows, row => row.Contains("message 30", StringComparison.Ordinal));

        var latestCompositor = new TerminalScreenCompositor(new StringWriter(), new TerminalImageRenderer());
        var latestTranscript = string.Join('\n', Enumerable.Range(0, 30).Select(index => $"message {index:D2}"));
        latestCompositor.Compose("", 0, null, null, latestTranscript, "fixture-model", overlay: null,
            scrollOffset: 0, columns: 80, height: 12, new TranscriptSearchController(), new TerminalMouseRouter());
        latestTranscript += "\nmessage 30\nmessage 31\nmessage 32";
        var latest = latestCompositor.Compose("", 0, null, null, latestTranscript, "fixture-model",
            overlay: null, scrollOffset: 0, columns: 80, height: 12,
            new TranscriptSearchController(), new TerminalMouseRouter());

        Assert.Contains(latest.Rows, row => row.Contains("message 32", StringComparison.Ordinal));
        Assert.DoesNotContain(latest.Rows, row => row.Contains("message 00", StringComparison.Ordinal));
    }

    [Fact]
    public void ScrolledTranscriptScrollbarUsesViewportHeightForItsScrollRange()
    {
        var compositor = new TerminalScreenCompositor(new StringWriter(), new TerminalImageRenderer());
        var transcript = string.Join('\n', Enumerable.Range(0, 80).Select(index => $"message {index:D2}"));

        var frame = compositor.Compose("", 0, null, null, transcript, "fixture-model", overlay: null,
            scrollOffset: 1, columns: 80, height: 24, new TranscriptSearchController(), new TerminalMouseRouter());

        var trackHeight = frame.TranscriptHeight;
        var contentHeight = TerminalTextLayout.Create(transcript, 80).RowCount;
        var thumbHeight = Math.Max(Math.Min(2, trackHeight),
            Math.Min(trackHeight, (int)Math.Round((double)trackHeight * trackHeight / contentHeight)));
        var maximumScrollTop = Math.Max(0, contentHeight - frame.TranscriptHeight);
        var scrollTop = Math.Max(0, maximumScrollTop - frame.ScrollOffset);
        var thumbOffset = maximumScrollTop == 0 ? 0 :
            (int)Math.Round((double)scrollTop / maximumScrollTop * (trackHeight - thumbHeight));
        var thumbRow = frame.TranscriptStartRow + thumbOffset;

        Assert.EndsWith("┃", TerminalTextLayout.StripFormatting(frame.Rows[thumbRow]));
        if (thumbOffset > 0)
            Assert.EndsWith("│", TerminalTextLayout.StripFormatting(frame.Rows[thumbRow - 1]));
    }

    [Fact]
    public void TranscriptSearchPositionsTheMatchUsingTheFullscreenScrollableViewport()
    {
        var compositor = new TerminalScreenCompositor(new StringWriter(), new TerminalImageRenderer());
        var transcript = string.Join('\n', Enumerable.Range(0, 120)
            .Select(index => index == 80 ? "unique-search-match" : $"transcript row {index:D3}"));
        var search = new TranscriptSearchController();
        search.SetQuery("unique-search-match", 0);

        var frame = compositor.Compose("", 0, null, null, transcript, "fixture-model", overlay: null,
            scrollOffset: 0, columns: 80, height: 24, search, new TerminalMouseRouter());
        var matchRow = frame.Rows.ToList().FindIndex(row =>
            TerminalTextLayout.StripFormatting(row).Contains("unique-search-match", StringComparison.Ordinal));

        Assert.Equal(frame.TranscriptHeight / 3, matchRow - frame.TranscriptStartRow);
    }

    [Fact]
    public void TranscriptSearchPlacesTheTerminalCursorAtTheQueryCaret()
    {
        var compositor = new TerminalScreenCompositor(new StringWriter(), new TerminalImageRenderer());
        var frame = compositor.Compose("draft", 2, null, null, "transcript", "fixture-model", overlay: null,
            scrollOffset: 0, columns: 80, height: 24, new TranscriptSearchController(),
            new TerminalMouseRouter(), transcriptSearchPanel: ["┌─┐", "│ query │", "└─┘"],
            transcriptSearchCursorColumn: 67);

        Assert.Equal(2, frame.CursorRow);
        Assert.Equal(68, frame.CursorColumn);
        Assert.False(frame.CursorVisible);
    }

    [Fact]
    public void ScrolledUnicodeTranscriptKeepsItsSourceAnchorAfterResize()
    {
        var compositor = new TerminalScreenCompositor(new StringWriter(), new TerminalImageRenderer());
        var transcript = string.Join('\n', Enumerable.Range(0, 30)
            .Select(index => $"item {index:D2} {new string('界', 20)} suffix"));
        const int height = 8;
        const int originalWidth = 24;
        const int resizedWidth = 40;
        const int transcriptHeight = 3;
        var originalLayout = TerminalTextLayout.Create(transcript, originalWidth);
        var targetSourceIndex = transcript.IndexOf("item 20", StringComparison.Ordinal);
        var targetRow = originalLayout.VisualRowAt(targetSourceIndex);
        var scrollOffset = TerminalTranscriptViewport.ScrollOffsetForFirstRow(
            originalLayout.RowCount, targetRow, transcriptHeight);

        compositor.Compose("", 0, null, null, transcript, "fixture-model", overlay: null,
            scrollOffset: 0, columns: originalWidth, height, new TranscriptSearchController(), new TerminalMouseRouter());
        var scrolled = compositor.Compose("", 0, null, null, transcript, "fixture-model", overlay: null,
            scrollOffset, columns: originalWidth, height, new TranscriptSearchController(), new TerminalMouseRouter());
        Assert.Contains("item 20", scrolled.Rows[0]);

        var resized = compositor.Compose("", 0, null, null, transcript, "fixture-model", overlay: null,
            scrolled.ScrollOffset, columns: resizedWidth, height,
            new TranscriptSearchController(), new TerminalMouseRouter());

        Assert.Contains("item 20", resized.Rows[0]);
    }

    [Fact]
    public void StartupTrustNoticeUsesAnIndentedPiWidthContinuation()
    {
        var presentation = new TerminalStartupPresentation(null, verbose: false);

        var header = presentation.BuildDetails(TerminalTheme.Default, 120, null,
            "This project is not trusted. Project .pi resources and packages are ignored. Use /trust to save a trust decision, then restart PiSharp.",
            expanded: false);

        Assert.Contains("then\n restart PiSharp.", header);
    }

    [Fact]
    public void StartupDetailsFollowThePiHeaderSpacer()
    {
        var presentation = new TerminalStartupPresentation(null, verbose: false);
        var content = presentation.BuildContent(TerminalTheme.Default, 120,
            [new TerminalStartupDetail("Skills", ["startup-skill"])],
            "This project is not trusted. Project .pi resources and packages are ignored. Use /trust to save a trust decision, then restart PiSharp.",
            expanded: false);
        var plain = System.Text.RegularExpressions.Regex.Replace(content, "\\u001b\\[[0-?]*[ -/]*[@-~]", "");

        Assert.Contains("extend PiSharp.\n\n[Skills]", plain);
        Assert.Contains("startup-skill\n\n This project is not trusted.", plain);
    }

    [Fact]
    public void EmptyAboveWidgetContainerKeepsThePiLeadingSpacer()
    {
        var compositor = new TerminalScreenCompositor(new StringWriter(), new TerminalImageRenderer(),
            useAlternateScreen: false);
        var frame = compositor.Compose("", 0, null, null, "", "fixture-model", overlay: null,
            scrollOffset: 0, columns: 80, height: 16, new TranscriptSearchController(), new TerminalMouseRouter());

        Assert.Equal("", frame.Rows[10]);
        Assert.Contains("─", frame.Rows[11]);
        Assert.Equal(12, frame.CursorRow);
        Assert.Equal("", frame.DocumentRows![0]);
        Assert.Contains("─", frame.DocumentRows[1]);
        Assert.Equal(2, frame.DocumentCursorRow);
    }

    [Fact]
    public void StartupExpansionShowsConfiguredHelpAndResourcePaths()
    {
        var presentation = new TerminalStartupPresentation(null, verbose: false,
            action => action == "app.tools.expand" ? "Alt+O" : null);
        var details = new[]
        {
            new TerminalStartupDetail("Skills", ["startup-skill"], ["/home/user/.agents/skills/startup-skill/SKILL.md"])
        };

        var compactHeader = presentation.Build(TerminalTheme.Default, expanded: false);
        var expandedHeader = presentation.Build(TerminalTheme.Default, expanded: true);
        var compactDetails = presentation.BuildDetails(TerminalTheme.Default, 120, details, null, expanded: false);
        var expandedDetails = presentation.BuildDetails(TerminalTheme.Default, 120, details, null, expanded: true);

        Assert.Contains("Press alt+o to show full startup help and loaded resources.", compactHeader);
        Assert.Contains("alt+o to expand tools", expandedHeader);
        Assert.Contains("startup-skill", compactDetails);
        Assert.DoesNotContain("SKILL.md", compactDetails);
        Assert.Contains("/home/user/.agents/skills/startup-skill/SKILL.md", expandedDetails);
    }

    [Fact]
    public void CompactStartupHelpUsesCurrentPiKeyNamesAndDoesNotAddAnExtraHintRow()
    {
        var displayKeys = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["app.interrupt"] = "Esc",
            ["app.clear"] = "Ctrl+C",
            ["app.exit"] = "Ctrl+D",
            ["app.tools.expand"] = "Ctrl+O"
        };
        var presentation = new TerminalStartupPresentation(null, verbose: false,
            action => displayKeys.GetValueOrDefault(action));
        var compact = System.Text.RegularExpressions.Regex.Replace(presentation.Build(
            TerminalTheme.Default, expanded: false), "\\u001b\\[[0-?]*[ -/]*[@-~]", "");

        Assert.Contains("escape interrupt · ctrl+c/ctrl+d clear/exit · / commands · ! bash · ctrl+o more", compact);
        Assert.Contains("Press ctrl+o to show full startup help and loaded resources.", compact);
        Assert.DoesNotContain("Use /hotkeys for shortcut help and / for commands.", compact);
    }

    [Fact]
    public void StartupHeaderUsesPinnedLogoColorsAndSeparateKeyHintStyles()
    {
        var theme = TerminalThemeCatalog.LoadBuiltIn("dark", TerminalColorMode.TrueColor);
        var presentation = new TerminalStartupPresentation(null, verbose: false);
        var logo = presentation.BuildLogo(theme);
        var compact = presentation.Build(theme, expanded: false);

        Assert.Contains("\u001b[38;2;228;138;122m\u001b[48;2;79;142;179m▀", logo);
        Assert.Contains("\u001b[38;2;79;142;179m█▀", compact);
        Assert.Contains("\u001b[38;2;234;182;93m█", compact);
        Assert.Contains(theme.Style("dim", "escape") + theme.Style("muted", " interrupt"), compact);
    }

    [Fact]
    public void ExpandedStartupHelpIncludesSupportedPiEditorAndBashActions()
    {
        var displayKeys = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["app.interrupt"] = "Esc",
            ["app.clear"] = "Ctrl+C",
            ["app.exit"] = "Ctrl+D",
            ["tui.editor.deleteToLineEnd"] = "Ctrl+K",
            ["app.model.cycleForward"] = "Ctrl+P",
            ["app.model.cycleBackward"] = "Ctrl+Shift+P",
            ["app.model.select"] = "Ctrl+L",
            ["app.thinking.cycle"] = "Shift+Tab",
            ["app.tools.expand"] = "Ctrl+O",
            ["app.thinking.toggle"] = "Ctrl+T",
            ["app.editor.external"] = "Ctrl+G",
            ["app.message.followUp"] = "Alt+Enter",
            ["app.message.dequeue"] = "Alt+Up",
            ["app.clipboard.pasteImage"] = "Ctrl+V"
        };
        var presentation = new TerminalStartupPresentation(null, verbose: false,
            action => displayKeys.GetValueOrDefault(action));
        var expanded = System.Text.RegularExpressions.Regex.Replace(presentation.Build(TerminalTheme.Default,
            expanded: true), "\\u001b\\[[0-?]*[ -/]*[@-~]", "");

        var expectedHints = new[]
        {
            "escape to interrupt",
            "ctrl+c to clear",
            "ctrl+c twice to exit",
            "ctrl+d to exit (empty)",
            "ctrl+k to delete to end",
            "ctrl+p/ctrl+shift+p to cycle models",
            "ctrl+l to select a model",
            "shift+tab to cycle thinking level",
            "ctrl+o to expand tools",
            "ctrl+t to expand thinking",
            "ctrl+g for external editor",
            "/ for commands",
            "! to run bash",
            "!! to run bash (no context)",
            "alt+enter to queue follow-up",
            "alt+up to edit all queued messages",
            "ctrl+v to paste files on macOS, images, or text",
            "drop files to attach"
        };

        var previousIndex = -1;
        foreach (var hint in expectedHints)
        {
            var currentIndex = expanded.IndexOf(hint, StringComparison.Ordinal);
            Assert.True(currentIndex > previousIndex, $"Expected startup hint in order: {hint}");
            previousIndex = currentIndex;
        }
    }

    [Fact]
    public void ExtensionOverlayFallsBackToClippedContentOnVerySmallTerminals()
    {
        var rows = new[] { "", "" };

        TerminalOverlayLayout.ApplyExtension(rows, ["Extension panel", "Second line"],
            columns: 4, rows: 2, theme: TerminalTheme.Default, options: null);

        Assert.Equal(new[] { "Exte", "Seco" }, rows);
        Assert.All(rows, row => Assert.InRange(TerminalTextLayout.Width(row), 0, 4));
        Assert.DoesNotContain("+", string.Join("", rows));
    }

    [Fact]
    public void QuietStartupStillShowsResourceDiagnosticsWithoutListingLoadedResources()
    {
        var presentation = new TerminalStartupPresentation(QuietStartupMode.Silent, verbose: false);
        var details = new[]
        {
            new TerminalStartupDetail("Skills", ["shared", "project-only"]),
            new TerminalStartupDetail("Skill conflicts", ["\"shared\" collision:", "project wins; user skipped"],
                ShowWhenQuiet: true,
                DiagnosticLines:
                [
                    new("\"shared\" collision:", 2, "warning"),
                    new("project wins; user skipped", 4, "dim", "✓", "success")
                ],
                HeaderStyle: "warning")
        };

        var theme = TerminalTheme.Default;
        var content = presentation.BuildDetails(theme, 120, details, null, expanded: false);

        Assert.Contains(theme.Style("warning", "[Skill conflicts]"), content);
        Assert.Contains(theme.Style("warning", "  \"shared\" collision:"), content);
        Assert.Contains(theme.Style("dim", "    ") + theme.Style("success", "✓") +
            " project wins; user skipped", content);
        Assert.DoesNotContain("[Skills]", content);
        Assert.DoesNotContain("project-only", content);
    }

    [Fact]
    public void QuietStartupRendersThemePathDiagnosticsAsSeparatePiCompatibleRows()
    {
        var presentation = new TerminalStartupPresentation(QuietStartupMode.Silent, verbose: false);
        var details = new TerminalStartupDetail("Theme conflicts",
            ["/project/.pi/extension-themes", "theme path does not exist"],
            ShowWhenQuiet: true, ShowEachItemWhenQuiet: true);

        var rendered = presentation.BuildDetails(TerminalTheme.Default, 120, [details], null, expanded: false);

        Assert.Contains("[Theme conflicts]", rendered);
        Assert.Contains("/project/.pi/extension-themes", rendered);
        Assert.Contains("theme path does not exist", rendered);
        Assert.DoesNotContain(", theme path does not exist", rendered);
    }

    [Fact]
    public void QuietStartupDiagnosticsScrollWithTheConversationDocument()
    {
        var compositor = new TerminalScreenCompositor(new StringWriter(), new TerminalImageRenderer());
        var presentation = new TerminalStartupPresentation(QuietStartupMode.Silent, verbose: false);
        const string path = "/project/.pi/extension-themes";
        var detail = new TerminalStartupDetail("Theme conflicts",
            [path, "theme path does not exist"], ShowWhenQuiet: true, ShowEachItemWhenQuiet: true,
            DiagnosticLines:
            [
                new(path, 2, "warning"),
                new("theme path does not exist", 4, "warning")
            ],
            HeaderStyle: "warning");
        var content = presentation.BuildDetails(TerminalTheme.Default, 100, [detail], null, expanded: false);
        var messages = string.Join('\n', Enumerable.Range(1, 30).Select(index => $"message-{index}"));

        var recent = compositor.Compose("", 0, null, null, messages, "fixture-model",
            overlay: null, scrollOffset: 0, columns: 100, height: 16,
            new TranscriptSearchController(), new TerminalMouseRouter(), TerminalTheme.Default,
            startupContent: content);
        var history = compositor.Compose("", 0, null, null, messages, "fixture-model",
            overlay: null, scrollOffset: 100, columns: 100, height: 16,
            new TranscriptSearchController(), new TerminalMouseRouter(), TerminalTheme.Default,
            startupContent: content);

        var recentText = string.Join('\n', recent.Rows);
        var historyText = string.Join('\n', history.Rows);
        Assert.Contains("message-30", recentText);
        Assert.DoesNotContain("[Theme conflicts]", recentText);
        Assert.Contains("[Theme conflicts]", historyText);
        Assert.Contains(path, historyText);
    }

    [Fact]
    public void ModalOverlayCoversUnderlyingStatusNotification()
    {
        var compositor = new TerminalScreenCompositor(new StringWriter(), new TerminalImageRenderer());

        var frame = compositor.Compose("", 0, null, null, "", "fixture-model",
            overlay: ["Selection title"], scrollOffset: 0, columns: 40, height: 3,
            new TranscriptSearchController(), new TerminalMouseRouter(), TerminalTheme.Default,
            statusNotification: "Loaded fixture-model");

        Assert.Contains("Selection title", frame.Rows[1]);
        Assert.DoesNotContain("Loaded fixture-model", frame.Rows[1]);
    }

    [Fact]
    public void SelectionFlashOverlaysTheRightSideOfTheFirstRenderedRow()
    {
        var compositor = new TerminalScreenCompositor(new StringWriter(), new TerminalImageRenderer());

        var frame = compositor.Compose("", 0, null, null, "transcript", "fixture-model",
            overlay: null, scrollOffset: 0, columns: 40, height: 12,
            new TranscriptSearchController(), new TerminalMouseRouter(), TerminalTheme.Default,
            transientFlash: "Copied!");

        Assert.EndsWith("\u001b[7m Copied! \u001b[27m", frame.Rows[0]);
        Assert.Equal(40, TerminalTextLayout.Width(frame.Rows[0]));
    }

    [Fact]
    public void SmallModelPickerKeepsWarningColorAcrossWrappedHintPadding()
    {
        const int width = 44;
        const string hint = "Only showing models from configured providers. Use /login to add providers.";
        var theme = TerminalTheme.Default;
        var selection = new TerminalSelectionList<string>("Select model",
            [new TerminalSelectionOption<string>("model", "model", "model")]);
        var panel = selection.RenderModelPicker(width, theme, hint, "Refreshing model catalogs…",
            refreshSuccess: false, model => model, out _, out _);
        var expected = panel[2];
        var compositor = new TerminalScreenCompositor(new StringWriter(), new TerminalImageRenderer());

        var frame = compositor.Compose("", 0, null, null, "", "fixture-model", overlay: null,
            scrollOffset: 0, columns: width, height: 14, new TranscriptSearchController(),
            new TerminalMouseRouter(), theme, editorPanel: panel, panelBottomMargin: 2);

        Assert.Equal(expected, frame.Rows[3]);
    }

    [Fact]
    public void IdleDockMatchesThePiEditorDirectoryAndStatusRows()
    {
        var compositor = new TerminalScreenCompositor(new StringWriter(), new TerminalImageRenderer());
        var theme = TerminalThemeCatalog.LoadBuiltIn("dark", TerminalColorMode.TrueColor);

        var frame = compositor.Compose("", 0, null, null, "", "0.0%/8.2k (auto)          fixture-model",
            overlay: null, scrollOffset: 0, columns: 100, height: 32,
            new TranscriptSearchController(), new TerminalMouseRouter(), theme);

        Assert.Contains('─', frame.Rows[27]);
        Assert.Contains("\u001b[7m ", frame.Rows[28]);
        Assert.Contains('─', frame.Rows[29]);
        Assert.Equal(theme.Style("thinkingOff", new string('─', 100)), frame.Rows[27]);
        Assert.Equal(theme.Style("thinkingOff", new string('─', 100)), frame.Rows[29]);
        var homeDirectory = Environment.GetEnvironmentVariable("HOME") ??
            Environment.GetEnvironmentVariable("USERPROFILE");
        Assert.Contains(TerminalPathDisplay.FormatForFooter(Environment.CurrentDirectory, homeDirectory), frame.Rows[30]);
        Assert.Contains("0.0%/8.2k (auto)", frame.Rows[31]);
        Assert.Equal(28, frame.CursorRow);
        Assert.Equal(1, frame.CursorColumn);
        Assert.False(frame.CursorVisible);
    }

    [Fact]
    public void ExtensionWorkingIndicatorFramesKeepTheirSuppliedForeground()
    {
        var compositor = new TerminalScreenCompositor(new StringWriter(), new TerminalImageRenderer());
        var theme = TerminalThemeCatalog.LoadBuiltIn("dark", TerminalColorMode.TrueColor);

        var custom = compositor.Compose("", 0, null, null, "", "fixture-model",
            overlay: null, scrollOffset: 0, columns: 80, height: 24,
            new TranscriptSearchController(), new TerminalMouseRouter(), theme,
            workingStatus: "Working", workingFrame: "◆", workingFrameVerbatim: true);
        var defaultFrame = compositor.Compose("", 0, null, null, "", "fixture-model",
            overlay: null, scrollOffset: 0, columns: 80, height: 24,
            new TranscriptSearchController(), new TerminalMouseRouter(), theme,
            workingStatus: "Working", workingFrame: "◆");

        Assert.Contains(theme.Style("thinkingOff", "── ") + "◆ ", custom.Rows[19]);
        Assert.DoesNotContain(theme.Fg("thinkingOff") + "◆", custom.Rows[19]);
        Assert.Contains(theme.Fg("thinkingOff") + "◆", defaultFrame.Rows[19]);
    }

    [Fact]
    public void TranscriptPaddingResetsInlineStylesBeforeTrailingCells()
    {
        var padded = TerminalScreenCompositor.PadTranscriptRow(
            "\u001b[1m\u001b[38;2;1;2;3mtext\u001b[22m\u001b[39m\u001b[0m\u001b[0m",
            transcriptWidth: 8, outputPadding: 1);
        var resetHeading = TerminalScreenCompositor.PadTranscriptRow(
            "\u001b[1m\u001b[4m\u001b[38;2;1;2;3mtext\u001b[22m\u001b[24m\u001b[39m\u001b[0m\u001b[0m",
            transcriptWidth: 8, outputPadding: 0);

        Assert.Equal(" \u001b[1m\u001b[38;2;1;2;3mtext\u001b[22m\u001b[39m\u001b[0m\u001b[0m     ", padded);
        Assert.Equal("\u001b[1m\u001b[4m\u001b[38;2;1;2;3mtext\u001b[22m\u001b[24m\u001b[39m\u001b[0m\u001b[0m    ", resetHeading);
    }

    [Fact]
    public void TranscriptPaddingDoesNotCarryStylesDisabledByIndividualSgrResets()
    {
        const string row = "\u001b[1m\u001b[4m\u001b[38;2;1;2;3mtext\u001b[22m\u001b[24m\u001b[39m";

        Assert.Equal(row + "    ", TerminalScreenCompositor.PadTranscriptRow(row, transcriptWidth: 8, outputPadding: 0));
    }

    private static int Count(string text, string value)
    {
        var count = 0;
        for (var index = 0; (index = text.IndexOf(value, index, StringComparison.Ordinal)) >= 0; index += value.Length)
            count++;
        return count;
    }
}
