using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.AI;
using PiSharp.Cli.Protocols;
using PiSharp.Runtime.Sessions;

namespace PiSharp.Tests;

public sealed class RpcSessionMessageProjectorTests
{
    [Fact]
    public void ProjectCompactedContextPreservesPiSummaryRoleAndAppliesContextEdit()
    {
        var session = new ConversationSession(Path.GetTempPath(), "fixture", null);
        session.Append(new ChatMessage(ChatRole.User, "old question"));
        session.Append(new ChatMessage(ChatRole.Assistant, "old answer"));
        session.Append(new ChatMessage(ChatRole.User, "retained question"));
        session.Append(new ChatMessage(ChatRole.Assistant, "original answer"));
        var plan = Assert.IsType<ConversationSession.CompactionPlan>(session.PrepareCompaction());
        session.AppendCompaction(plan, "summary of old turn", tokensBefore: 1234);
        var assistantEntry = session.Tree.ActivePath()
            .Last(node => node.Type == "chat" && ConversationSession.RestoreEntry(node).Role == ChatRole.Assistant);
        session.Tree.Append("context_edit", JsonSerializer.SerializeToElement(new
        {
            targetId = assistantEntry.Id,
            replacement = new { content = "edited answer" }
        }));

        var messages = RpcSessionMessageProjector.Project(session, "openai-completions");

        Assert.Equal("compactionSummary", messages[0]!["role"]!.GetValue<string>());
        Assert.Equal("summary of old turn", messages[0]!["summary"]!.GetValue<string>());
        Assert.Equal(1234, messages[0]!["tokensBefore"]!.GetValue<int>());
        Assert.Equal("retained question", ReadText(messages[1]!["content"]));
        Assert.Equal("assistant", messages[2]!["role"]!.GetValue<string>());
        Assert.Equal("edited answer", ReadText(messages[2]!["content"]));
        Assert.DoesNotContain(messages, message => message!["content"] is { } content && ReadText(content) == "old question");
        Assert.DoesNotContain(messages, message => message!["content"] is { } content && ReadText(content) == "original answer");
    }

    [Fact]
    public void ProjectContextEditPreservesPiCustomMessageFields()
    {
        var session = PiJsonlSessionInterchange.Import("""
            {"type":"session","version":3,"id":"custom-fixture","cwd":"/tmp","timestamp":"2026-09-27T00:00:00.000Z"}
            {"type":"custom_message","id":"custom-entry","parentId":null,"timestamp":"2026-09-27T00:00:01.000Z","customType":"fixture-note","content":[{"type":"text","text":"original"}],"display":true,"details":{"source":"fixture"}}
            {"type":"context_edit","id":"edit-entry","parentId":"custom-entry","timestamp":"2026-09-27T00:00:02.000Z","targetId":"custom-entry","replacement":{"content":"edited"}}
            """, Path.GetTempPath());

        var message = Assert.Single(RpcSessionMessageProjector.Project(session, null));

        Assert.Equal("custom", message!["role"]!.GetValue<string>());
        Assert.Equal("fixture-note", message["customType"]!.GetValue<string>());
        Assert.Equal("edited", message["content"]!.GetValue<string>());
        Assert.True(message["display"]!.GetValue<bool>());
        Assert.Equal("fixture", message["details"]!["source"]!.GetValue<string>());
    }

    private static string ReadText(JsonNode? content) => content switch
    {
        JsonValue value when value.TryGetValue<string>(out var text) => text,
        JsonArray parts => string.Concat(parts.OfType<JsonObject>()
            .Where(part => part["type"]?.GetValue<string>() == "text")
            .Select(part => part["text"]?.GetValue<string>() ?? "")),
        _ => ""
    };
}
