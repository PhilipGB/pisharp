using PiSharp.Cli.Tui;
using PiSharp.Runtime.Sessions;

namespace PiSharp.Tests;

public sealed class ToolCancellationPresentationTests
{
    [Fact]
    public void CancelledToolUsesPiErrorWordingAndErrorStyle()
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        var theme = TerminalThemeCatalog.LoadBuiltIn("dark", TerminalColorMode.TrueColor);
        using var screen = new TerminalScreen(output, error, () => 80, () => 24,
            new TerminalImageRenderer(), theme);
        var transcript = new InteractiveTranscript(screen.Output, screen.Error, screen: screen);

        transcript.Render(new AgentLifecycleEvent("prompt_accepted", Text: "Run the cancellable fixture tool."));
        transcript.Render(new AgentLifecycleEvent("tool_execution_started", Tool: "bash"));
        transcript.Render(new AgentLifecycleEvent("tool_execution_finished", Tool: "bash", IsError: true,
            Text: "Command aborted", Error: "Command aborted"));
        transcript.Render(new AgentLifecycleEvent("turn_interrupted"));

        var frame = LastFrame(output, 24, 80).Screen;
        Assert.Contains(" Command aborted", frame);
        Assert.Contains(" Error: This operation was aborted", frame);
        Assert.DoesNotContain(" Operation aborted", frame);
        SgrAssertions.AssertStyleAt(output.ToString(), "Error: This operation was aborted",
            foreground: true, italic: false);
    }

    [Fact]
    public void SuccessfulToolCancellationKeepsTextCancellationWording()
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        using var screen = new TerminalScreen(output, error, () => 80, () => 24);
        var transcript = new InteractiveTranscript(screen.Output, screen.Error, screen: screen);

        transcript.Render(new AgentLifecycleEvent("prompt_accepted", Text: "Run a tool."));
        transcript.Render(new AgentLifecycleEvent("tool_execution_started", Tool: "bash"));
        transcript.Render(new AgentLifecycleEvent("tool_execution_finished", Tool: "bash", Text: "ok"));
        transcript.Render(new AgentLifecycleEvent("turn_interrupted"));

        var frame = LastFrame(output, 24, 80).Screen;
        Assert.Contains(" Operation aborted", frame);
        Assert.DoesNotContain("Error: This operation was aborted", frame);
        Assert.Equal(1, Occurrences(frame, "Operation aborted"));
    }

    [Fact]
    public void OrdinaryToolFailureDoesNotBecomeAToolCancellationMessage()
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        using var screen = new TerminalScreen(output, error, () => 80, () => 24);
        var transcript = new InteractiveTranscript(screen.Output, screen.Error, screen: screen);

        transcript.Render(new AgentLifecycleEvent("prompt_accepted", Text: "Run a failing tool."));
        transcript.Render(new AgentLifecycleEvent("tool_execution_started", Tool: "bash"));
        transcript.Render(new AgentLifecycleEvent("tool_execution_finished", Tool: "bash", IsError: true,
            Text: "Command failed", Error: "Command failed"));
        transcript.Render(new AgentLifecycleEvent("turn_interrupted"));

        var frame = LastFrame(output, 24, 80).Screen;
        Assert.Contains(" Operation aborted", frame);
        Assert.DoesNotContain("Error: This operation was aborted", frame);
        Assert.DoesNotContain("Error: Command failed", frame);
    }

    [Fact]
    public void TextOnlyCancellationKeepsPiOperationAbortedWording()
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        using var screen = new TerminalScreen(output, error, () => 80, () => 24);
        var transcript = new InteractiveTranscript(screen.Output, screen.Error, screen: screen);

        transcript.Render(new AgentLifecycleEvent("prompt_accepted", Text: "Stream a response."));
        transcript.Render(new AgentLifecycleEvent("model_text_delta", Text: "Partial response."));
        transcript.Render(new AgentLifecycleEvent("turn_interrupted"));

        var frame = LastFrame(output, 24, 80).Screen;
        Assert.Contains(" Operation aborted", frame);
        Assert.DoesNotContain("Error: This operation was aborted", frame);
        Assert.Equal(1, Occurrences(frame, "Operation aborted"));
    }

    private static int Occurrences(string screen, string text)
    {
        var count = 0;
        var offset = 0;
        while ((offset = screen.IndexOf(text, offset, StringComparison.Ordinal)) >= 0)
        {
            count++;
            offset += text.Length;
        }
        return count;
    }

    private static TerminalOutputFrameReader.Frame LastFrame(StringWriter output, int rows, int columns) =>
        TerminalOutputFrameReader.Read(output.ToString(), rows, columns)[^1];
}
