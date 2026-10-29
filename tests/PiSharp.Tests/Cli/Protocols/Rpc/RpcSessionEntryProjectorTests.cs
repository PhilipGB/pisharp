using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.AI;
using PiSharp.Cli.Protocols;
using PiSharp.Runtime.Sessions;

namespace PiSharp.Tests;

public sealed class RpcSessionEntryProjectorTests
{
    [Fact]
    public void PiEntryProjectionUsesTheSameRecordMappingAsJsonlExport()
    {
        var session = new ConversationSession(Path.GetTempPath(), "fixture", null, "openai");
        session.Append(new ChatMessage(ChatRole.User, "hello"));
        session.Append(new ChatMessage(ChatRole.Assistant, "world"));
        session.SelectModel("next-model", null, "openai");

        var projected = PiJsonlSessionInterchange.ProjectEntries(session);
        var exported = PiJsonlSessionInterchange.Export(session).Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Skip(1).Select(line => JsonDocument.Parse(line)).ToArray();
        try
        {
            var exportedById = exported.ToDictionary(document => document.RootElement.GetProperty("id").GetString()!);
            Assert.Equal(session.Tree.Entries.Count, projected.Count);
            Assert.Equal(session.Tree.Entries.Select(entry => entry.Id), projected.Select(entry => entry.GetProperty("id").GetString()));
            foreach (var entry in projected)
            {
                var id = entry.GetProperty("id").GetString()!;
                Assert.Equal(exportedById[id].RootElement.GetRawText(), entry.GetRawText());
                Assert.True(entry.GetProperty("type").GetString() is "message" or "model_change");
            }
        }
        finally
        {
            foreach (var document in exported) document.Dispose();
        }
    }

    [Fact]
    public void RpcEntryProjectionHidesInternalToolLoadoutAndReparentsVisibleEntries()
    {
        var session = new ConversationSession(Path.GetTempPath(), "fixture", null, "openai");
        session.SelectModel("fixture", "http://127.0.0.1:9/v1", "openai");
        session.AppendThinkingLevelChange("off");
        session.AppendToolLoadout([]);
        using var append = session.BeginExtensionStateAppend("fixture-state",
            JsonSerializer.SerializeToElement(new { value = "stored" }), source: null);
        append.Commit();
        session.AppendUsage(new UsageRecord("fixture", "model", 1, 1, 0, 0, 2, null)
        {
            InputIncludingCachedTokens = 1
        });
        session.Tree.Append("context_projection", JsonSerializer.SerializeToElement(new { }));
        session.Tree.Append("tool_transcript", JsonSerializer.SerializeToElement(new { transcript = "internal" }));

        var entries = RpcSessionEntryProjector.ProjectEntries(session);

        Assert.Equal(["model_change", "thinking_level_change", "custom"],
            entries.Select(entry => entry.GetProperty("type").GetString()));
        Assert.False(entries[0].TryGetProperty("endpoint", out _));
        var thinkingId = entries[1].GetProperty("id").GetString();
        var extensionEntry = entries[2];
        Assert.Equal(thinkingId, extensionEntry.GetProperty("parentId").GetString());
        Assert.Equal(extensionEntry.GetProperty("id").GetString(), RpcSessionEntryProjector.ProjectLeafId(session));
        var appended = RpcSessionEntryProjector.ProjectAppendedEntry(session,
            extensionEntry.GetProperty("id").GetString()!);
        Assert.Equal(extensionEntry.GetRawText(), appended.GetRawText());
    }

    [Fact]
    public void VisibleTreeLeafOmitsHiddenToolLoadoutEntry()
    {
        var session = new ConversationSession(Path.GetTempPath(), "fixture", null, "openai");
        session.SelectModel("fixture", "http://127.0.0.1:9/v1", "openai");
        session.AppendThinkingLevelChange("off");
        session.AppendToolLoadout([]);

        var visibleEntries = RpcSessionEntryProjector.ProjectEntries(session);

        Assert.NotEqual(session.Tree.HeadId, RpcSessionEntryProjector.ProjectLeafId(session));
        Assert.Equal(visibleEntries[^1].GetProperty("id").GetString(),
            RpcSessionEntryProjector.ProjectLeafId(session));
    }

