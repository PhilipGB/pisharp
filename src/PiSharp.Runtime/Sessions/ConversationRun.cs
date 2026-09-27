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
    private AutoCompactionPolicy? _autoCompaction;
    private int _autoCompactionEnabled;
    private ModelPricing? _pricing;
    private readonly string? _sessionFile;
    private string? _provider;
    private readonly AgentRunRetryController _retryController;
    private string? _reasoningLevel;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _runtimeStateGate = new();
    private readonly PromptDeliveryController _promptDelivery;
    private readonly Queue<PendingRuntimeChange> _pendingRuntimeChanges = new();
    private readonly Queue<BashExecutionRecord> _pendingBashExecutions = new();
    private readonly ConversationCompactionCoordinator _compactionCoordinator;
    private AgentSession _execution;
    private int _historyCount;
    private string _currentModel;
    private string? _currentProvider;
    private string _providerRequestModel;
    private ModelPricing? _providerRequestPricing;
    private string? _persistedThinkingLevel;
    public ConversationSession Conversation { get; }
    public string? SessionFile => _sessionFile;
    public Task PersistAsync(CancellationToken cancellationToken = default) =>
        _save?.Invoke(cancellationToken) ?? Task.CompletedTask;
    public bool AutoCompactionEnabled => Volatile.Read(ref _autoCompactionEnabled) != 0;
    public bool IsCompacting => _compactionCoordinator.IsCompacting;
    public string CurrentModel { get { lock (_runtimeStateGate) return _currentModel; } }
    public string? CurrentProvider { get { lock (_runtimeStateGate) return _currentProvider; } }
    public UsageRecord CurrentProviderUsage(UsageDetails usage)
    {
        lock (_runtimeStateGate) return UsageRecord.Create(_providerRequestModel, "model", usage, _providerRequestPricing);
    }

    private ConversationRun(PiAgent agent, ConversationSession conversation, AgentSession execution, Func<CancellationToken, Task>? save,
        AutoCompactionPolicy? autoCompaction, ModelPricing? pricing, string? sessionFile, string? provider,
        string? reasoningLevel, AgentRunRetryPolicy retryPolicy, PromptDeliveryMode steeringMode,
        PromptDeliveryMode followUpMode,
        bool autoCompactionEnabled,
        Func<TimeSpan, CancellationToken, Task>? retryDelay)
    {
        _agent = agent;
        _save = save;
        _autoCompaction = autoCompaction;
        _autoCompactionEnabled = autoCompactionEnabled ? 1 : 0;
        _pricing = pricing;
        _sessionFile = sessionFile is null ? null : Path.GetFullPath(sessionFile);
        _provider = provider;
        _retryController = new AgentRunRetryController(retryPolicy, retryDelay);
        _reasoningLevel = reasoningLevel;
        _promptDelivery = new PromptDeliveryController(_runtimeStateGate, steeringMode, followUpMode);
        Conversation = conversation;
        _execution = execution;
        _historyCount = conversation.ContextMessages().Count;
        _currentModel = conversation.Model;
        _currentProvider = conversation.Provider;
        _providerRequestModel = conversation.Model;
        _providerRequestPricing = pricing;
        _persistedThinkingLevel = reasoningLevel;
        _compactionCoordinator = new ConversationCompactionCoordinator(Conversation, _agent,
            () => _autoCompaction, () => _pricing, _save,
            (messages, token) => _agent.RestoreHistoryAsync(messages, token),
            (execution, count) => { _execution = execution; _historyCount = count; });
    }

    public static async Task<ConversationRun> OpenAsync(PiAgent agent, ConversationSession conversation,
        CancellationToken cancellationToken = default, Func<CancellationToken, Task>? save = null,
        AutoCompactionPolicy? autoCompaction = null, ModelPricing? pricing = null, string? sessionFile = null,
        string? provider = null, string? reasoningLevel = null, AgentRunRetryPolicy? retryPolicy = null,
        Func<TimeSpan, CancellationToken, Task>? retryDelay = null,
        PromptDeliveryMode steeringMode = PromptDeliveryMode.OneAtATime,
        PromptDeliveryMode followUpMode = PromptDeliveryMode.OneAtATime,
        bool autoCompactionEnabled = true)
    {
        if (autoCompaction is not null) _ = autoCompaction.TriggerTokens;
        if (conversation.RecoverIncomplete() && save is not null) await save(cancellationToken);
        var execution = await agent.RestoreHistoryAsync(conversation.ContextMessages(), cancellationToken);
        return new ConversationRun(agent, conversation, execution, save, autoCompaction, pricing, sessionFile, provider,
            reasoningLevel, retryPolicy ?? AgentRunRetryPolicy.Default, steeringMode, followUpMode,
            autoCompactionEnabled, retryDelay);
    }

    /// <summary>Queue guidance ahead of follow-up work on the active application run.</summary>
    public bool TrySteer(string prompt) => TryQueue(prompt, steering: true, "steering");

    /// <summary>Queue work that runs after steering input has drained.</summary>
    public bool TryFollowUp(string prompt) => TryQueue(prompt, steering: false, "follow_up");

    /// <summary>Compatibility alias: an additional RPC prompt is follow-up work.</summary>
    public bool TryQueuePrompt(string prompt) => TryFollowUp(prompt);

    public void QueueRpcInput(string prompt, bool steering, Action<AgentLifecycleEvent> publishQueueUpdate)
        => _promptDelivery.QueueRpcInput(prompt, steering, publishQueueUpdate);

    private bool TryQueue(string prompt, bool steering, string kind)
        => _promptDelivery.TryQueueActive(prompt, steering, kind);

    public PendingPrompts GetPendingPrompts() => _promptDelivery.Snapshot();

    /// <summary>Remove and return input that has not reached the model, for editor restoration.</summary>
    public PendingPrompts ClearPendingPrompts(Action<AgentLifecycleEvent>? publishQueueUpdate = null) =>
        _promptDelivery.Clear(publishQueueUpdate);

    public int PendingPromptCount => _promptDelivery.Count;

    public PromptDeliveryMode SteeringMode => _promptDelivery.SteeringMode;

    public PromptDeliveryMode FollowUpMode => _promptDelivery.FollowUpMode;

    public void SetSteeringMode(PromptDeliveryMode mode) => _promptDelivery.SetSteeringMode(mode);

    public void SetFollowUpMode(PromptDeliveryMode mode) => _promptDelivery.SetFollowUpMode(mode);

    public bool SetThinkingLevelDuringRun(string level, ReasoningOptions? reasoning)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(level);
        lock (_runtimeStateGate)
        {
            if (!_promptDelivery.IsActive) return false;
            _agent.SetReasoningOptions(reasoning);
            Volatile.Write(ref _reasoningLevel, level);
            if (!string.Equals(_persistedThinkingLevel, level, StringComparison.Ordinal))
            {
                Conversation.AppendThinkingLevelChange(level);
                _persistedThinkingLevel = level;
            }
            return true;
        }
    }

    public bool TrySetModelDuringRun(string model, string? endpoint, string? provider,
        ModelPricing? pricing, AutoCompactionPolicy? autoCompaction, string thinkingLevel,
        ReasoningOptions? reasoning, Action activateRuntime)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(model);
        ArgumentException.ThrowIfNullOrWhiteSpace(thinkingLevel);
        ArgumentNullException.ThrowIfNull(activateRuntime);
        lock (_runtimeStateGate)
        {
            if (!_promptDelivery.IsActive) return false;
            _currentModel = model;
            _currentProvider = provider;
            Conversation.SelectModel(model, endpoint, provider);
            if (!string.Equals(_persistedThinkingLevel, thinkingLevel, StringComparison.Ordinal))
            {
                Conversation.AppendThinkingLevelChange(thinkingLevel);
                _persistedThinkingLevel = thinkingLevel;
            }
            _agent.SetReasoningOptions(reasoning);
            Volatile.Write(ref _reasoningLevel, thinkingLevel);
            _pendingRuntimeChanges.Enqueue(new PendingModelChange(provider, pricing, autoCompaction, activateRuntime));
            return true;
        }
    }

    public Task<BashExecutionResult> ExecuteBashAsync(string command, Action<string>? onUpdate = null,
        CancellationToken cancellationToken = default) => _agent.ExecuteBashAsync(command, onUpdate, cancellationToken);

    public void AbortBash() => _agent.AbortBash();

    public bool IsRetrying => _retryController.IsRetrying;

    public void SetAutoRetryEnabled(bool enabled) => _retryController.SetEnabled(enabled);

    public void AbortRetry() => _retryController.AbortRetry();

    public void SetAutoCompactionEnabled(bool enabled) => Volatile.Write(ref _autoCompactionEnabled, enabled ? 1 : 0);

    private sealed record PromptBatch(IReadOnlyList<ChatMessage> Messages, IReadOnlyList<string> Texts)
    {
        public string Text => string.Join("\n", Texts);
    }

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
                                    Publish(UsageEvent(CurrentProviderUsage(usage.Details)));
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
        => _promptDelivery.Begin(publish);

    private IReadOnlyList<ChatMessage> TakeSteeringForProvider(bool firstProviderRequest) =>
        _promptDelivery.TakeSteeringForProvider(firstProviderRequest);

    private PromptBatch? TakeQueuedPromptOrClose(object owner)
    {
        var batch = _promptDelivery.TakeNextBatchOrClose(owner, PersistPendingRuntimeChangesUnsafe);
        return batch is null ? null : new PromptBatch(
            batch.Messages.Select(prompt => new ChatMessage(ChatRole.User, prompt)).ToArray(), batch.Messages);
    }

    private void EndPromptLoop(object owner) => _promptDelivery.End(owner, () =>
    {
        PersistPendingRuntimeChangesUnsafe();
        FlushPendingBashExecutionsUnsafe();
    });

    private void PersistPendingRuntimeChangesUnsafe()
    {
        while (_pendingRuntimeChanges.TryDequeue(out var change))
        {
            switch (change)
            {
                case PendingModelChange model:
                    model.ActivateRuntime();
                    _provider = model.Provider;
                    _pricing = model.Pricing;
                    _autoCompaction = model.AutoCompaction;
                    break;
            }
        }
    }

    private abstract record PendingRuntimeChange;
    private sealed record PendingModelChange(string? Provider, ModelPricing? Pricing,
        AutoCompactionPolicy? AutoCompaction, Action ActivateRuntime) : PendingRuntimeChange;

    public bool RecordBashResult(string command, BashExecutionResult result, bool excludeFromContext = false)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(result);
        var execution = new BashExecutionRecord(command, result.Output, result.ExitCode, result.Cancelled,
            result.Truncated, result.FullOutputPath, excludeFromContext);
        lock (_runtimeStateGate)
        {
            if (_promptDelivery.IsActive)
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
        lock (_runtimeStateGate) FlushPendingBashExecutionsUnsafe();
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
        => await CompactWithResultAsync(focus, cancellationToken) is not null;

    public async Task<ConversationCompactionResult?> CompactWithResultAsync(string? focus = null,
        CancellationToken cancellationToken = default, Func<AgentLifecycleEvent, Task>? publishEvent = null)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (publishEvent is not null)
                await publishEvent(new AgentLifecycleEvent("compaction_start") { CompactionReason = "manual" });
            try
            {
                var result = await CompactCoreAsync(focus, cancellationToken);
                var error = result is null ? CompactionUnavailableError() : null;
                if (publishEvent is not null)
                    await publishEvent(new AgentLifecycleEvent("compaction_end", Error: error is null ? null :
                        "Compaction failed: " + error)
                    {
                        CompactionReason = "manual",
                        CompactionResult = result,
                        CompactionAborted = false,
                        CompactionWillRetry = false
                    });
                return result;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                if (publishEvent is not null)
                    await publishEvent(new AgentLifecycleEvent("compaction_end")
                    {
                        CompactionReason = "manual",
                        CompactionAborted = true,
                        CompactionWillRetry = false
                    });
                throw;
            }
            catch (Exception error)
            {
                if (publishEvent is not null)
                    await publishEvent(new AgentLifecycleEvent("compaction_end", Error: "Compaction failed: " + error.Message)
                    {
                        CompactionReason = "manual",
                        CompactionAborted = false,
                        CompactionWillRetry = false
                    });
                throw;
            }
        }
        finally { _gate.Release(); }
    }

    private string CompactionUnavailableError() => Conversation.Tree.ActivePath()
        .LastOrDefault(entry => entry.Type is not ("usage" or "context_projection"))?.Type == "compaction"
            ? "Already compacted"
            : "Nothing to compact (session too small)";

    private Task<ConversationCompactionResult?> CompactCoreAsync(string? focus, CancellationToken cancellationToken) =>
        _compactionCoordinator.CompactAsync(focus, cancellationToken);

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
        var providerRequestModel = Conversation.Model;
        var providerRequestProvider = Conversation.Provider;
        var providerRequestPricing = _pricing;
        var interruptionType = "turn_interrupted";
        string? interruptionError = null;
        void AppendAvailableProviderHistory()
        {
            var providerHistory = ObservedChatClient.NormalizeReadImagesForHistory(_agent.GetHistory(_execution));
            var canonicalHistory = Conversation.ContextMessages();
            var knownToolCallIds = canonicalHistory.SelectMany(message => message.Contents.OfType<FunctionResultContent>())
                .Select(result => result.CallId).ToHashSet(StringComparer.Ordinal);
            var canonicalIndex = 0;
            foreach (var providerMessage in providerHistory)
            {
                var matchEnd = providerTurnHistory.FindEquivalentRangeEnd(canonicalHistory, canonicalIndex, providerMessage);
                if (matchEnd >= 0)
                {
                    canonicalIndex = matchEnd;
                    continue;
                }
                var message = ProviderTurnHistoryReconciler.RetainUncheckpointedToolResults(providerMessage, knownToolCallIds);
                if (message is null) continue;
                matchEnd = providerTurnHistory.FindEquivalentRangeEnd(canonicalHistory, canonicalIndex, message);
                if (matchEnd >= 0) canonicalIndex = matchEnd;
                else
                {
                    Conversation.Append(message);
                    canonicalHistory.Add(message);
                    canonicalIndex = canonicalHistory.Count;
                }
            }
        }

        void CompleteProviderTurnUnsafe()
        {
            if (providerResponse is not null)
            {
                var completedResponse = providerResponse;
                var toolResults = turnToolResults.ToArray();
                providerTurnHistory.RecordCompletedTurn(completedResponse, toolResults);
                onEvent?.Invoke(new AgentLifecycleEvent("assistant_turn_completed")
                {
                    TurnMessage = completedResponse,
                    TurnToolResults = toolResults
                });
                providerResponse = null;
                turnToolResults.Clear();
            }
            AppendAvailableProviderHistory();
            foreach (var result in providerTurnHistory.MissingToolResults(Conversation.ContextMessages()))
                foreach (var normalized in ObservedChatClient.NormalizeReadImagesForHistory([result]))
                    Conversation.Append(normalized);
            PersistPendingRuntimeChangesUnsafe();
        }

        void Observe(AgentLifecycleEvent item)
        {
            if (item.Type is "turn_failed" or "turn_interrupted")
            {
                interruptionType = item.Type;
                interruptionError = item.Error;
            }
            if (item.Type == "model_request_started")
            {
                lock (_runtimeStateGate)
                {
                    CompleteProviderTurnUnsafe();
                    providerRequestModel = _currentModel;
                    providerRequestProvider = _currentProvider;
                    providerRequestPricing = _pricing;
                    _providerRequestModel = providerRequestModel;
                    _providerRequestPricing = providerRequestPricing;
                }
                partialAssistantText.Clear();
                onEvent?.Invoke(new("assistant_turn_started"));
            }
            else if (item.Type == "model_request_completed")
            {
                providerResponse = item.ProviderResponse;
                if (providerResponse?.Contents.OfType<FunctionCallContent>().Any() == true)
                {
                    lock (_runtimeStateGate)
                    {
                        Conversation.Append(providerResponse);
                    }
                }
                item = item with
                {
                    ProviderThinkingLevel = Volatile.Read(ref _reasoningLevel),
                    UsageSnapshot = item.ProviderUsage is { } usage
                        ? UsageRecord.Create(providerRequestModel, "model", usage, providerRequestPricing)
                        : null
                };
            }
            else if (item.ProviderUpdate?.Contents?.OfType<UsageContent>().LastOrDefault() is { } updateUsage)
                item = item with { UsageSnapshot = UsageRecord.Create(providerRequestModel, "model", updateUsage.Details, providerRequestPricing) };
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
            if (AutoCompactionEnabled && _autoCompaction is not null &&
                EstimateNextContext(estimatedPrompt) > _autoCompaction.TriggerTokens)
            {
                if (await CompactCoreAsync(null, cancellationToken) is not null)
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
            var inFlightBudget = !AutoCompactionEnabled || _autoCompaction is null ? null : new InFlightContextBudget(
                () => _autoCompaction ?? throw new InvalidOperationException("In-flight compaction policy was removed."),
                (messages, token) => _agent.SummarizeAsync(messages, null, token),
                async (summary, token) =>
                {
                    Conversation.MarkInFlightProjection();
                    if (summary.Usage is not null)
                        Conversation.AppendUsage(UsageRecord.Create(CurrentModel, "compaction", summary.Usage, _pricing));
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
            var updateEnumerator = updates.GetAsyncEnumerator(cancellationToken);
            Exception? streamError = null;
            try
            {
                while (true)
                {
                    AgentResponseUpdate update;
                    try
                    {
                        if (!await updateEnumerator.MoveNextAsync()) break;
                        update = updateEnumerator.Current;
                    }
                    catch (OperationCanceledException error)
                    {
                        streamError = error;
                        interruptionType = "turn_interrupted";
                        interruptionError = "Request was aborted";
                        throw;
                    }
                    catch (Exception error) when (cancellationToken.IsCancellationRequested)
                    {
                        streamError = error;
                        interruptionType = "turn_interrupted";
                        interruptionError = "Request was aborted";
                        throw new OperationCanceledException("Request was aborted", error, cancellationToken);
                    }
                    catch (Exception error)
                    {
                        streamError = error;
                        interruptionType = "turn_failed";
                        interruptionError = error.Message;
                        throw;
                    }
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
                                Conversation.AppendUsage(UsageRecord.Create(providerRequestModel, "model", usage.Details, providerRequestPricing));
                    yield return update;
                }
            }
            finally
            {
                try { await updateEnumerator.DisposeAsync(); }
                catch when (streamError is not null) { }
            }
            lock (_runtimeStateGate) CompleteProviderTurnUnsafe();
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
                lock (_runtimeStateGate)
                {
                    PersistPendingRuntimeChangesUnsafe();
                    var knownToolCallIds = existing.SelectMany(message => message.Contents.OfType<FunctionResultContent>())
                        .Select(result => result.CallId).ToHashSet(StringComparer.Ordinal);
                    var canonicalTail = existing.Skip(historyStartIndex).ToList();
                    var canonicalIndex = 0;
                    foreach (var providerMessage in appendedHistory)
                    {
                        var matchEnd = providerTurnHistory.FindEquivalentRangeEnd(canonicalTail, canonicalIndex, providerMessage);
                        if (matchEnd >= 0)
                        {
                            canonicalIndex = matchEnd;
                            continue;
                        }
                        var message = ProviderTurnHistoryReconciler.RetainUncheckpointedToolResults(providerMessage, knownToolCallIds);
                        if (message is null) continue;
                        matchEnd = providerTurnHistory.FindEquivalentRangeEnd(canonicalTail, canonicalIndex, message);
                        if (matchEnd >= 0) canonicalIndex = matchEnd;
                        else
                        {
                            Conversation.Append(message);
                            canonicalTail.Add(message);
                            canonicalIndex = canonicalTail.Count;
                        }
                    }
                }
                var canonicalHistory = Conversation.ContextMessages();
                if (canonicalHistory.Count != history.Count || canonicalHistory.Where((message, index) => !JsonElement.DeepEquals(
                    JsonSerializer.SerializeToElement(message, AIJsonUtilities.DefaultOptions),
                    JsonSerializer.SerializeToElement(history[index], AIJsonUtilities.DefaultOptions))).Any())
                    _execution = await _agent.RestoreHistoryAsync(canonicalHistory, CancellationToken.None);
                _historyCount = canonicalHistory.Count;
                if (!completed && accepted)
                {
                    Conversation.Tree.Append("interrupted", JsonSerializer.SerializeToElement(
                        new
                        {
                            prompt,
                            partialText = partialText.ToString(),
                            partialAssistantText = partialAssistantText.ToString(),
                            events,
                            terminalType = interruptionType,
                            stopReason = interruptionType == "turn_interrupted" ? "aborted" : "error",
                            errorMessage = interruptionError,
                            provider = providerRequestProvider,
                            model = providerRequestModel,
                            timestamp = DateTimeOffset.UtcNow
                        }));
                    var interruptedHistory = Conversation.ContextMessages();
                    _execution = await _agent.RestoreHistoryAsync(interruptedHistory, CancellationToken.None);
                    _historyCount = interruptedHistory.Count;
                }
                if (started) await durable!.FinishAsync(completed);
            }
            catch (OperationCanceledException)
            {
                interruptionType = "turn_interrupted";
                interruptionError = "Request was aborted";
                throw;
            }
            catch (Exception error)
            {
                interruptionType = "turn_failed";
                interruptionError = error.Message;
                throw;
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
        var attemptEntries = path.Skip(firstAttemptEntry).ToArray();
        var interrupted = attemptEntries.LastOrDefault(node => node.Type == "interrupted");
        var omittedIds = new List<string>();
        var matchingAssistant = -1;
        if (interrupted is not null)
        {
            var partialText = PiJsonlSessionInterchange.StringProperty(interrupted.Payload, "partialAssistantText");
            if (!string.IsNullOrEmpty(partialText)) omittedIds.Add(interrupted.Id);
            matchingAssistant = string.IsNullOrEmpty(partialText) ? -1 : Array.FindLastIndex(attemptEntries,
                node =>
                {
                    if (node.Type != "chat") return false;
                    var message = ConversationSession.RestoreEntry(node);
                    return message.Role == ChatRole.Assistant &&
                        string.Equals(message.Text, partialText, StringComparison.Ordinal);
                });
        }
        if (matchingAssistant >= 0)
        {
            for (var index = matchingAssistant; index < attemptEntries.Length; index++)
            {
                var entry = attemptEntries[index];
                if (index == matchingAssistant)
                    omittedIds.Add(entry.Id);
                else
                {
                    if (entry.Type != "chat" || ConversationSession.RestoreEntry(entry).Role != ChatRole.Tool) break;
                    omittedIds.Add(entry.Id);
                }
            }
        }
        else
        {
            var chatEntries = attemptEntries.Where(node => node.Type == "chat").ToArray();
            var failedAssistant = Array.FindLastIndex(chatEntries,
                node => ConversationSession.RestoreEntry(node).Role == ChatRole.Assistant);
            if (failedAssistant >= 0)
            {
                omittedIds.Add(chatEntries[failedAssistant].Id);
                for (var index = failedAssistant + 1; index < chatEntries.Length; index++)
                {
                    var message = ConversationSession.RestoreEntry(chatEntries[index]);
                    if (message.Role != ChatRole.Tool) break;
                    omittedIds.Add(chatEntries[index].Id);
                }
            }
        }
        var appended = omittedIds.Distinct(StringComparer.Ordinal)
            .Select(Conversation.AppendContextOmission).ToList();
        if (appended.Count > 0 && _save is not null) await _save(CancellationToken.None);
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
