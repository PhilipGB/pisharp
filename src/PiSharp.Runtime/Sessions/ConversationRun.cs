using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using NativeExtensions = PiSharp.Extensions.Abstractions;
using PiSharp.Core;
using PiSharp.Runtime.Extensions;
using PiSharp.Runtime.Tools;

namespace PiSharp.Runtime.Sessions;

/// <summary>One MAF execution session projecting the canonical selected conversation branch.</summary>
public sealed partial class ConversationRun
{
    private static readonly AsyncLocal<ConversationRun?> s_currentGateOwner = new();
    private readonly PiAgent _agent;
    private Func<string?, NativeExtensions.PiSharpExtensionSessionContext?,
        NativeExtensions.PiSharpExtensionCapabilities?>? _extensionCapabilitiesFactory;
    private readonly Func<CancellationToken, Task>? _save;
    private AutoCompactionPolicy? _autoCompaction;
    private AutoCompactionPolicy? _logicalContextPolicy;
    private int? _successfulPhysicalContextWindow;
    private readonly VirtualModelPhysicalContextResolver? _physicalContextResolver;
    private int? _keepRecentTokens;
    private int _autoCompactionEnabled;
    private ModelPricing? _pricing;
    private readonly string? _sessionFile;
    private string? _provider;
    private string? _providerApi;
    private readonly AgentRunRetryController _retryController;
    private string? _reasoningLevel;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private int _manualCompactionActive;
    private int _failedInFlightContextProjection;
    private readonly object _runtimeStateGate = new();
    private readonly PromptDeliveryController _promptDelivery;
    private readonly Queue<PendingRuntimeChange> _pendingRuntimeChanges = new();
    private readonly Queue<Func<CancellationToken, Task>> _pendingRuntimeTransitionNotifications = new();
    private readonly Queue<PiSharpCustomMessage> _pendingContextCustomMessages = new();
    private readonly Queue<ChatMessage> _pendingProviderContextMessages = new();
    private readonly Queue<BashExecutionRecord> _pendingBashExecutions = new();
    private readonly ConversationCompactionCoordinator _compactionCoordinator;
    private readonly ConversationToolState _toolState;
    private int _sessionStartInvoked;
    private int _sessionShutdownInvoked;
    private AgentSession _execution;
    private int _historyCount;
    private string _currentModel;
    private string? _currentProvider;
    private string _providerRequestModel;
    private ModelPricing? _providerRequestPricing;
    private string? _persistedThinkingLevel;
    public ConversationSession Conversation { get; }
    internal bool SessionShutdownInvoked => Volatile.Read(ref _sessionShutdownInvoked) != 0;
    public IReadOnlyList<string> ActiveToolNames =>
        _agent.GetToolLoadout(_execution).Snapshot.ActiveToolNames;
    public string? SessionFile => _sessionFile;
    public JsonElement SharePresentation => _agent.GetSharePresentation(_execution);
    public string ExportForShare() => SessionShareExport.ExportBranch(Conversation, SharePresentation);
    /// <summary>Returns stable public metadata for the configured tools in this session.</summary>
    public IReadOnlyList<NativeExtensions.PiSharpExtensionToolInfo> GetConfiguredTools()
    {
        var snapshot = _agent.GetToolLoadout(_execution).Snapshot;
        var active = snapshot.ActiveToolNames.ToHashSet(StringComparer.Ordinal);
        return Array.AsReadOnly(snapshot.Registered.Select(tool => new NativeExtensions.PiSharpExtensionToolInfo(
            tool.Function.Name,
            tool.Function.Description ?? string.Empty,
            active.Contains(tool.Function.Name),
            tool.Exposure switch
            {
                ToolExposure.Direct => NativeExtensions.PiSharpExtensionToolExposure.Direct,
                ToolExposure.ModelOnly => NativeExtensions.PiSharpExtensionToolExposure.ModelOnly,
                ToolExposure.CodeMode => NativeExtensions.PiSharpExtensionToolExposure.CodeMode,
                ToolExposure.Deferred => NativeExtensions.PiSharpExtensionToolExposure.Deferred,
                ToolExposure.Hidden => NativeExtensions.PiSharpExtensionToolExposure.Hidden,
                _ => throw new ArgumentOutOfRangeException(nameof(tool.Exposure))
            },
            tool.Namespace is null ? "builtin" : "mcp",
            Namespace: tool.Namespace?.Name)).ToArray());
    }

    /// <summary>Replaces the tools declared by the current session on its next provider request.</summary>
    public void SetActiveTools(IEnumerable<string> toolNames)
    {
        ArgumentNullException.ThrowIfNull(toolNames);
        var loadout = _agent.GetToolLoadout(_execution);
        loadout.SetActiveTools(toolNames);
        Conversation.AppendToolLoadout(loadout.Snapshot.ActiveToolNames);
    }

    public Task PersistAsync(CancellationToken cancellationToken = default) =>
        _save?.Invoke(cancellationToken) ?? Task.CompletedTask;

