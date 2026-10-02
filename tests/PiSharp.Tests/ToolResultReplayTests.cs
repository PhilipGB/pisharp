using Microsoft.Extensions.AI;
using PiSharp.Runtime.Sessions;

namespace PiSharp.Tests;

public sealed class ToolResultReplayTests
{
    [Fact]
    public void RecentTokenBudgetKeepsWholeTurnsAndNeverSplitsToolResults()
    {
        var conversation = new ConversationSession(Path.GetTempPath(), "fixture", null);
        conversation.Append(new ChatMessage(ChatRole.User, "first"));
        conversation.Append(new ChatMessage(ChatRole.Assistant, "reply one"));
        conversation.Append(new ChatMessage(ChatRole.User, "second"));
        conversation.Append(new ChatMessage(ChatRole.Assistant,
            [new FunctionCallContent("tool2", "read", new Dictionary<string, object?>())]));
        conversation.Append(new ChatMessage(ChatRole.Tool, [new FunctionResultContent("tool2", "read output")]));
        conversation.Append(new ChatMessage(ChatRole.User, "third"));
        var minimum = conversation.PrepareCompaction(0)!;
        Assert.Equal(5, minimum.MessagesToSummarize.Count);
        Assert.Equal(9, conversation.ContextMessages().Skip(2).Sum(message =>
            ConversationCompactionMetadata.EstimateTokens([message])));
        Assert.Empty(conversation.PrepareCompaction(10)!.MessagesToSummarize);
        var twoTurns = conversation.PrepareCompaction(9)!;
        Assert.Equal(2, twoTurns.MessagesToSummarize.Count);
        conversation.AppendCompaction(twoTurns, "First turn summary", 9);
        Assert.Equal(6, conversation.ActiveMessages().Count);
        Assert.Equal(5, conversation.ContextMessages().Count);
        Assert.Null(conversation.PrepareCompaction(100000));
    }

}
