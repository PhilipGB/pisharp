using PiSharp.Runtime.Sessions;

namespace PiSharp.Cli.Protocols;

/// <summary>Writes lifecycle events in the order required by the RPC stream.</summary>
internal sealed class RpcEventWriter(JsonLineWriter output)
{
    private readonly RpcEventProjector _projector = new();

    public Task EmitThinkingLevelChangedAsync(string level, CancellationToken cancellationToken = default) =>
        output.EmitAsync(new { type = "thinking_level_changed", level }, cancellationToken);

    public async Task<bool> RunAsync(ConversationRun run, string prompt, CancellationToken cancellationToken = default,
        Func<AgentLifecycleEvent, Task>? observeEvent = null, string? api = null)
    {
        var succeeded = false;
        var accepted = false;
        string? turnStartHead = null;
        await foreach (var item in run.RunEventsAsync(prompt, cancellationToken))
        {
            if (observeEvent is not null) await observeEvent(item);
            if (item.Type == "prompt_accepted")
            {
                turnStartHead = item.RunStartHead;
                accepted = true;
                await output.EmitAsync(new { type = "agent_start" }, CancellationToken.None);
                continue;
            }
            if (item.Type == "prompt_rejected") continue;
            if (item.Type == "agent_settled")
            {
                if (accepted) await output.EmitAsync(new { type = "agent_settled" }, CancellationToken.None);
                continue;
            }
            if (_projector.Project(item, run.Conversation, turnStartHead, accepted, api) is { } record)
                await output.EmitAsync(record, CancellationToken.None);
            if (item.Type == "agent_run_completed") succeeded = true;
        }
        return succeeded;
    }
}
