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
        Assert.Contains("Alt+O to expand or collapse details and tool output", expandedHeader);
        Assert.Contains("startup-skill", compactDetails);
        Assert.DoesNotContain("SKILL.md", compactDetails);
        Assert.Contains("/home/user/.agents/skills/startup-skill/SKILL.md", expandedDetails);
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
