using PiSharp.Cli.Tui;

namespace PiSharp.Tests;

public sealed class TerminalSkillInvocationTests
{
    [Fact]
    public void ParsesSkillEnvelopeAndTrimsSeparateUserMessage()
    {
        var parsed = TerminalSkillInvocationParser.Parse(
            "<skill name=\"review\" location=\"/project/.pi/skills/review/SKILL.md\">\n" +
            "References are relative to /project/.pi/skills/review.\n\nReview the change.\n" +
            "</skill>\n\n  focus on errors  ");

        Assert.NotNull(parsed);
        Assert.Equal("review", parsed.Name);
        Assert.Equal("/project/.pi/skills/review/SKILL.md", parsed.Location);
        Assert.Equal("References are relative to /project/.pi/skills/review.\n\nReview the change.", parsed.Content);
        Assert.Equal("focus on errors", parsed.UserMessage);
    }

    [Fact]
    public void LeavesOrdinaryOrTrailingTextMessagesUnparsed()
    {
        Assert.Null(TerminalSkillInvocationParser.Parse("ordinary prompt"));
        Assert.Null(TerminalSkillInvocationParser.Parse(
            "<skill name=\"review\" location=\"/review/SKILL.md\">\nbody\n</skill> trailing"));
    }

    [Fact]
    public void CollapsibleNonToolTranscriptSegmentUsesItsCollapsedViewUntilExpanded()
    {
        var buffer = new TerminalTranscriptBuffer();
        buffer.AppendThemed("expanded skill body", isError: false, isToolResult: false,
            _ => "expanded skill body", " [skill] review (ctrl+o to expand)\n",
            collapsedPreviewText: " [skill] review (ctrl+o to expand)\n",
            collapsedRenderer: _ => " [skill] review (ctrl+o to expand)\n", isCollapsible: true);

        Assert.Contains("[skill] review (ctrl+o to expand)", buffer.GetText(120));
        Assert.DoesNotContain("expanded skill body", buffer.GetText(120));

        buffer.ToggleExpanded();

        Assert.Contains("expanded skill body", buffer.GetText(120));
        Assert.DoesNotContain("to expand", buffer.GetText(120));
    }
}
