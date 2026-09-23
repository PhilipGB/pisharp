using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
namespace PiSharp.Runtime;

/// <summary>One shared streaming runtime for terminal and one-shot invocation.</summary>
public sealed class PiAgent
{
    private readonly InMemoryChatHistoryProvider _history = new();
    private readonly ChatClientAgent _agent;

    public PiAgent(IChatClient client, CodingTools tools, IReadOnlyList<string>? selectedTools = null, IReadOnlyList<string>? excludedTools = null, bool noTools = false, string? contextInstructions = null)
    {
        _agent = new ChatClientAgent(client, new ChatClientAgentOptions
        {
            Name = "PiSharp",
            ChatHistoryProvider = _history,
            ChatOptions = new ChatOptions
            {
                Instructions = "You are PiSharp, a coding agent. Inspect files before modifying them when tools are available. Use only the tools provided for this run.\n\n" + (contextInstructions ?? ""),
                Tools = tools.Create(selectedTools, excludedTools, noTools)
            }
        });
    }

    public async Task<AgentSession> CreateSessionAsync(CancellationToken cancellationToken = default) =>
        await _agent.CreateSessionAsync(cancellationToken);

    public IReadOnlyList<ChatMessage> GetHistory(AgentSession session) => _history.GetMessages(session).ToArray();

    public async Task<AgentSession> RestoreHistoryAsync(IEnumerable<ChatMessage> messages, CancellationToken cancellationToken = default)
    {
        var session = await CreateSessionAsync(cancellationToken);
        _history.SetMessages(session, messages.ToList());
        return session;
    }


    public async IAsyncEnumerable<AgentResponseUpdate> RunStreamingAsync(string prompt, AgentSession session,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var update in _agent.RunStreamingAsync(prompt, session: session, cancellationToken: cancellationToken))
            yield return update;
    }
}
