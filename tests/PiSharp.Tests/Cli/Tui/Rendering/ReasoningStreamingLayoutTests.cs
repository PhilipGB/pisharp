using PiSharp.Cli.Tui;
using PiSharp.Runtime.Sessions;

namespace PiSharp.Tests;

public sealed class ReasoningStreamingLayoutTests
{
    private const string UserPrompt = "Stream reasoning and then an answer.";
    private const string ReasoningBegin = "REASONING_BEGIN inspect the staged request.";
    private const string ReasoningContinuation = " REASONING_CONTINUATION choose the visible answer.";
    private const string AnswerBegin = "ANSWER_BEGIN the visible answer starts here.\n";
    private const string AnswerEnd = "ANSWER_END the visible answer completes.\n";

    [Theory]
    [InlineData(80, 24)]
    [InlineData(120, 40)]
    public void StreamedReasoningRemainsOneBlockWithOneHiddenPlaceholder(int columns, int rows)
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        using var screen = new TerminalScreen(output, error, () => columns, () => rows);
        var transcript = new InteractiveTranscript(screen.Output, screen.Error, screen: screen);

        screen.AppendUserMessage(UserPrompt);
        transcript.Render(new AgentLifecycleEvent("reasoning_delta", Text: ReasoningBegin));
        var first = LastFrame(output, rows, columns);
        Assert.Equal(1, Occurrences(first.Screen, "REASONING_BEGIN"));
        Assert.Equal(0, Occurrences(first.Screen, "REASONING_CONTINUATION"));
        var firstReasoningRow = RowContaining(first.Screen, "REASONING_BEGIN");
        Assert.Equal(" " + ReasoningBegin, first.Screen.Split(Environment.NewLine)[firstReasoningRow].TrimEnd());

        transcript.Render(new AgentLifecycleEvent("reasoning_delta", Text: ReasoningContinuation));
        var continued = LastFrame(output, rows, columns);
        Assert.Equal(1, Occurrences(continued.Screen, "REASONING_BEGIN"));
        Assert.Equal(0, Occurrences(continued.Screen, "Thinking..."));

        screen.SetThinkingBlocksHidden(true);
        screen.AppendThemedStatusLine("Thinking blocks: hidden");
        var hidden = LastFrame(output, rows, columns);
        Assert.Equal(1, Occurrences(hidden.Screen, "Thinking..."));
        Assert.Equal(0, Occurrences(hidden.Screen, "REASONING_BEGIN"));
        Assert.Equal(1, Occurrences(hidden.Screen, "Thinking blocks: hidden"));
        Assert.Equal(["", " " + UserPrompt, "", "", " Thinking...", "", " Thinking blocks: hidden"],
            RowsThrough(hidden.Screen, "Thinking blocks: hidden").Select(row => row.TrimEnd()).ToArray());
        Assert.True(RowContaining(hidden.Screen, "Thinking...") < RowContaining(hidden.Screen, "Thinking blocks: hidden"));

        transcript.Render(new AgentLifecycleEvent("model_text_delta", Text: AnswerBegin));
        var answer = LastFrame(output, rows, columns);
        Assert.Equal(1, Occurrences(answer.Screen, "Thinking..."));
        Assert.True(RowContaining(answer.Screen, "Thinking...") < RowContaining(answer.Screen, "ANSWER_BEGIN"));
        Assert.True(RowContaining(answer.Screen, "ANSWER_BEGIN") < RowContaining(answer.Screen, "Thinking blocks: hidden"));

        screen.SetThinkingBlocksHidden(false);
        screen.AppendThemedStatusLine("Thinking blocks: visible");
        var shown = LastFrame(output, rows, columns);
        Assert.Equal(1, Occurrences(shown.Screen, "REASONING_BEGIN"));
        Assert.Equal(0, Occurrences(shown.Screen, "Thinking..."));
        Assert.Equal(1, Occurrences(shown.Screen, "Thinking blocks: visible"));
        Assert.Equal(0, Occurrences(shown.Screen, "Thinking blocks: hidden"));
        Assert.True(RowContaining(shown.Screen, "REASONING_BEGIN") < RowContaining(shown.Screen, "ANSWER_BEGIN"));
        Assert.True(RowContaining(shown.Screen, "ANSWER_BEGIN") < RowContaining(shown.Screen, "Thinking blocks: visible"));