    [Fact]
    public void RpcProjectionHidesRunCheckpointAndProjectsGeneratedSystemMessage()
    {
        var session = new ConversationSession(Path.GetTempPath(), "fixture", null, "openai");
        session.Tree.Append("run_started", JsonSerializer.SerializeToElement(new
        {
            runId = "fixture-run",
            prompt = "hello"
        }));
        session.Append(new ChatMessage(ChatRole.User, "hello"));
        var systemMessage = JsonNode.Parse("""
            {"role":"system","content":"","sections":{"preamble":"fixture instructions"},"timestamp":1735689601000}
            """)!.AsObject();

        var entries = RpcSessionEntryProjector.ProjectEntries(session, systemMessage: systemMessage);

        Assert.Equal(["message", "message"], entries.Select(entry => entry.GetProperty("type").GetString()));
        Assert.Equal("system", entries[0].GetProperty("message").GetProperty("role").GetString());
        Assert.Equal("rpc-system-fixture-run", entries[0].GetProperty("id").GetString());
        Assert.Equal(entries[0].GetProperty("id").GetString(), entries[1].GetProperty("parentId").GetString());
        Assert.Equal(entries[1].GetProperty("id").GetString(),
            RpcSessionEntryProjector.ProjectLeafId(session, systemMessage));
    }

    [Fact]
    public void RpcProjectionDoesNotRepeatSystemMessageAfterCompactionSnapshot()
    {
        var session = new ConversationSession(Path.GetTempPath(), "fixture", null, "openai");
        session.Tree.Append("run_started", JsonSerializer.SerializeToElement(new { runId = "fixture-run" }));
        session.Append(new ChatMessage(ChatRole.User, "hello"));
        var user = session.Tree.ActivePath().Last();
        var systemMessage = JsonNode.Parse("""
            {"role":"system","content":"fixture instructions","sections":{"preamble":"fixture instructions"}}
            """)!.AsObject();
        session.Tree.Append("compaction", JsonSerializer.SerializeToElement(new
        {
            summary = "fixture summary",
            firstKeptEntryId = user.Id,
            tokensBefore = 10,
            systemMessage = JsonSerializer.Deserialize<JsonElement>(systemMessage.ToJsonString())
        }));
        session.Tree.Append("run_finished", JsonSerializer.SerializeToElement(new { runId = "fixture-run" }));
        session.Tree.Append("run_started", JsonSerializer.SerializeToElement(new { runId = "fixture-retry" }));

        var entries = RpcSessionEntryProjector.ProjectEntries(session, systemMessage: systemMessage);

        var systemEntries = entries.Where(entry => entry.TryGetProperty("message", out var message) &&
            message.GetProperty("role").GetString() == "system").ToArray();
        Assert.Single(systemEntries);
        Assert.Equal("rpc-system-fixture-run", systemEntries[0].GetProperty("id").GetString());
        var compaction = Assert.Single(entries, entry => entry.GetProperty("type").GetString() == "compaction");
        Assert.Equal(compaction.GetProperty("id").GetString(),
            RpcSessionEntryProjector.ProjectLeafId(session, systemMessage));
    }

    [Fact]
    public void RpcEntryProjectionUsesPiUserContentArrayForNativeMessages()
    {
        var session = new ConversationSession(Path.GetTempPath(), "fixture", null, "openai");
        session.Append(new ChatMessage(ChatRole.User, "native prompt"));

        var entry = Assert.Single(RpcSessionEntryProjector.ProjectEntries(session));
        var content = entry.GetProperty("message").GetProperty("content");

        Assert.Equal(JsonValueKind.Array, content.ValueKind);
        Assert.Equal("native prompt", content[0].GetProperty("text").GetString());
    }

