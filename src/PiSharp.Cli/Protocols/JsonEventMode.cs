using PiSharp.Runtime.Sessions;

namespace PiSharp.Cli.Protocols;

/// <summary>PiSharp-owned LF-framed lifecycle contract, shared with RPC and the .NET API.</summary>
public sealed class JsonEventMode(JsonLineWriter output)
{
    public JsonEventMode(TextWriter writer) : this(new JsonLineWriter(writer)) { }

    public Task HeaderAsync(ConversationSession conversation, CancellationToken cancellationToken = default) =>
        output.EmitAsync(new
        {
            type = "session",
            format = "pisharp",
            version = ConversationSession.FormatVersion,
            id = conversation.Id,
            timestamp = DateTimeOffset.UtcNow,
            cwd = conversation.WorkingDirectory
        }, cancellationToken);

    public async Task RejectAsync(string reason)
    {
        await output.EmitAsync(new { type = "event", format = "pisharp", data = new AgentLifecycleEvent("prompt_rejected", Error: reason) });
        await output.EmitAsync(new { type = "event", format = "pisharp", data = new AgentLifecycleEvent("agent_settled") });
    }

    public Task<bool> RunAsync(ConversationRun run, string prompt, CancellationToken cancellationToken = default,
        Func<AgentLifecycleEvent, Task>? observeEvent = null) =>
        RunAsync(run, new Microsoft.Extensions.AI.ChatMessage(Microsoft.Extensions.AI.ChatRole.User, prompt), prompt,
            cancellationToken, observeEvent);

    public async Task<bool> RunAsync(ConversationRun run, Microsoft.Extensions.AI.ChatMessage prompt,
        string displayText, CancellationToken cancellationToken = default,
        Func<AgentLifecycleEvent, Task>? observeEvent = null)
    {
        var succeeded = false;
        await foreach (var item in run.RunEventsAsync(prompt, displayText, cancellationToken))
        {
            // A canceled run must still report its interruption and settlement to consumers.
            if (observeEvent is not null) await observeEvent(item);
            await output.EmitAsync(new { type = "event", format = "pisharp", data = item }, CancellationToken.None);
            if (item.Type == "agent_run_completed") succeeded = true;
        }
        return succeeded;
    }
}
