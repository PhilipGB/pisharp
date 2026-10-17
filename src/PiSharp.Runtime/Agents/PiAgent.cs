using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using PiSharp.Runtime.Extensions;
using PiSharp.Runtime.Codemode;
using PiSharp.Runtime.Providers;
using PiSharp.Runtime.Resources;
using PiSharp.Runtime.Sessions;
using PiSharp.Runtime.Tools;
namespace PiSharp.Runtime;

/// <summary>One shared streaming runtime for terminal and one-shot invocation.</summary>
public sealed class PiAgent
{
    private readonly InMemoryChatHistoryProvider _history = new();
    private readonly ICodemodeModels? _codemodeModels;
    private readonly CodingTools _codingTools;
    private readonly ChatClientAgent _agent;
    private readonly PiCompactionSummarizer _summarizer;
    private readonly MutableChatClient _chatClient;
    private readonly RoutedChatClient _routedChatClient;
    private readonly PiSharpToolHookPipeline _toolHooks;
    private readonly AgentToolCatalog _toolCatalog;
    private readonly ExtensionRegistration? _liveExtensionRegistration;
    private readonly ConcurrentDictionary<string, AIFunction> _runtimeToolFunctions = new(StringComparer.Ordinal);
    private readonly Dictionary<string, PiSharpToolRegistration> _runtimeToolRegistrations = new(StringComparer.Ordinal);
    private readonly object _runtimeToolGate = new();
    private readonly string? _customSystemPrompt;
    private readonly string? _appendSystemPrompt;
    private readonly string? _projectInstructions;
    private readonly Func<IReadOnlyList<string>, IReadOnlyList<string>, string>? _getSkillInstructions;
    private readonly PiSharpContextTransformPipeline _contextTransforms;
    private readonly IReadOnlyList<ExtensionLifecycleHandlerRegistration> _extensionLifecycleHandlers;
    private readonly IReadOnlyList<PiSharpBeforeAgentStartHandlerRegistration> _beforeAgentStartHandlers;
    private readonly Func<string?>? _getAdditionalSystemInstructions;
    private readonly PiSharpExtensionContext _extensionContext;
    private readonly SemaphoreSlim _runGate = new(1, 1);
    private ReasoningOptions? _reasoning;
    private readonly ToolBatchTermination _toolBatchTermination = new();
    private DurableExecution? _active;
    private ConversationSession? _extensionConversation;
    private int _isRunning;
    private int _activeConversationRuns;
    private Action<AgentLifecycleEvent>? _events;
    private Func<bool, IReadOnlyList<ChatMessage>>? _takeSteering;
    private Func<CancellationToken, Task<IReadOnlyList<ChatMessage>>>? _takeContextMessagesForRequestAsync;
    private Func<IReadOnlyList<ChatMessage>, bool, CancellationToken, Task<IReadOnlyList<ChatMessage>>>? _projectContext;
    private readonly List<(ChatMessage Message, string? AfterCallId)> _injectedSteering = [];
    private int _providerRequestIndex;
    private int _supportsImages;
    private ModelImageResizeOptions? _selectedImageResizeOptions;
    private long _systemMessageTimestamp;
    private ToolLoadout? _currentToolLoadout;
    private RunSystemPromptOverride? _runSystemPromptOverride;
    private readonly ConditionalWeakTable<AgentSession, ToolDeclarationTranscript> _toolTranscripts = new();
    private ToolDeclarationTranscript? _currentToolTranscript;

    private sealed record RunSystemPromptOverride(string? Text,
        PiSharp.Extensions.Abstractions.PiSharpSystemPromptOptions? BaseOptions = null,
        PiSharp.Extensions.Abstractions.PiSharpSystemPromptOptions? EditedOptions = null,
        string? AdditionalInstructions = null);

    internal ToolDeclarationTranscript GetToolTranscript(AgentSession session) =>
        _toolTranscripts.GetValue(session, _ => new());