        transcript.Render(new AgentLifecycleEvent("model_text_delta", Text: AnswerEnd));
        transcript.FinishTurn();
        var final = LastFrame(output, rows, columns);
        Assert.Equal(ExpectedRows(columns),
            RowsThrough(final.Screen, "Thinking blocks: visible").Select(row => row.TrimEnd()).ToArray());
        Assert.Equal(rows - 4, final.CursorRow);
        Assert.Equal(0, final.CursorColumn);
    }

    [Theory]
    [InlineData(80, 24)]
    [InlineData(120, 40)]
    public void HiddenReasoningKeepsOnePlaceholderAcrossResize(int columns, int rows)
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        var currentColumns = columns;
        var currentRows = rows;
        using var screen = new TerminalScreen(output, error, () => currentColumns, () => currentRows);
        var transcript = new InteractiveTranscript(screen.Output, screen.Error, screen: screen);

        screen.AppendUserMessage(UserPrompt);
        transcript.Render(new AgentLifecycleEvent("reasoning_delta", Text: ReasoningBegin));
        transcript.Render(new AgentLifecycleEvent("reasoning_delta", Text: ReasoningContinuation));
        screen.SetThinkingBlocksHidden(true);
        screen.AppendThemedStatusLine("Thinking blocks: hidden");

        currentColumns = columns == 80 ? 120 : 80;
        currentRows = rows == 24 ? 40 : 24;
        screen.RefreshIfResized();
        var resized = LastFrame(output, currentRows, currentColumns);

        Assert.Equal(1, Occurrences(resized.Screen, "Thinking..."));
        Assert.Equal(0, Occurrences(resized.Screen, "REASONING_BEGIN"));
    }


    [Theory]
    [InlineData(80, 24)]
    [InlineData(120, 40)]
    public void VisibleReasoningUsesPiItalicThinkingStyle(int columns, int rows)
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        var theme = TerminalThemeCatalog.LoadBuiltIn("dark", TerminalColorMode.TrueColor);
        using var screen = new TerminalScreen(output, error, () => columns, () => rows,
            new TerminalImageRenderer(), theme);
        var transcript = new InteractiveTranscript(screen.Output, screen.Error, screen: screen);

        screen.AppendUserMessage(UserPrompt);
        transcript.Render(new AgentLifecycleEvent("reasoning_delta", Text: ReasoningBegin));
        transcript.Render(new AgentLifecycleEvent("reasoning_delta", Text: ReasoningContinuation));
        var visible = output.ToString();
        SgrAssertions.AssertStyleAt(visible, "REASONING_BEGIN", italic: true, foreground: true);
        if (columns == 80)
            SgrAssertions.AssertStyleAt(visible, "visible answer.", italic: true, foreground: true);

        var hiddenStart = output.GetStringBuilder().Length;
        screen.SetThinkingBlocksHidden(true);
        screen.AppendThemedStatusLine("Thinking blocks: hidden");
        var hidden = output.ToString()[hiddenStart..];
        SgrAssertions.AssertStyleAt(hidden, "Thinking...", italic: true, foreground: true);
        SgrAssertions.AssertStyleAt(hidden, "Thinking blocks: hidden", italic: false);

        var shownStart = output.GetStringBuilder().Length;
        screen.SetThinkingBlocksHidden(false);
        screen.AppendThemedStatusLine("Thinking blocks: visible");
        var shown = output.ToString()[shownStart..];
        SgrAssertions.AssertStyleAt(shown, "REASONING_BEGIN", italic: true, foreground: true);

        var answerStart = output.GetStringBuilder().Length;
        transcript.Render(new AgentLifecycleEvent("model_text_delta", Text: AnswerBegin));
        var answer = output.ToString()[answerStart..];
        SgrAssertions.AssertStyleAt(answer, "ANSWER_BEGIN", italic: false);
    }

    private static string[] ExpectedRows(int columns)
    {
        if (columns == 80)
        {
            return
            [
                "",
                " " + UserPrompt,
                "",
                "",
                " REASONING_BEGIN inspect the staged request. REASONING_CONTINUATION choose the",
                " visible answer.",
                "",
                " ANSWER_BEGIN the visible answer starts here.",
                " ANSWER_END the visible answer completes.",
                "",
                " Thinking blocks: visible"
            ];
        }

        return
        [
            "",
            " " + UserPrompt,
            "",
            "",
            " " + ReasoningBegin + ReasoningContinuation,
            "",
            " ANSWER_BEGIN the visible answer starts here.",
            " ANSWER_END the visible answer completes.",
            "",
            " Thinking blocks: visible"
        ];
    }

    private static string[] RowsThrough(string screen, string finalRow)
    {
        var rows = screen.Split(Environment.NewLine);
        var end = RowContaining(screen, finalRow);
        return rows[..(end + 1)];
    }

    private static int RowContaining(string screen, string text)
    {
        var rows = screen.Split(Environment.NewLine);
        var index = Array.FindIndex(rows, row => row.Contains(text, StringComparison.Ordinal));
        Assert.True(index >= 0, $"Expected a row containing '{text}'.{Environment.NewLine}{screen}");
        return index;
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
