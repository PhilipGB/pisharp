using System.Text.Json;
using PiSharp.Cli.Tui;
using PiSharp.Runtime.Sessions;

namespace PiSharp.Tests;

public sealed class InteractiveTranscriptTests
{
    [Fact]
    public void InteractiveTranscriptStreamsAssistantTextAndFiltersTerminalControls()
    {
        using var output = new StringWriter();
        using var status = new StringWriter();
        var transcript = new InteractiveTranscript(output, status);

        transcript.Render(new("model_text_delta", Text: "Hello \u001b[2J界"));
        transcript.Render(new("reasoning_delta", Text: "checking"));
        transcript.FinishTurn();

        Assert.Equal("Hello [2J界" + Environment.NewLine, output.ToString());
        Assert.Equal("checking", status.ToString());
        Assert.False(transcript.HasAssistantOutput);
    }

    [Fact]
    public void SearchToolDetailsRenderLimitAndByteTruncationSummary()
    {
        using var output = new StringWriter();
        using var status = new StringWriter();
        var transcript = new InteractiveTranscript(output, status);
        using var document = JsonDocument.Parse("""
            {
              "matchLimitReached": 200,
              "linesTruncated": true,
              "truncation": {
                "truncated": true,
                "truncatedBy": "bytes",
                "totalBytes": 90000,
                "outputBytes": 50000
              }
            }
            """);

        transcript.Render(new("tool_execution_finished", Tool: "grep", Text: "2 matches", Details: document.RootElement.Clone()));

        Assert.Contains("← 2 matches", status.ToString());
        Assert.Contains("match limit reached (200)", status.ToString());
        Assert.Contains("one or more matching lines were truncated", status.ToString());
        Assert.Contains("output truncated by bytes (50000 of 90000 bytes shown)", status.ToString());
    }

    [Fact]
    public void StructuredSearchRecordsAreSummarizedAndThinkingCanBeHidden()
    {
        using var output = new StringWriter();
        using var status = new StringWriter();
        var transcript = new InteractiveTranscript(output, status, hideThinking: true);

        transcript.Render(new("tool_execution_finished", Tool: "find", IsError: false,
            Details: new SearchDetails(EntryLimitReached: 100)));
        transcript.Render(new("reasoning_delta", Text: "private reasoning"));

        Assert.Contains("entry limit reached (100)", status.ToString());
        Assert.DoesNotContain("private reasoning", status.ToString());
    }

    [Fact]
    public void PrintTranscriptKeepsAssistantOutputOnlyAndReportsAgentFailure()
    {
        using var output = new StringWriter();
        using var status = new StringWriter();
        var transcript = new InteractiveTranscript(output, status, interactive: false);

        transcript.Render(new("model_text_delta", Text: "plain \u001b[31mtext"));
        transcript.Render(new("tool_execution_finished", Tool: "grep", Details: new SearchDetails(ResultLimitReached: 4)));
        transcript.Render(new("turn_failed", Error: "offline"));
        transcript.FinishTurn();

        Assert.Equal("plain \u001b[31mtext" + Environment.NewLine, output.ToString());
        Assert.Equal("Agent error: offline" + Environment.NewLine, status.ToString());
        Assert.Equal(1, transcript.ExitCode);
    }

    private sealed record SearchDetails(int? ResultLimitReached = null, int? EntryLimitReached = null);
}
