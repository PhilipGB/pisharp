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

    [Fact]
    public void IdleFooterSeparatesContextUsageFromTheRightAlignedModelName()
    {
        var session = new ConversationSession(Path.GetTempPath(), "fixture-model", null, "fixture");
        var model = new ModelDescriptor("fixture-model", null, 8192, null,
            Provider: "fixture",
            Reasoning: false, Api: "openai-completions");

        var footer = TerminalModelStatus.FormatIdleFooter(model, "off", session, 8192,
            autoCompactionEnabled: true, width: 100);

        const string left = "0.0%/8.2k (auto)";
        Assert.StartsWith(left, footer);
        Assert.EndsWith("fixture-model", footer);
        Assert.Equal(100, TerminalTextLayout.Width(footer));

        var disabled = TerminalModelStatus.FormatIdleFooter(model, "off", session, 8192,
            autoCompactionEnabled: false, width: 100);
        Assert.DoesNotContain(" (auto)", disabled);

        var multipleProviders = TerminalModelStatus.FormatIdleFooter(model, "off", session, 8192,
            autoCompactionEnabled: true, width: 100, availableProviderCount: 2);
        Assert.EndsWith("(fixture) fixture-model", multipleProviders);
        Assert.Equal(100, TerminalTextLayout.Width(multipleProviders));

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
