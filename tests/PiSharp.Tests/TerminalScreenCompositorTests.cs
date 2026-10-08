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

        var frame = compositor.Compose("", 0, null, null, "", "0.0%/8.2k (auto)          fixture-model",
            overlay: null, scrollOffset: 0, columns: 100, height: 32,
            new TranscriptSearchController(), new TerminalMouseRouter(), TerminalTheme.Default);

        Assert.Contains('─', frame.Rows[27]);
        Assert.Contains("\u001b[7m ", frame.Rows[28]);
        Assert.Contains('─', frame.Rows[29]);
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