    /// <summary>Notifies configured extensions after this session has been restored and bound to the runtime.</summary>
    public async Task<IReadOnlyList<AgentLifecycleEvent>> NotifySessionStartAsync(
        NativeExtensions.PiSharpExtensionSessionStartReason reason,
        CancellationToken cancellationToken = default)
    {
        if (Interlocked.Exchange(ref _sessionStartInvoked, 1) != 0) return [];
        BindExtensionContext();
        return await _agent.NotifySessionStartAsync(reason, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Notifies configured extensions before this session is replaced or the host shuts down.</summary>
    public async Task<IReadOnlyList<AgentLifecycleEvent>> NotifySessionShutdownAsync(
        NativeExtensions.PiSharpExtensionSessionShutdownReason reason,
        CancellationToken cancellationToken = default)
    {
        if (Interlocked.Exchange(ref _sessionShutdownInvoked, 1) != 0) return [];
        BindExtensionContext();
        return await _agent.NotifySessionShutdownAsync(reason, cancellationToken).ConfigureAwait(false);
    }

    private void BindExtensionContext()
    {
        _agent.SetExtensionConversation(Conversation);
        _agent.SetExtensionSessionContextFactory(source => CreateExtensionSessionContext(source));
        _agent.SetPendingMessageState(() => _promptDelivery.Count > 0);
        _agent.SetVirtualModelSession(Conversation, _reasoningLevel ?? "off", _save);
        if (_extensionCapabilitiesFactory is { } capabilitiesFactory)
            _agent.SetExtensionCapabilitiesFactory(capabilitiesFactory);
    }

    /// <summary>Creates a public, branch-aware extension session bridge for this conversation.</summary>
    public NativeExtensions.PiSharpExtensionSessionContext CreateExtensionSessionContext(string? source = null,
        Func<JsonElement, CancellationToken, Task>? entryAppended = null) =>
        new(() => new NativeExtensions.PiSharpExtensionSessionSnapshot(Conversation.Id, Conversation.Name,
                Conversation.Tree.HeadId, Conversation.ExtensionStateEntries()),
            (customType, data, cancellationToken) => AppendExtensionStateAsync(customType, data, source,
                entryAppended, cancellationToken), SetSessionNameAsync, SetEntryLabelAsync);

    /// <summary>Installs the host-owned callback capabilities used by extension event contexts.</summary>
    internal void SetExtensionCapabilitiesFactory(
        Func<string?, NativeExtensions.PiSharpExtensionSessionContext?,
            NativeExtensions.PiSharpExtensionCapabilities?> factory)
    {
        ArgumentNullException.ThrowIfNull(factory);
        _extensionCapabilitiesFactory = factory;
    }

    /// <summary>Changes the active session name and commits only after its normal save succeeds.</summary>
    public Task SetSessionNameAsync(string name, CancellationToken cancellationToken = default) =>
        SetSessionNameAsync(name, saveOverride: null, cancellationToken);

    internal async Task SetSessionNameAsync(string name,
        Func<CancellationToken, Task>? saveOverride, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(name);
        var ownsGate = ReferenceEquals(s_currentGateOwner.Value, this);
        if (!ownsGate) await _gate.WaitAsync(cancellationToken);
        var previousName = Conversation.Name;
        try
        {
            using var change = Conversation.BeginSessionNameChange(name);
            var save = _save ?? saveOverride;
            if (save is not null) await save(cancellationToken).ConfigureAwait(false);
            change.Commit();
        }
        finally
        {
            if (!ownsGate) _gate.Release();
        }
        await NotifySessionInfoChangedIfChangedAsync(previousName, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Publishes the already committed current session name to native extension observers.</summary>
    internal Task NotifySessionInfoChangedAsync(CancellationToken cancellationToken)
    {
        BindExtensionContext();
        return _agent.ExtensionEvents.DispatchSessionInfoChangedAsync(
            new NativeExtensions.PiSharpExtensionSessionInfoChangedEvent(Conversation.Name), cancellationToken);
    }

    /// <summary>Publishes a name change only after its owner has committed the canonical session state.</summary>
    internal Task NotifySessionInfoChangedIfChangedAsync(string? previousName, CancellationToken cancellationToken) =>
        string.Equals(previousName, Conversation.Name, StringComparison.Ordinal)
            ? Task.CompletedTask
            : NotifySessionInfoChangedAsync(cancellationToken);

    /// <summary>Changes an existing entry label and commits only after its normal save succeeds.</summary>
    public async Task SetEntryLabelAsync(string entryId, string? label,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(entryId);
        var ownsGate = ReferenceEquals(s_currentGateOwner.Value, this);
        if (!ownsGate) await _gate.WaitAsync(cancellationToken);
        try
        {
            using var change = Conversation.BeginSessionLabelChange(entryId, label);
            if (_save is not null) await _save(cancellationToken).ConfigureAwait(false);
            change.Commit();
        }
        finally
        {
            if (!ownsGate) _gate.Release();
        }
    }

    private async Task<NativeExtensions.PiSharpExtensionSessionEntry> AppendExtensionStateAsync(string customType,
        JsonElement data, string? source, Func<JsonElement, CancellationToken, Task>? entryAppended,
        CancellationToken cancellationToken)
    {
        var ownsGate = ReferenceEquals(s_currentGateOwner.Value, this);
        if (!ownsGate) await _gate.WaitAsync(cancellationToken);
        try
        {
            using var change = Conversation.BeginExtensionStateAppend(customType, data, source);
            if (_save is not null) await _save(cancellationToken);
            change.Commit();
            if (entryAppended is not null)
            {
                var node = Conversation.Tree.Entries.Single(entry => entry.Id == change.Entry.Id);
                var projected = JsonSerializer.SerializeToElement(PiJsonlEntryCodec.ProjectEntry(Conversation, node));
                await entryAppended(projected, CancellationToken.None).ConfigureAwait(false);
            }
            return change.Entry;
        }
        finally
        {
            if (!ownsGate) _gate.Release();
        }
    }
    public bool AutoCompactionEnabled => Volatile.Read(ref _autoCompactionEnabled) != 0;
    public bool IsCompacting => _compactionCoordinator.IsCompacting;
    public int? ContextWindowTokens { get { lock (_runtimeStateGate) return _agent.HasVirtualModelRouter ? _successfulPhysicalContextWindow : _autoCompaction?.ContextWindowTokens; } }
    public int? EstimatedContextTokens
    {
        get
        {
            var projection = Conversation.ProjectContextUsageForEstimate();
            if (projection.HasCompaction && !projection.HasValidAssistantUsage) return null;
            return projection.HasValidAssistantUsage && !projection.IsInvalidated
                ? _agent.EstimateContextTokens(projection.UsageTokens, projection.TrailingMessages)
                : _agent.EstimateContextTokens(Conversation.ContextMessages());
        }
    }
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
        int? keepRecentTokens,
        Func<TimeSpan, CancellationToken, Task>? retryDelay, VirtualModelPhysicalContextResolver? physicalContextResolver, string? providerApi)
    {
        _agent = agent;
        _save = save;
        _autoCompaction = autoCompaction;
        _logicalContextPolicy = autoCompaction;
        _physicalContextResolver = physicalContextResolver;
        _keepRecentTokens = keepRecentTokens;
        _autoCompactionEnabled = autoCompactionEnabled ? 1 : 0;
        _pricing = pricing;
        _sessionFile = sessionFile is null ? null : Path.GetFullPath(sessionFile);
        _provider = provider;
        _providerApi = providerApi;
        _retryController = new AgentRunRetryController(retryPolicy, retryDelay);
        _reasoningLevel = reasoningLevel;
        _promptDelivery = new PromptDeliveryController(_runtimeStateGate, steeringMode, followUpMode);
        Conversation = conversation;
        _toolState = new ConversationToolState(agent, conversation);
        _execution = execution;
        _historyCount = conversation.ContextMessages().Count;
        _currentModel = conversation.Model;
        _currentProvider = conversation.Provider;
        _providerRequestModel = conversation.Model;
        _providerRequestPricing = pricing;
        _persistedThinkingLevel = reasoningLevel;
        _compactionCoordinator = new ConversationCompactionCoordinator(Conversation, _agent,
            () => _autoCompaction, () => _keepRecentTokens, () => _pricing, _save,
            RestoreExecutionAsync,
            (execution, count) => { _toolState.Restore(execution); _execution = execution; _historyCount = count; },
            () => HasSessionBeforeCompactHandlers, DispatchSessionBeforeCompactAsync);
    }

    public static async Task<ConversationRun> OpenAsync(PiAgent agent, ConversationSession conversation,
        CancellationToken cancellationToken = default, Func<CancellationToken, Task>? save = null,
        AutoCompactionPolicy? autoCompaction = null, ModelPricing? pricing = null, string? sessionFile = null,
        string? provider = null, string? reasoningLevel = null, AgentRunRetryPolicy? retryPolicy = null,
        Func<TimeSpan, CancellationToken, Task>? retryDelay = null,
        PromptDeliveryMode steeringMode = PromptDeliveryMode.OneAtATime,
        PromptDeliveryMode followUpMode = PromptDeliveryMode.OneAtATime,
        bool autoCompactionEnabled = true, int? keepRecentTokens = null,
        VirtualModelPhysicalContextResolver? physicalContextResolver = null, string? providerApi = null,
        IReadOnlyList<string>? activeToolNamesOverride = null)
    {
        if (autoCompaction is not null) _ = autoCompaction.TriggerTokens;
        if (conversation.RecoverIncomplete() && save is not null) await save(cancellationToken);
        var execution = await agent.RestoreHistoryAsync(conversation.ContextMessages(), cancellationToken);
        var restoredToolNames = activeToolNamesOverride?.Distinct(StringComparer.Ordinal).ToArray();
        var run = new ConversationRun(agent, conversation, execution, save, autoCompaction, pricing, sessionFile, provider,
            reasoningLevel, retryPolicy ?? AgentRunRetryPolicy.Default, steeringMode, followUpMode,
            autoCompactionEnabled, keepRecentTokens, retryDelay, physicalContextResolver, providerApi);
        run._toolState.Restore(execution, restoredToolNames, openingSession: true);
        await run.RestorePhysicalContextPolicyAsync(cancellationToken);
        if (restoredToolNames is not null)
        {
            conversation.AppendToolLoadout(restoredToolNames);
            if (save is not null) await save(cancellationToken);
        }
        return run;
    }

    public Task<VirtualModelPhysicalContext?> GetPhysicalContextAsync(CancellationToken cancellationToken = default) =>
        _agent.HasVirtualModelRouter && _physicalContextResolver is not null
            ? _physicalContextResolver(Conversation.Snapshot().ActiveMessages(), cancellationToken)
            : Task.FromResult<VirtualModelPhysicalContext?>(null);

    private async Task RestorePhysicalContextPolicyAsync(CancellationToken cancellationToken)
    {
        if (!_agent.HasVirtualModelRouter || _physicalContextResolver is null) return;
        var context = await GetPhysicalContextAsync(cancellationToken);
        _autoCompaction = context is null ? _logicalContextPolicy : context.ContextPolicy;
        _successfulPhysicalContextWindow = context?.Model.ContextLength;
        _agent.SetPhysicalImageLimits(context?.Model);
    }

    private async Task<AgentSession> RestoreExecutionAsync(IEnumerable<ChatMessage> messages, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var execution = await _agent.RestoreHistoryAsync(messages, token);
        token.ThrowIfCancellationRequested();
        _toolState.Restore(execution);
        return execution;
    }

    /// <summary>Queue guidance ahead of follow-up work on the active application run.</summary>
    public bool TrySteer(string prompt) => TryQueue(prompt, steering: true, "steering");

    /// <summary>Queue work that runs after steering input has drained.</summary>
    public bool TryFollowUp(string prompt) => TryQueue(prompt, steering: false, "follow_up");

    /// <summary>Compatibility alias: an additional RPC prompt is follow-up work.</summary>
    public bool TryQueuePrompt(string prompt) => TryFollowUp(prompt);

    /// <summary>Whether this run currently owns the active prompt loop.</summary>
    public bool IsActive => _promptDelivery.IsActive;

    /// <summary>The effective thinking level currently applied to this run.</summary>
    public string ThinkingLevel => Volatile.Read(ref _reasoningLevel) ?? "off";

    /// <summary>Whether an extension callback can compact without reentering a run or compaction.</summary>
    internal bool CanCompactFromExtensionCallback => !_promptDelivery.IsActive &&
        !_compactionCoordinator.IsCompacting && Volatile.Read(ref _manualCompactionActive) == 0 && _gate.CurrentCount != 0;

    /// <summary>Queue user input through the active run's ordinary steering/follow-up path.</summary>
    public bool TryQueueExtensionUserMessage(ChatMessage message, string promptText, bool followUp = false)
    {
        ArgumentNullException.ThrowIfNull(message);
        ArgumentException.ThrowIfNullOrWhiteSpace(promptText);
        return _promptDelivery.TryQueueActive(message, promptText, steering: !followUp,
            followUp ? "extension_follow_up" : "extension_steering", skipBeforeAgentStart: true);
    }

    /// <summary>
    /// Queue a custom extension message through the active run's steering/follow-up path.
    /// A request made while idle should be started with <see cref="RunCustomMessageEventsAsync"/>.
    /// </summary>
    public bool TryQueueExtensionMessage(PiSharpCustomMessage message, bool followUp = false)
    {
        ArgumentNullException.ThrowIfNull(message);
        var projected = PiSharpCustomMessageProjection.ToChatMessage(message);
        return _promptDelivery.TryQueueActive(projected, message.Content, steering: !followUp,
            followUp ? "extension_custom_follow_up" : "extension_custom_steering", skipBeforeAgentStart: true);
    }

    private bool TryQueueActiveExtensionMessage(PiSharpCustomMessage message,
        NativeExtensions.PiSharpMessageDeliveryMode deliveryMode)
    {
        var projected = PiSharpCustomMessageProjection.ToChatMessage(message);
        var kind = deliveryMode switch
        {
            NativeExtensions.PiSharpMessageDeliveryMode.Steer => "extension_custom_steering",
            NativeExtensions.PiSharpMessageDeliveryMode.FollowUp => "extension_custom_follow_up",
            _ => throw new ArgumentOutOfRangeException(nameof(deliveryMode))
        };
        return _promptDelivery.TryDeliverExtensionMessage(projected, message.Content, deliveryMode, kind,
            skipBeforeAgentStart: true);
    }

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

    public bool SetThinkingLevelDuringRun(string level, ReasoningOptions? reasoning,
        Func<CancellationToken, Task>? notifyAfterActivation = null, Action? updateSelectionOwner = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(level);
        lock (_runtimeStateGate)
        {
            if (!_promptDelivery.IsActive) return false;
            _agent.SetReasoningOptions(reasoning);
            Volatile.Write(ref _reasoningLevel, level);
            _agent.SetVirtualModelThinkingLevel(level);
            if (!string.Equals(_persistedThinkingLevel, level, StringComparison.Ordinal))
            {
                Conversation.AppendThinkingLevelChange(level);
                _persistedThinkingLevel = level;
            }
            updateSelectionOwner?.Invoke();
            if (notifyAfterActivation is not null)
                _pendingRuntimeTransitionNotifications.Enqueue(notifyAfterActivation);
            return true;
        }
    }

    public bool TrySetModelDuringRun(string model, string? endpoint, string? provider,
        ModelPricing? pricing, AutoCompactionPolicy? autoCompaction, int keepRecentTokens, string thinkingLevel,
        ReasoningOptions? reasoning, Action activateRuntime, string? providerApi = null,
        Func<CancellationToken, Task>? notifyAfterActivation = null, Action? updateSelectionOwner = null)
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
            _agent.SetVirtualModelThinkingLevel(thinkingLevel);
            updateSelectionOwner?.Invoke();
            _pendingRuntimeChanges.Enqueue(new PendingModelChange(provider, pricing, autoCompaction,
                keepRecentTokens, activateRuntime, providerApi, notifyAfterActivation));
            return true;
        }
    }

    public Task<BashExecutionResult> ExecuteBashAsync(string command, Action<string>? onUpdate = null,
        CancellationToken cancellationToken = default) => _agent.ExecuteBashAsync(command, onUpdate, cancellationToken);

    /// <summary>Runs the ordered extension input interception before normal prompt processing.</summary>
    public Task<NativeExtensions.PiSharpInputResult> InterceptInputAsync(string text,
        IReadOnlyList<DataContent>? images, NativeExtensions.PiSharpInputSource source,
        NativeExtensions.PiSharpInputStreamingBehavior? streamingBehavior,
        CancellationToken cancellationToken = default)
    {
        BindExtensionContext();
        return _agent.InterceptInputAsync(text, images, source, streamingBehavior, cancellationToken);
    }

    /// <summary>Runs source-ordered user-bash handlers. A handler error terminates dispatch.</summary>
    public Task<NativeExtensions.PiSharpUserBashResult?> InterceptUserBashAsync(string command,
        bool excludeFromContext, Func<string, Task> emitUpdateAsync,
        CancellationToken cancellationToken = default)
    {
        BindExtensionContext();
        return _agent.InterceptUserBashAsync(command, excludeFromContext, Conversation.WorkingDirectory,
            emitUpdateAsync, cancellationToken);
    }
    /// <summary>Executes a user-bash extension operation through the host's output and cancellation pipeline.</summary>
    public Task<BashExecutionResult> ExecuteUserBashOperationAsync(string command,
        NativeExtensions.PiSharpUserBashOperation operation, Action<string>? onUpdate = null,
        CancellationToken cancellationToken = default) =>
        _agent.ExecuteUserBashOperationAsync(command, operation, onUpdate, cancellationToken);

    public void AbortBash() => _agent.AbortBash();

    public bool IsRetrying => _retryController.IsRetrying;

    public void SetAutoRetryEnabled(bool enabled) => _retryController.SetEnabled(enabled);

    public void AbortRetry() => _retryController.AbortRetry();

    public Task AbortRetryAndWaitAsync() => _retryController.AbortRetryAndWaitAsync();

    public void SetAutoCompactionEnabled(bool enabled) => Volatile.Write(ref _autoCompactionEnabled, enabled ? 1 : 0);

    private sealed record PromptBatch(IReadOnlyList<ChatMessage> Messages, IReadOnlyList<string> Texts,
        PiSharpCustomMessage? StartingCustomMessage = null, bool SkipBeforeAgentStart = false,
        bool Continuation = false)
    {
        public string Text => string.Join("\n", Texts);
    }

    /// <summary>Authoritative ordered lifecycle, including actual model and tool boundaries.
    /// Queued prompts are additional turns in the same run. No completion is emitted when execution
    /// fails. Disposing the stream aborts the current turn and leaves unstarted prompts queued.</summary>
    public IAsyncEnumerable<AgentLifecycleEvent> RunEventsAsync(string prompt,
        CancellationToken cancellationToken = default) => RunEventsAsync(new ChatMessage(ChatRole.User, prompt), prompt, cancellationToken);

    public IAsyncEnumerable<AgentLifecycleEvent> RunEventsAsync(ChatMessage promptMessage, string promptText,
        CancellationToken cancellationToken = default) =>
        RunEventsCoreAsync(promptMessage, promptText, null, cancellationToken);

    /// <summary>Submit user input through the same normal turn path used by interactive prompts.</summary>
    public IAsyncEnumerable<AgentLifecycleEvent> RunUserMessageEventsAsync(ChatMessage promptMessage, string promptText,
        CancellationToken cancellationToken = default) => RunEventsAsync(promptMessage, promptText, cancellationToken);

    /// <summary>Start one normal agent run from an extension custom message while this run is idle.</summary>
    public IAsyncEnumerable<AgentLifecycleEvent> RunCustomMessageEventsAsync(PiSharpCustomMessage message,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);
        return RunEventsCoreAsync(PiSharpCustomMessageProjection.ToChatMessage(message), message.Content,
            message, cancellationToken);
    }

