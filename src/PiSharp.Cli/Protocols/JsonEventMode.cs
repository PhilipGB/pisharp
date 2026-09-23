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

    public async Task<bool> RunAsync(ConversationRun run, string prompt, CancellationToken cancellationToken = default)
    {
        var succeeded = false;
        await foreach (var item in run.RunEventsAsync(prompt, cancellationToken))
        {
            // A canceled run must still report its interruption and settlement to consumers.
            await output.EmitAsync(new { type = "event", format = "pisharp", data = item }, CancellationToken.None);
            if (item.Type == "agent_run_completed") succeeded = true;
        }
        return succeeded;
    }
}
