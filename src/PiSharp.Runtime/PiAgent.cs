using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace PiSharp.Runtime;

/// <summary>One shared streaming runtime for terminal and one-shot invocation.</summary>
public sealed class PiAgent(IChatClient client, CodingTools tools)
{
    private readonly ChatClientAgent _agent = new(client, new ChatClientAgentOptions
    {
        Name = "PiSharp",
        ChatOptions = new ChatOptions
        {
            Instructions = "You are PiSharp, a coding agent. Inspect files before modifying them. Use read for text and bash for shell commands. Use edit for targeted changes and write for new files.",
            Tools = tools.Create()
        }
    });

    public async Task<AgentSession> CreateSessionAsync(CancellationToken cancellationToken = default) =>
        await _agent.CreateSessionAsync(cancellationToken);

    public ValueTask<System.Text.Json.JsonElement> SerializeSessionAsync(AgentSession session, CancellationToken cancellationToken = default) =>
        _agent.SerializeSessionAsync(session, cancellationToken: cancellationToken);

    public ValueTask<AgentSession> DeserializeSessionAsync(System.Text.Json.JsonElement snapshot, CancellationToken cancellationToken = default) =>
        _agent.DeserializeSessionAsync(snapshot, cancellationToken: cancellationToken);

    public async IAsyncEnumerable<AgentResponseUpdate> RunStreamingAsync(string prompt, AgentSession session,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var update in _agent.RunStreamingAsync(prompt, session: session, cancellationToken: cancellationToken))
            yield return update;
    }
}
