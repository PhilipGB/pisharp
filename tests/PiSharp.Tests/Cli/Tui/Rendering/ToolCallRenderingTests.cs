using PiSharp.Cli.Tui;
using PiSharp.Runtime.Extensions;
using PiSharp.Runtime.Sessions;

namespace PiSharp.Tests;

public sealed class ToolCallRenderingTests
{
    [Fact]
    public void FallbackToolCallShowsBoundedArguments()
    {
        using var output = new StringWriter();
        using var status = new StringWriter();
        var transcript = new InteractiveTranscript(output, status);
        var longValue = new string('x', 200);
        transcript.Render(new AgentLifecycleEvent("tool_execution_started", Tool: "custom_tool")
        {
            ToolArguments = new Dictionary<string, object?>
            {
                ["query"] = "pi",
                ["long"] = longValue,
                ["text"] = "line one\nline two"
            }
        });
        var rendered = status.ToString();
        Assert.Contains("custom_tool", rendered);
        Assert.Contains("query=\"pi\"", rendered);
        Assert.Contains("long=\"xxx", rendered);
        Assert.Contains("...", rendered);
        Assert.DoesNotContain(longValue, rendered);
    }

    [Fact]
    public void ExpandingToolCallsShowsMultilineArgumentValues()
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        using var screen = new TerminalScreen(output, error, () => 100, () => 24);
        var transcript = new InteractiveTranscript(screen.Output, screen.Error, screen: screen);
        var longValue = new string('x', 120);
        transcript.Render(new AgentLifecycleEvent("tool_execution_started", Tool: "custom_tool")
        {
            ToolArguments = new Dictionary<string, object?>
            {
                ["query"] = "pi",
                ["long"] = longValue,
                ["text"] = "line one\nline two"
            }
        });
        var collapsed = VisibleFrame(output.ToString());
        Assert.Contains("query=\"pi\"", collapsed);
        Assert.DoesNotContain(longValue, collapsed);
        screen.ToggleToolResultsExpanded();
        var expanded = VisibleFrame(output.ToString());
        Assert.Contains("query: pi", expanded);
        Assert.Contains("long:", expanded);
        Assert.True(expanded.Count(character => character == 'x') >= longValue.Length);
        Assert.Contains("text: line one", expanded);
        Assert.Contains("line two", expanded);
    }

    [Fact]
    public void ExpandingToolResultsRerendersTheRendererWithExpandedContext()
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        using var screen = new TerminalScreen(output, error, () => 100, () => 24);
        var renderer = new PiSharpToolRenderer(renderResult: (_, context) =>
            PiSharpToolRenderView.FromText(context.IsExpanded ? "all six lines shown" : "five lines shown"));
        var transcript = new InteractiveTranscript(screen.Output, screen.Error, screen: screen,
            toolRenderer: _ => renderer);
        transcript.Render(new AgentLifecycleEvent("tool_execution_finished", Tool: "custom_tool", Text: "body"));
        Assert.Contains("five lines shown", VisibleFrame(output.ToString()));
        Assert.DoesNotContain("all six lines shown", VisibleFrame(output.ToString()));
        screen.ToggleToolResultsExpanded();
        Assert.Contains("all six lines shown", VisibleFrame(output.ToString()));
    }

    [Fact]
    public void MutedToolStyleUsesItsColorWithoutAddingDimUnlessRequested()
    {
        var theme = TerminalThemeCatalog.LoadBuiltIn("dark", TerminalColorMode.TrueColor);
        var muted = TerminalToolPresentation.Render(
            PiSharpToolRenderView.FromText("Elapsed 0.0s", PiSharpToolTextStyle.Muted), theme);
        var explicitlyDimmed = TerminalToolPresentation.Render(new(
            [new("Elapsed 0.0s", PiSharpToolTextStyle.Muted, Dim: true)]), theme);

        Assert.Contains(theme.Fg("muted"), muted);
        Assert.DoesNotContain("\u001b[2m", muted);
        Assert.Contains("\u001b[2m", explicitlyDimmed);
    }

    [Fact]
    public void ToolPanelDoesNotStyleAnEmptyLineAfterItsTrailingNewline()
    {
        var theme = TerminalThemeCatalog.LoadBuiltIn("dark", TerminalColorMode.TrueColor);
        var rendered = TerminalConversationPresentation.RenderToolPanel("tool output\n", theme, "toolPendingBg");

        Assert.EndsWith("\u001b[0m\n\u001b[0m", rendered, StringComparison.Ordinal);
        Assert.Equal(1, Count(rendered, theme.Bg("toolPendingBg")));

        var padded = TerminalConversationPresentation.RenderToolPanel("tool output\n", theme,
            "toolSuccessBg", bottomPadding: true);
        Assert.EndsWith(theme.Bg("toolSuccessBg") + " \u001b[0m", padded, StringComparison.Ordinal);

        var blankRow = "\u001b[0m" + theme.Bg("toolSuccessBg") + "\u001b[0m";
        var terminalRow = TerminalScreenCompositor.PadTranscriptRow(blankRow, transcriptWidth: 78, outputPadding: 1);
        Assert.Equal(80, TerminalTextLayout.Width(terminalRow));
        Assert.StartsWith("\u001b[0m" + theme.Bg("toolSuccessBg"), terminalRow, StringComparison.Ordinal);
    }

    [Fact]
    public void ToolPanelBottomPaddingSurvivesTranscriptWrapping()
    {
        var theme = TerminalThemeCatalog.LoadBuiltIn("dark", TerminalColorMode.TrueColor);
        var rendered = TerminalConversationPresentation.RenderToolPanel("tool output\n", theme,
            "toolSuccessBg", bottomPadding: true);
        var layout = TerminalTextLayout.Create(rendered + Environment.NewLine, width: 78);

        Assert.True(layout.RowCount >= 2);
        var blankPanelRow = layout.Rows.TakeLast(2).FirstOrDefault(row =>
            row.Contains(theme.Bg("toolSuccessBg"), StringComparison.Ordinal));
        Assert.NotNull(blankPanelRow);
        var terminalRow = TerminalScreenCompositor.PadTranscriptRow(blankPanelRow,
            transcriptWidth: 78, outputPadding: 1);
        Assert.Equal(80, TerminalTextLayout.Width(terminalRow));
        Assert.StartsWith("\u001b[0m" + theme.Bg("toolSuccessBg"), terminalRow, StringComparison.Ordinal);
    }

    [Fact]
    public void WrappedToolPanelPaddingKeepsTheActiveTextStyleUntilTheRowEnds()
    {
        var theme = TerminalThemeCatalog.LoadBuiltIn("dark", TerminalColorMode.TrueColor);
        var background = theme.Bg("toolPendingBg");
        var styledCommand = theme.Style("toolTitle", new string('x', 79), bold: true);
        var row = TerminalTextLayout.Create(background + styledCommand, width: 78).Rows[0];

        var padded = TerminalScreenCompositor.PadTranscriptRow(row, transcriptWidth: 78, outputPadding: 1,
            preserveActiveStyleOnPadding: true);
        var cellStyle = TerminalTextLayout.SgrStateAtLastCell(row);
        var expected = row[..background.Length] + " " + row[background.Length..^"\u001b[0m".Length] +
            cellStyle + " \u001b[0m";

        Assert.Equal(expected, padded);
    }

    [Fact]
    public void TranscriptCompositorKeepsBlankRowsWithAVisiblePanelBackground()
    {
        var theme = TerminalThemeCatalog.LoadBuiltIn("dark", TerminalColorMode.TrueColor);
        var row = theme.Bg("toolSuccessBg") + " " + "\u001b[0m";

        Assert.False(TerminalScreenCompositor.IsBlankRow(row));
        Assert.True(TerminalScreenCompositor.IsBlankRow("\u001b[2m \u001b[0m"));
    }

    [Fact]
    public void ToolResultCollapsedPreviewRetainsItsBottomPanelPadding()
    {
        var theme = TerminalThemeCatalog.LoadBuiltIn("dark", TerminalColorMode.TrueColor);
        var transcript = new TerminalTranscriptBuffer();
        var presentation = new TerminalConversationPresentation(transcript, new TerminalImageRenderer(_ => null),
            () => 80, () => theme);
        var view = PiSharpToolRenderView.FromText("read fixture-image.png");

        Assert.True(presentation.AppendToolResult(view, view, images: null));

        Assert.Contains(theme.Bg("toolSuccessBg") + " \u001b[0m", transcript.GetText(78));
    }

    [Theory]
    [InlineData("kitty", true)]
    [InlineData("xterm", false)]
    public void ReadToolPathUsesFileHyperlinkOnlyOnSupportedTerminals(string termProgram, bool supported)
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        using var screen = new TerminalScreen(output, error, () => 100, () => 24);
        var workingDirectory = Path.GetFullPath(".");
        var environment = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["TERM_PROGRAM"] = termProgram,
            ["TERM"] = "xterm-256color"
        };
        var transcript = new InteractiveTranscript(screen.Output, screen.Error, screen: screen,
            workingDirectory: workingDirectory,
            terminalEnvironment: name => environment.GetValueOrDefault(name));

        transcript.Render(new AgentLifecycleEvent("tool_execution_started", Tool: "read")
        {
            ToolArguments = new Dictionary<string, object?> { ["path"] = "fixture-image.png" }
        });

        var frames = TerminalOutputFrameReader.Read(output.ToString(), rows: 24, columns: 100);
        Assert.NotEmpty(frames);
        var frame = frames[^1];
        Assert.Contains("read fixture-image.png", frame.Screen);
        var fileUrl = new Uri(Path.GetFullPath("fixture-image.png", workingDirectory)).AbsoluteUri;
        var open = $"\u001b]8;;{fileUrl}\u001b\\";
        Assert.Equal(supported, frame.Output.Contains(open, StringComparison.Ordinal));
        Assert.Equal(supported, frame.Output.Contains("\u001b]8;;\u001b\\", StringComparison.Ordinal));
    }

    private static string VisibleFrame(string output)
    {
        var frames = TerminalOutputFrameReader.Read(output, rows: 24, columns: 100);
        return frames.Count == 0 ? output : frames[^1].Screen;
    }

    private static int Count(string text, string value)
    {
        var count = 0;
        for (var offset = 0; (offset = text.IndexOf(value, offset, StringComparison.Ordinal)) >= 0;
            offset += value.Length)
            count++;
        return count;
    }
}
