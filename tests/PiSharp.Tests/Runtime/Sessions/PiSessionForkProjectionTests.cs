using System.Text.Json;
using Microsoft.Extensions.AI;
using PiSharp.Runtime.Sessions;

namespace PiSharp.Tests;

public sealed class PiSessionForkProjectionTests
{
    [Fact]
    public void CloneMovesBranchLabelsAfterRetainedEntries()
    {
        var session = new ConversationSession(Path.GetTempPath(), "fixture", null);
        session.Append(new ChatMessage(ChatRole.User, "question"));
        var targetId = session.Tree.HeadId!;
        session.Append(new ChatMessage(ChatRole.Assistant, "answer"));
        var answerId = session.Tree.HeadId!;
        session.Tree.Append("label", JsonSerializer.SerializeToElement(new { targetId, label = "selected" }));
        session.AppendThinkingLevelChange("off");

        var clone = session.ForkForSessionReplacement();
        var branch = clone.Tree.ActivePath();

        Assert.Equal(["chat", "chat", "thinking_level_change", "label"], branch.Select(entry => entry.Type));
        Assert.Equal(answerId, branch[2].ParentId);
        Assert.Equal(branch[2].Id, branch[3].ParentId);
        Assert.Equal(targetId, branch[3].Payload.GetProperty("targetId").GetString());
        Assert.Equal("selected", branch[3].Payload.GetProperty("label").GetString());
    }

    [Fact]
    public void ForkBeforeSelectedUserMovesEarlierLabelsToBranchTail()
    {
        var session = new ConversationSession(Path.GetTempPath(), "fixture", null);
        session.Append(new ChatMessage(ChatRole.User, "root"));
        var targetId = session.Tree.HeadId!;
        session.Tree.Append("label", JsonSerializer.SerializeToElement(new { targetId, label = "selected" }));
        session.Append(new ChatMessage(ChatRole.Assistant, "answer"));
        session.Append(new ChatMessage(ChatRole.User, "draft"));
        var selectedId = session.Tree.HeadId!;

        var (fork, draft) = session.ForkAtUser(selectedId);
        var branch = fork.Tree.ActivePath();

        Assert.Equal("draft", draft);
        Assert.Equal(["chat", "chat", "label"], branch.Select(entry => entry.Type));
        Assert.Equal(branch[1].Id, branch[2].ParentId);
        Assert.Equal(targetId, branch[2].Payload.GetProperty("targetId").GetString());
    }
}
