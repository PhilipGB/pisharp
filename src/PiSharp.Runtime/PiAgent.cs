using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using PiSharp.Core;

namespace PiSharp.Runtime;

/// <summary>One shared streaming runtime for terminal and one-shot invocation.</summary>
public sealed class PiAgent
{
    private readonly InMemoryChatHistoryProvider _history = new();
    private readonly ChatClientAgent _agent;

    public PiAgent(IChatClient client, CodingTools tools, IReadOnlyList<string>? selectedTools = null, IReadOnlyList<string>? excludedTools = null, bool noTools = false)
    {
        _agent = new ChatClientAgent(client, new ChatClientAgentOptions
        {
            Name = "PiSharp",
            ChatHistoryProvider = _history,
            ChatOptions = new ChatOptions
            {
                Instructions = "You are PiSharp, a coding agent. Inspect files before modifying them when tools are available. Use only the tools provided for this run.",
                Tools = tools.Create(selectedTools, excludedTools, noTools)
            }
        });
    }

    public async Task<AgentSession> CreateSessionAsync(CancellationToken cancellationToken = default) =>
        await _agent.CreateSessionAsync(cancellationToken);

    /// <summary>Experimental branch restore. Does not append subsequent turns to the Pi journal.</summary>
    public async Task<AgentSession> CreateSessionFromJournalAsync(PiSessionJournal journal, CancellationToken cancellationToken = default)
    {
        // Perform the entire conversion before mutating session history, so unsupported
        // content cannot leave a partially imported conversation behind.
        var history = PiHistoryBridge.ToChatMessages(PiSessionProjection.Build(journal.Tree));
        var session = await CreateSessionAsync(cancellationToken);
        _history.SetMessages(session, history);
        return session;
    }

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
