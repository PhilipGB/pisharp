using System.Text.Json;
using System.Text.Json.Serialization;
using PiSharp.Runtime.Sessions;

namespace PiSharp.Cli.Protocols;

internal sealed class RpcStateCommandHandler(
    JsonLineWriter output,
    Func<ConversationRun> currentRun,
    Func<string?> getThinkingLevel,
    Func<bool> isStreaming,
    Func<JsonElement?> getModel)
{
    public async Task<bool> TryHandleAsync(string command, JsonElement? id, CancellationToken cancellationToken)
    {
        if (command != "get_state") return false;

        var run = currentRun();
        var conversation = run.Conversation;
        var queue = run.GetPendingPrompts();
        await output.EmitAsync(new
        {
            id,
            type = "response",
            command,
            success = true,
            data = new RpcSessionState(
                getModel(),
                getThinkingLevel() ?? "off",
                isStreaming(),
                run.IsCompacting,
                run.SteeringMode.ToSettingValue(),
                run.FollowUpMode.ToSettingValue(),
                run.SessionFile,
                conversation.Id,
                conversation.Name,
                run.AutoCompactionEnabled,
                conversation.ActiveMessages().Count,
                queue.InDeliveryOrder.Count)
        }, cancellationToken);
        return true;
    }
}

internal sealed record RpcSessionState(
    [property: JsonPropertyName("model"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] JsonElement? Model,
    [property: JsonPropertyName("thinkingLevel")] string ThinkingLevel,
    [property: JsonPropertyName("isStreaming")] bool IsStreaming,
    [property: JsonPropertyName("isCompacting")] bool IsCompacting,
    [property: JsonPropertyName("steeringMode")] string SteeringMode,
    [property: JsonPropertyName("followUpMode")] string FollowUpMode,
    [property: JsonPropertyName("sessionFile"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? SessionFile,
    [property: JsonPropertyName("sessionId")] string SessionId,
    [property: JsonPropertyName("sessionName"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? SessionName,
    [property: JsonPropertyName("autoCompactionEnabled")] bool AutoCompactionEnabled,
    [property: JsonPropertyName("messageCount")] int MessageCount,
    [property: JsonPropertyName("pendingMessageCount")] int PendingMessageCount);
