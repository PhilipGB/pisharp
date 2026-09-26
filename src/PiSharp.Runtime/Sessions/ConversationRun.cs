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
    private readonly PromptDeliveryQueue _promptQueue = new();
    private readonly Queue<string> _pendingThinkingChanges = new();
    private readonly Queue<BashExecutionRecord> _pendingBashExecutions = new();
    private object? _promptLoopOwner;
    private Action<AgentLifecycleEvent>? _promptQueueEvents;
    private AgentSession _execution;
    private int _historyCount;
    public ConversationSession Conversation { get; }

    private ConversationRun(PiAgent agent, ConversationSession conversation, AgentSession execution, Func<CancellationToken, Task>? save,
        AutoCompactionPolicy? autoCompaction, ModelPricing? pricing, string? sessionFile, string? provider,
        string? reasoningLevel, AgentRunRetryPolicy retryPolicy, PromptDeliveryMode steeringMode,
        PromptDeliveryMode followUpMode,
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
        _promptQueue.SetSteeringMode(steeringMode);
        _promptQueue.SetFollowUpMode(followUpMode);
        Conversation = conversation;
        _execution = execution;
        _historyCount = conversation.ContextMessages().Count;
    }

    public static async Task<ConversationRun> OpenAsync(PiAgent agent, ConversationSession conversation,
        CancellationToken cancellationToken = default, Func<CancellationToken, Task>? save = null,
        AutoCompactionPolicy? autoCompaction = null, ModelPricing? pricing = null, string? sessionFile = null,
        string? provider = null, string? reasoningLevel = null, AgentRunRetryPolicy? retryPolicy = null,
        Func<TimeSpan, CancellationToken, Task>? retryDelay = null,
        PromptDeliveryMode steeringMode = PromptDeliveryMode.OneAtATime,
        PromptDeliveryMode followUpMode = PromptDeliveryMode.OneAtATime)
    {
        if (autoCompaction is not null) _ = autoCompaction.TriggerTokens;
        if (conversation.RecoverIncomplete() && save is not null) await save(cancellationToken);
        var execution = await agent.RestoreHistoryAsync(conversation.ContextMessages(), cancellationToken);
        return new ConversationRun(agent, conversation, execution, save, autoCompaction, pricing, sessionFile, provider,
            reasoningLevel, retryPolicy ?? AgentRunRetryPolicy.Default, steeringMode, followUpMode, retryDelay);
    }

    /// <summary>Queue guidance ahead of follow-up work on the active application run.</summary>
    public bool TrySteer(string prompt) => TryQueue(prompt, steering: true, "steering");

    /// <summary>Queue work that runs after steering input has drained.</summary>
    public bool TryFollowUp(string prompt) => TryQueue(prompt, steering: false, "follow_up");

    /// <summary>Compatibility alias: an additional RPC prompt is follow-up work.</summary>
    public bool TryQueuePrompt(string prompt) => TryFollowUp(prompt);

    private bool TryQueue(string prompt, bool steering, string kind)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(prompt);
        lock (_promptQueueGate)
        {
            if (_promptLoopOwner is null) return false;
            _promptQueue.Enqueue(prompt, steering);
            _promptQueueEvents?.Invoke(new("prompt_queued", Text: prompt, Tool: kind));
            _promptQueueEvents?.Invoke(QueueEvent(_promptQueue.Snapshot()));
            return true;
        }
    }

    public PendingPrompts GetPendingPrompts()
    {
        lock (_promptQueueGate) return _promptQueue.Snapshot();
    }

    /// <summary>Remove and return input that has not reached the model, for editor restoration.</summary>
    public PendingPrompts ClearPendingPrompts()
    {
        lock (_promptQueueGate)
        {
            var pending = _promptQueue.Clear();
            _promptQueueEvents?.Invoke(QueueEvent(PendingPrompts.Empty));
            return pending;
        }
    }

    public int PendingPromptCount
    {
        get { lock (_promptQueueGate) return _promptQueue.Count; }
    }

    public PromptDeliveryMode SteeringMode
    {
        get { lock (_promptQueueGate) return _promptQueue.SteeringMode; }
    }

    public PromptDeliveryMode FollowUpMode
    {
        get { lock (_promptQueueGate) return _promptQueue.FollowUpMode; }
    }

    public void SetSteeringMode(PromptDeliveryMode mode)
    {
        lock (_promptQueueGate) _promptQueue.SetSteeringMode(mode);
    }

    public void SetFollowUpMode(PromptDeliveryMode mode)
    {
        lock (_promptQueueGate) _promptQueue.SetFollowUpMode(mode);
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

    private sealed record PromptBatch(IReadOnlyList<ChatMessage> Messages, IReadOnlyList<string> Texts)
    {
        public string Text => string.Join("\n", Texts);
    }

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
                PromptBatch? current = new([promptMessage], [promptText]);
                var stopRun = false;
                while (current is not null)
                {
                    var turn = current;
                    activeTurnOutcomeSent = false;
                    var currentMessage = turn.Messages[0];
                    var outcome = await _retryController.RunTurnAsync(
                        (continuation, observeAttempt) => continuation
                            ? RunStreamingContinuationAsync(turn.Text, linked.Token, observeAttempt)
                            : turn.Messages.Count > 1
                                ? RunStreamingBatchAsync(turn.Messages, turn.Text, linked.Token, observeAttempt)
                                : RunStreamingAsync(currentMessage, turn.Texts[0], linked.Token, observeAttempt),
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
                        (head, pathLength, token) => OmitFailedRetryContextAsync(head, pathLength, Publish, token),
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
            var prompts = _promptQueue.TakeSteeringForProvider();
            if (prompts.Count == 0) return [];
            _promptQueueEvents?.Invoke(QueueEvent(_promptQueue.Snapshot()));
            var messages = new ChatMessage[prompts.Count];
            for (var index = 0; index < prompts.Count; index++)
            {
                var message = new ChatMessage(ChatRole.User, prompts[index]);
                messages[index] = message;
                _promptQueueEvents?.Invoke(new AgentLifecycleEvent("steering_message_accepted", Text: prompts[index])
                {
                    PromptMessage = message,
                    MessageTimestamp = DateTimeOffset.UtcNow
                });
            }
            return messages;
        }
    }

    private PromptBatch? TakeQueuedPromptOrClose(object owner)
    {
        lock (_promptQueueGate)
        {
            if (!ReferenceEquals(_promptLoopOwner, owner)) return null;
            PersistPendingThinkingChangesUnsafe();
            if (_promptQueue.TakeNextBatch() is { } batch)
            {
                _promptQueueEvents?.Invoke(QueueEvent(_promptQueue.Snapshot()));
                return new PromptBatch(batch.Messages.Select(prompt => new ChatMessage(ChatRole.User, prompt)).ToArray(),
                    batch.Messages);
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

    private IAsyncEnumerable<AgentResponseUpdate> RunStreamingBatchAsync(IReadOnlyList<ChatMessage> promptMessages,
        string prompt, CancellationToken cancellationToken, Action<AgentLifecycleEvent>? onEvent)
    {
        if (promptMessages.Count < 2) throw new ArgumentException("A prompt batch requires multiple messages.", nameof(promptMessages));
        return RunStreamingAttemptAsync(null, prompt, false, cancellationToken, onEvent, promptMessages);
    }

    private IAsyncEnumerable<AgentResponseUpdate> RunStreamingContinuationAsync(string prompt,
        CancellationToken cancellationToken, Action<AgentLifecycleEvent>? onEvent) =>
        RunStreamingAttemptAsync(null, prompt, true, cancellationToken, onEvent);

    private async IAsyncEnumerable<AgentResponseUpdate> RunStreamingAttemptAsync(ChatMessage? promptMessage, string prompt,
        bool retryContinuation,
        [EnumeratorCancellation] CancellationToken cancellationToken = default,
        Action<AgentLifecycleEvent>? onEvent = null,
        IReadOnlyList<ChatMessage>? acceptedPromptMessages = null)
    {
        await _gate.WaitAsync(cancellationToken);
        var completed = false;
        var partialText = new System.Text.StringBuilder();
        var lastProgress = 0;
        var lastProgressAt = DateTimeOffset.UtcNow;
        var events = new List<string>();
        var partialAssistantText = new System.Text.StringBuilder();
        var providerTurnHistory = new ProviderTurnHistoryReconciler();
        DurableExecution? durable = _save is null ? null : new DurableExecution(Conversation, _save);
        var started = false;
        var accepted = false;
        ChatMessage? providerResponse = null;
        var turnToolResults = new List<ChatMessage>();
        void CompleteProviderTurn()
        {
            if (providerResponse is null) return;
            var toolResults = turnToolResults.ToArray();
            providerTurnHistory.RecordCompletedTurn(providerResponse, toolResults);
            onEvent?.Invoke(new AgentLifecycleEvent("assistant_turn_completed")
            {
                TurnMessage = providerResponse,
                TurnToolResults = toolResults
            });
            providerResponse = null;
            turnToolResults.Clear();
        }

        void Observe(AgentLifecycleEvent item)
        {
            if (item.Type == "model_request_started")
            {
                CompleteProviderTurn();
                partialAssistantText.Clear();
                onEvent?.Invoke(new("assistant_turn_started"));
            }
            else if (item.Type == "model_request_completed")
            {
                providerResponse = item.ProviderResponse;
                item = item with
                {
                    ProviderThinkingLevel = Volatile.Read(ref _reasoningLevel),
                    UsageSnapshot = item.ProviderUsage is { } usage
                        ? UsageRecord.Create(Conversation.Model, "model", usage, _pricing)
                        : null
                };
            }
            else if (item.ProviderUpdate?.Contents?.OfType<UsageContent>().LastOrDefault() is { } updateUsage)
                item = item with { UsageSnapshot = UsageRecord.Create(Conversation.Model, "model", updateUsage.Details, _pricing) };
            else if (item.Type == "tool_execution_finished" && item.ToolResultMessage is { } result)
                turnToolResults.Add(result);
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
            var inputMessages = promptMessage is not null ? [promptMessage] : acceptedPromptMessages ?? [];
            if (inputMessages.Count > 0)
            {
                var previousHead = Conversation.Tree.HeadId;
                foreach (var inputMessage in inputMessages) Conversation.Append(inputMessage);
                try
                {
                    if (promptMessage is null)
                    {
                        var history = Conversation.ContextMessages();
                        _execution = await _agent.RestoreHistoryAsync(history, cancellationToken);
                        _historyCount = history.Count;
                    }
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
            if (inputMessages.Count == 0)
                onEvent?.Invoke(new("agent_attempt_started")
                {
                    RunStartHead = attemptStartHead,
                    RunStartPathLength = attemptStartPathLength
                });
            else
            {
                foreach (var inputMessage in inputMessages)
                {
                    var promptImages = inputMessage.Contents?.OfType<DataContent>().ToArray();
                    var messageText = promptMessage is not null ? prompt : inputMessage.Text ?? string.Empty;
                    onEvent?.Invoke(new("prompt_accepted", Text: messageText)
                    {
                        Images = promptImages is { Length: > 0 } ? promptImages : null,
                        PromptMessage = inputMessage,
                        MessageTimestamp = DateTimeOffset.UtcNow,
                        RunStartHead = attemptStartHead,
                        RunStartPathLength = attemptStartPathLength
                    });
                }
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
            CompleteProviderTurn();
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
                var historyStartIndex = _historyCount + (promptInHistory ? 1 : 0);
                var appendedHistory = history.Skip(historyStartIndex).ToList();
                if (!completed)
                    providerTurnHistory.RestoreMissingMessages(appendedHistory);
                foreach (var message in appendedHistory)
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
        Action<AgentLifecycleEvent> publish, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var path = Conversation.Tree.ActivePath();
        var start = attemptStartHead is null ? -1 : path.ToList().FindIndex(node => node.Id == attemptStartHead);
        var firstAttemptEntry = start >= 0 ? start + 1 : Math.Clamp(attemptStartPathLength, 0, path.Count);
        var attemptEntries = path.Skip(firstAttemptEntry).Where(node => node.Type == "chat").ToArray();
        var failedAssistant = Array.FindLastIndex(attemptEntries, node =>
            ConversationSession.RestoreEntry(node).Role == ChatRole.Assistant);
        var changed = false;
        var appended = new List<ConversationNode>();
        if (failedAssistant >= 0)
        {
            var omitted = new List<string> { attemptEntries[failedAssistant].Id };
            for (var index = failedAssistant + 1; index < attemptEntries.Length; index++)
            {
                var message = ConversationSession.RestoreEntry(attemptEntries[index]);
                if (message.Role != ChatRole.Tool) break;
                omitted.Add(attemptEntries[index].Id);
            }
            foreach (var entryId in omitted) appended.Add(Conversation.AppendContextOmission(entryId));
            changed = true;
        }
        if (changed && _save is not null) await _save(CancellationToken.None);
        foreach (var entry in appended)
            publish(new AgentLifecycleEvent("entry_appended")
            {
                AppendedEntry = JsonSerializer.SerializeToElement(
                    PiJsonlSessionInterchange.ProjectEntry(Conversation, entry))
            });
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
