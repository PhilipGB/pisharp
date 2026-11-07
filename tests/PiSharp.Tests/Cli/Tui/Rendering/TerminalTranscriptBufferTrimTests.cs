using PiSharp.Cli.Tui;

namespace PiSharp.Tests;

public sealed class TerminalTranscriptBufferTrimTests
{
    [Fact]
    public void CharacterTrimmingKeepsLiveAssistantBeforeTrailingStatus()
    {
        var buffer = new TerminalTranscriptBuffer();
        buffer.AppendMarkdown("old", new string('a', 10), isError: false);
        buffer.AppendMarkdown("big", new string('b', TerminalTranscriptBuffer.MaximumTranscriptCharacters - 10),
            isError: false);
        buffer.BeginAssistantMessage();
        buffer.AppendThemedStatus("STATUS-STATUS-STATUS\n", _ => "STATUS-STATUS-STATUS\n",
            "STATUS-STATUS-STATUS\n");

        AssertOrdering(buffer, "REASONING\nANSWER\n");
    }

    [Fact]
    public void SegmentTrimmingKeepsLiveAssistantBeforeTrailingStatus()
    {
        var buffer = new TerminalTranscriptBuffer();
        for (var index = 0; index < TerminalTranscriptBuffer.MaximumTranscriptSegments; index++)
            buffer.AppendMarkdown($"segment-{index}", "x", isError: false);
        buffer.BeginAssistantMessage();
        buffer.AppendThemedStatus("STATUS-STATUS-STATUS\n", _ => "STATUS-STATUS-STATUS\n",
            "STATUS-STATUS-STATUS\n");

        AssertOrdering(buffer, "REASONING\nANSWER\n");
    }

    private static void AssertOrdering(TerminalTranscriptBuffer buffer, string liveText)
    {
        var live = buffer.GetTextWithLiveAssistant(80, liveText, out _);
        var liveReasoning = live.IndexOf("REASONING", StringComparison.Ordinal);
        var liveStatus = live.IndexOf("STATUS", StringComparison.Ordinal);
        Assert.True(liveReasoning >= 0 && liveReasoning < liveStatus,
            $"live ordering: reasoning={liveReasoning}, status={liveStatus}");

        buffer.AppendThinkingBlock("REASONING", "Thinking...", _ => "REASONING\n", _ => "Thinking...\n",
            TerminalTheme.Default, hidden: false);
        buffer.AppendMarkdown("ANSWER", "ANSWER\n", isError: false);

        var committed = buffer.GetText(80);
        var reasoning = committed.IndexOf("REASONING", StringComparison.Ordinal);
        var answer = committed.IndexOf("ANSWER", StringComparison.Ordinal);
        var status = committed.IndexOf("STATUS", StringComparison.Ordinal);
        Assert.True(reasoning >= 0 && reasoning < answer && answer < status,
            $"committed ordering: reasoning={reasoning}, answer={answer}, status={status}");
    }
}
