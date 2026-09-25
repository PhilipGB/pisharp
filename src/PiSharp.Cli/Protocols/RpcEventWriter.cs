using System.Text.Json;
using PiSharp.Runtime.Sessions;

namespace PiSharp.Cli.Protocols;

/// <summary>Projects application lifecycle events onto the Pi RPC event stream.</summary>
internal sealed class RpcEventWriter(JsonLineWriter output)
{
    public Task EmitThinkingLevelChangedAsync(string level, CancellationToken cancellationToken = default) =>
        output.EmitAsync(new { type = "thinking_level_changed", level }, cancellationToken);

    public async Task<bool> RunAsync(ConversationRun run, string prompt, CancellationToken cancellationToken = default,
        Func<AgentLifecycleEvent, Task>? observeEvent = null)
    {
        var succeeded = false;
        await foreach (var item in run.RunEventsAsync(prompt, cancellationToken))
        {
            if (observeEvent is not null) await observeEvent(item);
            if (Project(item) is { } record) await output.EmitAsync(record, CancellationToken.None);
            if (item.Type == "agent_run_completed") succeeded = true;
        }
        return succeeded;
    }

    private static object? Project(AgentLifecycleEvent item) => item.Type switch
    {
        "prompt_queued" => null,
        "queue_update" => ProjectQueueUpdate(item),
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
