using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.AI;
using PiSharp.Cli.Protocols;
using PiSharp.Runtime.Sessions;

namespace PiSharp.Tests;

public sealed class RpcSessionStatsProjectorTests
{
    [Fact]
    public void ProjectsPiStatsAcrossAllBranchesAndUsageEntries()
    {
        var session = new ConversationSession(Path.GetTempPath(), "fixture", null);
        session.Append(new ChatMessage(ChatRole.User, "first"));
        var firstId = session.Tree.HeadId!;
        session.Append(new ChatMessage(ChatRole.Assistant, "first answer"));
        session.AppendUsage(new UsageRecord("fixture", "model", 10, 2, 3, 0, 12, 0.5m, CachedWriteTokens: 1));

        session.Tree.Select(firstId);
        session.Append(new ChatMessage(ChatRole.User, "alternate"));
        session.Append(new ChatMessage(ChatRole.Assistant, "alternate answer"));

        var stats = RpcSessionStatsProjector.Project(session, "/tmp/fixture.session.json", null, null);

        Assert.Equal("/tmp/fixture.session.json", stats["sessionFile"]!.GetValue<string>());
        Assert.Equal(session.Id, stats["sessionId"]!.GetValue<string>());
        Assert.Equal(2, stats["userMessages"]!.GetValue<int>());
        Assert.Equal(2, stats["assistantMessages"]!.GetValue<int>());
        Assert.Equal(4, stats["totalMessages"]!.GetValue<int>());
        Assert.Equal(10, stats["tokens"]!["input"]!.GetValue<long>());
        Assert.Equal(2, stats["tokens"]!["output"]!.GetValue<long>());
        Assert.Equal(3, stats["tokens"]!["cacheRead"]!.GetValue<long>());
        Assert.Equal(1, stats["tokens"]!["cacheWrite"]!.GetValue<long>());
        Assert.Equal(16, stats["tokens"]!["total"]!.GetValue<long>());
        Assert.Equal(0.5m, stats["cost"]!.GetValue<decimal>());
    }

    [Fact]
    public void AggregatesUsageFromImportedPiMessagesAndEntryKinds()
    {
        const string json = """
            {"type":"session","version":3,"id":"stats-import","timestamp":"2026-09-27T00:00:00.000Z","cwd":"/tmp"}
            {"type":"message","id":"user","parentId":null,"timestamp":"2026-09-27T00:00:01.000Z","message":{"role":"user","content":"question"}}
            {"type":"message","id":"assistant","parentId":"user","timestamp":"2026-09-27T00:00:02.000Z","message":{"role":"assistant","content":[{"type":"text","text":"answer"},{"type":"toolCall","id":"call","name":"read","arguments":{}}],"usage":{"input":2,"output":3,"cacheRead":4,"cacheWrite":5,"totalTokens":14,"cost":{"input":0,"output":0,"cacheRead":0,"cacheWrite":0,"total":0.1}}}}
            {"type":"message","id":"tool-result","parentId":"assistant","timestamp":"2026-09-27T00:00:03.000Z","message":{"role":"toolResult","toolCallId":"call","toolName":"read","content":"contents","usage":{"input":10,"output":20,"cacheRead":30,"cacheWrite":40,"totalTokens":100,"cost":{"input":0,"output":0,"cacheRead":0,"cacheWrite":0,"total":1}}}}
            {"type":"compaction","id":"compaction","parentId":"tool-result","timestamp":"2026-09-27T00:00:04.000Z","summary":"summary","firstKeptEntryId":"user","tokensBefore":100,"usage":{"input":100,"output":200,"cacheRead":300,"cacheWrite":400,"totalTokens":1000,"cost":{"input":0,"output":0,"cacheRead":0,"cacheWrite":0,"total":2}}}
            {"type":"branch_summary","id":"branch-summary","parentId":"user","timestamp":"2026-09-27T00:00:05.000Z","fromId":"compaction","summary":"branch","usage":{"input":1000,"output":2000,"cacheRead":3000,"cacheWrite":4000,"totalTokens":10000,"cost":{"input":0,"output":0,"cacheRead":0,"cacheWrite":0,"total":3}}}
            {"type":"usage","id":"usage","parentId":"branch-summary","timestamp":"2026-09-27T00:00:06.000Z","kind":"cache_warm","provider":"fixture","model":"fixture","usage":{"input":10000,"output":20000,"cacheRead":30000,"cacheWrite":40000,"totalTokens":100000,"cost":{"input":0,"output":0,"cacheRead":0,"cacheWrite":0,"total":4}}}
            """;
        var session = PiJsonlSessionInterchange.Import(json);
        var toolEntry = session.Tree.Entries.Single(entry => entry.Id == "tool-result");
        Assert.NotNull(ConversationSession.PiEntryFromChatPayload(toolEntry.Payload));
        var restoredTool = ConversationSession.RestoreEntry(toolEntry);
        Assert.Equal(ChatRole.Tool, restoredTool.Role);
        Assert.Contains(restoredTool.Contents, content => content is FunctionResultContent);

        var stats = RpcSessionStatsProjector.Project(session, null, null, null);

        Assert.Equal(1, stats["userMessages"]!.GetValue<int>());
        Assert.Equal(1, stats["assistantMessages"]!.GetValue<int>());
        Assert.Equal(1, stats["toolCalls"]!.GetValue<int>());
        Assert.Equal(1, stats["toolResults"]!.GetValue<int>());
        Assert.Equal(3, stats["totalMessages"]!.GetValue<int>());
        Assert.Equal(11112, stats["tokens"]!["input"]!.GetValue<long>());
        Assert.Equal(22223, stats["tokens"]!["output"]!.GetValue<long>());
        Assert.Equal(33334, stats["tokens"]!["cacheRead"]!.GetValue<long>());
        Assert.Equal(44445, stats["tokens"]!["cacheWrite"]!.GetValue<long>());
        Assert.Equal(111114, stats["tokens"]!["total"]!.GetValue<long>());
        Assert.Equal(10.1m, stats["cost"]!.GetValue<decimal>());
    }
}
