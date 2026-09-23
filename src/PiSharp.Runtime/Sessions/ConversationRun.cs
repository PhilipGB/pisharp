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
    private readonly AutoCompactionPolicy? _autoCompaction;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private AgentSession _execution;
    private int _historyCount;
    public ConversationSession Conversation { get; }

    private ConversationRun(PiAgent agent, ConversationSession conversation, AgentSession execution, Func<CancellationToken, Task>? save,
        AutoCompactionPolicy? autoCompaction)
    {
        _agent = agent;
        _save = save;
        _autoCompaction = autoCompaction;
        Conversation = conversation;
        _execution = execution;
        _historyCount = conversation.ContextMessages().Count;
    }

    public static async Task<ConversationRun> OpenAsync(PiAgent agent, ConversationSession conversation,
        CancellationToken cancellationToken = default, Func<CancellationToken, Task>? save = null,
        AutoCompactionPolicy? autoCompaction = null)
    {
        if (autoCompaction is not null) _ = autoCompaction.TriggerTokens;
        if (conversation.RecoverIncomplete() && save is not null) await save(cancellationToken);
        var execution = await agent.RestoreHistoryAsync(conversation.ContextMessages(), cancellationToken);
        return new ConversationRun(agent, conversation, execution, save, autoCompaction);
    }

    /// <summary>Authoritative ordered lifecycle, including actual model and tool boundaries.
    /// No completion is emitted when execution fails. Disposing the stream aborts the run.</summary>
    public async IAsyncEnumerable<AgentLifecycleEvent> RunEventsAsync(string prompt,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var channel = Channel.CreateBounded<AgentLifecycleEvent>(new BoundedChannelOptions(256)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.Wait
        });
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        using var consumerClosed = new CancellationTokenSource();
        void Publish(AgentLifecycleEvent value) => channel.Writer.WriteAsync(value, linked.Token).AsTask().GetAwaiter().GetResult();
        var pump = Task.Run(async () =>
        {
            var accepted = false;
            try
            {
                await foreach (var update in RunStreamingAsync(prompt, linked.Token, value =>
                {
                    if (value.Type == "prompt_accepted") accepted = true;
                    Publish(value);
                }))
                {
                    if (update.Contents is not null)
                        foreach (var content in update.Contents)
                        {
                            if (content is TextReasoningContent reasoning)
                                Publish(new("reasoning_delta", Text: reasoning.Text));
                            else if (content is UsageContent usage)
                                Publish(new("usage", Text: usage.Details?.ToString()));
                        }
                }
                Publish(new("turn_completed"));
                Publish(new("agent_run_completed"));
            }
            catch (OperationCanceledException)
            {
                if (!consumerClosed.IsCancellationRequested)
                    try { await channel.Writer.WriteAsync(new(accepted ? "turn_interrupted" : "prompt_rejected"), consumerClosed.Token); }
                    catch (OperationCanceledException) { }
            }
            catch (Exception error)
            {
                if (!consumerClosed.IsCancellationRequested)
                    try { await channel.Writer.WriteAsync(new(accepted ? "turn_failed" : "prompt_rejected", Error: error.Message), consumerClosed.Token); }
                    catch (OperationCanceledException) { }
            }
            finally
            {
                try { await channel.Writer.WriteAsync(new("agent_settled"), consumerClosed.Token); }
                catch (OperationCanceledException) { }
                channel.Writer.TryComplete();
            }
        }, CancellationToken.None);
        try
        {
            await foreach (var value in channel.Reader.ReadAllAsync()) yield return value;
        }
        finally
        {
            consumerClosed.Cancel();
            linked.Cancel();
            await pump;
        }
    }

    /// <summary>Manually summarize completed earlier turns; no raw session messages are removed.</summary>
    public async Task<bool> CompactAsync(string? focus = null, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try { return await CompactCoreAsync(focus, cancellationToken); }
        finally { _gate.Release(); }
    }

    // Called only while the run gate is held. A failed summary or save cannot switch active context.
    private async Task<bool> CompactCoreAsync(string? focus, CancellationToken cancellationToken)
    {
        var plan = Conversation.PrepareCompaction();
        if (plan is null) return false;
        var summary = await _agent.SummarizeAsync(plan.MessagesToSummarize, focus, cancellationToken);
        var previousHead = Conversation.Tree.HeadId;
        try
        {
            Conversation.AppendCompaction(plan, summary);
            var messages = Conversation.ContextMessages();
            var restored = await _agent.RestoreHistoryAsync(messages, cancellationToken);
            if (_save is not null) await _save(cancellationToken);
            _execution = restored;
            _historyCount = messages.Count;
            return true;
        }
        catch { Conversation.Tree.Select(previousHead); throw; }
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
                var history = Conversation.ContextMessages();
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
        var accepted = false;
        try
        {
            if (_autoCompaction is not null && AutoCompactionPolicy.Estimate(Conversation.ContextMessages(), prompt) > _autoCompaction.TriggerTokens)
            {
                if (await CompactCoreAsync(null, cancellationToken))
                    onEvent?.Invoke(new("context_compacted", Text: "Automatic context summary saved; raw history retained."));
                if (AutoCompactionPolicy.Estimate(Conversation.ContextMessages(), prompt) > _autoCompaction.TriggerTokens)
                    throw new InvalidOperationException("Estimated context still exceeds the configured budget; shorten the prompt or increase the model context window.");
            }
            if (durable is not null)
            {
                await durable.StartAsync(prompt, cancellationToken);
                started = true;
            }
            accepted = true;
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
                var existing = Conversation.ContextMessages();
                if (existing.Count != _historyCount || existing.Where((message, index) => !JsonElement.DeepEquals(
                    JsonSerializer.SerializeToElement(message, AIJsonUtilities.DefaultOptions),
                    JsonSerializer.SerializeToElement(history[index], AIJsonUtilities.DefaultOptions))).Any())
                    throw new InvalidDataException("MAF changed existing canonical conversation history.");
                foreach (var message in history.Skip(_historyCount)) Conversation.Append(message);
                _historyCount = history.Count;
                if (!completed && accepted) Conversation.Tree.Append("interrupted", JsonSerializer.SerializeToElement(
                    new { prompt, partialText = partialText.ToString(), events, timestamp = DateTimeOffset.UtcNow }));
                if (started) await durable!.FinishAsync(completed);
            }
            finally { _gate.Release(); }
        }
    }
}
