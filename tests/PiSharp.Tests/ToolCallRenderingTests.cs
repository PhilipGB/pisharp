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

    private static string VisibleFrame(string output)
    {
        var frames = TerminalOutputFrameReader.Read(output, rows: 24, columns: 100);
        return frames.Count == 0 ? output : frames[^1].Screen;
    }
}