    public string SystemInstructions => GetSystemInstructionsForCurrentLoadout();
    internal JsonElement GetSharePresentation(AgentSession session)
    {
        var loadout = GetToolLoadout(session).Snapshot;
        return JsonSerializer.SerializeToElement(new
        {
            systemPrompt = BuildSystemPrompt(loadout.Declared, loadout.ActiveToolNames).Text,
            tools = loadout.Declared.Select(tool => new
            {
                name = tool.Registration.Function.Name,
                description = tool.Description,
                parameters = tool.Registration.Function.JsonSchema
            })
        });
    }
    public IReadOnlyList<AIFunctionDeclaration> ToolDeclarations => _toolCatalog.ToolDeclarations;
    internal bool RestoresRecordedToolLoadout => _toolCatalog.RestoresRecordedToolLoadout;
    /// <summary>Gets the independent tool loadout associated with one agent session.</summary>
    public ToolLoadout GetToolLoadout(AgentSession session) => _toolCatalog.GetToolLoadout(session);

    internal void RestoreToolLoadout(AgentSession session, IEnumerable<string> activeToolNames) =>
        _toolCatalog.RestoreToolLoadout(session, activeToolNames);

    internal void RestoreCodemodeStore(AgentSession session, IReadOnlyDictionary<string, System.Text.Json.JsonElement> values) =>
        GetToolLoadout(session).SetCodemodeStore(values);

    public long? SystemMessageTimestamp => Volatile.Read(ref _systemMessageTimestamp) is var timestamp && timestamp != 0
        ? timestamp
        : null;

    public PiAgent(IChatClient client, CodingTools tools, IReadOnlyList<string>? selectedTools = null, IReadOnlyList<string>? excludedTools = null, bool noTools = false, string? contextInstructions = null, string? systemPrompt = null, string? appendSystemPrompt = null,
        IReadOnlyCollection<AIFunction>? extensionTools = null, ProviderRetryPolicy? retryPolicy = null,
        ReasoningOptions? reasoning = null, bool blockImages = false, bool noBuiltinTools = false, bool supportsImages = true,
        IReadOnlyCollection<PiSharpToolRegistration>? extensionToolRegistrations = null,
        IReadOnlyList<PiSharpToolCallHook>? extensionToolCallHooks = null,
        IReadOnlyList<PiSharpToolResultHook>? extensionToolResultHooks = null,
        ExtensionRegistration? liveExtensionRegistration = null,
        VirtualModelRequestRouter? virtualModelRequestRouter = null, ICodemodeModels? codemodeModels = null,
        Func<string?>? getAdditionalSystemInstructions = null,
        IReadOnlyList<PiSharpContextTransform>? extensionContextTransforms = null,
        Func<IReadOnlyList<string>, IReadOnlyList<string>, string>? getSkillInstructions = null,
        IReadOnlyList<string>? initialActiveToolNames = null,
        IReadOnlyList<ExtensionLifecycleHandlerRegistration>? extensionLifecycleHandlers = null,
        PiSharpExtensionContextConfiguration? extensionContextConfiguration = null,
        IReadOnlyDictionary<string, int>? thinkingBudgets = null,
        IReadOnlyList<PiSharpBeforeAgentStartHandlerRegistration>? beforeAgentStartHandlers = null)
    {
        _contextTransforms = new(extensionContextTransforms);
        _extensionLifecycleHandlers = extensionLifecycleHandlers?.ToArray() ?? [];
        _beforeAgentStartHandlers = beforeAgentStartHandlers?.ToArray() ?? [];
        _liveExtensionRegistration = liveExtensionRegistration;
        _getAdditionalSystemInstructions = getAdditionalSystemInstructions;
        _extensionContext = new(extensionContextConfiguration ?? new PiSharpExtensionContextConfiguration(
                tools.WorkingDirectory, "print", false, false, UnavailableProjectTrustExtensionUi.Instance),
            () => Volatile.Read(ref _extensionConversation),
            () => Volatile.Read(ref _currentToolLoadout)?.Snapshot.ActiveToolNames.ToArray() ?? [],
            () => Volatile.Read(ref _isRunning) == 0 && Volatile.Read(ref _activeConversationRuns) == 0);
        _codemodeModels = codemodeModels;
        _codingTools = tools;
        _customSystemPrompt = systemPrompt;
        _appendSystemPrompt = appendSystemPrompt;
        _projectInstructions = contextInstructions;
        _getSkillInstructions = getSkillInstructions;
        _selectedImageResizeOptions = tools.ImageResizeOptions;
        _toolHooks = new PiSharpToolHookPipeline(extensionToolCallHooks, extensionToolResultHooks);
        _chatClient = new MutableChatClient(client);
        _routedChatClient = new RoutedChatClient(_chatClient, PublishEvent, route => SetPhysicalImageLimits(route.Model));
        _routedChatClient.SetRouter(virtualModelRequestRouter);
        _supportsImages = supportsImages ? 1 : 0;
        _reasoning = reasoning;
        _summarizer = new PiCompactionSummarizer(_routedChatClient);
        _toolCatalog = new AgentToolCatalog(tools, selectedTools, excludedTools, noTools, noBuiltinTools,
            extensionTools, extensionToolRegistrations, initialActiveToolNames);
        lock (_runtimeToolGate)
        {
            foreach (var registration in _toolCatalog.Registrations)
                UpdateRuntimeToolFunction(registration);
        }
        if (liveExtensionRegistration is not null)
        {
            var weakAgent = new WeakReference<PiAgent>(this);
            Action<IReadOnlyCollection<PiSharpToolRegistration>>? handler = null;
            handler = definitions =>
            {
                if (weakAgent.TryGetTarget(out var agent)) agent.RefreshExtensionTools(definitions);
                else liveExtensionRegistration.ToolDefinitionsChanged -= handler;
            };
            liveExtensionRegistration.ToolDefinitionsChanged += handler;
        }
        var initialLoadout = _toolCatalog.CreateLoadout().Snapshot;
        var initialSystemInstructions = BuildSystemPrompt(initialLoadout.Declared,
            initialLoadout.ActiveToolNames).Text;
        _agent = new ChatClientAgent(new ObservedChatClient(_routedChatClient, PublishEvent,
            retryPolicy ?? ProviderRetryPolicy.Default, TakeSteeringForRequest, blockImages, ProjectForRequestAsync,
            supportsImages, () => Volatile.Read(ref _reasoning), () => _routedChatClient.HasRouter || Volatile.Read(ref _supportsImages) != 0,
            _routedChatClient, GetToolsForRequest, _routedChatClient, GetAdditionalSystemInstructionsForCurrentTurn,
            _contextTransforms.ApplyAsync, (messages, options) =>
                _currentToolTranscript?.Project(messages, options), GetSystemInstructionsForCurrentLoadout,
            () => thinkingBudgets, TakeContextMessagesForRequestAsync), new ChatClientAgentOptions
            {
                Name = "PiSharp",
                ChatHistoryProvider = _history,
                AllowConcurrentInvocation = true,
                ChatOptions = new ChatOptions
                {
                    Instructions = initialSystemInstructions,
                    Tools = [],
                    Reasoning = reasoning
                }
            });
    }

