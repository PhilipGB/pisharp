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
    private readonly ModelPricing? _pricing;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _promptQueueGate = new();
    private readonly Queue<string> _steeringQueue = new();
    private readonly Queue<string> _followUpQueue = new();
    private object? _promptLoopOwner;
    private Action<AgentLifecycleEvent>? _promptQueueEvents;
    private AgentSession _execution;
    private int _historyCount;
    public ConversationSession Conversation { get; }

    private ConversationRun(PiAgent agent, ConversationSession conversation, AgentSession execution, Func<CancellationToken, Task>? save,
        AutoCompactionPolicy? autoCompaction, ModelPricing? pricing)
    {
        _agent = agent;
        _save = save;
        _autoCompaction = autoCompaction;
        _pricing = pricing;
        Conversation = conversation;
        _execution = execution;
        _historyCount = conversation.ContextMessages().Count;
    }

    public static async Task<ConversationRun> OpenAsync(PiAgent agent, ConversationSession conversation,
        CancellationToken cancellationToken = default, Func<CancellationToken, Task>? save = null,
        AutoCompactionPolicy? autoCompaction = null, ModelPricing? pricing = null)
    {
        if (autoCompaction is not null) _ = autoCompaction.TriggerTokens;
        if (conversation.RecoverIncomplete() && save is not null) await save(cancellationToken);
        var execution = await agent.RestoreHistoryAsync(conversation.ContextMessages(), cancellationToken);
        return new ConversationRun(agent, conversation, execution, save, autoCompaction, pricing);
    }

    /// <summary>Queue guidance ahead of follow-up work on the active application run.</summary>
    public bool TrySteer(string prompt) => TryQueue(prompt, _steeringQueue, "steering");

    /// <summary>Queue work that runs after steering input has drained.</summary>
    public bool TryFollowUp(string prompt) => TryQueue(prompt, _followUpQueue, "follow_up");

    /// <summary>Compatibility alias: an additional RPC prompt is follow-up work.</summary>
    public bool TryQueuePrompt(string prompt) => TryFollowUp(prompt);

    private bool TryQueue(string prompt, Queue<string> queue, string kind)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(prompt);
        lock (_promptQueueGate)
        {
            if (_promptLoopOwner is null) return false;
            queue.Enqueue(prompt);
            _promptQueueEvents?.Invoke(new("prompt_queued", Text: prompt, Tool: kind));
            _promptQueueEvents?.Invoke(QueueEvent(SnapshotQueues()));
            return true;
        }
    }

    public PendingPrompts GetPendingPrompts()
    {
        lock (_promptQueueGate) return SnapshotQueues();
    }

    /// <summary>Remove and return input that has not reached the model, for editor restoration.</summary>
    public PendingPrompts ClearPendingPrompts()
    {
        lock (_promptQueueGate)
        {
            var pending = SnapshotQueues();
            _steeringQueue.Clear();
            _followUpQueue.Clear();
            _promptQueueEvents?.Invoke(QueueEvent(PendingPrompts.Empty));
            return pending;
        }
    }

    public int PendingPromptCount
    {
        get { lock (_promptQueueGate) return _steeringQueue.Count + _followUpQueue.Count; }
    }

    private PendingPrompts SnapshotQueues() => new(_steeringQueue.ToArray(), _followUpQueue.ToArray());
    private static AgentLifecycleEvent QueueEvent(PendingPrompts pending) => new("queue_update",
        Text: JsonSerializer.Serialize(new { steering = pending.Steering, followUp = pending.FollowUp }));

    /// <summary>Authoritative ordered lifecycle, including actual model and tool boundaries.
    /// Queued prompts are additional turns in the same run. No completion is emitted when execution
    /// fails. Disposing the stream aborts the current turn and leaves unstarted prompts queued.</summary>
    public IAsyncEnumerable<AgentLifecycleEvent> RunEventsAsync(string prompt,
        CancellationToken cancellationToken = default) => RunEventsAsync(new ChatMessage(ChatRole.User, prompt), prompt, cancellationToken);

    public async IAsyncEnumerable<AgentLifecycleEvent> RunEventsAsync(ChatMessage promptMessage, string promptText,
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
            object? owner = null;
            try
            {
                owner = BeginPromptLoop(Publish);
                string? current = promptText;
                var firstTurn = true;
                while (current is not null)
                {
                    var currentMessage = firstTurn ? promptMessage : new ChatMessage(ChatRole.User, current);
                    firstTurn = false;
                    await foreach (var update in RunStreamingAsync(currentMessage, current, linked.Token, value =>
                    {
                        if (value.Type == "prompt_accepted") accepted = true;
                        Publish(value);
                    }))
                    {
                        if (update.Contents is not null)
                            foreach (var content in update.Contents)
                            {
                                if (content is TextReasoningContent reasoning && !string.IsNullOrEmpty(reasoning.Text))
                                    Publish(new("reasoning_delta", Text: reasoning.Text));
                                else if (content is UsageContent usage)
                                    Publish(UsageEvent(UsageRecord.Create(Conversation.Model, "model", usage.Details, _pricing)));
                            }
                    }
                    Publish(new("turn_completed"));
                    current = TakeQueuedPromptOrClose(owner);
                }
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
                if (owner is not null) EndPromptLoop(owner);
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

    private object BeginPromptLoop(Action<AgentLifecycleEvent> publish)
    {
        lock (_promptQueueGate)
        {
            if (_promptLoopOwner is not null) throw new InvalidOperationException("An agent run is already active.");
            _promptLoopOwner = new object();
            _promptQueueEvents = publish;
            return _promptLoopOwner;
        }
    }

    private IReadOnlyList<ChatMessage> TakeSteeringForProvider()
    {
        lock (_promptQueueGate)
        {
            if (!_steeringQueue.TryDequeue(out var prompt)) return [];
            _promptQueueEvents?.Invoke(QueueEvent(SnapshotQueues()));
            return [new ChatMessage(ChatRole.User, prompt)];
        }
    }

    private string? TakeQueuedPromptOrClose(object owner)
    {
        lock (_promptQueueGate)
        {
            if (!ReferenceEquals(_promptLoopOwner, owner)) return null;
            if (_steeringQueue.TryDequeue(out var steering))
            {
                _promptQueueEvents?.Invoke(QueueEvent(SnapshotQueues()));
                return steering;
            }
            if (_followUpQueue.TryDequeue(out var followUp))
            {
                _promptQueueEvents?.Invoke(QueueEvent(SnapshotQueues()));
                return followUp;
            }
            _promptLoopOwner = null;
            _promptQueueEvents = null;
            return null;
        }
    }

    private void EndPromptLoop(object owner)
    {
        lock (_promptQueueGate)
        {
            if (!ReferenceEquals(_promptLoopOwner, owner)) return;
            _promptLoopOwner = null;
            _promptQueueEvents = null;
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
        var plan = Conversation.PrepareCompaction(_autoCompaction?.KeepRecentTokens);
        if (plan is null) return false;
        var summary = await _agent.SummarizeAsync(plan.MessagesToSummarize, focus, cancellationToken);
        var previousHead = Conversation.Tree.HeadId;
        try
        {
            Conversation.AppendCompaction(plan, summary.Text, _autoCompaction?.KeepRecentTokens);
            if (summary.Usage is not null)
                Conversation.AppendUsage(UsageRecord.Create(Conversation.Model, "compaction", summary.Usage, _pricing));
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

    public IAsyncEnumerable<AgentResponseUpdate> RunStreamingAsync(string prompt,
        CancellationToken cancellationToken = default, Action<AgentLifecycleEvent>? onEvent = null) =>
        RunStreamingAsync(new ChatMessage(ChatRole.User, prompt), prompt, cancellationToken, onEvent);

    public async IAsyncEnumerable<AgentResponseUpdate> RunStreamingAsync(ChatMessage promptMessage, string prompt,
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
            if (_autoCompaction is not null && EstimateNextContext(prompt) > _autoCompaction.TriggerTokens)
            {
                if (await CompactCoreAsync(null, cancellationToken))
                    onEvent?.Invoke(new("context_compacted", Text: "Automatic context summary saved; raw history retained."));
                if (EstimateNextContext(prompt) > _autoCompaction.TriggerTokens)
                    throw new InvalidOperationException("Estimated context still exceeds the configured budget; shorten the prompt or increase the model context window.");
            }
            if (durable is not null)
            {
                await durable.StartAsync(prompt, cancellationToken);
                started = true;
            }
            accepted = true;
            onEvent?.Invoke(new("prompt_accepted", Text: prompt));
            await foreach (var update in _agent.RunStreamingDurableAsync(promptMessage, _execution, cancellationToken, durable,
                onEvent, TakeSteeringForProvider))
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
                        else if (content is UsageContent usage)
                            Conversation.AppendUsage(UsageRecord.Create(Conversation.Model, "model", usage.Details, _pricing));
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

    private int EstimateNextContext(string prompt)
    {
        var measured = Conversation.LatestContextUsageTokens();
        if (measured is null) return AutoCompactionPolicy.Estimate(Conversation.ContextMessages(), prompt);
        var pending = (prompt.Length + 1L) / 2 + 64;
        return (int)Math.Min(int.MaxValue, measured.Value + pending);
    }

    private static AgentLifecycleEvent UsageEvent(UsageRecord usage) => new("usage",
        Text: $"{usage.TotalTokens} tokens" + (usage.Cost is null ? "" : $" · ${usage.Cost:0.######}"),
        InputTokens: usage.InputTokens, OutputTokens: usage.OutputTokens,
        CachedInputTokens: usage.CachedInputTokens, ReasoningTokens: usage.ReasoningTokens,
        TotalTokens: usage.TotalTokens, Cost: usage.Cost, CachedWriteTokens: usage.CachedWriteTokens);
}
