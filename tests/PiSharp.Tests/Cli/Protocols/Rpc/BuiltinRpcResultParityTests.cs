using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.AI;
using PiSharp.Cli.Protocols;
using PiSharp.Runtime.Extensions;
using PiSharp.Runtime.Sessions;
using PiSharp.Runtime.Tools;

namespace PiSharp.Tests;

public sealed class BuiltinRpcResultParityTests
{
    public static IEnumerable<object[]> StructuredCases()
    {
        foreach (var (tool, caseId) in new[] { ("grep", "grep-match-limit"), ("find", "find-result-limit"), ("ls", "ls-over-limit") })
        {
            using var source = SearchProcessProtocolTests.SourceFixture(tool, "results");
            var row = source.RootElement.GetProperty("cases").EnumerateArray().Single(item => item.GetProperty("caseId").GetString() == caseId);
            yield return [tool, row.GetProperty("expectedResult").GetRawText()];
        }
        using var workflow = SearchProcessProtocolTests.SourceFixture("agent", "workflow");
        var edit = workflow.RootElement.GetProperty("outcomes").EnumerateArray()
            .Single(item => item.GetProperty("tool").GetString() == "edit" && !item.GetProperty("isError").GetBoolean());
        yield return ["edit", edit.GetProperty("result").GetRawText()];
    }

    [Theory]
    [MemberData(nameof(StructuredCases))]
    public void StructuredDetailsRemainVisibleInRpcAndJsonlAfterCanonicalReload(string tool, string expected)
    {
        using var document = JsonDocument.Parse(expected);
        var result = document.RootElement;
        var session = new ConversationSession(Path.GetTempPath(), "fixture", null);
        session.Append(new ChatMessage(ChatRole.User, "inspect"));
        session.Append(new ChatMessage(ChatRole.Assistant, [new FunctionCallContent("result-call", tool, new Dictionary<string, object?>())]));
        var text = result.GetProperty("content")[0].GetProperty("text").GetString()!;
        var details = result.GetProperty("details").Clone();
        object output = tool == "edit" ? new PiSharpToolResult(text, Details: details) : new SearchToolOutput(text, details);
        session.Append(new ChatMessage(ChatRole.Tool, [new FunctionResultContent("result-call", output)]));
        var restored = ConversationSession.Parse(session.ToJson());
        var projected = RpcSessionMessageProjector.Project(restored, "openai-completions");
        var message = projected.OfType<JsonObject>().Single(message => message["role"]?.GetValue<string>() == "toolResult");
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(details.GetRawText()), message["details"]), "RPC details missing or changed for " + tool);
        Assert.Equal(tool, message["toolName"]!.GetValue<string>());
        var exported = PiJsonlSessionInterchange.Export(restored);
        using var row = JsonDocument.Parse(exported.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Single(line => line.Contains("\"role\":\"toolResult\"", StringComparison.Ordinal)));
        Assert.True(JsonElement.DeepEquals(details, row.RootElement.GetProperty("message").GetProperty("details")));
    }

    [Fact]
    public void RpcResolvesEachToolNameAtItsCallEvenWhenLegacyIdsAreReused()
    {
        var session = new ConversationSession(Path.GetTempPath(), "fixture", null);
        foreach (var tool in new[] { "read", "edit", "ls" })
        {
            session.Append(new ChatMessage(ChatRole.Assistant, [new FunctionCallContent("legacy-reused", tool, new Dictionary<string, object?>())]));
            session.Append(new ChatMessage(ChatRole.Tool, [new FunctionResultContent("legacy-reused", tool + " output")]));
        }
        var projected = RpcSessionMessageProjector.Project(ConversationSession.Parse(session.ToJson()), "openai-completions");
        Assert.Equal(["read", "edit", "ls"], projected.OfType<JsonObject>()
            .Where(message => message["role"]?.GetValue<string>() == "toolResult")
            .Select(message => message["toolName"]!.GetValue<string>()));
    }
}