    private IReadOnlyList<ChatMessage> TakeSteeringForRequest(IEnumerable<ChatMessage> messages)
    {
        var firstProviderRequest = _providerRequestIndex++ == 0;
        var steering = _takeSteering?.Invoke(firstProviderRequest) ?? [];
        if (steering.Count == 0) return steering;
        var afterCallId = messages.SelectMany(message => message.Contents).OfType<FunctionResultContent>()
            .LastOrDefault()?.CallId;
        _injectedSteering.AddRange(steering.Select(message => (message, afterCallId)));
        return steering;
    }

    private Task<IReadOnlyList<ChatMessage>> TakeContextMessagesForRequestAsync(CancellationToken cancellationToken) =>
        _takeContextMessagesForRequestAsync?.Invoke(cancellationToken) ?? Task.FromResult<IReadOnlyList<ChatMessage>>([]);

    private IReadOnlyList<AITool> GetToolsForRequest()
    {
        var loadout = Volatile.Read(ref _currentToolLoadout) ??
            _toolCatalog.CreateLoadout();
        var declarations = loadout.GetDeclarationsForRequest();
        return FunctionsFor(declarations);
    }

    private IReadOnlyList<AITool> GetToolsForContextEstimate()
    {
        var loadout = Volatile.Read(ref _currentToolLoadout) ??
            _toolCatalog.CreateLoadout();
        return FunctionsFor(loadout.Snapshot.TranscriptDeclarations);
    }

    private IReadOnlyList<AITool> FunctionsFor(IEnumerable<PiSharpToolDeclaration> declarations) =>
        Array.AsReadOnly<AITool>(declarations.Select(declaration =>
        {
            var function = _runtimeToolFunctions[declaration.Registration.Function.Name];
            return string.Equals(declaration.Description, function.Description, StringComparison.Ordinal) &&
                declaration.Registration.ConstrainedSampling is null
                ? function
                : new DeclaredAIFunction(function, declaration.Description, declaration.Registration.ConstrainedSampling);
        }).ToArray());

