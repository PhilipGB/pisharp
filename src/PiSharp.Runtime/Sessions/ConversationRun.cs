using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using PiSharp.Core;
using PiSharp.Runtime.Tools;

namespace PiSharp.Runtime.Sessions;

/// <summary>One MAF execution session projecting the canonical selected conversation branch.</summary>
public sealed class ConversationRun
{
    private readonly PiAgent _agent;
    private readonly Func<CancellationToken, Task>? _save;
    private readonly AutoCompactionPolicy? _autoCompaction;
    private readonly ModelPricing? _pricing;
    private readonly string? _sessionFile;
    private readonly string? _provider;
    private readonly AgentRunRetryController _retryController;
    private string? _reasoningLevel;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _promptQueueGate = new();
    private readonly Queue<string> _steeringQueue = new();
    private readonly Queue<string> _followUpQueue = new();
    private readonly Queue<string> _pendingThinkingChanges = new();
    private readonly Queue<BashExecutionRecord> _pendingBashExecutions = new();
    private object? _promptLoopOwner;
    private Action<AgentLifecycleEvent>? _promptQueueEvents;
    private AgentSession _execution;
    private int _historyCount;
    public ConversationSession Conversation { get; }

    private ConversationRun(PiAgent agent, ConversationSession conversation, AgentSession execution, Func<CancellationToken, Task>? save,
        AutoCompactionPolicy? autoCompaction, ModelPricing? pricing, string? sessionFile, string? provider,
        string? reasoningLevel, AgentRunRetryPolicy retryPolicy,
        Func<TimeSpan, CancellationToken, Task>? retryDelay)
    {
        _agent = agent;
        _save = save;
        _autoCompaction = autoCompaction;
        _pricing = pricing;
        _sessionFile = sessionFile is null ? null : Path.GetFullPath(sessionFile);
        _provider = provider;
        _retryController = new AgentRunRetryController(retryPolicy, retryDelay);
        _reasoningLevel = reasoningLevel;
        Conversation = conversation;
        _execution = execution;
        _historyCount = conversation.ContextMessages().Count;
    }

    public static async Task<ConversationRun> OpenAsync(PiAgent agent, ConversationSession conversation,
        CancellationToken cancellationToken = default, Func<CancellationToken, Task>? save = null,
        AutoCompactionPolicy? autoCompaction = null, ModelPricing? pricing = null, string? sessionFile = null,
        string? provider = null, string? reasoningLevel = null, AgentRunRetryPolicy? retryPolicy = null,
        Func<TimeSpan, CancellationToken, Task>? retryDelay = null)
    {
        if (autoCompaction is not null) _ = autoCompaction.TriggerTokens;
        if (conversation.RecoverIncomplete() && save is not null) await save(cancellationToken);
        var execution = await agent.RestoreHistoryAsync(conversation.ContextMessages(), cancellationToken);
        return new ConversationRun(agent, conversation, execution, save, autoCompaction, pricing, sessionFile, provider,
            reasoningLevel, retryPolicy ?? AgentRunRetryPolicy.Default, retryDelay);
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

    public bool SetThinkingLevelDuringRun(string level, ReasoningOptions? reasoning)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(level);
        lock (_promptQueueGate)
        {
            if (_promptLoopOwner is null) return false;
            _agent.SetReasoningOptions(reasoning);
            Volatile.Write(ref _reasoningLevel, level);
            _pendingThinkingChanges.Enqueue(level);
            return true;
        }
    }

    public Task<BashExecutionResult> ExecuteBashAsync(string command, Action<string>? onUpdate = null,
        CancellationToken cancellationToken = default) => _agent.ExecuteBashAsync(command, onUpdate, cancellationToken);

    public void AbortBash() => _agent.AbortBash();

    public bool IsRetrying => _retryController.IsRetrying;

    public void SetAutoRetryEnabled(bool enabled) => _retryController.SetEnabled(enabled);

