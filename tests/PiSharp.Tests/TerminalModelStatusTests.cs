using Microsoft.Extensions.AI;
using PiSharp.Cli.Tui;
using PiSharp.Runtime.Providers;
using PiSharp.Runtime.Sessions;

namespace PiSharp.Tests;

public sealed class TerminalModelStatusTests
{
    [Fact]
    public void VirtualStatusKeepsLogicalSelectionAndUsesLatestSuccessfulPhysicalResponseOnTheBranch()
    {
        var session = new ConversationSession(Path.GetTempPath(), "auto", null, "router");
        session.Append(Response("large", "low"));
        var branch = session.Tree.HeadId;
        session.Append(Response("small", "high"));
        session.Append(Response("failed", "off", "error"));
        var logical = new ModelDescriptor("auto", null, null, null, Api: "pi-virtual");
        Assert.Equal("router/auto · thinking max → small · thinking high · context 8,000", TerminalModelStatus.Format("router", logical, "max", session, 8000));
        session.Tree.Select(branch);
        Assert.Equal("router/auto · thinking max → large · thinking low · context 50,000", TerminalModelStatus.Format("router", logical, "max", session, 50000));
        Assert.Equal("auto", session.Model);
    }

    [Fact]
    public void OrdinarySelectionDoesNotDisplayPreviousVirtualDispatch()
    {
        var session = new ConversationSession(Path.GetTempPath(), "ordinary", null, "physical");
        session.Append(Response("small", "high"));
        Assert.Equal("physical/ordinary · thinking low", TerminalModelStatus.Format("physical",
            new ModelDescriptor("ordinary", null, null, null, Api: "openai-completions"), "low", session, 50000));
    }

    private static ChatMessage Response(string model, string thinking, string? stopReason = null) => new(ChatRole.Assistant, "answer")
    {
        AdditionalProperties = new()
        {
            ["pisharp.provider"] = "physical",
            ["pisharp.model"] = model,
            ["pisharp.thinkingLevel"] = thinking,
            ["pisharp.stopReason"] = stopReason
        }
    };
}