    [Fact]
    public void RpcEntryProjectionAttachesNativeAssistantUsageAndHidesUsageRecord()
    {
        var session = new ConversationSession(Path.GetTempPath(), "fixture", null, "openai");
        session.Append(new ChatMessage(ChatRole.User, "question"));
        session.AppendUsage(new UsageRecord("fixture-model", "model", 12, 2, 0, 0, 14, null)
        {
            InputIncludingCachedTokens = 12
        });
        session.Tree.Append("tool_transcript", JsonSerializer.SerializeToElement(new { transcript = "internal" }));
        session.Append(new ChatMessage(ChatRole.Assistant, "answer"));

        var entries = RpcSessionEntryProjector.ProjectEntries(session);
        var assistant = entries.Single(entry => entry.GetProperty("message").GetProperty("role").GetString() == "assistant");

        Assert.Equal(2, entries.Count);
        Assert.Equal(12, assistant.GetProperty("message").GetProperty("usage").GetProperty("input").GetInt64());
        Assert.Equal(2, assistant.GetProperty("message").GetProperty("usage").GetProperty("output").GetInt64());
        Assert.DoesNotContain(entries, entry => entry.TryGetProperty("customType", out var customType) &&
            customType.GetString() == "pisharp.usage");
    }

    [Fact]
    public void TreeProjectionSortsSiblingsAndAppliesTheLatestLabel()
    {
        var session = ImportSession("""
            {"type":"message","id":"root","parentId":null,"timestamp":"2025-01-01T00:00:01Z","message":{"role":"user","content":"root","timestamp":1735689601000}}
            {"type":"message","id":"later","parentId":"root","timestamp":"2025-01-01T00:00:03Z","message":{"role":"assistant","content":[{"type":"text","text":"later"}],"timestamp":1735689603000}}
            {"type":"message","id":"earlier","parentId":"root","timestamp":"2025-01-01T00:00:02Z","message":{"role":"user","content":"earlier","timestamp":1735689602000}}
            {"type":"label","id":"label-one","parentId":"earlier","timestamp":"2025-01-01T00:00:04Z","targetId":"earlier","label":"checkpoint"}
            {"type":"label","id":"label-two","parentId":"label-one","timestamp":"2025-01-01T00:00:05Z","targetId":"earlier","label":"latest"}
            """);

        var root = Assert.Single(RpcSessionEntryProjector.ProjectTree(session));
        Assert.Equal("root", root.Entry.GetProperty("id").GetString());
        Assert.Equal(["earlier", "later"], root.Children.Select(node => node.Entry.GetProperty("id").GetString()));
        var earlier = root.Children[0];
        Assert.Equal("latest", earlier.Label);
        Assert.Equal("2025-01-01T00:00:05Z", earlier.LabelTimestamp);
        Assert.Equal("label-one", Assert.Single(earlier.Children).Entry.GetProperty("id").GetString());
        Assert.Equal("label-two", Assert.Single(earlier.Children[0].Children).Entry.GetProperty("id").GetString());

        using var document = JsonDocument.Parse(JsonSerializer.Serialize(RpcSessionEntryProjector.ProjectTree(session)));
        var nodes = document.RootElement[0].GetProperty("children");
        Assert.Equal(["entry", "children", "label", "labelTimestamp"],
            nodes[0].EnumerateObject().Select(property => property.Name));
        Assert.Equal(["entry", "children"],
            nodes[1].EnumerateObject().Select(property => property.Name));
    }

    [Fact]
    public void EmptyLatestLabelClearsTreeLabel()
    {
        var session = ImportSession("""
            {"type":"message","id":"root","parentId":null,"timestamp":"2025-01-01T00:00:01Z","message":{"role":"user","content":"root","timestamp":1735689601000}}
            {"type":"label","id":"label-one","parentId":"root","timestamp":"2025-01-01T00:00:02Z","targetId":"root","label":"checkpoint"}
            {"type":"label","id":"label-clear","parentId":"label-one","timestamp":"2025-01-01T00:00:03Z","targetId":"root","label":""}
            """);

        var root = Assert.Single(RpcSessionEntryProjector.ProjectTree(session));
        Assert.Null(root.Label);
        Assert.Null(root.LabelTimestamp);
    }

    private static ConversationSession ImportSession(string entries)
    {
        var cwd = Path.GetFullPath(Path.GetTempPath());
        var input = $$"""
            {"type":"session","version":3,"id":"tree-session","timestamp":"2025-01-01T00:00:00Z","cwd":"{{cwd}}"}
            {{entries}}
            """;
        return PiJsonlSessionInterchange.Import(input);
    }
}
