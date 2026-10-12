using PiSharp.Cli;
using PiSharp.Cli.Tui;

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
    public void RestoreLastFrameWritesCompactViewportToNormalBuffer()
    {
        using var output = new StringWriter();
        var compositor = new TerminalScreenCompositor(output, new TerminalImageRenderer());
        compositor.Render(new TerminalScreenCompositor.Frame(
            ["transcript", "", "\u001b[2m────\u001b[0m", "   ", "\u001b[7mX\u001b[27m", "cwd", "footer"],
            4, 1, 0, 10, 7, RestoreStartRow: 2));
        output.GetStringBuilder().Clear();

        compositor.RestoreLastFrameToNormalBuffer();

        var restored = output.ToString();
        Assert.Contains("\u001b[?2026h\r\n\r\u001b[2K\u001b[2m────\u001b[0m\r\n\r\u001b[2K   \r\n\r\u001b[2KX", restored);
        Assert.Contains("\r\u001b[2Kcwd\r\n\r\u001b[2Kfooter", restored);
        Assert.EndsWith("\u001b[?7h\r\n\r\n\u001b[?25h\u001b[?2026l", restored);
        Assert.DoesNotContain("\u001b[7m", restored);
        Assert.DoesNotContain("\u001b[27m", restored);
        Assert.DoesNotContain("transcript", restored);
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

    [Fact]
    public void StartupHeaderStaysAboveTranscriptAndStatusNotifications()
    {
        var compositor = new TerminalScreenCompositor(new StringWriter(), new TerminalImageRenderer());

        var frame = compositor.Compose("", 0, null, null, "project history", "fixture-model",
            overlay: null, scrollOffset: 0, columns: 80, height: 24,
            new TranscriptSearchController(), new TerminalMouseRouter(), TerminalTheme.Default,
            statusNotification: "Loaded fixture-model", startupHeader: "brand\nhelp\n\nstartup trust notice");

        Assert.Contains("brand", frame.Rows[0]);
        Assert.Contains("help", frame.Rows[1]);
        Assert.Contains("startup trust notice", frame.Rows[3]);
        Assert.DoesNotContain("Loaded fixture-model", frame.Rows[1]);
        Assert.Contains("Loaded fixture-model", frame.Rows[5]);
        Assert.Contains("project history", frame.Rows[4]);
    }

    [Fact]
    public void StartupDetailsUseTheScrollableTranscriptBelowTheFixedHeader()
    {
        var compositor = new TerminalScreenCompositor(new StringWriter(), new TerminalImageRenderer());

        var frame = compositor.Compose("", 0, null, null, "project history", "fixture-model",
            overlay: null, scrollOffset: 0, columns: 80, height: 24,
            new TranscriptSearchController(), new TerminalMouseRouter(), TerminalTheme.Default,
            startupHeader: "brand", startupContent: "[Skills]\n  startup-skill\n\n[Prompts]\n  /review");

        Assert.Contains("brand", frame.Rows[0]);
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
    public void StartupTrustNoticeUsesAnIndentedPiWidthContinuation()
    {
        var presentation = new TerminalStartupPresentation(null, verbose: false);

        var header = presentation.BuildDetails(TerminalTheme.Default, 120, null,
            "This project is not trusted. Project .pi resources and packages are ignored. Use /trust to save a trust decision, then restart PiSharp.",
            expanded: false);

        Assert.Contains("then\n restart PiSharp.", header);
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

        Assert.Contains("Press Alt+O to show full startup help and loaded resources.", compactHeader);
        Assert.Contains("Alt+O to expand tools", expandedHeader);
        Assert.Contains("startup-skill", compactDetails);
        Assert.DoesNotContain("SKILL.md", compactDetails);
        Assert.Contains("/home/user/.agents/skills/startup-skill/SKILL.md", expandedDetails);
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
            "Esc to interrupt",
            "Ctrl+C to clear",
            "Ctrl+C twice to exit",
            "Ctrl+D to exit (empty)",
            "Ctrl+K to delete to end",
            "Ctrl+P/Ctrl+Shift+P to cycle models",
            "Ctrl+L to select a model",
            "Shift+Tab to cycle thinking level",
            "Ctrl+O to expand tools",
            "Ctrl+G for external editor",
            "/ for commands",
            "! to run bash",
            "!! to run bash (no context)",
            "Alt+Enter to queue follow-up",
            "Alt+Up to edit all queued messages",
            "Ctrl+V to paste files on macOS, images, or text",
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
        Assert.Contains(Environment.CurrentDirectory, frame.Rows[30]);
        Assert.Contains("0.0%/8.2k (auto)", frame.Rows[31]);
        Assert.Equal(28, frame.CursorRow);
        Assert.Equal(1, frame.CursorColumn);
        Assert.False(frame.CursorVisible);
    }

    private static int Count(string text, string value)
    {
        var count = 0;
        for (var index = 0; (index = text.IndexOf(value, index, StringComparison.Ordinal)) >= 0; index += value.Length)
            count++;
        return count;
    }
}