    /// <summary>Start a custom-message run with its extension source recorded by the host.</summary>
    public IAsyncEnumerable<AgentLifecycleEvent> RunCustomMessageEventsAsync(
        NativeExtensions.PiSharpCustomMessage message, string? sourcePath,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);
        return RunCustomMessageEventsAsync(ToRuntimeCustomMessage(message, sourcePath), cancellationToken);
    }

    /// <summary>Queue an extension custom message into the active run.</summary>
    public bool TryQueueExtensionMessage(NativeExtensions.PiSharpCustomMessage message, string? sourcePath,
        bool followUp = false)
    {
        ArgumentNullException.ThrowIfNull(message);
        return TryQueueExtensionMessage(ToRuntimeCustomMessage(message, sourcePath), followUp);
    }

    /// <summary>
    /// Applies the extension custom-message delivery rules. Returns true only when the caller
    /// should start a new custom-message run; all other deliveries have already been queued or
    /// appended to the conversation.
    /// </summary>
    public async Task<bool> DeliverExtensionCustomMessageAsync(NativeExtensions.PiSharpCustomMessage message,
        string? sourcePath, bool triggerTurn, NativeExtensions.PiSharpMessageDeliveryMode deliveryMode,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);
        if (!Enum.IsDefined(deliveryMode)) throw new ArgumentOutOfRangeException(nameof(deliveryMode));
        var customMessage = ToRuntimeCustomMessage(message, sourcePath);
        var projected = PiSharpCustomMessageProjection.ToChatMessage(customMessage);

        if (deliveryMode == NativeExtensions.PiSharpMessageDeliveryMode.NextTurn)
        {
            _promptDelivery.TryDeliverExtensionMessage(projected, customMessage.Content, deliveryMode,
                "extension_custom_next_turn");
            return false;
        }

        if (triggerTurn)
        {
            return !TryQueueActiveExtensionMessage(customMessage, deliveryMode);
        }

        lock (_runtimeStateGate)
        {
            if (_promptDelivery.IsActive)
            {
                _pendingContextCustomMessages.Enqueue(customMessage);
                return false;
            }
        }

        if (await QueueExtensionMessageForNextTurnAsync(message, sourcePath, cancellationToken)) return false;

        // A run may have claimed ownership between the initial idle check and the idle append.
        lock (_runtimeStateGate)
        {
            if (_promptDelivery.IsActive)
            {
                _pendingContextCustomMessages.Enqueue(customMessage);
                return false;
            }
        }
        throw new InvalidOperationException("The conversation changed while the custom message was being delivered.");
    }

    /// <summary>
    /// Persist a custom message into an idle conversation for the next ordinary turn.
    /// Returns false when a run already owns the prompt loop.
    /// </summary>
    public async Task<bool> QueueExtensionMessageForNextTurnAsync(NativeExtensions.PiSharpCustomMessage message,
        string? sourcePath, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);
        if (_promptDelivery.IsActive) return false;

        var customMessage = ToRuntimeCustomMessage(message, sourcePath);
        var chatMessage = PiSharpCustomMessageProjection.ToChatMessage(customMessage);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (!_promptDelivery.TryReserveIdle(out var reservation)) return false;
            using (reservation)
            {
                var previousHead = Conversation.Tree.HeadId;
                var previousHistoryCount = _historyCount;
                try
                {
                    var snapshot = PiSharpExtensionMessageMapper.Snapshot(chatMessage);
                    await _agent.ExtensionEvents.DispatchMessageStartAsync(new(snapshot), cancellationToken).ConfigureAwait(false);
                    var finalized = await _agent.ExtensionEvents.DispatchMessageEndAsync(snapshot,
                        cancellationToken).ConfigureAwait(false);
                    chatMessage = PiSharpExtensionMessageMapper.Restore(finalized);
                    Conversation.Append(chatMessage);
                    _agent.AppendToHistory(_execution, chatMessage);
                    _historyCount++;
                    if (_save is not null) await _save(cancellationToken);
                    return true;
                }
                catch
                {
                    Conversation.Tree.Select(previousHead);
                    var history = Conversation.ContextMessages();
                    _execution = await RestoreExecutionAsync(history, CancellationToken.None);
                    _historyCount = previousHistoryCount;
                    throw;
                }
            }
        }
        finally { _gate.Release(); }
    }

    private static PiSharpCustomMessage ToRuntimeCustomMessage(NativeExtensions.PiSharpCustomMessage message,
        string? sourcePath) => new(message.CustomType, message.Content, message.Display)
        {
            Source = string.IsNullOrWhiteSpace(sourcePath) ? null : sourcePath
        };

    private async IAsyncEnumerable<AgentLifecycleEvent> RunEventsCoreAsync(ChatMessage promptMessage, string promptText,
        PiSharpCustomMessage? startingCustomMessage,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        BindExtensionContext();
        var channel = Channel.CreateBounded<AgentLifecycleEvent>(new BoundedChannelOptions(256)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.Wait
        });
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        using var consumerClosed = new CancellationTokenSource();
        var initialEntryIds = Conversation.Tree.ActivePath().Select(entry => entry.Id).ToHashSet(StringComparer.Ordinal);
        var initialRunBoundaryVersion = _boundaryContextVersion;
        void Publish(AgentLifecycleEvent value)
        {
            // A tool may report its durable outcome after cancellation has been requested.
            // Keep those terminal records observable while the consumer is still attached.
            var token = value.Type is "tool_execution_finished" or "tool_outcome_unknown"
                ? consumerClosed.Token
                : linked.Token;
            channel.Writer.WriteAsync(value, token).AsTask().GetAwaiter().GetResult();
        }
        var pump = Task.Run(async () =>
        {
            var accepted = false;
            var nativeAgentStarted = false;
            var agentRunOpen = false;
            var nativeAgentOutcome = NativeExtensions.PiSharpExtensionAgentOutcome.Completed;
            string? nativeAgentError = null;
            var activeTurnOutcomeSent = false;
            object? owner = null;
            var conversationRunActivityStarted = false;
            try
            {
                owner = BeginPromptLoop(Publish, out var nextTurnMessages);
                _agent.BeginConversationRunActivity();
                conversationRunActivityStarted = true;
                var initialMessages = new List<ChatMessage> { promptMessage };
                var initialTexts = new List<string> { promptText };
                foreach (var nextTurnMessage in nextTurnMessages)
                {
                    initialMessages.Add(nextTurnMessage.Message);
                    initialTexts.Add(nextTurnMessage.Text);
                }
                PromptBatch? current = new(initialMessages, initialTexts, startingCustomMessage);
                _boundaryContinuationRequested = false;
                _terminalBoundaryTurn = null;
                var stopRun = false;
                while (current is not null)
                {
                    var turn = current;
                    activeTurnOutcomeSent = false;
                    var currentMessage = turn.Messages.FirstOrDefault();
                    var customStart = turn.StartingCustomMessage;
                    if (!turn.SkipBeforeAgentStart && customStart is null &&
                        currentMessage is not null && PiSharpCustomMessageProjection.TryRead(currentMessage, out var queuedCustom))
                        customStart = queuedCustom;
                    AgentTurnOutcome outcome;
                    if (!turn.Continuation) _agent.ClearRunSystemPromptOverride();
                    var injectedMessages = turn.SkipBeforeAgentStart ? [] :
                        await _agent.PrepareBeforeAgentStartAsync(currentMessage!,
                            customStart is null ? turn.Texts[0] : "", Publish, linked.Token, _execution,
                            customStart);
                    var acceptedMessages = turn.Messages.Concat(injectedMessages.Select(
                        PiSharpCustomMessageProjection.ToChatMessage)).ToArray();
                    acceptedMessages = (await _agent.NormalizeAcceptedPromptImagesAsync(acceptedMessages,
                        linked.Token).ConfigureAwait(false)).ToArray();
                    var lengthStopRecovery = CreateLengthStopRecoveryCoordinator(Publish);
                    outcome = await _retryController.RunTurnAsync(
                        (continuation, observeAttempt) => continuation || turn.Continuation
                            ? RunStreamingContinuationAsync(turn.Text, linked.Token, observeAttempt)
                            : turn.SkipBeforeAgentStart || customStart is not null || injectedMessages.Count > 0 || acceptedMessages.Length > 1
                                ? RunStreamingAcceptedMessagesAsync(acceptedMessages, turn.Text, linked.Token, observeAttempt)
                                : RunStreamingAttemptAsync(acceptedMessages[0], turn.Texts[0], false,
                                    linked.Token, observeAttempt),
                        item =>
                        {
                            if (item.Type == "prompt_accepted") accepted = true;
                            if (item.Type is "prompt_accepted" or "agent_attempt_started")
                            {
                                nativeAgentOutcome = NativeExtensions.PiSharpExtensionAgentOutcome.Completed;
                                nativeAgentError = null;
                                // A prompt accepted into a fresh agent run (the first run, or a
                                // continuation that begins after a previous agent_end, e.g. after a
                                // failed turn) starts the agent lifecycle again (Pi parity).
                                if (item.Type == "prompt_accepted" && !agentRunOpen)
                                {
                                    nativeAgentStarted = true;
                                    agentRunOpen = true;
                                    _agent.BeginExtensionAgentRun();
                                    _agent.ExtensionEvents.DispatchAgentStartAsync(
                                        new NativeExtensions.PiSharpExtensionAgentStartEvent(), linked.Token, Publish)
                                        .GetAwaiter().GetResult();
                                }
                            }
                            else if (item.Type == "turn_failed")
                            {
                                nativeAgentOutcome = NativeExtensions.PiSharpExtensionAgentOutcome.Error;
                                nativeAgentError = item.Error;
                            }
                            else if (item.Type == "turn_interrupted")
                            {
                                nativeAgentOutcome = NativeExtensions.PiSharpExtensionAgentOutcome.Aborted;
                                nativeAgentError = item.Error;
                            }
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
                            _execution = await RestoreExecutionAsync(history, token);
                            _historyCount = history.Count;
                        },
                        FlushPendingBashExecutions,
                        () => Conversation.Tree.HeadId,
                        linked.Token,
                        (response, attempted, head, pathLength, token) => lengthStopRecovery.TryRecoverAsync(
                            response, attempted, head, pathLength, Publish, token));
                    activeTurnOutcomeSent = true;
                    if (outcome != AgentTurnOutcome.Completed)
                    {
                        nativeAgentOutcome = NativeExtensions.PiSharpExtensionAgentOutcome.Error;
                        stopRun = true;
                        if (_terminalBoundaryTurn is { } terminalTurn)
                        {
                            await DispatchMutableTurnBoundaryAsync(terminalTurn, nativeAgentOutcome, linked.Token, Publish)
                                .ConfigureAwait(false);
                            _terminalBoundaryTurn = null;
                        }
                        if (outcome == AgentTurnOutcome.Failed &&
                            Interlocked.Exchange(ref _failedInFlightContextProjection, 0) == 0)
                            await CompactAfterFailedTurnAsync(linked.Token, Publish).ConfigureAwait(false);
                    }
                    if (!stopRun && _boundaryContinuationRequested)
                    {
                        _boundaryContinuationRequested = false;
                        await CompactAfterBoundaryAsync(linked.Token, Publish).ConfigureAwait(false);
                        current = new([], [promptText], SkipBeforeAgentStart: true, Continuation: true);
                        continue;
                    }
                    current = stopRun ? null : await TakeQueuedPromptOrCloseAsync(owner, linked.Token, closeWhenEmpty: false);
                    if (current is not null) continue;
                    if (agentRunOpen)
                    {
                        var messages = Conversation.Tree.ActivePath()
                            .Where(entry => !initialEntryIds.Contains(entry.Id) && entry.Type == "chat")
                            .Select(ConversationMessageCodec.RestoreEntry).ToArray();
                        agentRunOpen = false;
                        await _agent.ExtensionEvents.DispatchAgentEndAsync(new(messages, nativeAgentOutcome,
                            nativeAgentError), linked.Token, Publish).ConfigureAwait(false);
                    }
                    current = await TakeQueuedPromptOrCloseAsync(owner, linked.Token, closeWhenEmpty: false);
                    if (_boundaryContextVersion != initialRunBoundaryVersion || _agent.ExtensionEvents.HasBoundaryHandlers)
                        await CompactAfterBoundaryAsync(linked.Token, Publish).ConfigureAwait(false);
                    current ??= await TakeQueuedPromptOrCloseAsync(owner, linked.Token, closeWhenEmpty: false);
                    if (current is null && await DispatchBeforeSettleBoundaryAsync(nativeAgentOutcome, linked.Token, Publish)
                            .ConfigureAwait(false))
                        current = new([], [promptText], SkipBeforeAgentStart: true, Continuation: true);
                    current ??= await TakeQueuedPromptOrCloseAsync(owner, linked.Token, closeWhenEmpty: true);
                    if (current is not null)
                    {
                        stopRun = false;
                    }
                }
                if (!stopRun) Publish(new("agent_run_completed"));
            }
            catch (OperationCanceledException)
            {
                if (nativeAgentStarted)
                    nativeAgentOutcome = NativeExtensions.PiSharpExtensionAgentOutcome.Aborted;
                nativeAgentError ??= "Request was aborted";
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
                if (nativeAgentStarted)
                    nativeAgentOutcome = NativeExtensions.PiSharpExtensionAgentOutcome.Error;
                nativeAgentError = error.Message;
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
                try
                {
                    if (nativeAgentStarted && linked.IsCancellationRequested &&
                        _terminalBoundaryTurn is { } interruptedBoundary && !consumerClosed.IsCancellationRequested)
                    {
                        using var terminalCancellation = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                        try
                        {
                            await DispatchMutableTurnBoundaryAsync(interruptedBoundary,
                                NativeExtensions.PiSharpExtensionAgentOutcome.Aborted, terminalCancellation.Token,
                                item => channel.Writer.TryWrite(item)).ConfigureAwait(false);
                        }
                        catch (OperationCanceledException) when (terminalCancellation.IsCancellationRequested) { }
                        _terminalBoundaryTurn = null;
                    }
                    if (agentRunOpen)
                    {
                        var messages = Conversation.Tree.ActivePath()
                            .Where(entry => !initialEntryIds.Contains(entry.Id) && entry.Type == "chat")
                            .Select(ConversationMessageCodec.RestoreEntry).ToArray();
                        try
                        {
                            await _agent.ExtensionEvents.DispatchAgentEndAsync(new(messages, nativeAgentOutcome,
                                nativeAgentError), linked.Token,
                                item => channel.Writer.TryWrite(item)).ConfigureAwait(false);
                        }
                        catch (OperationCanceledException) when (linked.IsCancellationRequested)
                        {
                            // Host cancellation releases a pending terminal observer and settles this run.
                        }
                    }
                    if (owner is not null) await EndPromptLoopAsync(owner);
                }
                finally
                {
                    _agent.ClearRunSystemPromptOverride();
                    if (conversationRunActivityStarted) _agent.EndConversationRunActivity();
                    if (nativeAgentStarted)
                    {
                        var aborted = linked.IsCancellationRequested;
                        using var settledCancellation = aborted
                            ? CancellationTokenSource.CreateLinkedTokenSource(consumerClosed.Token)
                            : CancellationTokenSource.CreateLinkedTokenSource(linked.Token, consumerClosed.Token);
                        // An already aborted run still delivers its terminal observer, with bounded cleanup.
                        // Normal settlement follows the owning lifetime without changing its captured outcome.
                        if (aborted) settledCancellation.CancelAfter(TimeSpan.FromSeconds(2));
                        try
                        {
                            await _agent.ExtensionEvents.DispatchAgentSettledAsync(new(aborted),
                                settledCancellation.Token, item => channel.Writer.TryWrite(item)).ConfigureAwait(false);
                        }
                        catch (OperationCanceledException) when (settledCancellation.IsCancellationRequested) { }
                    }
                    try
                    {
                        await channel.Writer.WriteAsync(new AgentLifecycleEvent("agent_settled")
                        {
                            Aborted = linked.IsCancellationRequested
                        }, consumerClosed.Token);
                    }
                    catch (OperationCanceledException) { }
                    channel.Writer.TryComplete();
                }
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

    private object BeginPromptLoop(Action<AgentLifecycleEvent> publish,
        out IReadOnlyList<QueuedPrompt> nextTurnMessages) => _promptDelivery.Begin(publish, out nextTurnMessages);

    private async Task<IReadOnlyList<ChatMessage>> TakeSteeringForProviderAsync(bool firstProviderRequest,
        bool contextContinuation, CancellationToken cancellationToken)
    {
        ChatMessage[] messages;
        lock (_runtimeStateGate)
        {
            messages = _promptDelivery.TakeSteeringForProvider(firstProviderRequest, contextContinuation).ToArray();
        }
        if (messages.Length == 0) return messages;

        var accepted = new List<ChatMessage>(messages.Length);
        try
        {
            foreach (var message in messages)
            {
                var snapshot = PiSharpExtensionMessageMapper.Snapshot(message);
                await _agent.ExtensionEvents.DispatchMessageStartAsync(new(snapshot), cancellationToken).ConfigureAwait(false);
                var finalized = await _agent.ExtensionEvents.DispatchMessageEndAsync(snapshot, cancellationToken).ConfigureAwait(false);
                accepted.Add(PiSharpExtensionMessageMapper.Restore(finalized));
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            accepted.Clear();
            accepted.AddRange(messages);
            lock (_runtimeStateGate)
                foreach (var message in accepted) Conversation.Append(message);
            if (_save is not null) await _save(CancellationToken.None).ConfigureAwait(false);
            throw;
        }

        lock (_runtimeStateGate)
            foreach (var message in accepted) Conversation.Append(message);
        if (_save is not null) await _save(CancellationToken.None).ConfigureAwait(false);
        return accepted;
    }

    private async Task<PromptBatch?> TakeQueuedPromptOrCloseAsync(object owner, CancellationToken cancellationToken,
        bool closeWhenEmpty = true)
    {
        while (true)
        {
            var appendedPendingContextMessages = await FlushPendingContextCustomMessagesAsync(
                includeInNextProviderRequest: false, cancellationToken).ConfigureAwait(false);
            var transition = _promptDelivery.TakeNextBatchOrClose(owner, () =>
            {
                PersistPendingRuntimeChangesUnsafe();
                return false;
            }, closeWhenEmpty);
            await DispatchPendingRuntimeTransitionNotificationsAsync().ConfigureAwait(false);
            if ((transition.PendingContextMessagesAppended || appendedPendingContextMessages) && _save is not null)
                await _save(CancellationToken.None);

            if (transition.Batch is { } batch)
            {
                var messages = batch.Messages.Select(prompt => prompt.Message).ToArray();
                var texts = batch.Messages.Select(prompt => prompt.Text).ToArray();
                var startingCustomMessage = messages.Length > 0 && PiSharpCustomMessageProjection.TryRead(messages[0],
                    out var customMessage) ? customMessage : null;
                return new PromptBatch(messages, texts, startingCustomMessage, batch.Messages[0].SkipBeforeAgentStart);
            }
            if (transition.Closed) return null;
        }
    }

    private async Task<bool> FlushPendingContextCustomMessagesAsync(bool includeInNextProviderRequest,
        CancellationToken cancellationToken)
    {
        List<PiSharpCustomMessage> pending = [];
        lock (_runtimeStateGate)
            while (_pendingContextCustomMessages.TryDequeue(out var customMessage)) pending.Add(customMessage);
        if (pending.Count == 0) return false;

        var accepted = new List<ChatMessage>(pending.Count);
        try
        {
            foreach (var customMessage in pending)
            {
                var chatMessage = PiSharpCustomMessageProjection.ToChatMessage(customMessage);
                var snapshot = PiSharpExtensionMessageMapper.Snapshot(chatMessage);
                await _agent.ExtensionEvents.DispatchMessageStartAsync(new(snapshot), cancellationToken).ConfigureAwait(false);
                var finalized = await _agent.ExtensionEvents.DispatchMessageEndAsync(snapshot,
                    cancellationToken).ConfigureAwait(false);
                accepted.Add(PiSharpExtensionMessageMapper.Restore(finalized));
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            accepted.Clear();
            accepted.AddRange(pending.Select(message => PiSharpCustomMessageProjection.ToChatMessage(message)));
            AppendAccepted(accepted);
            if (_save is not null) await _save(CancellationToken.None).ConfigureAwait(false);
            throw;
        }

        AppendAccepted(accepted);
        if (_save is not null) await _save(CancellationToken.None).ConfigureAwait(false);
        return true;

        void AppendAccepted(IReadOnlyList<ChatMessage> messages)
        {
            lock (_runtimeStateGate)
            {
                foreach (var message in messages)
                {
                    Conversation.Append(message);
                    _agent.AppendToHistory(_execution, message);
                    _historyCount++;
                    if (includeInNextProviderRequest) _pendingProviderContextMessages.Enqueue(message);
                }
            }
        }
    }

    private async Task<IReadOnlyList<ChatMessage>> TakePendingProviderContextMessagesAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ChatMessage[] messages;
        await FlushPendingContextCustomMessagesAsync(includeInNextProviderRequest: true, cancellationToken)
            .ConfigureAwait(false);
        lock (_runtimeStateGate)
        {
            messages = _pendingProviderContextMessages.ToArray();
            _pendingProviderContextMessages.Clear();
        }
        if (messages.Length == 0) return messages;

        // A continuation must never observe a canonical message that recovery could lose.
        // Save before releasing the handoff, including when a request is cancelled.
        if (_save is not null) await _save(CancellationToken.None);
        return messages;
    }

    private void EndPromptLoop(object owner) => _promptDelivery.End(owner, () =>
    {
        PersistPendingRuntimeChangesUnsafe();
        FlushPendingBashExecutionsUnsafe();
    });

    private async Task EndPromptLoopAsync(object owner)
    {
        while (true)
        {
            var appended = await FlushPendingContextCustomMessagesAsync(includeInNextProviderRequest: false,
                CancellationToken.None).ConfigureAwait(false);
            if (appended) continue;
            var ended = false;
            lock (_runtimeStateGate)
            {
                if (_pendingContextCustomMessages.Count == 0)
                {
                    EndPromptLoop(owner);
                    ended = true;
                }
            }
            if (ended)
            {
                await DispatchPendingRuntimeTransitionNotificationsAsync().ConfigureAwait(false);
                return;
            }
            if (_save is not null) await _save(CancellationToken.None);
        }
    }

    private async Task DispatchPendingRuntimeTransitionNotificationsAsync(CancellationToken cancellationToken = default)
    {
        while (true)
        {
            Func<CancellationToken, Task>? notification;
            lock (_runtimeStateGate)
                notification = _pendingRuntimeTransitionNotifications.TryDequeue(out var pending) ? pending : null;
            if (notification is null) return;
            await notification(cancellationToken).ConfigureAwait(false);
        }
    }

    private void PersistPendingRuntimeChangesUnsafe()
    {
        while (_pendingRuntimeChanges.TryDequeue(out var change))
        {
            switch (change)
            {
                case PendingModelChange model:
                    model.ActivateRuntime();
                    _provider = model.Provider;
                    _providerApi = model.ProviderApi;
                    _pricing = model.Pricing;
                    _autoCompaction = model.AutoCompaction;
                    _logicalContextPolicy = model.AutoCompaction;
                    _keepRecentTokens = model.KeepRecentTokens;
                    if (model.NotifyAfterActivation is not null)
                        _pendingRuntimeTransitionNotifications.Enqueue(model.NotifyAfterActivation);
                    break;
            }
        }
    }

    private abstract record PendingRuntimeChange;
    private sealed record PendingModelChange(string? Provider, ModelPricing? Pricing,
        AutoCompactionPolicy? AutoCompaction, int KeepRecentTokens, Action ActivateRuntime, string? ProviderApi,
        Func<CancellationToken, Task>? NotifyAfterActivation) : PendingRuntimeChange;

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
        var message = ConversationContextProjection.BashExecutionContextMessage(execution);
        _agent.AppendToHistory(_execution, message);
        _historyCount++;
    }

    /// <summary>Manually summarize completed earlier turns; no raw session messages are removed.</summary>
    public async Task<bool> CompactAsync(string? focus = null, CancellationToken cancellationToken = default)
        => await CompactWithResultAsync(focus, cancellationToken) is not null;

    public async Task<ConversationCompactionResult?> CompactWithResultAsync(string? focus = null,
        CancellationToken cancellationToken = default, Func<AgentLifecycleEvent, Task>? publishEvent = null)
    {
        var ownsGate = ReferenceEquals(s_currentGateOwner.Value, this);
        if (ownsGate && _promptDelivery.IsActive)
            throw new InvalidOperationException("Manual compaction cannot reenter an active agent run.");
        if (!ownsGate) await _gate.WaitAsync(cancellationToken);
        if (Interlocked.CompareExchange(ref _manualCompactionActive, 1, 0) != 0)
        {
            if (!ownsGate) _gate.Release();
            throw new InvalidOperationException("Manual compaction is already in progress.");
        }
        var previousGateOwner = s_currentGateOwner.Value;
        s_currentGateOwner.Value = this;
        BindExtensionContext();
        try
        {
            if (publishEvent is not null)
                await publishEvent(new AgentLifecycleEvent("compaction_start") { CompactionReason = "manual" });
            ConversationCompactionResult? completedResult = null;
            string? terminalErrorMessage = null;
            var failureDispatchStarted = false;
            var completionPublished = false;
            try
            {
                await RestorePhysicalContextPolicyAsync(cancellationToken);
                var result = await CompactCoreAsync(focus, cancellationToken,
                    publishLifecycleEvent: publishEvent);
                if (result is null)
                {
                    var errorMessage = terminalErrorMessage = "Compaction failed: " + CompactionUnavailableError();
                    if (publishEvent is not null)
                    {
                        completionPublished = true;
                        await publishEvent(new AgentLifecycleEvent("compaction_end", Error: errorMessage)
                        {
                            CompactionReason = "manual",
                            CompactionAborted = false,
                            CompactionWillRetry = false
                        });
                    }
                    failureDispatchStarted = true;
                    await NativeExtensionCompactionEventPublisher.PublishFailedAsync(_agent,
                        NativeExtensions.PiSharpExtensionCompactionReason.Manual, willRetry: false,
                        aborted: false, errorMessage: errorMessage, cancellationToken: cancellationToken).ConfigureAwait(false);
                    return null;
                }

                completedResult = result;
                await NativeExtensionCompactionEventPublisher.PublishCompletedAsync(_agent, Conversation, result,
                    NativeExtensions.PiSharpExtensionCompactionReason.Manual, willRetry: false,
                    cancellationToken).ConfigureAwait(false);
                if (publishEvent is not null)
                {
                    completionPublished = true;
                    await publishEvent(new AgentLifecycleEvent("compaction_end")
                    {
                        CompactionReason = "manual",
                        CompactionResult = result,
                        CompactionAborted = false,
                        CompactionWillRetry = false
                    });
                }
                return result;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                if (publishEvent is not null && !completionPublished)
                {
                    completionPublished = true;
                    await publishEvent(completedResult is null
                        ? new AgentLifecycleEvent("compaction_end")
                        {
                            CompactionReason = "manual",
                            CompactionAborted = true,
                            CompactionWillRetry = false
                        }
                        : new AgentLifecycleEvent("compaction_end")
                        {
                            CompactionReason = "manual",
                            CompactionResult = completedResult,
                            CompactionAborted = false,
                            CompactionWillRetry = false
                        });
                }
                if (terminalErrorMessage is not null && !failureDispatchStarted)
                {
                    failureDispatchStarted = true;
                    await NativeExtensionCompactionEventPublisher.PublishFailedWithinDeadlineAsync(_agent,
                        NativeExtensions.PiSharpExtensionCompactionReason.Manual, willRetry: false,
                        errorMessage: terminalErrorMessage)
                        .ConfigureAwait(false);
                }
                else if (completedResult is null && terminalErrorMessage is null)
                {
                    failureDispatchStarted = true;
                    await NativeExtensionCompactionEventPublisher.PublishAbortedAsync(_agent,
                        NativeExtensions.PiSharpExtensionCompactionReason.Manual,
                        _compactionCoordinator.LastCompactionFromExtension).ConfigureAwait(false);
                }
                throw;
            }
            catch (Exception error)
            {
                var abortedByExtension = _compactionCoordinator.LastCompactionCancelledByExtension;
                terminalErrorMessage ??= abortedByExtension ? null : "Compaction failed: " + error.Message;
                if (publishEvent is not null && !completionPublished)
                {
                    completionPublished = true;
                    await publishEvent(completedResult is null
                        ? abortedByExtension
                            ? new AgentLifecycleEvent("compaction_end")
                            {
                                CompactionReason = "manual",
                                CompactionAborted = true,
                                CompactionWillRetry = false
                            }
                            : new AgentLifecycleEvent("compaction_end", Error: "Compaction failed: " + error.Message)
                            {
                                CompactionReason = "manual",
                                CompactionAborted = false,
                                CompactionWillRetry = false
                            }
                        : new AgentLifecycleEvent("compaction_end")
                        {
                            CompactionReason = "manual",
                            CompactionResult = completedResult,
                            CompactionAborted = false,
                            CompactionWillRetry = false
                        });
                }
                if (completedResult is null && !failureDispatchStarted)
                {
                    failureDispatchStarted = true;
                    if (abortedByExtension)
                        await NativeExtensionCompactionEventPublisher.PublishAbortedAsync(_agent,
                            NativeExtensions.PiSharpExtensionCompactionReason.Manual).ConfigureAwait(false);
                    else
                        await NativeExtensionCompactionEventPublisher.PublishFailedAsync(_agent,
                            NativeExtensions.PiSharpExtensionCompactionReason.Manual, willRetry: false,
                            aborted: false, errorMessage: terminalErrorMessage, cancellationToken: cancellationToken,
                            fromExtension: _compactionCoordinator.LastCompactionFromExtension).ConfigureAwait(false);
                }
                throw;
            }
        }
        finally
        {
            s_currentGateOwner.Value = previousGateOwner;
            Interlocked.Exchange(ref _manualCompactionActive, 0);
            if (!ownsGate) _gate.Release();
        }
    }

    private string CompactionUnavailableError() => Conversation.Tree.ActivePath()
        .LastOrDefault(entry => entry.Type is not ("usage" or "context_projection"))?.Type == "compaction"
            ? "Already compacted"
            : "Nothing to compact (session too small)";

    private static bool IsRpcInternalEntry(ConversationNode entry) => entry.Type is
        "tool_loadout" or "run_started" or "run_finished" or "run_recovered" or "tool_intent" or
        "tool_outcome" or "tool_skipped" or "assistant_progress" or "usage" or "context_projection" or
        "tool_transcript";

    private (string Model, string? Provider) GetCurrentRequestModel()
    {
        lock (_runtimeStateGate) return (_currentModel, _currentProvider);
    }

    private Task<ConversationCompactionResult?> CompactCoreAsync(string? focus, CancellationToken cancellationToken,
        NativeExtensions.PiSharpExtensionCompactionReason reason = NativeExtensions.PiSharpExtensionCompactionReason.Manual,
        bool willRetry = false, Func<AgentLifecycleEvent, Task>? publishLifecycleEvent = null) =>
        _compactionCoordinator.CompactAsync(focus, cancellationToken, reason: reason, willRetry: willRetry,
            publishLifecycleEvent: publishLifecycleEvent);

    /// <summary>Selects the exact entry and rebuilds MAF state before the next run.</summary>
    public async Task SelectAsync(string? id, CancellationToken cancellationToken = default) =>
        await NavigateTreeCoreAsync(id, new ConversationTreeSelectionOptions(), returnPromptToEditor: false,
            cancellationToken, publishLifecycleEvent: null).ConfigureAwait(false);

    /// <summary>Navigates like Pi tree navigation, returning a selected user prompt for editing.</summary>
    public Task<ConversationTreeNavigationResult> NavigateTreeAsync(string? id,
        ConversationTreeSelectionOptions? options = null, CancellationToken cancellationToken = default,
        Func<AgentLifecycleEvent, Task>? publishLifecycleEvent = null) =>
        NavigateTreeCoreAsync(id, options ?? new ConversationTreeSelectionOptions(), returnPromptToEditor: true,
            cancellationToken, publishLifecycleEvent);

    private async Task<ConversationTreeNavigationResult> NavigateTreeCoreAsync(string? id,
        ConversationTreeSelectionOptions options, bool returnPromptToEditor, CancellationToken cancellationToken,
        Func<AgentLifecycleEvent, Task>? publishLifecycleEvent)
    {
        if (options.CustomInstructions?.Length > 4096)
            throw new ArgumentException("Branch summary instructions exceed 4096 characters.", nameof(options));
        if (options.ReserveTokens < 0)
            throw new ArgumentOutOfRangeException(nameof(options), "Branch summary token reserve must be nonnegative.");
        await _gate.WaitAsync(cancellationToken);
        var previousGateOwner = s_currentGateOwner.Value;
        s_currentGateOwner.Value = this;
        try
        {
            var oldLeafEntryId = Conversation.Tree.HeadId;
            if (string.Equals(id, oldLeafEntryId, StringComparison.Ordinal)) return new();

            // Validate and describe the requested path without changing the selected head.
            var oldPath = Conversation.Tree.ActivePath();
            var targetPath = id is null ? Array.Empty<ConversationNode>() : Conversation.Tree.ClonePath(id).ActivePath();
            var selectedEntry = targetPath.LastOrDefault();
            var navigationHeadId = id;
            string? editorText = null;
            if (returnPromptToEditor && selectedEntry is not null &&
                ConversationContextProjection.ContextMessageForNode(selectedEntry) is { Role: var role } message &&
                role == ChatRole.User)
            {
                navigationHeadId = selectedEntry.ParentId;
                editorText = message.Text;
            }
            var commonPathLength = 0;
            while (commonPathLength < oldPath.Count && commonPathLength < targetPath.Count &&
                oldPath[commonPathLength].Id == targetPath[commonPathLength].Id)
                commonPathLength++;
            var commonAncestorEntryId = commonPathLength == 0 ? null : oldPath[commonPathLength - 1].Id;
            var currentModelEntryId = oldPath.LastOrDefault(node => node.Type == "model_change")?.Id;
            var selectedModelEntryId = targetPath.LastOrDefault(node => node.Type == "model_change")?.Id;
            if (currentModelEntryId != selectedModelEntryId)
                throw new InvalidOperationException("Branch changes the model. Select the matching model before running.");

            var beforeTreeEvent = new NativeExtensions.PiSharpExtensionSessionBeforeTreeEvent(id,
                oldLeafEntryId, commonAncestorEntryId,
                oldPath.Skip(commonPathLength).Select(node => new NativeExtensions.PiSharpExtensionSessionBranchEntry(
                    node.Id, node.ParentId, node.Type, node.Timestamp)), options.Summarize);
            var decision = await DispatchSessionBeforeTreeAsync(beforeTreeEvent, cancellationToken).ConfigureAwait(false);
            if (decision?.Cancel == true)
                throw new InvalidOperationException("Session tree navigation was cancelled by an extension.");

            var customInstructions = decision?.CustomInstructions ?? options.CustomInstructions;
            var replaceInstructions = decision?.ReplaceInstructions ?? options.ReplaceInstructions;
            var label = decision?.Label ?? options.Label;
            string? summaryText = null;
            JsonElement? summaryDetails = null;
            UsageRecord? summaryUsage = null;
            var summaryFromExtension = false;
            if (options.Summarize && !string.IsNullOrWhiteSpace(decision?.Summary))
            {
                if (decision.Summary.Length > 64 * 1024)
                    throw new InvalidDataException("Extension branch summary exceeds 64KB.");
                if (decision.Details is { ValueKind: JsonValueKind.Undefined })
                    throw new InvalidDataException("Extension branch summary details must be a JSON value.");
                if (decision.Usage is { } extensionUsage &&
                    (extensionUsage.InputTokens < 0 || extensionUsage.OutputTokens < 0 ||
                     extensionUsage.CachedInputTokens < 0 || extensionUsage.ReasoningTokens < 0 ||
                     extensionUsage.TotalTokens < 0 || extensionUsage.CachedWriteTokens < 0))
                    throw new InvalidDataException("Extension branch summary usage cannot be negative.");

                summaryText = decision.Summary;
                summaryDetails = decision.Details?.Clone();
                if (decision.Usage is { } suppliedUsage)
                    summaryUsage = UsageRecord.Create(CurrentModel, "branch_summary", new UsageDetails
                    {
                        InputTokenCount = suppliedUsage.InputTokens + suppliedUsage.CachedInputTokens,
                        OutputTokenCount = suppliedUsage.OutputTokens,
                        CachedInputTokenCount = suppliedUsage.CachedInputTokens,
                        ReasoningTokenCount = suppliedUsage.ReasoningTokens,
                        TotalTokenCount = suppliedUsage.TotalTokens,
                        AdditionalCounts = new() { ["cacheWriteTokens"] = suppliedUsage.CachedWriteTokens }
                    }, _pricing);
                summaryFromExtension = true;
            }

            // A before-tree callback may append session state. Preserve that committed state if
            // navigation or its save fails, while restoring the prior execution on the selected
            // branch after the callback.
            var previous = Conversation.Tree.HeadId;
            var entriesBeforeSelection = Conversation.Tree.Entries.Count;
            var selectionMutationStarted = false;
            string? summaryEntryId = null;
            var currentModelAfterHook = Conversation.Tree.ActivePath().LastOrDefault(node => node.Type == "model_change")?.Id;
            if (currentModelAfterHook != selectedModelEntryId)
                throw new InvalidOperationException("Branch changes the model. Select the matching model before running.");
            try
            {
                if (options.Summarize && summaryText is null)
                {
                    var summaryEntries = oldPath.Skip(commonPathLength).ToArray();
                    var tokenBudget = _autoCompaction is { } contextPolicy
                        ? Math.Max(0, contextPolicy.ContextWindowTokens - options.ReserveTokens)
                        : Math.Max(0, 128_000 - options.ReserveTokens);
                    var preparation = BranchSummaryPreparation.Prepare(summaryEntries, tokenBudget);
                    if (preparation.Messages.Count > 0)
                    {
                        var summary = await _agent.SummarizeBranchAsync(preparation.Messages, customInstructions,
                            replaceInstructions, cancellationToken, publishLifecycleEvent).ConfigureAwait(false);
                        summaryText = summary.Text + preparation.FileOperationsSuffix;
                        summaryDetails = JsonSerializer.SerializeToElement(new
                        {
                            readFiles = preparation.ReadFiles,
                            modifiedFiles = preparation.ModifiedFiles
                        });
                        summaryUsage = summary.PhysicalUsage?.FirstOrDefault() ??
                            (summary.Usage is { } usage
                                ? UsageRecord.Create(CurrentModel, "branch_summary", usage, _pricing)
                                : null);
                    }
                }
                entriesBeforeSelection = Conversation.Tree.Entries.Count;
                if (Conversation.Tree.HeadId != previous)
                    throw new InvalidOperationException("Conversation changed during tree navigation.");
                selectionMutationStarted = true;
                Conversation.Tree.Select(navigationHeadId);
                Conversation.RecoverIncomplete();
                ConversationNode? summaryEntry = null;
                if (!string.IsNullOrWhiteSpace(summaryText))
                {
                    var sourceEntry = oldPath.LastOrDefault(entry => !IsRpcInternalEntry(entry));
                    var sourceEntryId = sourceEntry is null ? "root" : PiJsonlEntryCodec.ProjectedEntryId(sourceEntry);
                    summaryEntry = Conversation.AppendBranchSummary(sourceEntryId, summaryText,
                        summaryDetails, summaryFromExtension, summaryUsage);
                    summaryEntryId = summaryEntry.Id;
                }
                if (!string.IsNullOrEmpty(label) && summaryEntry is not null)
                    Conversation.Tree.Append("label", JsonSerializer.SerializeToElement(new
                    {
                        targetId = summaryEntry.Id,
                        label
                    }));
                else if (!string.IsNullOrEmpty(label) && id is not null)
                    Conversation.Tree.Append("label", JsonSerializer.SerializeToElement(new
                    {
                        targetId = id,
                        label
                    }));
                var history = Conversation.ContextMessages();
                var restored = await RestoreExecutionAsync(history, cancellationToken);
                VirtualModelPhysicalContext? physicalContext = null;
                if (_agent.HasVirtualModelRouter && _physicalContextResolver is not null)
                    physicalContext = await _physicalContextResolver(Conversation.Snapshot().ActiveMessages(), cancellationToken)
                        .ConfigureAwait(false);
                if (_save is not null) await _save(cancellationToken).ConfigureAwait(false);

                if (_agent.HasVirtualModelRouter && _physicalContextResolver is not null)
                {
                    _autoCompaction = physicalContext is null ? _logicalContextPolicy : physicalContext.ContextPolicy;
                    _successfulPhysicalContextWindow = physicalContext?.Model.ContextLength;
                    _agent.SetPhysicalImageLimits(physicalContext?.Model);
                }
                _execution = restored;
                _historyCount = history.Count;
            }
            catch
            {
                var appendedDuringSelection = Conversation.Tree.Entries.Skip(entriesBeforeSelection).Reverse().ToArray();
                foreach (var entry in appendedDuringSelection)
                    Conversation.Tree.RollbackAppend(entry, entry.ParentId);
                if (selectionMutationStarted) Conversation.Tree.Select(previous);
                throw;
            }

            // Navigation is committed now. A caller cancellation after persistence must not
            // suppress its completion event, and an observer must not hold the session gate
            // indefinitely after the host has committed the new head.
            using var observerCancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(250));
            try
            {
                await DispatchSessionTreeAsync(new NativeExtensions.PiSharpExtensionSessionTreeEvent(
                    oldLeafEntryId, Conversation.Tree.HeadId, summaryEntryId,
                    summaryEntryId is null ? null : summaryFromExtension), observerCancellation.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (observerCancellation.IsCancellationRequested)
            {
                // The navigation is already durable. Expire only the best-effort observer.
            }
            return new ConversationTreeNavigationResult(editorText);
        }
        finally
        {
            s_currentGateOwner.Value = previousGateOwner;
            _gate.Release();
        }
    }

    public IAsyncEnumerable<AgentResponseUpdate> RunStreamingAsync(string prompt,
        CancellationToken cancellationToken = default, Action<AgentLifecycleEvent>? onEvent = null) =>
        RunStreamingAsync(new ChatMessage(ChatRole.User, prompt), prompt, cancellationToken, onEvent);

    public async IAsyncEnumerable<AgentResponseUpdate> RunStreamingAsync(ChatMessage promptMessage, string prompt,
        [EnumeratorCancellation] CancellationToken cancellationToken = default,
        Action<AgentLifecycleEvent>? onEvent = null)
    {
        var normalizedMessages = await _agent.NormalizeAcceptedPromptImagesAsync([promptMessage], cancellationToken)
            .ConfigureAwait(false);
        await foreach (var update in RunStreamingAttemptAsync(normalizedMessages[0], prompt, false,
            cancellationToken, onEvent).WithCancellation(cancellationToken).ConfigureAwait(false))
            yield return update;
    }

    private IAsyncEnumerable<AgentResponseUpdate> RunStreamingAcceptedMessagesAsync(
        IReadOnlyList<ChatMessage> promptMessages, string prompt, CancellationToken cancellationToken,
        Action<AgentLifecycleEvent>? onEvent)
    {
        if (promptMessages.Count == 0) throw new ArgumentException("An accepted turn requires at least one message.", nameof(promptMessages));
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
        Interlocked.Exchange(ref _failedInFlightContextProjection, 0);
        var previousGateOwner = s_currentGateOwner.Value;
        s_currentGateOwner.Value = this;
        var completed = false;
        var attemptStream = new ConversationAttemptStream();
        DurableExecution? durable = _save is null ? null : new DurableExecution(Conversation, _save);
        var started = false;
        var accepted = false;
        var attribution = new ProviderRequestAttribution(Conversation.Model, Conversation.Provider,
            _providerApi, _pricing, _reasoningLevel);
        BindExtensionContext();
        VirtualModelContextCoordinator? virtualContext = null;
        var physicalOverflowCompacted = false;
        UsageRecord? latestCompletedProviderUsage = null;
        AgentLifecycleEvent? latestCompletedProviderResponse = null;
        string? requestModelAtStart = null;
        string? requestProviderAtStart = null;
        IReadOnlyList<JsonElement>? expectedProviderPrefix = null;
        var providerTurns = new ProviderTurnJournal(Conversation, () => _agent.GetHistory(_execution),
            () => virtualContext?.HasCompacted == true || physicalOverflowCompacted, () => _historyCount, onEvent);
        var nativeTurnOpen = false;
        var nativeTurnStartDispatched = false;
        var initialBoundaryContextVersion = _boundaryContextVersion;

        ProviderTurnSnapshot CompleteProviderTurnUnsafe()
        {
            var completedTurn = providerTurns.CompleteTurn();
            PersistPendingRuntimeChangesUnsafe();
            return completedTurn;
        }

        async Task CompleteNativeTurnAsync(ProviderTurnSnapshot turn, bool aborted, string? errorMessage)
        {
            if (!nativeTurnOpen) return;
            var previousOwner = s_currentGateOwner.Value;
            s_currentGateOwner.Value = this;
            try
            {
                var turnIndex = _agent.ExtensionTurnIndex;
                nativeTurnOpen = false;
                nativeTurnStartDispatched = false;
                if (!aborted && errorMessage is null) _agent.CompleteExtensionTurn();
                var entryIds = FindTurnEntryIds(turn);
                var eventData = new NativeExtensions.PiSharpExtensionTurnEndEvent(
                    turnIndex, turn.Message, turn.ToolResults, entryIds.MessageEntryId,
                    entryIds.ToolResultEntryIds, aborted, errorMessage);
                _terminalBoundaryTurn = aborted || errorMessage is not null ? eventData : null;
                await _agent.ExtensionEvents.DispatchTurnEndAsync(eventData, cancellationToken,
                    item => onEvent?.Invoke(item)).ConfigureAwait(false);
                if (aborted || errorMessage is not null)
                {
                    return;
                }
                _boundaryContinuationRequested = await DispatchMutableTurnBoundaryAsync(eventData,
                    NativeExtensions.PiSharpExtensionAgentOutcome.Completed, cancellationToken,
                    item => onEvent?.Invoke(item)).ConfigureAwait(false);
            }
            finally { s_currentGateOwner.Value = previousOwner; }
        }

        async Task<IReadOnlyList<ChatMessage>> PrepareTurnAsync(IReadOnlyList<ChatMessage> messages,
            CancellationToken token)
        {
            ProviderTurnSnapshot previousTurn;
            lock (_runtimeStateGate) previousTurn = CompleteProviderTurnUnsafe();
            await CompleteNativeTurnAsync(previousTurn, false, null).ConfigureAwait(false);
            if (_boundaryContextVersion != initialBoundaryContextVersion)
                await CompactAfterBoundaryAsync(token, item => onEvent?.Invoke(item)).ConfigureAwait(false);
            await DispatchPendingRuntimeTransitionNotificationsAsync(token).ConfigureAwait(false);
            // Message lifecycle handlers can be asynchronous. Flush context messages here,
            // before the synchronous request-start observer emits this request's turn notifications.
            // The later provider-context handoff collects any messages queued by those notifications.
            await FlushPendingContextCustomMessagesAsync(includeInNextProviderRequest: true, token)
                .ConfigureAwait(false);
            if (_boundaryContextVersion == initialBoundaryContextVersion) return messages;
            return messages.TakeWhile(message => message.Role == ChatRole.System || message.Role == new ChatRole("developer"))
                .Concat(Conversation.ContextMessages()).ToArray();
        }

        void Observe(AgentLifecycleEvent item)
        {
            if (item.Type is "nested_model_usage" or "nested_tool_usage" && item.UsageSnapshot is { } nestedUsage)
            {
                lock (_runtimeStateGate) Conversation.AppendUsage(nestedUsage);
            }
            if (item.Type is "tool_loadout_changed" or "codemode_store_changed" or
                "model_request_completed" or "model_request_failed" or "model_request_interrupted")
                lock (_runtimeStateGate) _toolState.Observe(item);
            if (item.Type is "turn_failed" or "turn_interrupted")
            {
                attemptStream.InterruptionType = item.Type;
                attemptStream.InterruptionError = item.Error;
            }
            if (item.Type == "model_request_started")
            {
                latestCompletedProviderUsage = null;
                latestCompletedProviderResponse = null;
                lock (_runtimeStateGate)
                {
                    // The provider request following tool execution is the natural continuation
                    // of that turn. Commit context-only extension messages after the completed
                    // tool results so the continuation sees them without starting another turn.
                    requestModelAtStart = _currentModel;
                    requestProviderAtStart = _currentProvider;
                    attribution.Begin(_currentModel, _currentProvider, _providerApi, _pricing);
                    _providerRequestModel = attribution.Model;
                    _providerRequestPricing = attribution.Pricing;
                }
                _boundaryContinuationRequested = false;
                providerTurns.PartialAssistantText.Clear();
                providerTurns.MarkRequestStarted();
                if (!nativeTurnOpen)
                {
                    if (!nativeTurnStartDispatched)
                        _agent.ExtensionEvents.DispatchTurnStartAsync(new NativeExtensions.PiSharpExtensionTurnStartEvent(
                            _agent.ExtensionTurnIndex, DateTimeOffset.UtcNow), cancellationToken,
                            item => onEvent?.Invoke(item)).GetAwaiter().GetResult();
                    nativeTurnStartDispatched = false;
                    nativeTurnOpen = true;
                }
                onEvent?.Invoke(new("assistant_turn_started"));
                foreach (var queued in _promptDelivery.FlushPendingBatchQueueEvents())
                    onEvent?.Invoke(queued);
            }
            else if (item.Type == "model_request_routed")
            {
                lock (_runtimeStateGate)
                {
                    attribution.Route(item);
                    _providerRequestModel = attribution.Model;
                    _providerRequestPricing = attribution.Pricing;
                    _autoCompaction = item.ProviderContextPolicy;
                }
            }
            else if (item.Type == "model_request_completed")
            {
                lock (_runtimeStateGate)
                {
                    providerTurns.Response = attribution.AttributeResponse(item, _reasoningLevel);
                    providerTurns.MarkRequestCompleted();
                    if (item.ProviderProviderId is not null) _successfulPhysicalContextWindow = _autoCompaction?.ContextWindowTokens;
                    if (providerTurns.Response is not null && (attribution.Api is not null || item.ProviderProviderId is not null ||
                        providerTurns.Response.Contents.OfType<FunctionCallContent>().Any()))
                        Conversation.Append(providerTurns.Response);
                }
                item = attribution.Complete(item with
                {
                    ProviderRequestedModelId = requestModelAtStart,
                    ProviderRequestedProviderId = requestProviderAtStart
                }, providerTurns.Response);
                latestCompletedProviderResponse = item;
            }
            else if (item.ProviderUpdate?.Contents?.OfType<UsageContent>().LastOrDefault() is { } updateUsage)
                item = item with { UsageSnapshot = attribution.Usage(updateUsage.Details) };
            else if (item.Type == "tool_execution_finished" && item.ToolResultMessage is { } result)
            {
                // MAF can finish parallel tools on different continuations; serialize the turn
                // snapshot with CompleteProviderTurnUnsafe so no result is lost during closure.
                lock (_runtimeStateGate)
                {
                    providerTurns.RecordToolResult(result, item.NestedToolCalls);
                }
            }
            else if (item.Type == "model_text_delta" && item.Text is not null) providerTurns.PartialAssistantText.Append(item.Text);
            if (item.UsageSnapshot is { } providerUsageSnapshot)
                latestCompletedProviderUsage = providerUsageSnapshot;
            onEvent?.Invoke(item);
        }
        try
        {
            // A live provider failure has not checkpointed anything: the interrupted request is not
            // an unknown tool outcome, and Pi does not warn about replaying it (the crash/restart
            // recovery paths keep the notice). Close the run without a visible notice.
            if (!retryContinuation && Conversation.RecoverIncomplete(announce: false))
            {
                var history = Conversation.ContextMessages();
                var restored = await RestoreExecutionAsync(history, cancellationToken);
                if (_save is not null) await _save(cancellationToken);
                _execution = restored;
                _historyCount = history.Count;
            }
            await RestorePhysicalContextPolicyAsync(cancellationToken);
            var estimatedPrompt = retryContinuation ? "" : prompt;
            var contextTriggerTokens = _autoCompaction?.TriggerTokens;
            if (!_agent.HasVirtualModelRouter && AutoCompactionEnabled && contextTriggerTokens is > 0 &&
                EstimateNextContext(estimatedPrompt) > contextTriggerTokens.Value)
            {
                var plan = ConversationCompactionCoordinator.PreparePlan(Conversation,
                    _autoCompaction?.KeepRecentTokens ?? _keepRecentTokens);
                if (plan is not null)
                {
                    onEvent?.Invoke(new("compaction_start") { CompactionReason = "threshold" });
                    ConversationCompactionResult? completedResult = null;
                    string? terminalErrorMessage = null;
                    var failureDispatchStarted = false;
                    var completionPublished = false;
                    try
                    {
                        var result = await CompactCoreAsync(null, cancellationToken,
                            NativeExtensions.PiSharpExtensionCompactionReason.Threshold);
                        if (result is null)
                        {
                            terminalErrorMessage = "Auto-compaction failed: " + CompactionUnavailableError();
                            onEvent?.Invoke(new("compaction_end", Error: terminalErrorMessage)
                            {
                                CompactionReason = "threshold",
                                CompactionAborted = false,
                                CompactionWillRetry = false
                            });
                            completionPublished = true;
                            failureDispatchStarted = true;
                            await NativeExtensionCompactionEventPublisher.PublishFailedAsync(_agent,
                                NativeExtensions.PiSharpExtensionCompactionReason.Threshold, willRetry: false,
                                aborted: false, errorMessage: terminalErrorMessage, cancellationToken: cancellationToken)
                                .ConfigureAwait(false);
                        }
                        else
                        {
                            completedResult = result;
                            await NativeExtensionCompactionEventPublisher.PublishCompletedAsync(_agent, Conversation,
                                result, NativeExtensions.PiSharpExtensionCompactionReason.Threshold, willRetry: false,
                                cancellationToken).ConfigureAwait(false);
                            onEvent?.Invoke(new("compaction_end")
                            {
                                CompactionReason = "threshold",
                                CompactionResult = result,
                                CompactionAborted = false,
                                CompactionWillRetry = false
                            });
                            completionPublished = true;
                            onEvent?.Invoke(new("context_compacted", Text: "Automatic context summary saved; raw history retained."));
                        }
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        if (!completionPublished)
                        {
                            completionPublished = true;
                            onEvent?.Invoke(completedResult is null
                                ? new("compaction_end")
                                {
                                    CompactionReason = "threshold",
                                    CompactionAborted = true,
                                    CompactionWillRetry = false
                                }
                                : new("compaction_end")
                                {
                                    CompactionReason = "threshold",
                                    CompactionResult = completedResult,
                                    CompactionAborted = false,
                                    CompactionWillRetry = false
                                });
                        }
                        if (terminalErrorMessage is not null && !failureDispatchStarted)
                        {
                            failureDispatchStarted = true;
                            await NativeExtensionCompactionEventPublisher.PublishFailedWithinDeadlineAsync(_agent,
                                NativeExtensions.PiSharpExtensionCompactionReason.Threshold, willRetry: false,
                                errorMessage: terminalErrorMessage)
                                .ConfigureAwait(false);
                        }
                        else if (completedResult is null && terminalErrorMessage is null)
                        {
                            failureDispatchStarted = true;
                            await NativeExtensionCompactionEventPublisher.PublishAbortedAsync(_agent,
                                NativeExtensions.PiSharpExtensionCompactionReason.Threshold,
                                _compactionCoordinator.LastCompactionFromExtension)
                                .ConfigureAwait(false);
                        }
                        throw;
                    }
                    catch (Exception error)
                    {
                        var abortedByExtension = _compactionCoordinator.LastCompactionCancelledByExtension;
                        terminalErrorMessage ??= abortedByExtension ? null : "Auto-compaction failed: " + error.Message;
                        if (!completionPublished)
                        {
                            completionPublished = true;
                            onEvent?.Invoke(completedResult is null
                                ? abortedByExtension
                                    ? new("compaction_end")
                                    {
                                        CompactionReason = "threshold",
                                        CompactionAborted = true,
                                        CompactionWillRetry = false
                                    }
                                    : new("compaction_end", Error: terminalErrorMessage)
                                    {
                                        CompactionReason = "threshold",
                                        CompactionAborted = false,
                                        CompactionWillRetry = false
                                    }
                                : new("compaction_end")
                                {
                                    CompactionReason = "threshold",
                                    CompactionResult = completedResult,
                                    CompactionAborted = false,
                                    CompactionWillRetry = false
                                });
                        }
                        if (completedResult is null && !failureDispatchStarted)
                        {
                            failureDispatchStarted = true;
                            if (abortedByExtension)
                                await NativeExtensionCompactionEventPublisher.PublishAbortedAsync(_agent,
                                    NativeExtensions.PiSharpExtensionCompactionReason.Threshold)
                                    .ConfigureAwait(false);
                            else
                                await NativeExtensionCompactionEventPublisher.PublishFailedAsync(_agent,
                                    NativeExtensions.PiSharpExtensionCompactionReason.Threshold, willRetry: false,
                                    aborted: false, errorMessage: terminalErrorMessage,
                                    cancellationToken: cancellationToken,
                                    fromExtension: _compactionCoordinator.LastCompactionFromExtension)
                                    .ConfigureAwait(false);
                        }
                        throw;
                    }
                }
                if (EstimateNextContext(estimatedPrompt) > contextTriggerTokens.Value)
                    throw new InvalidOperationException("Estimated context still exceeds the configured budget; shorten the prompt or increase the model context window.");
            }
            if (durable is not null)
            {
                await durable.StartAsync(prompt, cancellationToken);
                started = true;
            }
            var attemptStartHead = Conversation.Tree.HeadId;
            var attemptStartPathLength = Conversation.Tree.ActivePath().Count;
            // Persist the system/tool section (and loadout) before the prompt so the transcript
            // precedes the first user message, matching Pi's session ordering.
            lock (_runtimeStateGate) _toolState.BeginRun(_execution);
            var inputMessages = promptMessage is not null ? [promptMessage] : acceptedPromptMessages ?? [];
            if (inputMessages.Count > 0)
            {
                var originalInputMessages = inputMessages.ToArray();
                var acceptedInputMessages = new List<ChatMessage>(inputMessages.Count);
                async Task PersistInputMessagesAsync(IReadOnlyList<ChatMessage> messages)
                {
                    var previousHead = Conversation.Tree.HeadId;
                    foreach (var inputMessage in messages) Conversation.Append(inputMessage);
                    try
                    {
                        if (promptMessage is null)
                        {
                            var history = Conversation.ContextMessages();
                            _execution = await RestoreExecutionAsync(history, cancellationToken);
                            _historyCount = history.Count;
                        }
                        if (_save is not null) await _save(CancellationToken.None);
                    }
                    catch
                    {
                        Conversation.Tree.Select(previousHead);
                        var history = Conversation.ContextMessages();
                        _execution = await RestoreExecutionAsync(history, CancellationToken.None);
                        _historyCount = history.Count;
                        throw;
                    }
                }

                try
                {
                    foreach (var originalMessage in inputMessages)
                    {
                        var promptImages = originalMessage.Contents?.OfType<DataContent>().ToArray();
                        var messageText = promptMessage is not null ? prompt : originalMessage.Text ?? string.Empty;
                        onEvent?.Invoke(new("prompt_accepted", Text: messageText)
                        {
                            Images = promptImages is { Length: > 0 } ? promptImages : null,
                            PromptMessage = originalMessage,
                            MessageTimestamp = DateTimeOffset.UtcNow,
                            RunStartHead = attemptStartHead,
                            RunStartPathLength = attemptStartPathLength
                        });

                        if (!nativeTurnOpen && !nativeTurnStartDispatched)
                        {
                            await _agent.ExtensionEvents.DispatchTurnStartAsync(new(
                                _agent.ExtensionTurnIndex, DateTimeOffset.UtcNow), cancellationToken,
                                item => onEvent?.Invoke(item)).ConfigureAwait(false);
                            nativeTurnStartDispatched = true;
                        }

                        var snapshot = PiSharpExtensionMessageMapper.Snapshot(originalMessage);
                        await _agent.ExtensionEvents.DispatchMessageStartAsync(new(snapshot), cancellationToken).ConfigureAwait(false);
                        var finalized = await _agent.ExtensionEvents.DispatchMessageEndAsync(snapshot,
                            cancellationToken).ConfigureAwait(false);
                        acceptedInputMessages.Add(PiSharpExtensionMessageMapper.Restore(finalized));
                    }
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    acceptedInputMessages.Clear();
                    acceptedInputMessages.AddRange(originalInputMessages);
                    if (promptMessage is not null) promptMessage = acceptedInputMessages[0];
                    await PersistInputMessagesAsync(acceptedInputMessages).ConfigureAwait(false);
                    throw;
                }
                if (promptMessage is not null) promptMessage = acceptedInputMessages[0];
                await PersistInputMessagesAsync(acceptedInputMessages).ConfigureAwait(false);
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
                // Each input was published before its lifecycle callbacks so agent_start precedes
                // message_start; the accepted replacement is persisted and passed to MAF below.
            }
            virtualContext = new VirtualModelContextCoordinator(Conversation, _agent,
                (reason, willRetry, token) => _compactionCoordinator.CompactAsync(null, token,
                    adoptExecutionHistory: false, reason, willRetry),
                () => _compactionCoordinator.LastCompactionFromExtension,
                () => _compactionCoordinator.LastCompactionCancelledByExtension,
                () => AutoCompactionEnabled, Observe, () =>
                {
                    expectedProviderPrefix ??= _agent.GetHistory(_execution).Take(_historyCount)
                        .Select(message => JsonSerializer.SerializeToElement(message, AIJsonUtilities.DefaultOptions)).ToArray();
                });
            _agent.SetVirtualModelContextPreparation(virtualContext.PrepareAsync);
            var inFlightBudget = !AutoCompactionEnabled || _autoCompaction is null || _autoCompaction.TriggerTokens <= 0
                ? null : new InFlightContextBudget(
                () => _autoCompaction ?? throw new InvalidOperationException("In-flight compaction policy was removed."),
                (messages, token) => _agent.SummarizeAsync(messages, null, token),
                async (summary, token) =>
                {
                    Conversation.MarkInFlightProjection();
                    foreach (var record in CompactionUsageAccounting.Records(summary, CurrentModel, _pricing))
                        Conversation.AppendUsage(record);
                    if (_save is not null) await _save(token);
                    onEvent?.Invoke(new("context_compacted_in_flight", Text: "Continuation request summarized; canonical history was not changed."));
                });
            async Task<IReadOnlyList<ChatMessage>> ProjectRequestContextAsync(
                IReadOnlyList<ChatMessage> messages, bool force, CancellationToken token)
            {
                if (inFlightBudget is null)
                    return messages;
                async Task<IReadOnlyList<ChatMessage>> ProjectInFlightBudgetAsync(
                    IReadOnlyList<ChatMessage> requestMessages, bool forceProjection, CancellationToken projectionToken)
                {
                    try
                    {
                        return await inFlightBudget.ProjectAsync(requestMessages, forceProjection, projectionToken)
                            .ConfigureAwait(false);
                    }
                    catch
                    {
                        Interlocked.Exchange(ref _failedInFlightContextProjection, 1);
                        throw;
                    }
                }
                var keepRecentTokens = _autoCompaction?.KeepRecentTokens ?? _keepRecentTokens;
                if (!force || !HasSessionBeforeCompactHandlers ||
                    ConversationCompactionCoordinator.PreparePlan(Conversation, keepRecentTokens) is null)
                    return await ProjectInFlightBudgetAsync(messages, force, token).ConfigureAwait(false);

                var instructionPrefix = messages.TakeWhile(message => message.Role == ChatRole.System ||
                    message.Role == new ChatRole("developer")).ToArray();
                var requestHistory = messages.Skip(instructionPrefix.Length).ToArray();
                var canonicalHistory = Conversation.ContextMessages();
                if (requestHistory.Length < canonicalHistory.Count || Enumerable.Range(0, canonicalHistory.Count).Any(index =>
                    !JsonElement.DeepEquals(
                        JsonSerializer.SerializeToElement(ChatMessageProperties.WithoutRequestAttribution(requestHistory[index]), AIJsonUtilities.DefaultOptions),
                        JsonSerializer.SerializeToElement(ChatMessageProperties.WithoutRequestAttribution(canonicalHistory[index]), AIJsonUtilities.DefaultOptions))))
                    return await ProjectInFlightBudgetAsync(messages, force, token).ConfigureAwait(false);

                expectedProviderPrefix ??= _agent.GetHistory(_execution).Take(_historyCount)
                    .Select(message => JsonSerializer.SerializeToElement(message, AIJsonUtilities.DefaultOptions)).ToArray();
                Observe(new AgentLifecycleEvent("compaction_start") { CompactionReason = "overflow" });
                var compactionCompleted = false;
                var completionPublished = false;
                ConversationCompactionResult? compactedResult = null;
                try
                {
                    compactedResult = await _compactionCoordinator.CompactAsync(null, token,
                        adoptExecutionHistory: false, NativeExtensions.PiSharpExtensionCompactionReason.Overflow,
                        willRetry: true).ConfigureAwait(false);
                    if (compactedResult is null)
                        return await ProjectInFlightBudgetAsync(messages, force, token).ConfigureAwait(false);

                    physicalOverflowCompacted = true;
                    compactionCompleted = true;
                    await NativeExtensionCompactionEventPublisher.PublishCompletedAsync(_agent, Conversation,
                        compactedResult, NativeExtensions.PiSharpExtensionCompactionReason.Overflow,
                        willRetry: true, token).ConfigureAwait(false);
                    Observe(new AgentLifecycleEvent("compaction_end")
                    {
                        CompactionReason = "overflow",
                        CompactionResult = compactedResult,
                        CompactionWillRetry = true
                    });
                    completionPublished = true;
                    return instructionPrefix.Concat(Conversation.ContextMessages())
                        .Concat(requestHistory.Skip(canonicalHistory.Count)).ToArray();
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    if (!compactionCompleted)
                    {
                        Observe(new AgentLifecycleEvent("compaction_end")
                        {
                            CompactionReason = "overflow",
                            CompactionAborted = true,
                            CompactionWillRetry = false
                        });
                        await NativeExtensionCompactionEventPublisher.PublishAbortedAsync(_agent,
                            NativeExtensions.PiSharpExtensionCompactionReason.Overflow,
                            _compactionCoordinator.LastCompactionFromExtension).ConfigureAwait(false);
                    }
                    else if (!completionPublished)
                    {
                        Observe(new AgentLifecycleEvent("compaction_end")
                        {
                            CompactionReason = "overflow",
                            CompactionResult = compactedResult,
                            CompactionWillRetry = true
                        });
                    }
                    throw;
                }
                catch (Exception error)
                {
                    if (!compactionCompleted)
                    {
                        var abortedByExtension = _compactionCoordinator.LastCompactionCancelledByExtension;
                        Observe(abortedByExtension
                            ? new AgentLifecycleEvent("compaction_end")
                            {
                                CompactionReason = "overflow",
                                CompactionAborted = true,
                                CompactionWillRetry = false
                            }
                            : new AgentLifecycleEvent("compaction_end", Error: "Context overflow recovery failed: " + error.Message)
                            {
                                CompactionReason = "overflow",
                                CompactionWillRetry = false
                            });
                        if (abortedByExtension)
                            await NativeExtensionCompactionEventPublisher.PublishAbortedAsync(_agent,
                                NativeExtensions.PiSharpExtensionCompactionReason.Overflow).ConfigureAwait(false);
                        else
                            await NativeExtensionCompactionEventPublisher.PublishFailedWithinDeadlineAsync(_agent,
                                NativeExtensions.PiSharpExtensionCompactionReason.Overflow, willRetry: false,
                                errorMessage: "Context overflow recovery failed: " + error.Message,
                                fromExtension: _compactionCoordinator.LastCompactionFromExtension).ConfigureAwait(false);
                    }
                    else if (!completionPublished)
                    {
                        Observe(new AgentLifecycleEvent("compaction_end")
                        {
                            CompactionReason = "overflow",
                            CompactionResult = compactedResult,
                            CompactionWillRetry = true
                        });
                    }
                    throw;
                }
            }

            var projectRequestContext = inFlightBudget is null
                ? null
                : (Func<IReadOnlyList<ChatMessage>, bool, CancellationToken, Task<IReadOnlyList<ChatMessage>>>)ProjectRequestContextAsync;
            _agent.SetTurnBoundaryPreparation(PrepareTurnAsync);
            var codexSessionId = _providerApi == "openai-codex-responses" ? Conversation.Id : null;
            var updates = promptMessage is null
                ? _agent.RunStreamingContinuationDurableAsync(_execution, cancellationToken, durable,
                    Observe, (first, token) => TakeSteeringForProviderAsync(first, retryContinuation, token), projectRequestContext,
                    CreateBashSessionEnvironment(), codexSessionId, TakePendingProviderContextMessagesAsync, CreateBashSessionEnvironment)
                : _agent.RunStreamingDurableAsync(promptMessage, _execution, cancellationToken, durable,
                    Observe, (first, token) => TakeSteeringForProviderAsync(first, retryContinuation, token), projectRequestContext,
                    CreateBashSessionEnvironment(), codexSessionId, TakePendingProviderContextMessagesAsync, CreateBashSessionEnvironment);
            await foreach (var update in attemptStream.PumpAsync(updates, durable,
                usage => Conversation.AppendUsage(attribution.Usage(usage)), cancellationToken))
                yield return update;
            ProviderTurnSnapshot lastTurn;
            lock (_runtimeStateGate) lastTurn = CompleteProviderTurnUnsafe();
            await CompleteNativeTurnAsync(lastTurn, aborted: false, errorMessage: null).ConfigureAwait(false);
            completed = true;
            if (!LengthStopRecoveryCoordinator.IsRecoverable(
                Conversation, latestCompletedProviderResponse, GetCurrentRequestModel))
                await CompactAfterCompletedTurnAsync(cancellationToken, onEvent, latestCompletedProviderUsage)
                    .ConfigureAwait(false);
        }
        finally
        {
            try
            {
                _agent.SetVirtualModelContextPreparation(null);
                _agent.SetTurnBoundaryPreparation(null);
                var history = _agent.GetHistory(_execution);
                if (history.Count < _historyCount) throw new InvalidDataException("MAF discarded canonical conversation history.");
                var existing = Conversation.ContextMessages();
                var hasCanonicalCompaction = virtualContext?.HasCompacted == true || physicalOverflowCompacted;
                var expectedPrefix = hasCanonicalCompaction && _boundaryContextVersion == initialBoundaryContextVersion
                    ? expectedProviderPrefix! :
                    existing.Take(_historyCount).Select(message => JsonSerializer.SerializeToElement(message, AIJsonUtilities.DefaultOptions)).ToArray();
                if (expectedPrefix.Count < _historyCount || Enumerable.Range(0, _historyCount).Any(index =>
                    !JsonElement.DeepEquals(expectedPrefix[index],
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
                    providerTurns.RestoreMissingMessages(appendedHistory);
                lock (_runtimeStateGate)
                {
                    PersistPendingRuntimeChangesUnsafe();
                    var canonicalTail = (hasCanonicalCompaction ? Conversation.ActiveMessages() : existing.Skip(historyStartIndex)).ToList();
                    providerTurns.AppendMissingMessages(appendedHistory, canonicalTail, existing);
                }
                var canonicalHistory = Conversation.ContextMessages();
                if (canonicalHistory.Count != history.Count || canonicalHistory.Where((message, index) => !JsonElement.DeepEquals(
                    JsonSerializer.SerializeToElement(message, AIJsonUtilities.DefaultOptions),
                    JsonSerializer.SerializeToElement(history[index], AIJsonUtilities.DefaultOptions))).Any())
                    _execution = await RestoreExecutionAsync(canonicalHistory, CancellationToken.None);
                _historyCount = canonicalHistory.Count;
                if (!completed && accepted)
                {
                    // Pi persists the assistant of a failed or interrupted provider request (keeping
                    // partial content when there is any), plus the trailing error assistant of the
                    // aborted continuation after a tool round, so the next provider request sees the
                    // closed turn.
                    var aborted = attemptStream.InterruptionType == "turn_interrupted";
                    var trailingAfterTools = aborted && providerTurns.SnapshotTurn().ToolResults.Count > 0;
                    var partialText = providerTurns.PartialAssistantText.ToString();
                    var failedAssistant = new ChatMessage(ChatRole.Assistant, trailingAfterTools ? "" : partialText);
                    var failedProperties = new AdditionalPropertiesDictionary
                    {
                        ["pisharp.stopReason"] = aborted && !trailingAfterTools ? "aborted" : "error",
                        ["pisharp.errorMessage"] = attemptStream.InterruptionError ?? ""
                    };
                    failedAssistant.AdditionalProperties = failedProperties;
                    Conversation.Append(failedAssistant);
                    Conversation.Tree.Append("interrupted", JsonSerializer.SerializeToElement(
                        new
                        {
                            prompt,
                            partialText = attemptStream.PartialText.ToString(),
                            partialAssistantText = providerTurns.PartialAssistantText.ToString(),
                            events = attemptStream.Events,
                            terminalType = attemptStream.InterruptionType,
                            stopReason = attemptStream.InterruptionType == "turn_interrupted" ? "aborted" : "error",
                            errorMessage = attemptStream.InterruptionError,
                            provider = attribution.Provider,
                            model = attribution.Model,
                            timestamp = DateTimeOffset.UtcNow
                        }));
                    var interruptedHistory = Conversation.ContextMessages();
                    _execution = await RestoreExecutionAsync(interruptedHistory, CancellationToken.None);
                    _historyCount = interruptedHistory.Count;
                }
                if (started) await durable!.FinishAsync(completed,
                    aborted: !completed && attemptStream.InterruptionType == "turn_interrupted");
                if (nativeTurnOpen)
                {
                    var turn = providerTurns.SnapshotTurn();
                    var aborted = !completed && attemptStream.InterruptionType == "turn_interrupted";
                    await CompleteNativeTurnAsync(turn, aborted, !completed ? attemptStream.InterruptionError : null)
                        .ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
                attemptStream.InterruptionType = "turn_interrupted";
                attemptStream.InterruptionError = "Request was aborted";
                throw;
            }
            catch (Exception error)
            {
                attemptStream.InterruptionType = "turn_failed";
                attemptStream.InterruptionError = error.Message;
                throw;
            }
            finally
            {
                s_currentGateOwner.Value = previousGateOwner;
                _gate.Release();
            }
        }
    }

    private int EstimateNextContext(string prompt)
    {
        var measured = Conversation.LatestContextUsageTokens();
        if (measured is null) return AutoCompactionPolicy.Estimate(Conversation.ContextMessages(), prompt);
        var pending = (prompt.Length + 1L) / 2 + 64;
        return (int)Math.Min(int.MaxValue, measured.Value + pending);
    }

    private Task CompactAfterFailedTurnAsync(CancellationToken cancellationToken,
        Action<AgentLifecycleEvent>? publish)
    {
        var projection = Conversation.ProjectContextUsageForEstimate();
        var estimate = projection.HasValidAssistantUsage && !projection.IsInvalidated
            ? _agent.EstimateContextTokens(projection.UsageTokens, projection.TrailingMessages)
            : _agent.EstimateContextTokens(Conversation.ContextMessages());
        return CompactAfterCompletedTurnAsync(cancellationToken, publish, latestProviderUsage: null,
            contextEstimateOverride: estimate);
    }

    private async Task CompactAfterCompletedTurnAsync(CancellationToken cancellationToken,
        Action<AgentLifecycleEvent>? publish, UsageRecord? latestProviderUsage,
        int? contextEstimateOverride = null)
    {
        var policy = _autoCompaction;
        var activePath = Conversation.Tree.ActivePath();
        var latestCompactionIndex = activePath.ToList().FindLastIndex(entry => entry.Type == "compaction");
        var hasTransientProjection = activePath.Skip(latestCompactionIndex + 1)
            .Any(entry => entry.Type == "context_projection");
        var silentOverflow = !hasTransientProjection && policy is { ContextWindowTokens: > 0 } &&
            latestProviderUsage?.InputTokenCountIncludingCached is long reportedInput &&
            reportedInput > policy.ContextWindowTokens;
        var reason = silentOverflow
            ? NativeExtensions.PiSharpExtensionCompactionReason.Overflow
            : NativeExtensions.PiSharpExtensionCompactionReason.Threshold;
        var reasonText = silentOverflow ? "overflow" : "threshold";
        if (hasTransientProjection || !AutoCompactionEnabled || policy is null || policy.TriggerTokens <= 0 ||
            (!silentOverflow && (contextEstimateOverride ?? EstimateNextContext("")) <= policy.TriggerTokens) ||
            ConversationCompactionCoordinator.PreparePlan(Conversation,
                policy.KeepRecentTokens ?? _keepRecentTokens) is null)
            return;

        publish?.Invoke(new AgentLifecycleEvent("compaction_start") { CompactionReason = reasonText });
        ConversationCompactionResult? result = null;
        try
        {
            result = await CompactCoreAsync(null, cancellationToken, reason).ConfigureAwait(false);
            if (result is null)
            {
                var unavailable = "Auto-compaction failed: " + CompactionUnavailableError();
                publish?.Invoke(new AgentLifecycleEvent("compaction_end", Error: unavailable)
                {
                    CompactionReason = reasonText,
                    CompactionWillRetry = false
                });
                await NativeExtensionCompactionEventPublisher.PublishFailedAsync(_agent, reason,
                    willRetry: false, aborted: false, errorMessage: unavailable, cancellationToken).ConfigureAwait(false);
                return;
            }

            await NativeExtensionCompactionEventPublisher.PublishCompletedAsync(_agent, Conversation, result,
                reason, willRetry: false, cancellationToken).ConfigureAwait(false);
            publish?.Invoke(new AgentLifecycleEvent("compaction_end")
            {
                CompactionReason = reasonText,
                CompactionResult = result,
                CompactionWillRetry = false
            });
            publish?.Invoke(new AgentLifecycleEvent("context_compacted",
                Text: "Automatic context summary saved; raw history retained."));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            publish?.Invoke(new AgentLifecycleEvent("compaction_end")
            {
                CompactionReason = reasonText,
                CompactionResult = result,
                CompactionAborted = result is null,
                CompactionWillRetry = false
            });
            if (result is null)
                await NativeExtensionCompactionEventPublisher.PublishAbortedAsync(_agent, reason,
                    _compactionCoordinator.LastCompactionFromExtension).ConfigureAwait(false);
            throw;
        }
        catch (Exception error)
        {
            var abortedByExtension = result is null && _compactionCoordinator.LastCompactionCancelledByExtension;
            publish?.Invoke(abortedByExtension
                ? new AgentLifecycleEvent("compaction_end")
                {
                    CompactionReason = reasonText,
                    CompactionAborted = true,
                    CompactionWillRetry = false
                }
                : new AgentLifecycleEvent("compaction_end", Error: "Auto-compaction failed: " + error.Message)
                {
                    CompactionReason = reasonText,
                    CompactionResult = result,
                    CompactionWillRetry = false
                });
            if (result is null)
            {
                if (abortedByExtension)
                    await NativeExtensionCompactionEventPublisher.PublishAbortedAsync(_agent, reason,
                        _compactionCoordinator.LastCompactionFromExtension).ConfigureAwait(false);
                else
                    await NativeExtensionCompactionEventPublisher.PublishFailedWithinDeadlineAsync(_agent,
                        reason, willRetry: false, errorMessage: "Auto-compaction failed: " + error.Message,
                        fromExtension: _compactionCoordinator.LastCompactionFromExtension).ConfigureAwait(false);
            }
        }
    }

    private LengthStopRecoveryCoordinator CreateLengthStopRecoveryCoordinator(
        Action<AgentLifecycleEvent> publish) => new(
            Conversation,
            _agent,
            _compactionCoordinator,
            () => AutoCompactionEnabled,
            GetCurrentRequestModel,
            CompactionUnavailableError,
            _autoCompaction?.KeepRecentTokens ?? _keepRecentTokens,
            OmitFailedRetryContextAsync,
            async cancellationToken =>
            {
                var history = Conversation.ContextMessages();
                _execution = await RestoreExecutionAsync(history, cancellationToken).ConfigureAwait(false);
                _historyCount = history.Count;
            });

    private (string? MessageEntryId, IReadOnlyList<string> ToolResultEntryIds) FindTurnEntryIds(
        ProviderTurnSnapshot turn)
    {
        var path = Conversation.Tree.ActivePath();
        var startIndex = 0;
        string? messageEntryId = null;
        if (turn.Message is { } message)
        {
            for (var index = path.Count - 1; index >= 0; index--)
            {
                if (path[index].Type != "chat") continue;
                if (!ProviderTurnHistoryReconciler.AreEquivalent(message,
                    ConversationMessageCodec.RestoreEntry(path[index]))) continue;
                messageEntryId = path[index].Id;
                startIndex = index + 1;
                break;
            }
        }

        var toolResultEntryIds = new List<string>();
        foreach (var result in turn.ToolResults)
        {
            for (var index = startIndex; index < path.Count; index++)
            {
                if (path[index].Type != "chat") continue;
                if (!ProviderTurnHistoryReconciler.AreEquivalent(result,
                    ConversationMessageCodec.RestoreEntry(path[index]))) continue;
                toolResultEntryIds.Add(path[index].Id);
                startIndex = index + 1;
                break;
            }
        }
        return (messageEntryId, Array.AsReadOnly(toolResultEntryIds.ToArray()));
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
            var partialText = PiJsonlEntryCodec.StringProperty(interrupted.Payload, "partialAssistantText");
            if (!string.IsNullOrEmpty(partialText)) omittedIds.Add(interrupted.Id);
            matchingAssistant = string.IsNullOrEmpty(partialText) ? -1 : Array.FindLastIndex(attemptEntries,
                node =>
                {
                    if (node.Type != "chat") return false;
                    var message = ConversationMessageCodec.RestoreEntry(node);
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
                    if (entry.Type != "chat" || ConversationMessageCodec.RestoreEntry(entry).Role != ChatRole.Tool) break;
                    omittedIds.Add(entry.Id);
                }
            }
        }
        else
        {
            var chatEntries = attemptEntries.Where(node => node.Type == "chat").ToArray();
            var failedAssistant = Array.FindLastIndex(chatEntries,
                node => ConversationMessageCodec.RestoreEntry(node).Role == ChatRole.Assistant);
            if (failedAssistant >= 0)
            {
                omittedIds.Add(chatEntries[failedAssistant].Id);
                for (var index = failedAssistant + 1; index < chatEntries.Length; index++)
                {
                    var message = ConversationMessageCodec.RestoreEntry(chatEntries[index]);
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
                    PiJsonlEntryCodec.ProjectEntry(Conversation, entry))
            });
    }

    private IReadOnlyDictionary<string, string?> CreateBashSessionEnvironment()
    {
        lock (_runtimeStateGate)
        {
            var environment = new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["PI_SESSION_ID"] = Conversation.Id,
                ["PI_MODEL"] = Conversation.Model
            };
            var provider = _currentProvider ?? _provider ?? Conversation.Provider;
            if (provider is not null) environment["PI_PROVIDER"] = provider;
            if (_sessionFile is not null) environment["PI_SESSION_FILE"] = _sessionFile;
            var reasoningLevel = Volatile.Read(ref _reasoningLevel);
            if (reasoningLevel is not null) environment["PI_REASONING_LEVEL"] = reasoningLevel;
            return environment;
        }
    }

    private static AgentLifecycleEvent UsageEvent(UsageRecord usage) => new("usage",
        Text: (usage.TotalTokens is long totalTokens ? $"{totalTokens} tokens" : "token usage unavailable") +
            (usage.Cost is null ? "" : $" · ${usage.Cost:0.######}"),
        InputTokens: usage.InputTokens, OutputTokens: usage.OutputTokens,
        CachedInputTokens: usage.CachedInputTokens, ReasoningTokens: usage.ReasoningTokens,
        TotalTokens: usage.TotalTokens, Cost: usage.Cost, CachedWriteTokens: usage.CachedWriteTokens);
}
