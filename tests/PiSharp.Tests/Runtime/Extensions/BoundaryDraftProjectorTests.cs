using System.Text.Json;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using PiSharp.Extensions.Abstractions;
using PiSharp.Runtime.Sessions;

namespace PiSharp.Tests;

public sealed class BoundaryDraftProjectorTests
{
    [Fact]
    public void PreviewAppliesAllDraftKindsWithoutChangingCanonicalEntries()
    {
        var session = Session();
        session.Append(new ChatMessage(ChatRole.User, "original"));
        var originalHead = session.Tree.HeadId!;
        var coordinator = Coordinator(session);
        var before = session.Tree.Entries.ToArray();
        var preview = coordinator.Preview([
            new PiSharpCustomEntryDraft("state", JsonSerializer.SerializeToElement(new { value = 42 })),
            PiSharpContextEditEntryDraft.FromText(originalHead, "replacement"),
            new PiSharpCustomMessageEntryDraft("context", "additional", false)
        ], false, default);

        Assert.Equal(before, session.Tree.Entries);
        Assert.Equal(originalHead, session.Tree.HeadId);
        Assert.Equal(new[] { "replacement", "additional" }, preview.ContextMessages.Select(message => message.Text));
        Assert.True(preview.CanContinue);
        Assert.Contains(preview.ContextEntries, entry => entry.Type == "extension_state");
        preview.ContextMessages[0].Contents.Clear();
        Assert.Equal("replacement", preview.ContextMessages[0].Text);

        var compacted = coordinator.Preview([new PiSharpCompactionEntryDraft("checkpoint", null)], false, default);
        Assert.Single(compacted.ContextEntries);
        Assert.Equal("compaction", compacted.ContextEntries[0].Type);
        Assert.Contains("checkpoint", Assert.Single(compacted.ContextMessages).Text);
        Assert.Equal(before, session.Tree.Entries);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CommitFailureRollsBackEveryEntryAndDoesNotAdoptExecution(bool restorationFails)
    {
        var session = Session();
        session.Append(new ChatMessage(ChatRole.User, "request"));
        var before = session.Tree.Entries.ToArray();
        var adopted = false;
        var saves = 0;
        var coordinator = new ExtensionBoundaryCoordinator(session,
            _ => { saves++; return Task.FromException(new IOException("save failed")); },
            (_, _) => restorationFails
                ? Task.FromException<AgentSession>(new InvalidOperationException("restore failed"))
                : Task.FromResult<AgentSession>(null!),
            (_, _) => adopted = true, () => ([], []));

        await Assert.ThrowsAnyAsync<Exception>(() => coordinator.CommitAsync([
            new PiSharpCustomEntryDraft("state"), new PiSharpCustomMessageEntryDraft("message", "context", false)
        ], default));

        Assert.Equal(before, session.Tree.Entries);
        Assert.Equal(before[^1].Id, session.Tree.HeadId);
        Assert.False(adopted);
        Assert.Equal(restorationFails ? 0 : 1, saves);
        session.Append(new ChatMessage(ChatRole.User, "recovered"));
        Assert.Equal("recovered", session.ContextMessages()[^1].Text);
    }

    [Fact]
    public async Task SuccessfulCommitPersistsBeforeAdoptingExecution()
    {
        var session = Session();
        session.Append(new ChatMessage(ChatRole.User, "request"));
        var order = new List<string>();
        var coordinator = new ExtensionBoundaryCoordinator(session,
            _ => { order.Add("save"); return Task.CompletedTask; },
            (_, _) => { order.Add("restore"); return Task.FromResult<AgentSession>(null!); },
            (_, _) => order.Add("adopt"), () => ([], []));
        await coordinator.CommitAsync([new PiSharpCustomMessageEntryDraft("context", "durable", false)], default);
        Assert.Equal(new[] { "restore", "save", "adopt" }, order);
        Assert.Equal("durable", session.ContextMessages()[^1].Text);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CanContinueUsesProjectedFinalRoleAndPendingMessages(bool beforeSettle)
    {
        var session = Session();
        session.Append(new ChatMessage(ChatRole.User, "request"));
        session.Append(new ChatMessage(ChatRole.Assistant, "response"));
        Assert.False(Coordinator(session).Preview([], beforeSettle, default).CanContinue);
        Assert.True(Coordinator(session).Preview([
            new PiSharpCustomMessageEntryDraft("context", "continue", false)
        ], beforeSettle, default).CanContinue);
        var coordinator = new ExtensionBoundaryCoordinator(session, null,
            (_, _) => Task.FromResult<AgentSession>(null!), (_, _) => { },
            () => ([new ChatMessage(ChatRole.User, "queued")], []));
        Assert.True(coordinator.Preview([], beforeSettle, default).CanContinue);
    }

    [Fact]
    public void InvalidContextEditCannotSplitToolPairsOrTargetAnotherBranch()
    {
        var session = Session();
        session.Append(new ChatMessage(ChatRole.User, "request"));
        session.Append(new ChatMessage(ChatRole.Assistant, [new FunctionCallContent("call", "tool")]));
        var assistantId = session.Tree.HeadId!;
        session.Append(new ChatMessage(ChatRole.Tool, [new FunctionResultContent("call", "result")]));
        var head = session.Tree.HeadId;
        Assert.Throws<InvalidDataException>(() => Coordinator(session).Preview([
            new PiSharpContextEditEntryDraft(assistantId, null)
        ], false, default));
        Assert.Equal(head, session.Tree.HeadId);
        Assert.Throws<InvalidDataException>(() => Coordinator(session).Preview([
            PiSharpContextEditEntryDraft.FromText("stale", "value")
        ], false, default));
        Assert.Throws<InvalidDataException>(() => Coordinator(session).Preview([
            new PiSharpCompactionEntryDraft("unsafe", head)
        ], false, default));
    }

    private static ConversationSession Session() => new(Path.GetTempPath(), "test", null);

    private static ExtensionBoundaryCoordinator Coordinator(ConversationSession session) => new(session, null,
        (_, _) => Task.FromResult<AgentSession>(null!), (_, _) => { }, () => ([], []));
}
