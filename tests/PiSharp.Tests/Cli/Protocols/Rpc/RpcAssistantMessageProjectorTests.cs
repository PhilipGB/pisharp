using System.Text.Json;
using Microsoft.Extensions.AI;
using PiSharp.Cli.Protocols;
using PiSharp.Runtime.Extensions;
using PiSharp.Runtime.Sessions;

namespace PiSharp.Tests;

public sealed class RpcAssistantMessageProjectorTests
{
    [Fact]
    public void StreamedToolCallMessageStartsWithItsInitialPartialContent()
    {
        var projector = new RpcAssistantMessageProjector();
        var conversation = new ConversationSession(Path.GetTempPath(), "fixture", null);
        var partialArguments = "{\"command\":\"echo ok\"}";

        var projected = projector.ProjectUpdate(new AgentLifecycleEvent("model_text_delta")
        {
            StreamedToolCallDelta = new ProviderToolCallDelta(0, "call-id", "bash", partialArguments)
        }, conversation, "openai-completions");

        using var start = JsonDocument.Parse(JsonSerializer.Serialize(projected[0]));
        var toolCall = Assert.Single(start.RootElement.GetProperty("message").GetProperty("content").EnumerateArray());
        Assert.Equal("toolCall", toolCall.GetProperty("type").GetString());
        Assert.Equal("call-id", toolCall.GetProperty("id").GetString());
        Assert.Equal("bash", toolCall.GetProperty("name").GetString());
        Assert.Equal(partialArguments, toolCall.GetProperty("partialArgs").GetString());
        Assert.Equal(0, toolCall.GetProperty("streamIndex").GetInt32());
        Assert.Equal("echo ok", toolCall.GetProperty("arguments").GetProperty("command").GetString());

        using var update = JsonDocument.Parse(JsonSerializer.Serialize(projected[1]));
        Assert.Equal("toolcall_start", update.RootElement.GetProperty("assistantMessageEvent")
            .GetProperty("type").GetString());
    }

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
