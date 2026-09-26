using System.Text.Json;
using System.Text.Json.Nodes;
using PiSharp.Runtime.Sessions;

namespace PiSharp.Cli.Protocols;

internal sealed class RpcEventProjector
{
    public object? Project(AgentLifecycleEvent item, ConversationSession conversation, string? runStartHead,
        bool runAccepted, string? api) => item.Type switch
        {
            "prompt_queued" or "prompt_accepted" or "agent_attempt_started" or "prompt_rejected" or "agent_run_completed" or "agent_settled" => null,
            "queue_update" => ProjectQueueUpdate(item),
            "auto_retry_start" => new
            {
                type = "auto_retry_start",
                attempt = item.RetryAttempt,
                maxAttempts = item.RetryMaxAttempts,
                delayMs = item.RetryDelayMs,
                errorMessage = item.Error
            },
            "auto_retry_end" => ProjectRetryEnd(item),
            "turn_completed" or "turn_failed" or "turn_interrupted" when runAccepted => new
            {
                type = "agent_end",
                messages = PiJsonlSessionInterchange.ProjectRunMessages(conversation, runStartHead, api,
                    item.Type == "turn_completed" ? null : item.Type, item.Error, item.TurnEndHead),
                willRetry = item.WillRetry ?? false
            },
            "turn_completed" or "turn_failed" or "turn_interrupted" => null,
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

    private static JsonObject ProjectRetryEnd(AgentLifecycleEvent item)
    {
        var record = new JsonObject
        {
            ["type"] = "auto_retry_end",
            ["success"] = item.RetrySuccess ?? false,
            ["attempt"] = item.RetryAttempt ?? 0
        };
        if (item.RetryFinalError is not null) record["finalError"] = item.RetryFinalError;
        return record;
    }

    private static string[] ReadQueue(JsonElement payload, string propertyName) =>
        payload.TryGetProperty(propertyName, out var queue) && queue.ValueKind == JsonValueKind.Array
            ? queue.EnumerateArray().Where(item => item.ValueKind == JsonValueKind.String)
                .Select(item => item.GetString()!).ToArray()
            : [];
}
