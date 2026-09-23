using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using PiSharp.Core;

namespace PiSharp.Runtime.Sessions;

/// <summary>One MAF execution session projecting the canonical selected conversation branch.</summary>
public sealed class ConversationRun
{
    private readonly PiAgent _agent;
    private readonly Func<CancellationToken, Task>? _save;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private AgentSession _execution;
    private int _historyCount;
    public ConversationSession Conversation { get; }

    private ConversationRun(PiAgent agent, ConversationSession conversation, AgentSession execution, Func<CancellationToken, Task>? save)
    {
        _agent = agent;
        _save = save;
        Conversation = conversation;
        _execution = execution;
        _historyCount = conversation.ActiveMessages().Count;
    }

    public static async Task<ConversationRun> OpenAsync(PiAgent agent, ConversationSession conversation,
        CancellationToken cancellationToken = default, Func<CancellationToken, Task>? save = null)
    {
        if (conversation.RecoverIncomplete() && save is not null) await save(cancellationToken);
        var execution = await agent.RestoreHistoryAsync(conversation.ActiveMessages(), cancellationToken);
        return new ConversationRun(agent, conversation, execution, save);
    }

    /// <summary>Authoritative ordered lifecycle, including actual model and tool boundaries.
    /// No completion is emitted when execution fails. Disposing the stream aborts the run.</summary>
    public async IAsyncEnumerable<AgentLifecycleEvent> RunEventsAsync(string prompt,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var channel = Channel.CreateUnbounded<AgentLifecycleEvent>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false
        });
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var pump = Task.Run(async () =>
        {
            var accepted = false;
            try
            {
                await foreach (var update in RunStreamingAsync(prompt, linked.Token, value =>
                {
                    if (value.Type == "prompt_accepted") accepted = true;
                    channel.Writer.TryWrite(value);
                }))
                {
                    if (update.Contents is not null)
                        foreach (var content in update.Contents)
                        {
                            if (content is TextReasoningContent reasoning)
                                channel.Writer.TryWrite(new("reasoning_delta", Text: reasoning.Text));
                            else if (content is UsageContent usage)
                                channel.Writer.TryWrite(new("usage", Text: usage.Details?.ToString()));
                        }
                }
                channel.Writer.TryWrite(new("turn_completed"));
                channel.Writer.TryWrite(new("agent_run_completed"));
            }
            catch (OperationCanceledException) { channel.Writer.TryWrite(new(accepted ? "turn_interrupted" : "prompt_rejected")); }
            catch (Exception error) { channel.Writer.TryWrite(new(accepted ? "turn_failed" : "prompt_rejected", Error: error.Message)); }
            finally
            {
                channel.Writer.TryWrite(new("agent_settled"));
                channel.Writer.TryComplete();
            }
        }, CancellationToken.None);
        try
        {
            await foreach (var value in channel.Reader.ReadAllAsync()) yield return value;
        }
        finally
        {
            linked.Cancel();
            await pump;
        }
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
                var currentModelEntry = Conversation.Tree.ActivePath().LastOrDefault(node => node.Type == "model_change")?.Id;
                Conversation.Tree.Select(id);
                var selectedModelEntry = Conversation.Tree.ActivePath().LastOrDefault(node => node.Type == "model_change")?.Id;
                if (currentModelEntry != selectedModelEntry)
                    throw new InvalidOperationException("Branch changes the model. Select the matching model before running.");
                if (Conversation.RecoverIncomplete() && _save is not null) await _save(cancellationToken);
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
        [EnumeratorCancellation] CancellationToken cancellationToken = default,
        Action<AgentLifecycleEvent>? onEvent = null)
    {
        await _gate.WaitAsync(cancellationToken);
        var completed = false;
        var partialText = new System.Text.StringBuilder();
        var lastProgress = 0;
        var lastProgressAt = DateTimeOffset.UtcNow;
        var events = new List<string>();
        DurableExecution? durable = _save is null ? null : new DurableExecution(Conversation, _save);
        var started = false;
        try
        {
            if (durable is not null)
            {
                await durable.StartAsync(prompt, cancellationToken);
                started = true;
            }
            onEvent?.Invoke(new("prompt_accepted", Text: prompt));
            await foreach (var update in _agent.RunStreamingDurableAsync(prompt, _execution, cancellationToken, durable, onEvent))
            {
                if (!string.IsNullOrEmpty(update.Text))
                {
                    partialText.Append(update.Text);
                    if (durable is not null && (partialText.Length - lastProgress >= 512 ||
                        DateTimeOffset.UtcNow - lastProgressAt >= TimeSpan.FromSeconds(2)))
                    {
                        await durable.ProgressAsync(partialText.ToString());
                        lastProgress = partialText.Length;
                        lastProgressAt = DateTimeOffset.UtcNow;
                    }
                }
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
                if (started) await durable!.FinishAsync(completed);
            }
            finally { _gate.Release(); }
        }
    }
}
