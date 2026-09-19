using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using PiSharp.Core;
using PiSharp.Core.Models;
using PiSharp.Core.Models.Providers;
using PiSharp.Core.Settings;

namespace PiSharp.Cli;

internal sealed record BranchNavigationResult(bool Changed, bool SummaryAdded);

/// <summary>
/// Raised when a session-mutating operation conflicts with the operation that is already
/// running. The message is deterministic so interactive, print, and RPC hosts all surface the
/// same Pi-like conflict semantics.
/// </summary>
internal sealed class SessionOperationConflictException(string message) : InvalidOperationException(message);

internal sealed class SessionController : IProviderRequestCompactor
{
    private const string OperationTurn = "turn";
    private const string OperationCompaction = "compaction";
    private const string OperationNavigation = "navigation";

    /// <summary>Bounded wait used when manual compaction aborts an active turn (Pi semantics).</summary>
    private static readonly TimeSpan TurnDrainTimeout = TimeSpan.FromSeconds(10);

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        TypeInfoResolver = new DefaultJsonTypeInfoResolver(),
    };

    private readonly AgentBootstrap _bootstrap;
    private readonly CliOptions _options;
    private readonly SettingsManager _settings;
    private readonly AIAgent _agent;
    private readonly SessionStore? _store;
    private string _model;
    private readonly PiSessionChatHistoryProvider _sessionHistory;
    private readonly PiSummarizer _summarizer;
    private readonly ModelSessionState _modelState;
    private readonly Func<int> _contextTokens;
    private readonly Func<CompactionSettings> _compactionSettings;
    private readonly IConsoleIO _console;
    private readonly object _operationSync = new();

    /// <summary>
    /// Gets the compaction budget for the current model (settings, including per-model
    /// overrides, re-resolved on each access so /model changes apply immediately — pinned
    /// getCompactionSettings(model) is evaluated per decision).
    /// </summary>
    public CompactionSettings CompactionSettings => _compactionSettings();

    /// <summary>Gets the persistent session storage directory (null for ephemeral sessions).</summary>
    public string? StoreDirectory => _store?.WorkspaceDirectory;
    private string? _activeOperation;

    private IChatOutput _eventOutput = new SilentChatOutput();

    private SessionController(
        AgentBootstrap bootstrap,
        CliOptions options,
        SettingsManager settings,
        AIAgent agent,
        string model,
        SessionStore? store,
        SessionDocument? document,
        AgentSession session,
        string? activeTurnId,
        string? activeEntryId,
        PiSessionChatHistoryProvider sessionHistory,
        PiSummarizer summarizer,
        ModelSessionState modelState,
        Func<int> contextTokens,
        Func<CompactionSettings> compactionSettings,
        IConsoleIO? console = null)
    {
        _bootstrap = bootstrap;
        _options = options;
        _settings = settings;
        _agent = agent;
        _model = model;
        _store = store;
        Document = document;
        Session = session;
        ActiveTurnId = activeTurnId;
        ActiveEntryId = activeEntryId;
        _sessionHistory = sessionHistory;
        _summarizer = summarizer;
        _modelState = modelState;
        _contextTokens = contextTokens;
        _compactionSettings = compactionSettings;
        _console = console ?? new SystemConsoleIO();
        _sessionHistory.SetActiveDocument(document, activeEntryId);
    }

    /// <summary>Console seam used by interactive prompts (session picker).</summary>
    public IConsoleIO ConsoleIO => _console;

    /// <summary>Gets the live model/thinking state shared with the provider bridge.</summary>
    public ModelSessionState ModelState => _modelState;

    /// <summary>Gets the settings manager backing this session (defaults, thinking levels).</summary>
    public SettingsManager Settings => _settings;

    /// <summary>Gets the model runtime shared by this session (catalog, auth, availability).</summary>
    public ModelRuntime ModelRuntime => _bootstrap.ModelRuntime;

    /// <summary>Gets or sets how the active turn aborts (wired by the host to the turn coordinator).</summary>
    public Action? AbortActiveTurn { get; set; }

    /// <summary>
    /// Sets the session model (pinned AgentSession.setModel plus the session transcript
    /// append). Throws InvalidOperationException with the pinned "No API key for
    /// provider/model" message when the target provider has no auth. Appends a model_change
    /// entry, and a thinking_level_change entry when the applied level changed, to
    /// persistent sessions only.
    /// </summary>
    public async Task<SetModelResult> SetModelAsync(
        ModelInfo model,
        ModelMutationOptions options,
        CancellationToken cancellationToken = default)
    {
        var previousThinking = _modelState.ThinkingLevel;
        var result = await _modelState.SetModelAsync(model, options, cancellationToken).ConfigureAwait(false);
        _model = result.Model.Reference;
        await AppendModelChangeEntriesAsync(
            result.Model.Provider,
            result.Model.Id,
            previousThinking,
            result.ThinkingLevel,
            cancellationToken).ConfigureAwait(false);
        return result;
    }

    /// <summary>
    /// Sets the thinking level (pinned AgentSession.setThinkingLevel). Clamps to the current
    /// model's capabilities; appends a thinking_level_change entry to persistent sessions
    /// only when the effective level actually changed.
    /// </summary>
    public async Task<SetThinkingResult> SetThinkingLevelAsync(
        string level,
        ModelMutationOptions options,
        CancellationToken cancellationToken = default)
    {
        var result = _modelState.SetThinkingLevel(level, options);
        if (result.Changed)
        {
            await AppendThinkingLevelEntryAsync(result.Effective, cancellationToken).ConfigureAwait(false);
        }

        return result;
    }

    /// <summary>
    /// Cycles to the next/previous model (pinned AgentSession.cycleModel, exposed here as a
    /// command because the text CLI has no cycle keybinding). Returns null when there is at
    /// most one candidate. Appends a model_change entry, and a thinking entry when the
    /// applied level changed, to persistent sessions.
    /// </summary>
    public async Task<ModelCycleResult?> CycleModelAsync(
        string direction,
        ModelMutationOptions options,
        CancellationToken cancellationToken = default)
    {
        var previousThinking = _modelState.ThinkingLevel;
        var result = _modelState.CycleModel(direction, options);
        if (result is null)
        {
            return null;
        }

        _model = result.Model.Reference;
        await AppendModelChangeEntriesAsync(
            result.Model.Provider,
            result.Model.Id,
            previousThinking,
            result.ThinkingLevel,
            cancellationToken).ConfigureAwait(false);
        return result;
    }

    /// <summary>
    /// Cycles to the next thinking level (pinned AgentSession.cycleThinkingLevel). Returns
    /// null when the current model does not support thinking; appends a thinking_level_change
    /// entry to persistent sessions when the level changed.
    /// </summary>
    public async Task<string?> CycleThinkingLevelAsync(
        ModelMutationOptions options,
        CancellationToken cancellationToken = default)
    {
        var previous = _modelState.ThinkingLevel;
        var level = _modelState.CycleThinkingLevel(options);
        if (level is not null && !string.Equals(level, previous, StringComparison.Ordinal))
        {
            await AppendThinkingLevelEntryAsync(level, cancellationToken).ConfigureAwait(false);
        }

        return level;
    }

    /// <summary>
    /// Appends the durable entries for a model switch (pinned sessionManager.appendModelChange
    /// plus the thinking entry from setThinkingLevel). Ephemeral and pre-V3 sessions keep no
    /// model history.
    /// </summary>
    private async Task AppendModelChangeEntriesAsync(
        string provider,
        string modelId,
        string? previousThinking,
        string? newThinking,
        CancellationToken cancellationToken)
    {
        if (_store is null || Document is not { IsPiV3: true })
        {
            return;
        }

        var entries = new List<SessionEntry>
        {
            new ModelChangeEntry(
                Guid.NewGuid().ToString("N"), ActiveEntryId, DateTimeOffset.UtcNow, provider, modelId),
        };
        if (newThinking is not null && !string.Equals(newThinking, previousThinking, StringComparison.Ordinal))
        {
            entries.Add(new ThinkingLevelChangeEntry(
                Guid.NewGuid().ToString("N"), entries[^1].Id, DateTimeOffset.UtcNow, newThinking));
        }

        ActiveEntryId = entries[^1].Id;
        await _store.AppendEntriesAsync(Document, entries, cancellationToken).ConfigureAwait(false);
    }

    private async Task AppendThinkingLevelEntryAsync(string level, CancellationToken cancellationToken)
    {
        if (_store is null || Document is not { IsPiV3: true })
        {
            return;
        }

        var entry = new ThinkingLevelChangeEntry(
            Guid.NewGuid().ToString("N"), ActiveEntryId, DateTimeOffset.UtcNow, level);
        ActiveEntryId = entry.Id;
        await _store.AppendEntriesAsync(Document, [entry], cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Gets or sets the output sink for compaction events raised outside an explicit caller
    /// (per-provider-request and post-run automatic compaction).
    /// </summary>
    public IChatOutput EventOutput
    {
        get => _eventOutput;
        set => _eventOutput = value ?? new SilentChatOutput();
    }

    /// <summary>Gets the operation currently mutating the session, if any.</summary>
    public string? ActiveOperation
    {
        get
        {
            lock (_operationSync)
            {
                return _activeOperation;
            }
        }
    }

    private int _turnCount;

    /// <summary>Gets whether an agent turn is currently mutating the session.</summary>
    public bool IsTurnActive
    {
        get
        {
            lock (_operationSync)
            {
                return _turnCount > 0;
            }
        }
    }

    /// <summary>Gets whether a compaction or branch summary is currently running.</summary>
    public bool IsCompacting => ActiveOperation is OperationCompaction;

    /// <summary>Gets whether a session-tree operation (navigation, fork, new) is running.</summary>
    public bool IsNavigating => ActiveOperation is OperationNavigation;

    /// <summary>
    /// Marks the start of an agent turn. Fails deterministically when a compaction or session
    /// navigation is already in progress (Pi rejects prompts while compaction runs).
    /// </summary>
    public void EnterTurn()
    {
        lock (_operationSync)
        {
            if (_turnCount > 0)
            {
                throw new SessionOperationConflictException("An agent turn is already running.");
            }

            if (_activeOperation is OperationCompaction)
            {
                throw new SessionOperationConflictException(
                    "Cannot submit a prompt while compaction is in progress. Wait for compaction to finish and retry.");
            }

            if (_activeOperation is OperationNavigation)
            {
                throw new SessionOperationConflictException(
                    "Cannot submit a prompt while session navigation is in progress. Wait for navigation to finish and retry.");
            }

            _turnCount++;
            _activeOperation = OperationTurn;
        }
    }

    /// <summary>Marks the end of the agent turn started by <see cref="EnterTurn"/>.</summary>
    public void ExitTurn()
    {
        lock (_operationSync)
        {
            if (_turnCount == 0)
            {
                return;
            }

            if (--_turnCount == 0)
            {
                _activeOperation = null;
            }
        }
    }

    /// <summary>
    /// Runs a session-mutating operation exclusively. Compaction and navigation never run
    /// concurrently with a turn or with each other; conflicts surface as deterministic errors
    /// rather than queued work.
    /// </summary>
    public async Task<T> RunExclusiveAsync<T>(
        string operation,
        Func<CancellationToken, Task<T>> action,
        CancellationToken cancellationToken)
    {
        AcquireOperation(operation);
        try
        {
            return await action(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            ReleaseOperation(operation);
        }
    }

    private void AcquireOperation(string operation)
    {
        lock (_operationSync)
        {
            if (_turnCount > 0)
            {
                throw new SessionOperationConflictException(
                    operation == OperationNavigation
                        ? "Wait for the current response to finish before navigating the session tree."
                        : "Wait for the current response to finish before mutating the session.");
            }

            if (_activeOperation is not null)
            {
                throw new SessionOperationConflictException(
                    "Wait for the current compaction or tree navigation to finish before continuing.");
            }

            _activeOperation = operation;
        }
    }

    private void ReleaseOperation(string operation)
    {
        lock (_operationSync)
        {
            if (string.Equals(_activeOperation, operation, StringComparison.Ordinal))
            {
                _activeOperation = null;
            }
        }
    }

    /// <summary>
    /// Aborts the active turn and waits for it to settle, mirroring Pi's manual compaction,
    /// which aborts the current agent operation before compacting.
    /// </summary>
    public async Task WaitForTurnDrainAsync(CancellationToken cancellationToken)
    {
        if (!IsTurnActive)
        {
            return;
        }

        AbortActiveTurn?.Invoke();
        var deadline = DateTimeOffset.UtcNow + TurnDrainTimeout;
        while (IsTurnActive && DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(25, CancellationToken.None).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
        }

        if (IsTurnActive)
        {
            throw new SessionOperationConflictException(
                "The active turn did not stop in time; abort it and retry the operation.");
        }
    }

    public AgentSession Session { get; private set; }

    public SessionDocument? Document { get; private set; }

    public string? ActiveTurnId { get; private set; }

    public string? ActiveEntryId { get; private set; }

    public bool IsPersistent => _store is not null;

    public static async Task<SessionController> CreateAsync(
        AgentBootstrap bootstrap,
        CliOptions options,
        CancellationToken cancellationToken,
        SettingsManager? settings = null,
        IConsoleIO? console = null)
    {
        // Tests without a settings manager get the Pi defaults (empty global scope).
        var resolvedSettings = settings ??
            await SettingsManager.CreateFromStorageAsync(new InMemorySettingsStorage(), cancellationToken: cancellationToken);
        var agent = bootstrap.Agent;
        Func<CompactionSettings> compactionSettings = () => ResolveCompactionSettings(
            resolvedSettings,
            bootstrap.ModelState.Model);
        var summarizer = new PiSummarizer(
            bootstrap.SummaryClient,
            bootstrap.RetryPolicy,
            () => bootstrap.ModelState.Current?.EffectiveMaxOutput ?? 0);

        if (options.NoSession)
        {
            // Ephemeral session: resolve the startup model with no document (CLI > scoped >
            // settings default > first available); nothing is persisted.
            await ResolveStartupModelAsync(bootstrap, options, resolvedSettings, null, null, null, cancellationToken);

            return new SessionController(
                bootstrap,
                options,
                resolvedSettings,
                agent,
                ModelDisplayName(bootstrap.ModelState, options.Model),
                null,
                null,
                await agent.CreateSessionAsync(cancellationToken),
                null,
                null,
                bootstrap.SessionHistory,
                summarizer,
                bootstrap.ModelState,
                () => bootstrap.ModelState.Current?.EffectiveContextWindow ?? 128_000,
                compactionSettings,
                console);
        }

        var store = new SessionStore(
            options.WorkingDirectory,
            SettingsPaths.ResolveSessionDir(options.SessionDirectory, resolvedSettings.GetSessionDir()));
        var interactiveConsole = console ?? new SystemConsoleIO();
        SessionDocument? document = null;

        if (options.ForkSelector is not null)
        {
            // Pinned --fork <path|id>: resolve like --session (path, local id, then global
            // id) and fork the source's active branch into a fresh local session; the source
            // file is never modified and a global match needs no cross-project prompt.
            var forkResolution = await store.ResolveAsync(options.ForkSelector, cancellationToken)
                ?? throw new FileNotFoundException($"No session found matching '{options.ForkSelector}'.");
            var source = await store.LoadAsync(forkResolution.Path, cancellationToken);
            document = await store.ForkPiAsync(source, source.LatestEntryId, cancellationToken, options.SessionId);
        }
        else if (options.SessionSelector is not null)
        {
            // Pinned resolveSessionPath: a path-like selector opens (or, when the file does
            // not exist yet, creates a new session at) that path; otherwise the current
            // project's sessions are matched by exact id, then id prefix, then a global
            // search across every project.
            var resolution = await store.ResolveAsync(options.SessionSelector, cancellationToken)
                ?? throw new FileNotFoundException($"No session found matching '{options.SessionSelector}'.");

            if (IsForeignProject(resolution, options.WorkingDirectory))
            {
                // Pinned: a session from another project is never opened directly; it is
                // forked into the current directory after confirmation.
                Console.WriteLine($"Session found in different project: {resolution.ForeignCwd}");
                if (!await PromptConfirmAsync(interactiveConsole, "Fork this session into current directory? [y/N] ", cancellationToken))
                {
                    throw new OperationCanceledException("Aborted.");
                }

                var foreign = await store.LoadAsync(resolution.Path, cancellationToken);
                document = await store.ForkPiAsync(foreign, foreign.LatestEntryId, cancellationToken, options.SessionId);
            }
            else
            {
                document = File.Exists(resolution.Path)
                    ? await store.LoadAsync(resolution.Path, cancellationToken)
                    : await store.CreatePiAtAsync(resolution.Path, cancellationToken, null, options.SessionId);
            }
        }
        else if (options.ContinueSession)
        {
            // Pinned continueRecent: no previous session simply starts a fresh one.
            document = await store.ContinueAsync(cancellationToken);
        }
        else if (options.ResumeSession)
        {
            var selected = await SessionPicker.PickAsync(store, interactiveConsole, null, cancellationToken)
                ?? throw new OperationCanceledException("Session selection cancelled.");
            document = await store.LoadAsync(selected.Path, cancellationToken);
        }

        if (document is null)
        {
            if (options.SessionId is not null)
            {
                // Pinned: --session-id opens an exact-id local session when one exists, and
                // otherwise warns and creates a new session with that id.
                var existing = (await store.ListInfosAsync(cancellationToken))
                    .FirstOrDefault(info => string.Equals(info.Id, options.SessionId, StringComparison.Ordinal));
                if (existing is not null)
                {
                    document = await store.LoadAsync(existing.Path, cancellationToken);
                }
                else
                {
                    Console.Error.WriteLine($"Warning: No project session found with id '{options.SessionId}'; creating a new session with that id.");
                    document = await store.CreatePiAsync(cancellationToken, null, options.SessionId);
                }
            }
            else
            {
                document = await store.CreatePiAsync(cancellationToken);
            }
        }

        await EnsureCwdCompatibleAsync(document, options.WorkingDirectory, interactiveConsole, cancellationToken);
        var active = document.GetLatestTurnOnPath(document.LatestEntryId);
        var activeEntryId = document.IsPiV3 ? document.LatestEntryId : active?.Id;
        // Initial model/thinking entries are appended before the MAF session is restored so
        // the active leaf used for the state-cache boundary check already includes them.
        activeEntryId = await ResolveStartupModelAsync(
            bootstrap, options, resolvedSettings, store, document, activeEntryId, cancellationToken) ?? activeEntryId;
        var session = await RestoreInitialSessionAsync(agent, document, active, activeEntryId, cancellationToken);

        var controller = new SessionController(
            bootstrap,
            options,
            resolvedSettings,
            agent,
            ModelDisplayName(bootstrap.ModelState, options.Model),
            store,
            document,
            session,
            active?.Id,
            activeEntryId,
            bootstrap.SessionHistory,
            summarizer,
            bootstrap.ModelState,
            () => bootstrap.ModelState.Current?.EffectiveContextWindow ?? 128_000,
            compactionSettings,
            console);

        // Register this controller as the compaction authority for every model request that
        // the Harness agent issues. Ephemeral sessions keep no compaction authority at all.
        bootstrap.Compaction.Current = controller;

        if (!string.IsNullOrWhiteSpace(options.SessionName) && controller.IsPersistent)
        {
            await controller.SetNameAsync(options.SessionName, cancellationToken);
        }
        return controller;
    }

    /// <summary>
    /// Resolves the compaction budget for a model, including Pi's per-model overrides keyed
    /// by provider/modelId. Invalid configured values fall back to the Pi defaults with a
    /// warning so a config typo never blocks a session.
    /// </summary>
    private static CompactionSettings ResolveCompactionSettings(
        SettingsManager settings,
        PiSharp.Core.Models.ModelInfo? model)
    {
        try
        {
            return settings.ResolveCompactionSettings(model?.Provider, model?.Id);
        }
        catch (FormatException exception)
        {
            Console.Error.WriteLine($"Warning: {exception.Message} Using default compaction settings.");
            return new CompactionSettings();
        }
    }

    /// <summary>Displays the current model reference for banners and diagnostics.</summary>
    private static string ModelDisplayName(ModelSessionState modelState, string? fallback) =>
        modelState.Model?.Reference ?? fallback ?? "no model";

    /// <summary>
    /// Resolves and applies the startup model selection for a session document (pinned
    /// sdk.ts createAgentSession). <paramref name="activeEntryId"/> is null for ephemeral
    /// (no-document) sessions. Persists the initial model_change/thinking_level_change
    /// entries the same way pinned does (new sessions record both; existing sessions record
    /// a thinking level only when none is present) and returns the updated active entry.
    /// An unresolvable explicit --model aborts startup (pinned reportDiagnostics + exit).
    /// </summary>
    private static async Task<string?> ResolveStartupModelAsync(
        AgentBootstrap bootstrap,
        CliOptions options,
        SettingsManager settings,
        SessionStore? store,
        SessionDocument? document,
        string? activeEntryId,
        CancellationToken cancellationToken)
    {
        var modelState = bootstrap.ModelState;
        var path = document is { IsPiV3: true } && activeEntryId is not null
            ? document.GetActiveEntryPath(activeEntryId)
            : [];
        var context = SessionContextSettings.FromPath(path);
        var hasExistingSession = path.Any(entry =>
            entry is MessageEntry or CustomMessageEntry or CompactionEntry);

        var startup = ModelStartupResolver.Resolve(new ModelStartupInput(
            options.Provider,
            options.Model,
            options.Thinking,
            modelState.ScopedModels,
            hasExistingSession,
            context.Model,
            context.ThinkingLevel,
            context.HasThinkingEntry),
            settings,
            bootstrap.ModelRuntime);

        foreach (var warning in startup.Warnings)
        {
            Console.Error.WriteLine($"Warning: {warning}");
        }

        if (startup.Error is not null)
        {
            throw new InvalidOperationException(startup.Error);
        }

        if (startup.ModelFallbackMessage is not null)
        {
            Console.Error.WriteLine(startup.ModelFallbackMessage);
        }

        // Sync the explicit CLI/env limit overrides onto the live state so the effective
        // limits (compaction threshold, summarizer cap) follow them after every /model.
        modelState.SetSessionOverrides(
            options.ContextTokensExplicit ? (int?)options.ContextTokens : null,
            options.MaxOutputTokensExplicit ? (int?)options.MaxOutputTokens : null);
        modelState.ApplySelection(startup.Model is { } model
            ? new CurrentModelSelection(model, startup.ThinkingLevel, null, null)
            : null);

        // --api-key without --endpoint: non-persistent runtime override for the resolved
        // model's provider (pinned setRuntimeApiKey). With --endpoint the key was already
        // applied to the llama.cpp provider at runtime construction.
        if (startup.Model is { } keyedModel &&
            !string.IsNullOrWhiteSpace(options.ApiKey) &&
            string.IsNullOrWhiteSpace(options.Endpoint))
        {
            await bootstrap.ModelRuntime.SetRuntimeApiKeyAsync(
                keyedModel.Provider, options.ApiKey, cancellationToken);
        }

        if (store is null || document is not { IsPiV3: true })
        {
            return activeEntryId;
        }

        // Pinned sdk.ts: new sessions record the initial model and thinking for restore on
        // resume; existing sessions only gain a thinking entry when they have none.
        var entries = new List<SessionEntry>();
        if (!hasExistingSession)
        {
            if (startup.Model is { } initialModel)
            {
                entries.Add(new ModelChangeEntry(
                    Guid.NewGuid().ToString("N"), null, DateTimeOffset.UtcNow,
                    initialModel.Provider, initialModel.Id));
            }

            entries.Add(new ThinkingLevelChangeEntry(
                Guid.NewGuid().ToString("N"),
                entries.Count > 0 ? entries[^1].Id : activeEntryId,
                DateTimeOffset.UtcNow,
                startup.ThinkingLevel));
        }
        else if (!context.HasThinkingEntry)
        {
            entries.Add(new ThinkingLevelChangeEntry(
                Guid.NewGuid().ToString("N"), activeEntryId, DateTimeOffset.UtcNow,
                startup.ThinkingLevel));
        }

        if (entries.Count == 0)
        {
            return activeEntryId;
        }

        await store.AppendEntriesAsync(document, entries, cancellationToken);
        return entries[^1].Id;
    }

    private static async Task<AgentSession> RestoreInitialSessionAsync(
        AIAgent agent,
        SessionDocument document,
        SessionTurn? turn,
        string? entryId,
        CancellationToken cancellationToken)
    {
        if (turn is null || turn.AgentState.ValueKind != JsonValueKind.Object ||
            !turn.AgentState.EnumerateObject().Any())
        {
            return await agent.CreateSessionAsync(cancellationToken).ConfigureAwait(false);
        }

        // A cached state written before a newer compaction/branch boundary still contains the
        // discarded pre-compaction history. Treat it as stale and start fresh.
        if (document.IsPiV3 && document.HasBoundaryAfterStateCache(entryId))
        {
            return await agent.CreateSessionAsync(cancellationToken).ConfigureAwait(false);
        }

        return await agent.DeserializeSessionAsync(turn.AgentState, JsonOptions, cancellationToken).ConfigureAwait(false);
    }

    public async Task PersistTurnAsync(
        string userMessage,
        string assistantMessage,
        CancellationToken cancellationToken,
        IReadOnlyList<ToolExecutionRecord>? toolExecutions = null)
    {
        if (_store is null || Document is null)
        {
            return;
        }

        if (Document.IsPiV3)
        {
            return;
        }

        var state = await _agent.SerializeSessionAsync(Session, JsonOptions, cancellationToken);
        var turn = SessionTurn.Create(ActiveTurnId, userMessage, assistantMessage, state);
        await _store.AppendTurnAsync(Document, turn, cancellationToken);
        ActiveTurnId = turn.Id;
        ActiveEntryId = turn.Id;
        _sessionHistory.SetActiveDocument(Document, ActiveEntryId);
    }

    public async Task PersistUserMessageAsync(
        string message,
        IReadOnlyList<AIContent>? contents,
        CancellationToken cancellationToken)
    {
        EnsurePersistent();
        if (!Document!.IsPiV3)
        {
            return;
        }

        var entry = new MessageEntry(
            Guid.NewGuid().ToString("N"),
            ActiveEntryId,
            DateTimeOffset.UtcNow,
            CreateUserMessage(message, contents));
        await _store!.AppendEntriesAsync(Document, [entry], cancellationToken);
        ActiveEntryId = entry.Id;
        _sessionHistory.SetActiveDocument(Document, ActiveEntryId);
    }

    public async Task PersistAssistantMessagesAsync(
        IReadOnlyList<JsonElement> messages,
        CancellationToken cancellationToken)
    {
        EnsurePersistent();
        if (!Document!.IsPiV3 || messages.Count == 0)
        {
            return;
        }

        var entries = new List<SessionEntry>(messages.Count);
        var parentId = ActiveEntryId;
        foreach (var message in messages)
        {
            var entry = new MessageEntry(
                Guid.NewGuid().ToString("N"),
                parentId,
                GetMessageTimestamp(message) ?? DateTimeOffset.UtcNow,
                message.Clone());
            entries.Add(entry);
            parentId = entry.Id;
            if (IsMessageRole(message, "assistant"))
            {
                ActiveTurnId = entry.Id;
            }
        }
        await _store!.AppendEntriesAsync(Document, entries, cancellationToken);
        ActiveEntryId = parentId;
        _sessionHistory.SetActiveDocument(Document, ActiveEntryId);
    }

    public async Task PersistAgentStateCacheAsync(CancellationToken cancellationToken)
    {
        EnsurePersistent();
        if (!Document!.IsPiV3 || ActiveEntryId is null)
        {
            return;
        }

        var state = await _agent.SerializeSessionAsync(Session, JsonOptions, cancellationToken);
        var cache = new CustomEntry(
            Guid.NewGuid().ToString("N"),
            ActiveEntryId,
            DateTimeOffset.UtcNow,
            SessionEntryTypes.AgentStateCache,
            state.Clone());
        await _store!.AppendEntriesAsync(Document, [cache], cancellationToken);
        ActiveEntryId = cache.Id;
        _sessionHistory.SetActiveDocument(Document, ActiveEntryId);
    }

    /// <summary>
    /// Automatic compaction entry point (pre-prompt, post-run, and per-provider-request checks).
    /// The threshold trigger requires <see cref="PiCompactionPlanner.ShouldCompact"/>; the
    /// overflow trigger is authoritative and skips that check. Failures never kill the turn.
    /// </summary>
    public async Task<bool> TryAutoCompactAsync(
        IChatOutput output,
        CompactionReason reason,
        CancellationToken cancellationToken)
    {
        if (!IsPersistent || Document is null || !Document.IsPiV3 || ActiveEntryId is null)
        {
            return false;
        }

        var estimate = PiCompactionPlanner.EstimateContextTokens(Document.GetActiveContextEntries(ActiveEntryId));
        if (reason == CompactionReason.Threshold &&
            !PiCompactionPlanner.ShouldCompact(estimate.Tokens, _contextTokens(), _compactionSettings()))
        {
            return false;
        }

        // A successful response whose provider usage already exceeds the window is an overflow
        // even when the char-based estimate looks smaller.
        var effectiveReason = reason == CompactionReason.Threshold && estimate.UsageTokens > _contextTokens()
            ? CompactionReason.Overflow
            : reason;

        try
        {
            return (await CompactCoreAsync(null, effectiveReason, output, cancellationToken).ConfigureAwait(false))
                is not null;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return false;
        }
        catch (Exception)
        {
            // Automatic compaction must not fail the turn; CompactCore already emitted the
            // compaction_end(error) event.
            return false;
        }
    }

    /// <summary>
    /// Manual compaction entry point used by /compact, RPC compact, and extensions. It first
    /// aborts and drains any active turn (Pi semantics: manual compaction never races the agent),
    /// then runs exclusively so a second compaction or navigation cannot interleave.
    /// </summary>
    public async Task<PiSummaryResult> CompactAsync(
        string? customInstructions,
        CompactionReason reason,
        IChatOutput output,
        CancellationToken cancellationToken)
    {
        EnsurePersistent();
        if (reason != CompactionReason.Manual)
        {
            return await CompactCoreAsync(customInstructions, reason, output, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException("Nothing to compact (session is already compacted or too small).");
        }

        await WaitForTurnDrainAsync(cancellationToken).ConfigureAwait(false);
        return await RunExclusiveAsync(
                OperationCompaction,
                token => CompactCoreAsync(customInstructions, reason, output, token),
                cancellationToken)
            .ConfigureAwait(false)
            ?? throw new InvalidOperationException("Nothing to compact (session is already compacted or too small.");
    }

    /// <summary>
    /// The shared compaction core: plan, summarize, persist the durable entry, rebuild runtime
    /// state, and only then emit the success event. Returns null when automatic/overflow
    /// compaction finds nothing meaningful to compact.
    /// </summary>
    private async Task<PiSummaryResult?> CompactCoreAsync(
        string? customInstructions,
        CompactionReason reason,
        IChatOutput output,
        CancellationToken cancellationToken)
    {
        EnsurePersistent();
        if (!Document!.IsPiV3 || ActiveEntryId is null)
        {
            throw new InvalidOperationException("Pi-native compaction requires a Pi v3 session with history.");
        }

        var path = Document.GetActiveEntryPath(ActiveEntryId);
        var plan = PiCompactionPlanner.PrepareCompaction(path, _compactionSettings());
        if (plan is null)
        {
            if (reason == CompactionReason.Manual)
            {
                // Pi distinguishes the two manual-compaction conflicts.
                var lastEntry = path.Count == 0 ? null : path[^1];
                throw new InvalidOperationException(
                    lastEntry is CompactionEntry ? "Already compacted." : "Nothing to compact (session too small).");
            }

            // Genuinely nothing to compact (e.g. a single oversized prompt with no older
            // history). The caller surfaces the original error or proceeds without compaction.
            return null;
        }

        var reasonText = reason.ToString().ToLowerInvariant();
        output.CompactionStarted(reasonText);
        try
        {
            // The summarizer runs on the raw model client, so this request can never trigger
            // another round of session compaction.
            var result = await _summarizer.GenerateCompactionAsync(plan, customInstructions, cancellationToken)
                .ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();

            // Durable persistence completes before any success signal. The entry is appended at
            // the active leaf: after already-persisted tool results and before the next
            // assistant response, matching Pi's append-at-leaf ordering.
            var entry = new CompactionEntry(
                Guid.NewGuid().ToString("N"),
                ActiveEntryId,
                DateTimeOffset.UtcNow,
                result.Summary,
                result.FirstKeptEntryId,
                result.TokensBefore,
                result.Details,
                result.Usage);
            await _store!.AppendEntriesAsync(Document, [entry], cancellationToken).ConfigureAwait(false);
            ActiveEntryId = entry.Id;
            _sessionHistory.SetActiveDocument(Document, ActiveEntryId);

            // Invalidate the in-memory MAF runtime state: it may still describe the discarded
            // pre-compaction context. The typed session is the sole history authority.
            Session = await _agent.CreateSessionAsync(cancellationToken).ConfigureAwait(false);

            var after = PiCompactionPlanner.EstimateContextTokens(
                Document.GetActiveContextEntries(ActiveEntryId)).Tokens;
            output.CompactionFinished(reasonText, result.TokensBefore, after, false, null);
            return result;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // No entry was persisted and the active leaf is unchanged; report the abort.
            output.CompactionFinished(reasonText, 0, 0, true, null);
            throw;
        }
        catch (Exception exception)
        {
            output.CompactionFinished(reasonText, 0, 0, false, exception.Message);
            throw;
        }
    }

    public async Task<bool> EnsureContextFitsAsync(CompactionReason trigger, CancellationToken cancellationToken)
    {
        if (!IsPersistent || Document?.IsPiV3 != true || ActiveEntryId is null)
        {
            return false;
        }

        // An overflow trigger is authoritative (e.g. a successful response whose usage already
        // exceeded the window); the threshold trigger keeps Pi's ShouldCompact gate.
        if (trigger == CompactionReason.Overflow)
        {
            return await ForceCompactAsync(cancellationToken).ConfigureAwait(false);
        }

        var estimate = PiCompactionPlanner.EstimateContextTokens(Document.GetActiveContextEntries(ActiveEntryId));
        if (!PiCompactionPlanner.ShouldCompact(estimate.Tokens, _contextTokens(), _compactionSettings()))
        {
            return false;
        }

        try
        {
            return (await CompactCoreAsync(
                null, CompactionReason.Threshold, _eventOutput, cancellationToken).ConfigureAwait(false)) is not null;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            // A failed automatic compaction must not fail the provider request; the next
            // request (or the provider's overflow response) re-evaluates the decision.
            return false;
        }
    }

    public async Task<bool> ForceCompactAsync(CancellationToken cancellationToken)
    {
        if (!IsPersistent || Document?.IsPiV3 != true || ActiveEntryId is null)
        {
            return false;
        }

        try
        {
            return (await CompactCoreAsync(
                null, CompactionReason.Overflow, _eventOutput, cancellationToken).ConfigureAwait(false)) is not null;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return false;
        }
    }

    public IReadOnlyList<ChatMessage> GetEffectiveHistory() =>
        Document is { IsPiV3: true } && ActiveEntryId is not null
            ? PiSessionDocumentMessages.GetActiveEntryMessages(Document, ActiveEntryId)
            : [];

    /// <summary>
    /// Navigates the active branch, optionally summarizing the abandoned path. Runs
    /// exclusively: a navigation never races a turn, compaction, or another navigation, and a
    /// cancelled summarization leaves the original branch selected with no partial entry.
    /// </summary>
    public Task<BranchNavigationResult> NavigateAsync(
        string selector,
        bool summarize,
        string? customInstructions,
        IChatOutput output,
        CancellationToken cancellationToken)
    {
        EnsurePersistent();
        if (!Document!.IsPiV3)
        {
            throw new InvalidOperationException("Branch navigation requires a Pi v3 session.");
        }

        return RunExclusiveAsync(OperationNavigation, token => NavigateCoreAsync(selector, summarize, customInstructions, token), cancellationToken);
    }

    public Task<BranchNavigationResult> NavigateAsync(
        string selector,
        bool summarize,
        string? customInstructions,
        bool replaceInstructions,
        IChatOutput output,
        CancellationToken cancellationToken)
    {
        EnsurePersistent();
        if (!Document!.IsPiV3)
        {
            throw new InvalidOperationException("Branch navigation requires a Pi v3 session.");
        }

        return RunExclusiveAsync(OperationNavigation, token => NavigateCoreAsync(selector, summarize, customInstructions, replaceInstructions, token), cancellationToken);
    }

    private async Task<BranchNavigationResult> NavigateCoreAsync(
        string selector,
        bool summarize,
        string? customInstructions,
        CancellationToken cancellationToken)
    {
        return await NavigateCoreAsync(selector, summarize, customInstructions, replaceInstructions: false, cancellationToken);
    }

    private async Task<BranchNavigationResult> NavigateCoreAsync(
        string selector,
        bool summarize,
        string? customInstructions,
        bool replaceInstructions,
        CancellationToken cancellationToken)
    {
        var document = Document!;
        var target = selector.Equals("root", StringComparison.OrdinalIgnoreCase)
            ? null
            : document.ResolveEntry(selector);
        var targetId = target?.Id;
        var navigationTargetId = target is MessageEntry assistantTarget && IsMessageRole(assistantTarget.Message, "assistant")
            ? document.GetTurnLeafEntryId(target.Id)
            : targetId;
        var oldLeaf = ActiveEntryId;
        if (string.Equals(oldLeaf, navigationTargetId, StringComparison.OrdinalIgnoreCase))
        {
            return new BranchNavigationResult(false, false);
        }

        var summaryPlan = navigationTargetId is null
            ? new BranchSummaryPlan([], null, 0, new CompactionFileOperations([], []))
            : PiCompactionPlanner.CollectBranchSummary(
                document,
                oldLeaf,
                navigationTargetId,
                Math.Max(0, _contextTokens() - _compactionSettings().ReserveTokens));
        // The summarization runs before any navigation mutation, so a cancellation or failure
        // cannot leave a half-applied branch summary.
        var summary = summarize && summaryPlan.Entries.Count > 0
            ? await _summarizer.GenerateBranchSummaryAsync(summaryPlan, customInstructions, replaceInstructions, cancellationToken)
            : (Summary: (string?)null, Usage: (JsonElement?)null, Details: (JsonElement?)null);
        var newLeaf = target switch
        {
            MessageEntry message when IsMessageRole(message.Message, "user") => message.ParentId,
            CustomMessageEntry custom => custom.ParentId,
            null => null,
            _ => navigationTargetId,
        };

        if (summary.Summary is not null)
        {
            // The summary is attached at the navigation target (destination), not the old branch.
            var entry = new BranchSummaryEntry(
                Guid.NewGuid().ToString("N"),
                newLeaf,
                DateTimeOffset.UtcNow,
                oldLeaf ?? "root",
                summary.Summary,
                summary.Details,
                summary.Usage);
            await _store!.AppendEntriesAsync(document, [entry], cancellationToken);
            ActiveEntryId = entry.Id;
        }
        else
        {
            ActiveEntryId = newLeaf;
        }

        ActiveTurnId = document.GetLatestTurnOnPath(ActiveEntryId)?.Id;
        // The previous MAF session may still carry messages from the abandoned branch; rebuild
        // from the typed context, which is the only history authority.
        Session = await _agent.CreateSessionAsync(cancellationToken);
        _sessionHistory.SetActiveDocument(Document, ActiveEntryId);
        return new BranchNavigationResult(true, summary.Summary is not null);
    }

    public Task NewAsync(CancellationToken cancellationToken)
    {
        EnsurePersistent();
        return RunExclusiveAsync(
            OperationNavigation,
            async token =>
            {
                Document = await _store!.CreatePiAsync(token).ConfigureAwait(false);
                // /new re-runs the startup model resolution for the fresh session (pinned
                // runtimeHost.newSession): CLI model, then scoped models (new session), then
                // settings default / first available.
                ActiveEntryId = await ResolveStartupModelAsync(
                    _bootstrap, _options, _settings, _store, Document, null, token).ConfigureAwait(false);
                Session = await _agent.CreateSessionAsync(token).ConfigureAwait(false);
                ActiveTurnId = null;
                _sessionHistory.SetActiveDocument(Document, ActiveEntryId);
                return true;
            },
            cancellationToken);
    }

    public Task ForkAsync(string? turnSelector, CancellationToken cancellationToken)
    {
        EnsurePersistent();
        return RunExclusiveAsync(
            OperationNavigation,
            async token =>
            {
                await ForkCoreAsync(turnSelector, token).ConfigureAwait(false);
                return true;
            },
            cancellationToken);
    }

    private async Task ForkCoreAsync(string? entrySelector, CancellationToken cancellationToken)
    {
        var source = Document!;
        if (!source.IsFileFlushed)
        {
            // Pinned runtime guard: an unsaved (pre-first-assistant) session has no file to
            // fork from, so the operation is refused rather than silently dropped.
            throw new InvalidOperationException(
                "This session has not been saved yet. Wait for the first assistant response before cloning or forking it.");
        }

        string? targetId;
        string? returnedText = null;

        if (entrySelector is null)
        {
            // Pinned clone: fork "at" the active leaf.
            targetId = source.IsPiV3 ? ActiveEntryId : source.LatestTurn?.Id;
        }
        else if (source.IsPiV3)
        {
            // Pinned /fork: the selected entry must be a user message; fork "before" it
            // (target = its parent) and return its text to the editor — printed here in the
            // REPL, where there is no editor to re-enter into.
            var selected = source.ResolveEntry(entrySelector);
            if (selected is not MessageEntry message || !IsMessageRole(message.Message, "user"))
            {
                throw new InvalidOperationException("Can only fork from a user message.");
            }

            targetId = selected.ParentId;
            returnedText = ExtractTextContent(message.Message);
        }
        else
        {
            // Legacy turn-based sessions fork at turn granularity.
            targetId = source.ResolveTurn(entrySelector).Id;
        }

        Document = source.IsPiV3
            ? await _store!.ForkPiAsync(source, targetId, cancellationToken).ConfigureAwait(false)
            : await _store!.ForkAsync(source, targetId, _model, cancellationToken).ConfigureAwait(false);
        ActiveEntryId = Document.IsPiV3 ? Document.LatestEntryId : Document.LatestTurn?.Id;
        // The forked document carries the same entry history; re-resolving reproduces the
        // model/thinking recorded on it (including model changes made before the fork point).
        ActiveEntryId = await ResolveStartupModelAsync(
            _bootstrap, _options, _settings, _store, Document, ActiveEntryId, cancellationToken)
            ?? ActiveEntryId;
        ActiveTurnId = Document.GetLatestTurnOnPath(ActiveEntryId)?.Id;
        _sessionHistory.SetActiveDocument(Document, ActiveEntryId);
        Session = await RestoreSessionAsync(Document.GetLatestTurnOnPath(ActiveEntryId), cancellationToken).ConfigureAwait(false);

        if (returnedText is not null)
        {
            var flat = returnedText.ReplaceLineEndings(" ").Trim();
            var preview = flat.Length > 120 ? flat[..120] + "…" : flat;
            Console.WriteLine($"Fork point message (re-entered in the pinned UI): {preview}");
        }
    }

    /// <summary>Extracts the readable text of a message entry (string content or text blocks).</summary>
    private static string ExtractTextContent(JsonElement message)
    {
        if (!message.TryGetProperty("content", out var content))
        {
            return string.Empty;
        }

        if (content.ValueKind == JsonValueKind.String)
        {
            return content.GetString() ?? string.Empty;
        }

        if (content.ValueKind != JsonValueKind.Array)
        {
            return string.Empty;
        }

        return string.Join(" ", content.EnumerateArray()
            .Where(item => item.ValueKind == JsonValueKind.Object &&
                           item.TryGetProperty("type", out var itemType) &&
                           string.Equals(itemType.GetString(), "text", StringComparison.Ordinal))
            .Select(item => item.TryGetProperty("text", out var text) ? text.GetString() ?? string.Empty : string.Empty));
    }

    /// <summary>
    /// Pinned runtimeHost.importFromJsonl: copies the source file into the session directory
    /// (exclusive copy; name-1.jsonl, name-2.jsonl, ... on collision; a source that already
    /// lives in the session directory is opened in place) and switches to it as the current
    /// session. A missing stored cwd goes through the continue-in-current-cwd prompt.
    /// </summary>
    public async Task ImportAsync(string inputPath, CancellationToken cancellationToken)
    {
        EnsurePersistent();
        await RunExclusiveAsync(OperationNavigation, async token =>
        {
            var source = Path.GetFullPath(inputPath, _options.WorkingDirectory);
            if (!File.Exists(source))
            {
                throw new FileNotFoundException($"Import file not found: {source}", source);
            }

            var store = _store!;
            var sessionDir = store.WorkspaceDirectory;
            Directory.CreateDirectory(sessionDir);

            var destination = Path.Combine(sessionDir, Path.GetFileName(source));
            var sourceAlreadyStored = Path.GetFullPath(destination) == source;
            if (!sourceAlreadyStored)
            {
                var dot = destination.LastIndexOf('.');
                var name = dot <= 0 ? destination : destination[..dot];
                var extension = dot <= 0 ? string.Empty : destination[dot..];
                var suffix = 1;
                while (File.Exists(destination))
                {
                    destination = Path.Combine(sessionDir, $"{name}-{suffix++}{extension}");
                }

                // Pinned COPYFILE_EXCL: fail loudly if a concurrent import lands first.
                File.Copy(source, destination, overwrite: false);
            }

            var loaded = await store.LoadAsync(destination, token).ConfigureAwait(false);
            await EnsureCwdCompatibleAsync(loaded, _options.WorkingDirectory, _console, token).ConfigureAwait(false);
            await ActivateDocumentAsync(loaded, token).ConfigureAwait(false);
            return true;
        }, cancellationToken);
    }

    private static JsonElement CreateUserMessage(string message, IReadOnlyList<AIContent>? contents)
    {
        var imageContents = contents?.OfType<DataContent>()
            .Select(content => (object)new
            {
                type = "image",
                data = content.Base64Data.ToString(),
                mimeType = content.MediaType,
            })
            .ToArray() ?? [];
        object contentValue = imageContents.Length == 0
            ? message
            : new object[] { new { type = "text", text = message } }
                .Concat(imageContents)
                .ToArray();
        return JsonSerializer.SerializeToElement(
            new
            {
                role = "user",
                content = contentValue,
                timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            },
            JsonOptions);
    }

    private static DateTimeOffset? GetMessageTimestamp(JsonElement message)
    {
        if (!message.TryGetProperty("timestamp", out var timestamp) ||
            timestamp.ValueKind != JsonValueKind.Number ||
            !timestamp.TryGetInt64(out var milliseconds))
        {
            return null;
        }
        return DateTimeOffset.FromUnixTimeMilliseconds(milliseconds);
    }

    private static bool IsMessageRole(JsonElement message, string role) =>
        message.ValueKind == JsonValueKind.Object &&
        message.TryGetProperty("role", out var roleValue) &&
        string.Equals(roleValue.GetString(), role, StringComparison.Ordinal);

    public Task<bool> ResumeInteractiveAsync(CancellationToken cancellationToken)
    {
        EnsurePersistent();
        return RunExclusiveAsync(OperationNavigation, token => ResumeCoreAsync(token), cancellationToken);
    }

    private async Task<bool> ResumeCoreAsync(CancellationToken cancellationToken)
    {
        // Pinned /resume: the picker lists every project's sessions (or the whole explicit
        // session directory) with search, sort modes, rename, and delete.
        var selected = await SessionPicker.PickAsync(
            _store!, _console, Document?.FilePath, cancellationToken).ConfigureAwait(false);
        if (selected is null)
        {
            return false;
        }

        var document = await _store!.LoadAsync(selected.Path, cancellationToken).ConfigureAwait(false);
        await EnsureCwdCompatibleAsync(document, _options.WorkingDirectory, _console, cancellationToken).ConfigureAwait(false);
        await ActivateDocumentAsync(document, cancellationToken).ConfigureAwait(false);
        return true;
    }

    /// <summary>
    /// Shared session activation (resume/import/new): makes the document current, re-resolves
    /// the model and thinking recorded on it (pinned: saved model_change / assistant metadata
    /// and the last thinking_level_change win over the previous session's runtime model),
    /// and rebuilds the MAF session.
    /// </summary>
    private async Task ActivateDocumentAsync(SessionDocument document, CancellationToken cancellationToken)
    {
        Document = document;
        ActiveEntryId = document.IsPiV3 ? document.LatestEntryId : document.LatestTurn?.Id;
        ActiveEntryId = await ResolveStartupModelAsync(
            _bootstrap, _options, _settings, _store, Document, ActiveEntryId, cancellationToken)
            ?? ActiveEntryId;
        ActiveTurnId = Document.GetLatestTurnOnPath(ActiveEntryId)?.Id;
        _sessionHistory.SetActiveDocument(Document, ActiveEntryId);
        Session = await RestoreSessionAsync(Document.GetLatestTurnOnPath(ActiveEntryId), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Pinned getMissingSessionCwdIssue + promptForMissingSessionCwd, adapted to PiSharp's
    /// conservative cwd policy: a session whose stored working directory no longer exists
    /// offers "continue in the current directory" (interactive) or fails (non-interactive);
    /// an existing-but-different cwd is still refused because the startup cwd stays
    /// authoritative for resources, settings, and trust.
    /// </summary>
    internal static async Task EnsureCwdCompatibleAsync(
        SessionDocument document,
        string startupCwd,
        IConsoleIO? console,
        CancellationToken cancellationToken)
    {
        var sessionCwd = document.IsPiV3 ? document.PiHeader!.Cwd : document.Header.WorkingDirectory;
        if (string.IsNullOrWhiteSpace(sessionCwd))
        {
            return;
        }

        var expected = Path.TrimEndingDirectorySeparator(Path.GetFullPath(startupCwd));
        var actual = Path.TrimEndingDirectorySeparator(Path.GetFullPath(sessionCwd));
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (string.Equals(expected, actual, comparison))
        {
            return;
        }

        if (Directory.Exists(actual) || File.Exists(actual))
        {
            throw new InvalidOperationException($"Session belongs to a different workspace: {actual}");
        }

        // Pinned: the stored cwd is gone; continue in the current cwd or cancel.
        if (console is null || !console.IsInteractive)
        {
            throw new InvalidOperationException(
                $"Stored session working directory does not exist: {actual}\n"
                + $"Session file: {document.FilePath}\n"
                + $"Current working directory: {expected}");
        }

        console.WriteLine($"The stored session working directory does not exist: {actual}");
        console.WriteLine($"Session file: {document.FilePath}");
        console.WriteLine($"Current working directory: {expected}");
        try
        {
            var answer = ConsoleKeyInput.ReadLine(
                console, "Continue in the current directory? [y/N] ", cancellationToken);
            if (answer.Equals("y", StringComparison.OrdinalIgnoreCase) ||
                answer.Equals("yes", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // Esc on the confirm prompt: cancel.
        }

        throw new OperationCanceledException("Aborted.");
    }

    /// <summary>
    /// Restores the MAF runtime session. A cached MAF state is only restored when no compaction
    /// or branch-summary boundary post-dates it on the active path; otherwise the cache is known
    /// to describe discarded effective context and a fresh session is created instead.
    /// </summary>
    private async Task<AgentSession> RestoreSessionAsync(SessionTurn? turn, CancellationToken cancellationToken)
    {
        if (turn is null || turn.AgentState.ValueKind != JsonValueKind.Object ||
            !turn.AgentState.EnumerateObject().Any())
        {
            return await _agent.CreateSessionAsync(cancellationToken).ConfigureAwait(false);
        }

        if (Document is { IsPiV3: true } && Document.HasBoundaryAfterStateCache(ActiveEntryId))
        {
            // Stale cache: it was written before a compaction/branch boundary and may contain
            // pre-compaction history. Never restore it; the typed session is authoritative.
            return await _agent.CreateSessionAsync(cancellationToken).ConfigureAwait(false);
        }

        return await _agent.DeserializeSessionAsync(turn.AgentState, JsonOptions, cancellationToken).ConfigureAwait(false);
    }

    public SessionStatistics GetStatistics() => Document?.GetStatistics() ??
        new SessionStatistics(string.Empty, null, string.Empty, 0, 0, 0, 0, 0);

    public async Task SetNameAsync(string name, CancellationToken cancellationToken)
    {
        EnsurePersistent();
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (!Document!.IsPiV3)
        {
            throw new InvalidOperationException("Session naming requires a Pi v3 session.");
        }

        // Pinned appendSessionInfo sanitization: line breaks collapse to spaces, then trim.
        var sanitized = SanitizeName(name);
        var entry = new SessionInfoEntry(
            Guid.NewGuid().ToString("N"),
            ActiveEntryId,
            DateTimeOffset.UtcNow,
            sanitized);
        await _store!.AppendEntriesAsync(Document, [entry], cancellationToken);
        ActiveEntryId = entry.Id;
        _sessionHistory.SetActiveDocument(Document, ActiveEntryId);
    }

    /// <summary>
    /// Sets or clears a label on a session entry. A null/blank label clears the bookmark.
    /// The change is persisted as a Pi v3 <see cref="LabelEntry"/> appended to the active leaf.
    /// </summary>
    public async Task<string?> SetLabelAsync(
        string entrySelector,
        string? label,
        CancellationToken cancellationToken)
    {
        EnsurePersistent();
        if (!Document!.IsPiV3)
        {
            throw new InvalidOperationException("Labels require a Pi v3 session.");
        }

        var target = Document.ResolveEntry(entrySelector);
        var normalized = string.IsNullOrWhiteSpace(label) ? null : label.Trim();
        var entry = new LabelEntry(
            Guid.NewGuid().ToString("N"),
            ActiveEntryId,
            DateTimeOffset.UtcNow,
            target.Id,
            normalized);
        await _store!.AppendEntriesAsync(Document, [entry], cancellationToken);
        ActiveEntryId = entry.Id;
        _sessionHistory.SetActiveDocument(Document, ActiveEntryId);
        return Document.GetLabel(target.Id);
    }

    /// <summary>Pinned session-name sanitization: collapse every line break to a space, then trim.</summary>
    internal static string SanitizeName(string name)
    {
        var collapsed = name
            .Replace("\r\n", " ")
            .Replace('\r', ' ')
            .Replace('\n', ' ');
        return collapsed.Trim();
    }

    /// <summary>Returns the current label for the entry matched by an id or unambiguous prefix.</summary>
    public string? GetLabel(string entrySelector)
    {
        if (Document is null)
        {
            return null;
        }

        return Document.GetLabel(Document.ResolveEntry(entrySelector).Id);
    }

    public string FormatSessionInfo()
    {
        if (Document is null)
        {
            return "Session: ephemeral (--no-session)";
        }

        var name = string.IsNullOrWhiteSpace(Document.Name) ? string.Empty : $" | name: {Document.Name}";
        return $"Session: {Document.Header.SessionId}{name} | turns: {Document.Turns.Count} | active: {Short(ActiveEntryId) ?? "root"}\n{Document.FilePath}";
    }

    public string FormatTree()
    {
        if (Document is null)
        {
            return "Session tree is unavailable in --no-session mode.";
        }

        if (Document.Turns.Count == 0)
        {
            return "(empty session)";
        }

        var children = Document.Turns
            .GroupBy(turn => turn.ParentId ?? string.Empty, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.ToList(), StringComparer.OrdinalIgnoreCase);
        var lines = new List<string>();
        AppendChildren(string.Empty, string.Empty, true, children, lines);
        return string.Join(Environment.NewLine, lines);
    }

    private void AppendChildren(
        string parentId,
        string prefix,
        bool isRoot,
        IReadOnlyDictionary<string, List<SessionTurn>> children,
        List<string> lines)
    {
        if (!children.TryGetValue(parentId, out var nodes))
        {
            return;
        }

        for (var i = 0; i < nodes.Count; i++)
        {
            var node = nodes[i];
            var last = i == nodes.Count - 1;
            var connector = isRoot ? string.Empty : last ? "└─ " : "├─ ";
            var marker = string.Equals(node.Id, ActiveTurnId, StringComparison.OrdinalIgnoreCase) ? "*" : " ";
            var summary = node.UserMessage.ReplaceLineEndings(" ");
            if (summary.Length > 70)
            {
                summary = summary[..67] + "...";
            }

            var label = Document?.GetLabel(node.Id);
            var labelPrefix = string.IsNullOrWhiteSpace(label) ? string.Empty : $"[{label}] ";
            lines.Add($"{prefix}{connector}{marker} {Short(node.Id)}  {labelPrefix}{summary}");
            var childPrefix = isRoot ? string.Empty : prefix + (last ? "   " : "│  ");
            AppendChildren(node.Id, childPrefix, false, children, lines);
        }
    }

    /// <summary>True when a resolution hit lives in another project (different stored cwd).</summary>
    private static bool IsForeignProject(SessionResolution resolution, string workingDirectory)
    {
        if (resolution.ForeignCwd is null)
        {
            return false;
        }

        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        return !string.Equals(
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(resolution.ForeignCwd)),
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(workingDirectory)),
            comparison);
    }

    /// <summary>
    /// Pinned promptConfirm: a [y/N] question; Esc, EOF, and anything but y/yes decline.
    /// </summary>
    internal static async Task<bool> PromptConfirmAsync(IConsoleIO console, string prompt, CancellationToken cancellationToken)
    {
        try
        {
            var answer = ConsoleKeyInput.ReadLine(console, prompt, cancellationToken);
            return answer.Equals("y", StringComparison.OrdinalIgnoreCase) ||
                   answer.Equals("yes", StringComparison.OrdinalIgnoreCase);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return false;
        }
    }

    private void EnsurePersistent()
    {
        if (_store is null || Document is null)
        {
            throw new InvalidOperationException("This command is unavailable in --no-session mode.");
        }
    }

    private static string? Short(string? id) => id is null ? null : id[..Math.Min(8, id.Length)];
}
