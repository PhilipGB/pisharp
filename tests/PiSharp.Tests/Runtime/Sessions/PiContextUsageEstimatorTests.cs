using Microsoft.Extensions.AI;
using PiSharp.Runtime.Resources;
using PiSharp.Runtime.Sessions;

namespace PiSharp.Tests;

public sealed class PiContextUsageEstimatorTests
{
    [Fact]
    public void EmptyToolSetDoesNotAddTokensForAnEmptyJsonArray()
    {
        Assert.Equal(1, PiContextUsageEstimator.Estimate(4, [], []));
    }

    [Fact]
    public void SystemPromptEstimateCountsStructuredSectionsWithoutRenderedSeparators()
    {
        const string append = "Additional instructions.";
        var baseline = PiSystemPromptBuilder.BuildProjection("/workspace", [], null, null, null, null);
        var withAppend = PiSystemPromptBuilder.BuildProjection("/workspace", [], null, append, null, null);
        var renderedLengthDelta = withAppend.Text.Length - baseline.Text.Length;

        Assert.Equal(renderedLengthDelta - 2,
            withAppend.ContextEstimateCharacters - baseline.ContextEstimateCharacters);
    }

    [Fact]
    public void ProjectedEstimateAddsOnlyMessagesAfterTheLatestSuccessfulAssistantUsage()
    {
        var session = new ConversationSession(Path.GetTempPath(), "model", null, "provider");
        session.Append(new ChatMessage(ChatRole.User, new string('x', 8_000)));
        session.AppendUsage(new UsageRecord("model", "model", 10, 3, 0, 0, 13, null, CachedWriteTokens: 0));
        session.Append(new ChatMessage(ChatRole.Assistant, "answer"));
        session.Append(new ChatMessage(ChatRole.User, "pending"));

        var projection = session.ProjectContextUsageForEstimate();

        Assert.True(projection.HasValidAssistantUsage);
        Assert.False(projection.IsInvalidated);
        Assert.Equal(13, projection.UsageTokens);
        Assert.Equal("pending", Assert.Single(projection.TrailingMessages).Text);
        Assert.Equal(15, PiContextUsageEstimator.EstimateAfterUsage(projection.UsageTokens,
            projection.TrailingMessages));
    }

    [Fact]
    public void ContextEditInvalidatesProviderUsageForProjectedContext()
    {
        var session = new ConversationSession(Path.GetTempPath(), "model", null, "provider");
        session.Append(new ChatMessage(ChatRole.User, "request"));
        session.AppendUsage(new UsageRecord("model", "model", 10, 3, 0, 0, 13, null, CachedWriteTokens: 0));
        session.Append(new ChatMessage(ChatRole.Assistant, "answer"));
        var assistantId = session.Tree.HeadId!;
        session.AppendContextOmission(assistantId);

        var projection = session.ProjectContextUsageForEstimate();

        Assert.True(projection.HasValidAssistantUsage);
        Assert.True(projection.IsInvalidated);
        Assert.Empty(projection.TrailingMessages);
        Assert.Null(session.LatestContextUsageTokens());
    }

    [Fact]
    public void FailedAssistantUsageIsNotUsedAsAContextEstimate()
    {
        var session = new ConversationSession(Path.GetTempPath(), "model", null, "provider");
        session.Append(new ChatMessage(ChatRole.User, "request"));
        session.AppendUsage(new UsageRecord("model", "model", 10, 3, 0, 0, 13, null, CachedWriteTokens: 0));
        session.Append(new ChatMessage(ChatRole.Assistant, "partial")
        {
            AdditionalProperties = new() { ["pisharp.stopReason"] = "error" }
        });

        var projection = session.ProjectContextUsageForEstimate();

        Assert.False(projection.HasValidAssistantUsage);
        Assert.Null(session.LatestContextUsageTokens());
    }
}