    public void AbortRetry() => _retryController.AbortRetry();

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
            var activeTurnOutcomeSent = false;
            object? owner = null;
            try
            {
                owner = BeginPromptLoop(Publish);
                string? current = promptText;
                var firstTurn = true;
                var stopRun = false;
                while (current is not null)
                {
                    activeTurnOutcomeSent = false;
                    var currentMessage = firstTurn ? promptMessage : new ChatMessage(ChatRole.User, current);
                    firstTurn = false;
                    var outcome = await _retryController.RunTurnAsync(
                        (continuation, observeAttempt) => continuation
                            ? RunStreamingContinuationAsync(current, linked.Token, observeAttempt)
                            : RunStreamingAsync(currentMessage, current, linked.Token, observeAttempt),
                        item =>
                        {
                            if (item.Type == "prompt_accepted") accepted = true;
                            if (item.Type is "prompt_accepted" or "agent_attempt_started") activeTurnOutcomeSent = false;
                            Publish(item);
                            if (item.Type is "turn_completed" or "turn_failed" or "turn_interrupted" or "prompt_rejected")
                                activeTurnOutcomeSent = true;
                        },
                        update =>
                        {
                            if (update.Contents is null) return;
                            foreach (var content in update.Contents)
                            {
                                if (content is TextReasoningContent reasoning && !string.IsNullOrEmpty(reasoning.Text))
                                    Publish(new("reasoning_delta", Text: reasoning.Text));
                                else if (content is UsageContent usage)
                                    Publish(UsageEvent(UsageRecord.Create(Conversation.Model, "model", usage.Details, _pricing)));
                            }
                        },
                        OmitFailedRetryContextAsync,
                        async token =>
                        {
                            var history = Conversation.ContextMessages();
                            _execution = await _agent.RestoreHistoryAsync(history, token);
                            _historyCount = history.Count;
                        },
                        FlushPendingBashExecutions,
                        () => Conversation.Tree.HeadId,
                        linked.Token);
                    activeTurnOutcomeSent = true;
                    if (outcome != AgentTurnOutcome.Completed)
                    {
                        stopRun = true;
                        break;
                    }
                    current = TakeQueuedPromptOrClose(owner);
                }
                if (!stopRun) Publish(new("agent_run_completed"));
            }
            catch (OperationCanceledException)
            {
                if (!consumerClosed.IsCancellationRequested && !activeTurnOutcomeSent)
                    try
                    {
                        await channel.Writer.WriteAsync(new(accepted ? "turn_interrupted" : "prompt_rejected")
                        { TurnEndHead = Conversation.Tree.HeadId }, consumerClosed.Token);
                    }
                    catch (OperationCanceledException) { }
            }
            catch (Exception error)
            {
                if (!consumerClosed.IsCancellationRequested && !activeTurnOutcomeSent)
                    try
                    {
                        await channel.Writer.WriteAsync(new(accepted ? "turn_failed" : "prompt_rejected", Error: error.Message)
                        { TurnEndHead = Conversation.Tree.HeadId }, consumerClosed.Token);
                    }
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
            PersistPendingThinkingChangesUnsafe();
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
            PersistPendingThinkingChangesUnsafe();
            FlushPendingBashExecutionsUnsafe();
            _promptLoopOwner = null;
            _promptQueueEvents = null;
        }
    }

    private void PersistPendingThinkingChangesUnsafe()
    {
        while (_pendingThinkingChanges.TryDequeue(out var level))
        {
            // RunStreamingAsync has finished before the branch is changed, so its canonical history append cannot race this entry.
            Conversation.AppendThinkingLevelChange(level);
        }
    }

    public bool RecordBashResult(string command, BashExecutionResult result, bool excludeFromContext = false)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(result);
        var execution = new BashExecutionRecord(command, result.Output, result.ExitCode, result.Cancelled,
            result.Truncated, result.FullOutputPath, excludeFromContext);
        lock (_promptQueueGate)
        {
            if (_promptLoopOwner is not null)
            {
                _pendingBashExecutions.Enqueue(execution);
                return false;
            }
            AppendBashExecution(execution);
            return true;
        }
    }

    private void FlushPendingBashExecutions()
    {
        lock (_promptQueueGate) FlushPendingBashExecutionsUnsafe();
    }

    private void FlushPendingBashExecutionsUnsafe()
    {
        while (_pendingBashExecutions.TryDequeue(out var execution)) AppendBashExecution(execution);
    }

    private void AppendBashExecution(BashExecutionRecord execution)
    {
        Conversation.AppendBashExecution(execution);
        if (execution.ExcludeFromContext) return;
        var message = ConversationSession.BashExecutionContextMessage(execution);
        _agent.AppendToHistory(_execution, message);
        _historyCount++;
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

    public IAsyncEnumerable<AgentResponseUpdate> RunStreamingAsync(ChatMessage promptMessage, string prompt,
        CancellationToken cancellationToken = default, Action<AgentLifecycleEvent>? onEvent = null) =>
        RunStreamingAttemptAsync(promptMessage, prompt, false, cancellationToken, onEvent);

    private IAsyncEnumerable<AgentResponseUpdate> RunStreamingContinuationAsync(string prompt,
        CancellationToken cancellationToken, Action<AgentLifecycleEvent>? onEvent) =>
        RunStreamingAttemptAsync(null, prompt, true, cancellationToken, onEvent);

    private async IAsyncEnumerable<AgentResponseUpdate> RunStreamingAttemptAsync(ChatMessage? promptMessage, string prompt,
        bool retryContinuation,
        [EnumeratorCancellation] CancellationToken cancellationToken = default,
        Action<AgentLifecycleEvent>? onEvent = null)
    {
        await _gate.WaitAsync(cancellationToken);
        var completed = false;
        var partialText = new System.Text.StringBuilder();
        var lastProgress = 0;
        var lastProgressAt = DateTimeOffset.UtcNow;
        var events = new List<string>();
        var partialAssistantText = new System.Text.StringBuilder();
        DurableExecution? durable = _save is null ? null : new DurableExecution(Conversation, _save);
        var started = false;
        var accepted = false;
        void Observe(AgentLifecycleEvent item)
        {
            if (item.Type == "model_request_started") partialAssistantText.Clear();
            else if (item.Type == "model_text_delta" && item.Text is not null) partialAssistantText.Append(item.Text);
            onEvent?.Invoke(item);
        }
        try
        {
            // A failed settled run can have checkpointed side effects without MAF-persisted tool results.
            // Rebuild from canonical history with an explicit no-replay warning before accepting another prompt.
            if (!retryContinuation && Conversation.RecoverIncomplete())
            {
                var history = Conversation.ContextMessages();
                var restored = await _agent.RestoreHistoryAsync(history, cancellationToken);
                if (_save is not null) await _save(cancellationToken);
                _execution = restored;
                _historyCount = history.Count;
            }
            var estimatedPrompt = retryContinuation ? "" : prompt;
            if (_autoCompaction is not null && EstimateNextContext(estimatedPrompt) > _autoCompaction.TriggerTokens)
            {
                if (await CompactCoreAsync(null, cancellationToken))
                    onEvent?.Invoke(new("context_compacted", Text: "Automatic context summary saved; raw history retained."));
                if (EstimateNextContext(estimatedPrompt) > _autoCompaction.TriggerTokens)
                    throw new InvalidOperationException("Estimated context still exceeds the configured budget; shorten the prompt or increase the model context window.");
            }
            if (durable is not null)
            {
                await durable.StartAsync(prompt, cancellationToken);
                started = true;
            }
            var attemptStartHead = Conversation.Tree.HeadId;
            var attemptStartPathLength = Conversation.Tree.ActivePath().Count;
            if (promptMessage is not null)
            {
                var previousHead = Conversation.Tree.HeadId;
                Conversation.Append(promptMessage);
                try
                {
                    if (_save is not null) await _save(CancellationToken.None);
                }
                catch
                {
                    Conversation.Tree.Select(previousHead);
                    var history = Conversation.ContextMessages();
                    _execution = await _agent.RestoreHistoryAsync(history, CancellationToken.None);
                    _historyCount = history.Count;
                    throw;
                }
            }
            accepted = true;
            if (promptMessage is null)
                onEvent?.Invoke(new("agent_attempt_started")
                {
                    RunStartHead = attemptStartHead,
                    RunStartPathLength = attemptStartPathLength
                });
            else
            {
                var promptImages = promptMessage.Contents?.OfType<DataContent>().ToArray();
                onEvent?.Invoke(new("prompt_accepted", Text: prompt)
                {
                    Images = promptImages is { Length: > 0 } ? promptImages : null,
                    RunStartHead = attemptStartHead,
                    RunStartPathLength = attemptStartPathLength
                });
            }
            var inFlightBudget = _autoCompaction is null ? null : new InFlightContextBudget(_autoCompaction,
                (messages, token) => _agent.SummarizeAsync(messages, null, token),
                async (summary, token) =>
                {
                    Conversation.MarkInFlightProjection();
                    if (summary.Usage is not null)
                        Conversation.AppendUsage(UsageRecord.Create(Conversation.Model, "compaction", summary.Usage, _pricing));
                    if (_save is not null) await _save(token);
                    onEvent?.Invoke(new("context_compacted_in_flight", Text: "Continuation request summarized; canonical history was not changed."));
                });
            var updates = promptMessage is null
                ? _agent.RunStreamingContinuationDurableAsync(_execution, cancellationToken, durable,
                    Observe, TakeSteeringForProvider, inFlightBudget is null ? null : inFlightBudget.ProjectAsync,
                    CreateBashSessionEnvironment())
                : _agent.RunStreamingDurableAsync(promptMessage, _execution, cancellationToken, durable,
                    Observe, TakeSteeringForProvider, inFlightBudget is null ? null : inFlightBudget.ProjectAsync,
                    CreateBashSessionEnvironment());
            await foreach (var update in updates)
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
                if (existing.Count < _historyCount || Enumerable.Range(0, _historyCount).Any(index =>
                    !JsonElement.DeepEquals(JsonSerializer.SerializeToElement(existing[index], AIJsonUtilities.DefaultOptions),
                        JsonSerializer.SerializeToElement(history[index], AIJsonUtilities.DefaultOptions))))
                    throw new InvalidDataException("MAF changed existing canonical conversation history.");
                var promptInConversation = promptMessage is not null && existing.Count > _historyCount &&
                    JsonElement.DeepEquals(JsonSerializer.SerializeToElement(existing[_historyCount], AIJsonUtilities.DefaultOptions),
                        JsonSerializer.SerializeToElement(promptMessage, AIJsonUtilities.DefaultOptions));
                var promptInHistory = promptInConversation && history.Count > _historyCount &&
                    JsonElement.DeepEquals(JsonSerializer.SerializeToElement(history[_historyCount], AIJsonUtilities.DefaultOptions),
                        JsonSerializer.SerializeToElement(promptMessage, AIJsonUtilities.DefaultOptions));
                foreach (var message in history.Skip(_historyCount + (promptInHistory ? 1 : 0)))
                    Conversation.Append(message);
                var canonicalHistory = Conversation.ContextMessages();
                if (canonicalHistory.Count != history.Count || canonicalHistory.Where((message, index) => !JsonElement.DeepEquals(
                    JsonSerializer.SerializeToElement(message, AIJsonUtilities.DefaultOptions),
                    JsonSerializer.SerializeToElement(history[index], AIJsonUtilities.DefaultOptions))).Any())
                    _execution = await _agent.RestoreHistoryAsync(canonicalHistory, CancellationToken.None);
                _historyCount = canonicalHistory.Count;
                if (!completed && accepted) Conversation.Tree.Append("interrupted", JsonSerializer.SerializeToElement(
                    new { prompt, partialText = partialText.ToString(), partialAssistantText = partialAssistantText.ToString(), events, timestamp = DateTimeOffset.UtcNow }));
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

    private async Task OmitFailedRetryContextAsync(string? attemptStartHead, int attemptStartPathLength,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var path = Conversation.Tree.ActivePath();
        var start = attemptStartHead is null ? -1 : path.ToList().FindIndex(node => node.Id == attemptStartHead);
        var firstAttemptEntry = start >= 0 ? start + 1 : Math.Clamp(attemptStartPathLength, 0, path.Count);
        var attemptEntries = path.Skip(firstAttemptEntry).Where(node => node.Type == "chat").ToArray();
        var failedAssistant = Array.FindLastIndex(attemptEntries, node =>
            ConversationSession.RestoreEntry(node).Role == ChatRole.Assistant);
        var changed = false;
        if (failedAssistant >= 0)
        {
            var omitted = new List<string> { attemptEntries[failedAssistant].Id };
            for (var index = failedAssistant + 1; index < attemptEntries.Length; index++)
            {
                var message = ConversationSession.RestoreEntry(attemptEntries[index]);
                if (message.Role != ChatRole.Tool) break;
                omitted.Add(attemptEntries[index].Id);
            }
            foreach (var entryId in omitted) Conversation.AppendContextOmission(entryId);
            changed = true;
        }
        if (changed && _save is not null) await _save(CancellationToken.None);
    }

    private IReadOnlyDictionary<string, string?> CreateBashSessionEnvironment()
    {
        var environment = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["PI_SESSION_ID"] = Conversation.Id,
            ["PI_MODEL"] = Conversation.Model
        };
        var provider = _provider ?? Conversation.Provider;
        if (provider is not null) environment["PI_PROVIDER"] = provider;
        if (_sessionFile is not null) environment["PI_SESSION_FILE"] = _sessionFile;
        var reasoningLevel = Volatile.Read(ref _reasoningLevel);
        if (reasoningLevel is not null) environment["PI_REASONING_LEVEL"] = reasoningLevel;
        return environment;
    }

    private static AgentLifecycleEvent UsageEvent(UsageRecord usage) => new("usage",
        Text: $"{usage.TotalTokens} tokens" + (usage.Cost is null ? "" : $" · ${usage.Cost:0.######}"),
        InputTokens: usage.InputTokens, OutputTokens: usage.OutputTokens,
        CachedInputTokens: usage.CachedInputTokens, ReasoningTokens: usage.ReasoningTokens,
        TotalTokens: usage.TotalTokens, Cost: usage.Cost, CachedWriteTokens: usage.CachedWriteTokens);
}
