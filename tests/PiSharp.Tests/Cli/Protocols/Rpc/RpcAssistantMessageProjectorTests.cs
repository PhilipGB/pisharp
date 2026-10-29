using System.Text.Json;
using Microsoft.Extensions.AI;
using PiSharp.Cli.Protocols;
using PiSharp.Runtime.Extensions;
using PiSharp.Runtime.Sessions;

namespace PiSharp.Tests;

public sealed class RpcAssistantMessageProjectorTests
{
    [Fact]
    public void ToolResultNestedCallsOmitRedundantParentCallId()
    {
        var projector = new RpcAssistantMessageProjector();
        var conversation = new ConversationSession(Path.GetTempPath(), "fixture", null);
        var nestedCalls = new PiSharpNestedToolCalls(
            [new PiSharpNestedToolCall("root/1", "child", "root", "ok")], Complete: true);
        var resultMessage = new ChatMessage(ChatRole.Tool, [new FunctionResultContent("root", "complete")]);
        var projected = projector.ProjectToolResult(new AgentLifecycleEvent("tool_execution_finished",
            Tool: "orchestrator")
        { ToolResultMessage = resultMessage, NestedToolCalls = nestedCalls }, conversation);

        using var json = JsonDocument.Parse(JsonSerializer.Serialize(projected[0]));
        var call = json.RootElement.GetProperty("message").GetProperty("nestedCalls")
            .GetProperty("calls")[0];

        Assert.Equal("root/1", call.GetProperty("id").GetString());
        Assert.False(call.TryGetProperty("parentToolCallId", out _));
    }
}
