using System.Text.Json.Nodes;
using PiSharp.Runtime.Sessions;

namespace PiSharp.Cli.Protocols;

/// <summary>Writes lifecycle events in the order required by the RPC stream.</summary>
internal sealed class RpcEventWriter(JsonLineWriter output)
{
    private readonly object _projectorGate = new();
    private RpcEventProjector? _activeProjector;
    private string? _activeApi;

    public JsonArray ProjectMessagesSnapshot(ConversationSession conversation)
    {
        lock (_projectorGate)
            return (_activeProjector ?? new RpcEventProjector()).ProjectMessagesSnapshot(conversation, _activeApi);
    }

    public string? GetLastAssistantTextSnapshot(ConversationSession conversation)
    {
        lock (_projectorGate)
            return (_activeProjector ?? new RpcEventProjector()).GetLastAssistantTextSnapshot(conversation, _activeApi);
    }

    public Task EmitThinkingLevelChangedAsync(string level, CancellationToken cancellationToken = default) =>
        output.EmitAsync(new { type = "thinking_level_changed", level }, cancellationToken);

    public Task EmitSessionInfoChangedAsync(string? name, CancellationToken cancellationToken = default) =>
        output.EmitAsync(new { type = "session_info_changed", name }, cancellationToken);

    public async Task EmitCommandLifecycleAsync(ConversationRun run, AgentLifecycleEvent item)
    {
        var projector = new RpcEventProjector();
        foreach (var record in projector.Project(item, run.Conversation, null, null, false, null))
            await output.EmitAsync(record, CancellationToken.None);
    }

    public async Task<bool> RunAsync(ConversationRun run, string prompt, CancellationToken cancellationToken = default,
        Func<AgentLifecycleEvent, Task>? observeEvent = null, string? api = null)
    {
        var projector = new RpcEventProjector();
        lock (_projectorGate)
        {
            _activeProjector = projector;
            _activeApi = api;
        }
        var succeeded = false;
        var accepted = false;
        var turnOpen = false;
        string? runStartHead = null;
        string? turnStartHead = null;
        try
        {
            await foreach (var item in run.RunEventsAsync(prompt, cancellationToken))
            {
                if (observeEvent is not null) await observeEvent(item);
                if (item.Type == "prompt_accepted")
                {
                    if (!accepted)
                    {
                        runStartHead = item.RunStartHead;
                        await output.EmitAsync(new { type = "agent_start" }, CancellationToken.None);
                        lock (_projectorGate) projector.BeginAgent();
                    }
                    accepted = true;
                    turnStartHead = item.RunStartHead;
                    if (!turnOpen)
                    {
                        await output.EmitAsync(new { type = "turn_start" }, CancellationToken.None);
                        turnOpen = true;
                        lock (_projectorGate) projector.BeginTurn();
                    }
                    IReadOnlyList<object> promptRecords;
                    lock (_projectorGate) promptRecords = projector.ProjectPrompt(item, run.Conversation);
                    foreach (var record in promptRecords)
                        await output.EmitAsync(record, CancellationToken.None);
                    continue;
                }
                if (item.Type == "steering_message_accepted")
                {
                    IReadOnlyList<object> promptRecords;
                    lock (_projectorGate) promptRecords = projector.ProjectPrompt(item, run.Conversation);
                    foreach (var record in promptRecords)
                        await output.EmitAsync(record, CancellationToken.None);
                    continue;
                }
                if (item.Type == "agent_attempt_started")
                {
                    runStartHead = item.RunStartHead;
                    turnStartHead = item.RunStartHead;
                    await output.EmitAsync(new { type = "agent_start" }, CancellationToken.None);
                    await output.EmitAsync(new { type = "turn_start" }, CancellationToken.None);
                    lock (_projectorGate) projector.BeginAgent();
                    turnOpen = true;
                    continue;
                }
                if (item.Type == "prompt_rejected") continue;
                if (item.Type == "agent_settled")
                {
                    if (accepted) await output.EmitAsync(new { type = "agent_settled" }, CancellationToken.None);
                    continue;
                }
                if (item.Type == "assistant_turn_started" && turnOpen) continue;
                IReadOnlyList<object> records;
                lock (_projectorGate)
                    records = projector.Project(item, run.Conversation, turnStartHead, runStartHead,
                        accepted, api, turnOpen);
                foreach (var record in records)
                    await output.EmitAsync(record, CancellationToken.None);
                if (item.Type == "assistant_turn_started") turnOpen = true;
                if (item.Type is "assistant_turn_completed" or "turn_failed" or "turn_interrupted") turnOpen = false;
                if (item.Type == "agent_run_completed") succeeded = true;
            }
        }
        finally
        {
            lock (_projectorGate)
            {
                if (ReferenceEquals(_activeProjector, projector))
                {
                    _activeProjector = null;
                    _activeApi = null;
                }
            }
        }
        return succeeded;
    }
}
