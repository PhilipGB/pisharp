using System.Text.Json;
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
