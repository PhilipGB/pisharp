using System.Text.Json;
using Microsoft.Extensions.AI;
using PiSharp.Runtime.Extensions;
using PiSharp.Runtime.Sessions;

namespace PiSharp.Tests;

public sealed class CompactionNestedToolCallsTests
{
    [Fact]
    public void NestedFileCallsContributeToCompactionFileOperationDetails()
    {
        var conversation = new ConversationSession(Path.GetTempPath(), "fixture", null);
        conversation.Append(new ChatMessage(ChatRole.User, "inspect and update files"));
        conversation.Append(new ChatMessage(ChatRole.Assistant,
            [new FunctionCallContent("orchestrator-call", "orchestrate", new Dictionary<string, object?>())]));
        conversation.Append(new ChatMessage(ChatRole.Tool,
            [new FunctionResultContent("orchestrator-call", "updated")]), new PiSharpNestedToolCalls(
            [
                Call("read", "src/input.cs"),
                Call("write", "src/output.cs"),
                Call("edit", "src/output.cs")
            ], Complete: true));
        conversation.Append(new ChatMessage(ChatRole.User, "continue"));
        conversation.Append(new ChatMessage(ChatRole.Assistant, "done"));

        var plan = Assert.IsType<ConversationSession.CompactionPlan>(conversation.PrepareCompaction(keepRecentTokens: 1));
        var details = ConversationCompactionMetadata.CollectDetails(conversation, plan);

        Assert.Equal(["src/input.cs"], details.ReadFiles);
        Assert.Equal(["src/output.cs"], details.ModifiedFiles);
    }

    private static PiSharpNestedToolCall Call(string name, string path)
    {
        using var arguments = JsonDocument.Parse(JsonSerializer.Serialize(new { path }));
        return new PiSharpNestedToolCall("root/1", name, "root", "ok", arguments.RootElement.Clone());
    }
}
