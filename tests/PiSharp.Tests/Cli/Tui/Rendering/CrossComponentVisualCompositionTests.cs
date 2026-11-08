using PiSharp.Cli.Tui;
using PiSharp.Runtime.Sessions;

namespace PiSharp.Tests;

public sealed class CrossComponentVisualCompositionTests
{
    [Theory]
    [InlineData(80, 24)]
    [InlineData(120, 40)]
    public void ReasoningPythonBuiltinsAndToolCancellationComposeInOneTranscript(int columns, int rows)
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        var theme = TerminalThemeCatalog.LoadBuiltIn("dark", TerminalColorMode.TrueColor);
        using var screen = new TerminalScreen(output, error, () => columns, () => rows,
            new TerminalImageRenderer(), theme);
        var transcript = new InteractiveTranscript(screen.Output, screen.Error, screen: screen);

        transcript.Render(new AgentLifecycleEvent("prompt_accepted", Text: "Reason, show Python and cancel Bash."));
        transcript.Render(new AgentLifecycleEvent("reasoning_delta", Text: "REASONING_BEGIN inspect the request."));
        transcript.Render(new AgentLifecycleEvent("reasoning_delta", Text: " REASONING_END choose the answer."));
        screen.SetThinkingBlocksHidden(true);
        screen.AppendThemedStatusLine("Thinking blocks: hidden");
        transcript.Render(new AgentLifecycleEvent("model_text_delta",
            Text: "ANSWER_BEGIN the answer starts.\n\n```python\nprint(len(str(123)))\n```\n"));
        screen.SetThinkingBlocksHidden(false);
        screen.AppendThemedStatusLine("Thinking blocks: visible");
        var composed = LastFrame(output, rows, columns).Screen;
        Assert.Equal(1, Occurrences(composed, "REASONING_BEGIN"));
        Assert.Equal(1, Occurrences(composed, "Thinking blocks: visible"));
        Assert.Equal(0, Occurrences(composed, "Thinking blocks: hidden"));
        Assert.Contains("print(len(str(123)))", composed);
        var composedRows = composed.Split(Environment.NewLine);
        var reasoningRow = Array.FindIndex(composedRows, row => row.Contains("REASONING_BEGIN", StringComparison.Ordinal));
        var answerRow = Array.FindIndex(composedRows, row => row.Contains("ANSWER_BEGIN", StringComparison.Ordinal));
        var codeRow = Array.FindIndex(composedRows, row => row.Contains("print(len(str(123)))", StringComparison.Ordinal));
        Assert.True(reasoningRow >= 0 && answerRow > reasoningRow && codeRow > answerRow);

        transcript.Render(new AgentLifecycleEvent("tool_execution_started", Tool: "bash"));
        transcript.Render(new AgentLifecycleEvent("tool_execution_finished", Tool: "bash", IsError: true,
            Text: "Command aborted", Error: "Command aborted"));
        transcript.Render(new AgentLifecycleEvent("turn_interrupted"));
        transcript.FinishTurn();

        var frame = LastFrame(output, rows, columns).Screen;
        Assert.Contains(" Error: This operation was aborted", frame);
        Assert.DoesNotContain(" Operation aborted", frame);
        Assert.Equal(1, Occurrences(frame, "Error: This operation was aborted"));

        var raw = output.ToString();
        Assert.Contains(theme.Fg("syntaxType") + "print", raw);
        Assert.Contains(theme.Fg("syntaxType") + "len", raw);
        Assert.Contains(theme.Fg("syntaxType") + "str", raw);
        SgrAssertions.AssertStyleAt(raw, "REASONING_BEGIN", italic: true, foreground: true);
        SgrAssertions.AssertStyleAt(raw, "print", italic: false, foreground: true);
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
