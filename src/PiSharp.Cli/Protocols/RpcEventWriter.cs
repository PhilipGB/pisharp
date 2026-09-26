using PiSharp.Runtime.Sessions;

namespace PiSharp.Cli.Protocols;

/// <summary>Writes lifecycle events in the order required by the RPC stream.</summary>
internal sealed class RpcEventWriter(JsonLineWriter output)
{
    public Task EmitThinkingLevelChangedAsync(string level, CancellationToken cancellationToken = default) =>
        output.EmitAsync(new { type = "thinking_level_changed", level }, cancellationToken);

    public Task EmitSessionInfoChangedAsync(string? name, CancellationToken cancellationToken = default) =>
        output.EmitAsync(new { type = "session_info_changed", name }, cancellationToken);

    public async Task<bool> RunAsync(ConversationRun run, string prompt, CancellationToken cancellationToken = default,
        Func<AgentLifecycleEvent, Task>? observeEvent = null, string? api = null)
    {
        var projector = new RpcEventProjector();
        var succeeded = false;
        var accepted = false;
        var turnOpen = false;
        string? runStartHead = null;
        string? turnStartHead = null;
        await foreach (var item in run.RunEventsAsync(prompt, cancellationToken))
        {
            if (observeEvent is not null) await observeEvent(item);
            if (item.Type == "prompt_accepted")
            {
                if (!accepted)
                {
                    runStartHead = item.RunStartHead;
                    await output.EmitAsync(new { type = "agent_start" }, CancellationToken.None);
                    projector.BeginAgent();
                }
                accepted = true;
                turnStartHead = item.RunStartHead;
                if (!turnOpen)
                {
                    await output.EmitAsync(new { type = "turn_start" }, CancellationToken.None);
                    turnOpen = true;
                    projector.BeginTurn();
                }
                foreach (var record in projector.ProjectPrompt(item, run.Conversation))
                    await output.EmitAsync(record, CancellationToken.None);
                continue;
            }
            if (item.Type == "steering_message_accepted")
            {
                foreach (var record in projector.ProjectPrompt(item, run.Conversation))
                    await output.EmitAsync(record, CancellationToken.None);
                continue;
            }
            if (item.Type == "agent_attempt_started")
            {
                runStartHead = item.RunStartHead;
                turnStartHead = item.RunStartHead;
                await output.EmitAsync(new { type = "agent_start" }, CancellationToken.None);
                await output.EmitAsync(new { type = "turn_start" }, CancellationToken.None);
                projector.BeginAgent();
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
            foreach (var record in projector.Project(item, run.Conversation, turnStartHead, runStartHead,
                         accepted, api, turnOpen))
                await output.EmitAsync(record, CancellationToken.None);
            if (item.Type == "assistant_turn_started") turnOpen = true;
            if (item.Type is "assistant_turn_completed" or "turn_failed" or "turn_interrupted") turnOpen = false;
            if (item.Type == "agent_run_completed") succeeded = true;
        }
        return succeeded;
    }
}