    private string GetSystemInstructionsForCurrentLoadout()
    {
        var runOverride = Volatile.Read(ref _runSystemPromptOverride);
        if (runOverride is null) return GetSystemPromptForCurrentLoadout().Text;
        if (runOverride.Text is { } text) return text;

        var currentBase = GetSystemPromptOptionsForCurrentLoadout();
        var options = PiSystemPromptBuilder.ApplyChanges(runOverride.BaseOptions!, runOverride.EditedOptions!, currentBase);
        return RenderSystemPrompt(options, runOverride.AdditionalInstructions);
    }

    private string? GetAdditionalSystemInstructionsForCurrentTurn() =>
        Volatile.Read(ref _runSystemPromptOverride) is null
            ? _getAdditionalSystemInstructions?.Invoke()
            : null;

    internal async Task<IReadOnlyList<PiSharpCustomMessage>> PrepareBeforeAgentStartAsync(ChatMessage promptMessage,
        string promptText, Action<AgentLifecycleEvent> publish, CancellationToken cancellationToken,
        AgentSession session,
        PiSharpCustomMessage? startingCustomMessage = null)
    {
        ClearRunSystemPromptOverride();
        if (_beforeAgentStartHandlers.Count == 0) return [];

        var loadout = GetToolLoadout(session);
        var originalOptions = GetSystemPromptOptions(loadout.Snapshot);
        var systemPromptOptions = PiSystemPromptBuilder.CloneOptions(originalOptions);
        var additionalInstructions = _getAdditionalSystemInstructions?.Invoke();
        string? replacedSystemPrompt = null;

        var images = promptMessage.Contents.OfType<DataContent>()
            .Where(content => content.MediaType?.StartsWith("image/", StringComparison.OrdinalIgnoreCase) == true)
            .ToArray();
        var imageView = Array.AsReadOnly(images);
        var injectedMessages = new List<PiSharpCustomMessage>();
        foreach (var registration in _beforeAgentStartHandlers)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var result = await registration.Handler(
                    new(promptText, imageView,
                        replacedSystemPrompt ?? RenderSystemPrompt(systemPromptOptions, additionalInstructions),
                        startingCustomMessage is null ? PiSharpTurnMessageType.User : PiSharpTurnMessageType.Custom,
                        startingCustomMessage)
                    {
                        SystemPromptOptions = systemPromptOptions
                    }, _extensionContext, cancellationToken);
                if (result?.SystemPrompt is { } systemPrompt)
                    replacedSystemPrompt = systemPrompt;
                if (result?.InjectedMessage is { } injectedMessage)
                    injectedMessages.Add(injectedMessage with
                    {
                        Source = injectedMessage.Source ?? (registration.SourceInfo.Path.Length > 0
                            ? registration.SourceInfo.Path : null)
                    });
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception error)
            {
                publish(new AgentLifecycleEvent("extension_error", Error: error.Message,
                    Details: new { extensionPath = registration.SourceInfo.Path, eventType = "before_agent_start" }));
            }
        }

        if (replacedSystemPrompt is not null)
        {
            Volatile.Write(ref _runSystemPromptOverride, new(replacedSystemPrompt));
        }
        else
        {
            var currentBase = GetSystemPromptOptions(loadout.Snapshot);
            var finalOptions = PiSystemPromptBuilder.ApplyChanges(originalOptions, systemPromptOptions, currentBase);
            if (!PiSystemPromptBuilder.SectionsEqual(originalOptions, finalOptions))
                Volatile.Write(ref _runSystemPromptOverride, new(null,
                    PiSystemPromptBuilder.CloneOptions(currentBase), PiSystemPromptBuilder.CloneOptions(finalOptions),
                    additionalInstructions));
        }
        return injectedMessages;
    }

    internal void ClearRunSystemPromptOverride() => Volatile.Write(ref _runSystemPromptOverride, null);

    internal void BeginConversationRunActivity() => Interlocked.Increment(ref _activeConversationRuns);

    internal void EndConversationRunActivity() => Interlocked.Decrement(ref _activeConversationRuns);

    private PiSystemPromptBuilder.Projection GetSystemPromptForCurrentLoadout()
    {
        var loadout = Volatile.Read(ref _currentToolLoadout)?.Snapshot ??
            _toolCatalog.CreateLoadout().Snapshot;
        return BuildSystemPrompt(loadout.Declared, loadout.ActiveToolNames);
    }

    private PiSharp.Extensions.Abstractions.PiSharpSystemPromptOptions GetSystemPromptOptionsForCurrentLoadout()
    {
        var loadout = Volatile.Read(ref _currentToolLoadout)?.Snapshot ??
            _toolCatalog.CreateLoadout().Snapshot;
        return GetSystemPromptOptions(loadout);
    }

    private PiSharp.Extensions.Abstractions.PiSharpSystemPromptOptions GetSystemPromptOptions(
        ToolLoadoutSnapshot loadout) => PiSystemPromptBuilder.CreateOptions(_codingTools.WorkingDirectory, loadout.Declared,
            _customSystemPrompt, _appendSystemPrompt, _projectInstructions,
            _getSkillInstructions?.Invoke(loadout.ActiveToolNames,
                loadout.Declared.Select(declaration => declaration.Registration.Function.Name).ToArray()));

    private static string RenderSystemPrompt(PiSharp.Extensions.Abstractions.PiSharpSystemPromptOptions options,
        string? additionalInstructions)
    {
        var prompt = PiSystemPromptBuilder.BuildProjection(options).Text;
        return string.IsNullOrWhiteSpace(additionalInstructions)
            ? prompt
            : prompt + "\n\n" + additionalInstructions;
    }

    private PiSystemPromptBuilder.Projection BuildSystemPrompt(IReadOnlyList<PiSharpToolDeclaration> declarations,
        IReadOnlyList<string> activeToolNames) => PiSystemPromptBuilder.BuildProjection(
        PiSystemPromptBuilder.CreateOptions(_codingTools.WorkingDirectory, declarations, _customSystemPrompt,
            _appendSystemPrompt, _projectInstructions,
            _getSkillInstructions?.Invoke(activeToolNames,
                declarations.Select(declaration => declaration.Registration.Function.Name).ToArray())));

    private void RefreshExtensionTools(IReadOnlyCollection<PiSharpToolRegistration> definitions)
    {
        lock (_runtimeToolGate)
        {
            // Publish executable wrappers before catalog changes become visible to existing loadouts.
            _toolCatalog.ReplaceExtensions(definitions, registrations =>
            {
                foreach (var registration in registrations) UpdateRuntimeToolFunction(registration);
            });
        }
    }

    private void UpdateRuntimeToolFunction(PiSharpToolRegistration registration)
    {
        var name = registration.Function.Name;
        if (_runtimeToolRegistrations.TryGetValue(name, out var previous) &&
            ReferenceEquals(previous.Function, registration.Function) &&
            previous.OutputSchema.Equals(registration.OutputSchema) &&
            previous.ReachesTools == registration.ReachesTools &&
            previous.Namespace == registration.Namespace) return;
        _runtimeToolRegistrations[name] = registration;
        _runtimeToolFunctions[name] = new DurableToolFunction(registration.Function, () => _active,
            PublishEvent, _toolBatchTermination, () => Volatile.Read(ref _currentToolLoadout),
            () => _runtimeToolFunctions, _toolHooks, registration.OutputSchema, _codemodeModels,
            registration.ReachesTools, registration.Namespace, () => _extensionContext);
    }

    internal void SetExtensionConversation(ConversationSession conversation) =>
        Volatile.Write(ref _extensionConversation, conversation);

    internal void SetExtensionSessionContextFactory(
        Func<string?, PiSharp.Extensions.Abstractions.PiSharpExtensionSessionContext?> factory) =>
        _extensionContext.SetSessionContextFactory(factory);

    internal async Task<IReadOnlyList<AgentLifecycleEvent>> NotifySessionStartAsync(
        PiSharp.Extensions.Abstractions.PiSharpExtensionSessionStartReason reason,
        CancellationToken cancellationToken)
    {
        var errors = new List<AgentLifecycleEvent>();
        foreach (var registration in _liveExtensionRegistration?.SessionStartHandlers ?? [])
        {
            cancellationToken.ThrowIfCancellationRequested();
            try { await registration.Handler(reason, _extensionContext, cancellationToken).ConfigureAwait(false); }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception error)
            {
                errors.Add(ExtensionSessionLifecycleError(error, registration.SourceInfo.Path, "session_start"));
            }
        }
        return Array.AsReadOnly(errors.ToArray());
    }

    internal async Task<IReadOnlyList<AgentLifecycleEvent>> NotifySessionShutdownAsync(
        PiSharp.Extensions.Abstractions.PiSharpExtensionSessionShutdownReason reason,
        CancellationToken cancellationToken)
    {
        var errors = new List<AgentLifecycleEvent>();
        foreach (var registration in _liveExtensionRegistration?.SessionShutdownHandlers ?? [])
        {
            cancellationToken.ThrowIfCancellationRequested();
            try { await registration.Handler(reason, _extensionContext, cancellationToken).ConfigureAwait(false); }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception error)
            {
                errors.Add(ExtensionSessionLifecycleError(error, registration.SourceInfo.Path, "session_shutdown"));
            }
        }
        return Array.AsReadOnly(errors.ToArray());
    }

    private static AgentLifecycleEvent ExtensionSessionLifecycleError(Exception error, string sourcePath,
        string eventType) => new("extension_error", Error: error.Message,
        Details: new { extensionPath = sourcePath, eventType });

    internal void SetPendingMessageState(Func<bool> getPendingMessages) =>
        _extensionContext.SetPendingMessageState(getPendingMessages);

    private void PublishEvent(AgentLifecycleEvent item)
    {
        foreach (var registration in _extensionLifecycleHandlers)
        {
            try { registration.Handler(item, _extensionContext); }
            catch (Exception error)
            {
                try
                {
                    _events?.Invoke(new AgentLifecycleEvent("extension_error", Error: error.Message,
                        Details: new { extensionPath = registration.SourceInfo.Path, eventType = item.Type }));
                }
                catch (Exception) { }
            }
        }
        _events?.Invoke(item);
    }

    private IReadOnlyList<AITool> GetRuntimeFunctionsForRun() =>
        Array.AsReadOnly<AITool>(_runtimeToolFunctions.Values.ToArray());

    private Task<IReadOnlyList<ChatMessage>> ProjectForRequestAsync(IReadOnlyList<ChatMessage> messages, bool force, CancellationToken token) =>
        // The pre-prompt policy owns the first request; only a pre-content overflow can force it.
        (force || _providerRequestIndex > 1) && _projectContext is { } project
            ? project(messages, force, token) : Task.FromResult(messages);

    public Task<CompactionSummary> SummarizeAsync(IReadOnlyList<ChatMessage> messages, string? focus,
        CancellationToken cancellationToken = default, string? previousSummary = null, bool turnPrefix = false) =>
        _summarizer.SummarizeAsync(messages, focus, cancellationToken, previousSummary, turnPrefix);

    public sealed record CompactionSummary(string Text, UsageDetails? Usage, IReadOnlyList<UsageRecord>? PhysicalUsage = null);

    public async Task<AgentSession> CreateSessionAsync(CancellationToken cancellationToken = default) =>
        await _agent.CreateSessionAsync(cancellationToken);

    public void SetReasoningOptions(ReasoningOptions? reasoning) => Volatile.Write(ref _reasoning, reasoning);

    public void SetModelRuntime(IChatClient client, bool supportsImages, ModelImageResizeOptions? imageResizeOptions,
        VirtualModelRequestRouter? virtualModelRequestRouter = null, ICodemodeModels? codemodeModels = null)
    {
        _chatClient.SetClient(client);
        _routedChatClient.SetRouter(virtualModelRequestRouter);
        Volatile.Write(ref _supportsImages, supportsImages ? 1 : 0);
        _selectedImageResizeOptions = imageResizeOptions;
        _codingTools.SetImageResizeOptions(imageResizeOptions);
    }

    internal bool HasVirtualModelRouter => _routedChatClient.HasRouter;

    internal void SetPhysicalImageLimits(ModelDescriptor? model) =>
        _codingTools.SetImageResizeOptions(model is null ? _selectedImageResizeOptions : model.InputLimits?.Images?.Resize);

    internal void SetVirtualModelSession(ConversationSession session, string thinkingLevel,
        Func<CancellationToken, Task>? save) => _routedChatClient.SetSession(session, thinkingLevel, save);

    internal void SetVirtualModelContextPreparation(Func<IReadOnlyList<ChatMessage>, VirtualModelRequestRoute, bool,
        CancellationToken, Task<IReadOnlyList<ChatMessage>>>? prepare) => _routedChatClient.SetContextPreparation(prepare);

    internal void SetVirtualModelThinkingLevel(string thinkingLevel) => _routedChatClient.SetThinkingLevel(thinkingLevel);

    public Task<BashExecutionResult> ExecuteBashAsync(string command, Action<string>? onUpdate = null,
        CancellationToken cancellationToken = default) => _codingTools.ExecuteBashAsync(command, onUpdate, cancellationToken);

    public void AbortBash() => _codingTools.AbortBash();

    public IReadOnlyList<ChatMessage> GetHistory(AgentSession session) =>
        _history.GetMessages(session).Select(ChatMessageProperties.WithoutRequestAttribution).ToArray();

    internal int EstimateContextTokens(IEnumerable<ChatMessage> history)
    {
        var systemPrompt = GetSystemPromptForCurrentLoadout();
        var tools = GetToolsForContextEstimate();
        return PiContextUsageEstimator.Estimate(systemPrompt.ContextEstimateCharacters, tools, history);
    }

    internal int EstimateContextTokens(long usageTokens, IEnumerable<ChatMessage> trailingMessages) =>
        PiContextUsageEstimator.EstimateAfterUsage(usageTokens, trailingMessages);

    public void AppendToHistory(AgentSession session, ChatMessage message)
    {
        var messages = _history.GetMessages(session).ToList();
        messages.Add(message);
        _history.SetMessages(session, messages);
    }

    public async Task<AgentSession> RestoreHistoryAsync(IEnumerable<ChatMessage> messages, CancellationToken cancellationToken = default)
    {
        var session = await CreateSessionAsync(cancellationToken);
        _history.SetMessages(session, messages.ToList());
        return session;
    }


    public IAsyncEnumerable<AgentResponseUpdate> RunStreamingAsync(string prompt, AgentSession session,
        CancellationToken cancellationToken = default) => RunStreamingAsync(new ChatMessage(ChatRole.User, prompt), session, cancellationToken);

    public IAsyncEnumerable<AgentResponseUpdate> RunStreamingAsync(ChatMessage prompt, AgentSession session,
        CancellationToken cancellationToken = default) => RunStreamingDurableAsync(prompt, session, cancellationToken, null);

    internal IAsyncEnumerable<AgentResponseUpdate> RunStreamingDurableAsync(string prompt, AgentSession session,
        CancellationToken cancellationToken, DurableExecution? durable, Action<AgentLifecycleEvent>? onEvent = null,
        Func<bool, IReadOnlyList<ChatMessage>>? takeSteering = null,
        Func<IReadOnlyList<ChatMessage>, bool, CancellationToken, Task<IReadOnlyList<ChatMessage>>>? projectContext = null,
        IReadOnlyDictionary<string, string?>? bashSessionEnvironment = null, string? conversationSessionId = null,
        Func<CancellationToken, Task<IReadOnlyList<ChatMessage>>>? takeContextMessagesForRequestAsync = null) =>
        RunStreamingDurableAsync(new ChatMessage(ChatRole.User, prompt), session, cancellationToken, durable, onEvent,
            takeSteering, projectContext, bashSessionEnvironment, conversationSessionId, takeContextMessagesForRequestAsync);

    internal IAsyncEnumerable<AgentResponseUpdate> RunStreamingDurableAsync(ChatMessage prompt, AgentSession session,
        CancellationToken cancellationToken, DurableExecution? durable,
        Action<AgentLifecycleEvent>? onEvent = null, Func<bool, IReadOnlyList<ChatMessage>>? takeSteering = null,
        Func<IReadOnlyList<ChatMessage>, bool, CancellationToken, Task<IReadOnlyList<ChatMessage>>>? projectContext = null,
        IReadOnlyDictionary<string, string?>? bashSessionEnvironment = null, string? conversationSessionId = null,
        Func<CancellationToken, Task<IReadOnlyList<ChatMessage>>>? takeContextMessagesForRequestAsync = null) =>
        RunStreamingDurableCoreAsync(prompt, session, cancellationToken, durable, onEvent, takeSteering,
            projectContext, bashSessionEnvironment, conversationSessionId, takeContextMessagesForRequestAsync);

    internal IAsyncEnumerable<AgentResponseUpdate> RunStreamingContinuationDurableAsync(AgentSession session,
        CancellationToken cancellationToken, DurableExecution? durable, Action<AgentLifecycleEvent>? onEvent = null,
        Func<bool, IReadOnlyList<ChatMessage>>? takeSteering = null,
        Func<IReadOnlyList<ChatMessage>, bool, CancellationToken, Task<IReadOnlyList<ChatMessage>>>? projectContext = null,
        IReadOnlyDictionary<string, string?>? bashSessionEnvironment = null, string? conversationSessionId = null,
        Func<CancellationToken, Task<IReadOnlyList<ChatMessage>>>? takeContextMessagesForRequestAsync = null) =>
        RunStreamingDurableCoreAsync(null, session, cancellationToken, durable, onEvent, takeSteering,
            projectContext, bashSessionEnvironment, conversationSessionId, takeContextMessagesForRequestAsync);

    private async IAsyncEnumerable<AgentResponseUpdate> RunStreamingDurableCoreAsync(ChatMessage? prompt,
        AgentSession session,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken, DurableExecution? durable,
        Action<AgentLifecycleEvent>? onEvent, Func<bool, IReadOnlyList<ChatMessage>>? takeSteering,
        Func<IReadOnlyList<ChatMessage>, bool, CancellationToken, Task<IReadOnlyList<ChatMessage>>>? projectContext,
        IReadOnlyDictionary<string, string?>? bashSessionEnvironment, string? conversationSessionId,
        Func<CancellationToken, Task<IReadOnlyList<ChatMessage>>>? takeContextMessagesForRequestAsync)
    {
        await _runGate.WaitAsync(cancellationToken);
        try
        {
            Interlocked.CompareExchange(ref _systemMessageTimestamp, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), 0);
            var loadout = GetToolLoadout(session);
            loadout.BeginRun();
            Volatile.Write(ref _currentToolLoadout, loadout);
            _currentToolTranscript = GetToolTranscript(session);
            _active = durable;
            _events = onEvent;
            Volatile.Write(ref _isRunning, 1);
            _takeSteering = takeSteering;
            _takeContextMessagesForRequestAsync = takeContextMessagesForRequestAsync;
            _projectContext = projectContext;
            _providerRequestIndex = 0;
            _injectedSteering.Clear();
            var chatOptions = new ChatOptions
            {
                Tools = GetRuntimeFunctionsForRun().ToList()
            };
            if (conversationSessionId is { Length: > 0 })
            {
                var properties = new AdditionalPropertiesDictionary
                {
                    [ProviderSessionMetadata.ConversationSessionIdProperty] = conversationSessionId
                };
                chatOptions.AdditionalProperties = properties;
            }
            var runOptions = new ChatClientAgentRunOptions(chatOptions);
            if (bashSessionEnvironment is { Count: > 0 })
            {
                var properties = new AdditionalPropertiesDictionary();
                foreach (var (key, value) in bashSessionEnvironment)
                    if (value is not null) properties[key] = value;
                runOptions.AdditionalProperties = properties;
            }
            try
            {
                var updates = prompt is null
                    ? _agent.RunStreamingAsync(session, runOptions, cancellationToken)
                    : _agent.RunStreamingAsync(prompt, session, runOptions, cancellationToken);
                await foreach (var update in updates)
                    yield return update;
            }
            finally
            {
                PersistInjectedSteering(session);
                _history.SetMessages(session, ObservedChatClient.NormalizeReadImagesForHistory(_history.GetMessages(session)).ToList());
            }
        }
        finally
        {
            _projectContext = null;
            _takeSteering = null;
            _takeContextMessagesForRequestAsync = null;
            Volatile.Write(ref _isRunning, 0);
            _events = null;
            _active = null;
            Volatile.Write(ref _currentToolLoadout, null);
            _currentToolTranscript = null;
            _runGate.Release();
        }
    }

    private void PersistInjectedSteering(AgentSession session)
    {
        if (_injectedSteering.Count == 0) return;
        var history = _history.GetMessages(session).ToList();
        foreach (var (message, afterCallId) in _injectedSteering)
        {
            if (history.Any(item => ReferenceEquals(item, message))) continue;
            var index = afterCallId is null ? history.Count - 1 : history.FindLastIndex(item =>
                item.Contents.OfType<FunctionResultContent>().Any(result => result.CallId == afterCallId));
            var insertionIndex = Math.Clamp(index + 1, 0, history.Count);
            while (insertionIndex < history.Count && _injectedSteering.Any(steering =>
                steering.AfterCallId == afterCallId && ReferenceEquals(steering.Message, history[insertionIndex])))
                insertionIndex++;
            history.Insert(insertionIndex, message);
        }
        _history.SetMessages(session, history);
        _injectedSteering.Clear();
    }
}
