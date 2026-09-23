using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using PiSharp.Core;

namespace PiSharp.Runtime.Sessions;

/// <summary>One MAF execution session projecting the canonical selected conversation branch.</summary>
public sealed class ConversationRun
{
    private readonly PiAgent _agent;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private AgentSession _execution;
    private int _historyCount;
    public ConversationSession Conversation { get; }

    private ConversationRun(PiAgent agent, ConversationSession conversation, AgentSession execution)
    {
        _agent = agent;
        Conversation = conversation;
        _execution = execution;
        _historyCount = conversation.ActiveMessages().Count;
    }

    public static async Task<ConversationRun> OpenAsync(PiAgent agent, ConversationSession conversation,
        CancellationToken cancellationToken = default)
    {
        var execution = await agent.RestoreHistoryAsync(conversation.ActiveMessages(), cancellationToken);
        return new ConversationRun(agent, conversation, execution);
    }

    /// <summary>Selection is durable in Conversation.HeadId; rebuild MAF state before the next run.</summary>
    public async Task SelectAsync(string? id, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            // Check validity and build history before changing the current execution state.
            var previous = Conversation.Tree.HeadId;
            try
            {
                Conversation.Tree.Select(id);
                var history = Conversation.ActiveMessages();
                var restored = await _agent.RestoreHistoryAsync(history, cancellationToken);
                _execution = restored;
                _historyCount = history.Count;
            }
            catch { Conversation.Tree.Select(previous); throw; }
        }
        finally { _gate.Release(); }
    }

    public async IAsyncEnumerable<AgentResponseUpdate> RunStreamingAsync(string prompt,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        var completed = false;
        var partialText = new System.Text.StringBuilder();
        var events = new List<string>();
        try
        {
            await foreach (var update in _agent.RunStreamingAsync(prompt, _execution, cancellationToken))
            {
                if (!string.IsNullOrEmpty(update.Text)) partialText.Append(update.Text);
                if (update.Contents is not null)
                    foreach (var content in update.Contents)
                        if (content is FunctionCallContent call) events.Add($"call:{call.Name}:{call.CallId}");
                        else if (content is FunctionResultContent result) events.Add($"result:{result.CallId}:{(result.Exception is null ? "ok" : "error")}");
                yield return update;
            }
            completed = true;
        }
        finally
        {
            try
            {
                var history = _agent.GetHistory(_execution);
                if (history.Count < _historyCount) throw new InvalidDataException("MAF discarded canonical conversation history.");
                var existing = Conversation.ActiveMessages();
                if (existing.Count != _historyCount || existing.Where((message, index) => !JsonElement.DeepEquals(
                    JsonSerializer.SerializeToElement(message, AIJsonUtilities.DefaultOptions),
                    JsonSerializer.SerializeToElement(history[index], AIJsonUtilities.DefaultOptions))).Any())
                    throw new InvalidDataException("MAF changed existing canonical conversation history.");
                foreach (var message in history.Skip(_historyCount)) Conversation.Append(message);
                _historyCount = history.Count;
                if (!completed) Conversation.Tree.Append("interrupted", JsonSerializer.SerializeToElement(
                    new { prompt, partialText = partialText.ToString(), events, timestamp = DateTimeOffset.UtcNow }));
            }
            finally { _gate.Release(); }
        }
    }
}
