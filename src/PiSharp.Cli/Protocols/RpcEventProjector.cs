using System.Text.Json;
using PiSharp.Runtime.Sessions;

namespace PiSharp.Cli.Protocols;

internal sealed class RpcEventProjector
{
    public object? Project(AgentLifecycleEvent item, ConversationSession conversation, string? runStartHead,
        bool runAccepted, string? api) => item.Type switch
        {
            "prompt_queued" or "prompt_accepted" or "prompt_rejected" or "agent_settled" => null,
            "queue_update" => ProjectQueueUpdate(item),
            "agent_run_completed" or "turn_failed" or "turn_interrupted" when runAccepted => new
            {
                type = "agent_end",
                messages = PiJsonlSessionInterchange.ProjectRunMessages(conversation, runStartHead, api, item.Type, item.Error),
                willRetry = false
            },
            "agent_run_completed" or "turn_failed" or "turn_interrupted" => null,
            _ => new { type = "event", format = "pisharp", data = item }
        };

    private static object ProjectQueueUpdate(AgentLifecycleEvent item)
    {
        using var payload = JsonDocument.Parse(item.Text ?? "{}");
        return new
        {
            type = "queue_update",
            steering = ReadQueue(payload.RootElement, "steering"),
            followUp = ReadQueue(payload.RootElement, "followUp")
        };
    }

    private static string[] ReadQueue(JsonElement payload, string propertyName) =>
        payload.TryGetProperty(propertyName, out var queue) && queue.ValueKind == JsonValueKind.Array
            ? queue.EnumerateArray().Where(item => item.ValueKind == JsonValueKind.String)
                .Select(item => item.GetString()!).ToArray()
            : [];
}
