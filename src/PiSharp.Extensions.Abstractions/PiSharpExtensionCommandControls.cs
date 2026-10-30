namespace PiSharp.Extensions.Abstractions;

/// <summary>Stable metadata for one command available to the current host session.</summary>
public sealed record PiSharpExtensionCommandInfo(
    string Name,
    string? Description,
    string Source,
    string? SourcePath = null);

/// <summary>How a configured tool can be reached by the model and other tools.</summary>
public enum PiSharpExtensionToolExposure
{
    /// <summary>Callable directly as a model-declared tool.</summary>
    Direct,
    /// <summary>Available to the model through the host's model-only routing.</summary>
    ModelOnly,
    /// <summary>Reachable through the host's code-mode execution surface.</summary>
    CodeMode,
    /// <summary>Available after explicit deferred activation.</summary>
    Deferred,
    /// <summary>Registered but not available for activation.</summary>
    Hidden
}

/// <summary>Stable metadata for a configured tool without exposing its host function.</summary>
public sealed record PiSharpExtensionToolInfo(
    string Name,
    string Description,
    bool IsActive,
    PiSharpExtensionToolExposure Exposure,
    string Source,
    string? SourcePath = null,
    string? Namespace = null);

/// <summary>Stable identity and capabilities of the session's selected model.</summary>
public sealed record PiSharpExtensionModelInfo(
    string ProviderId,
    string ModelId,
    string DisplayName,
    int? ContextLength,
    bool SupportsReasoning);

/// <summary>Thinking levels supported by PiSharp's current model controls.</summary>
public enum PiSharpThinkingLevel
{
    /// <summary>Reasoning is disabled.</summary>
    Off,
    /// <summary>Minimal reasoning effort.</summary>
    Minimal,
    /// <summary>Low reasoning effort.</summary>
    Low,
    /// <summary>Medium reasoning effort.</summary>
    Medium,
    /// <summary>High reasoning effort.</summary>
    High,
    /// <summary>Extra-high reasoning effort.</summary>
    ExtraHigh,
    /// <summary>Maximum reasoning effort supported by the model.</summary>
    Max
}

/// <summary>Outcome of selecting a model by stable provider and model identity.</summary>
public enum PiSharpModelSelectionStatus
{
    /// <summary>The requested model became the session selection.</summary>
    Selected,
    /// <summary>No exact provider/model identity matched the current model catalogue.</summary>
    UnknownModel,
    /// <summary>The provider cannot currently authenticate the requested model.</summary>
    AuthenticationUnavailable
}

/// <summary>Small, provider-neutral outcome for a model selection request.</summary>
public sealed record PiSharpModelSelectionResult(
    PiSharpModelSelectionStatus Status,
    string? Error = null);

/// <summary>Outcome of requesting manual session compaction.</summary>
public enum PiSharpCompactionStatus
{
    /// <summary>The current session was compacted.</summary>
    Compacted,
    /// <summary>The current session did not have enough completed turns to compact.</summary>
    NotNeeded,
    /// <summary>The compaction request was cancelled.</summary>
    Cancelled,
    /// <summary>The compaction request failed.</summary>
    Failed
}

/// <summary>Provider-neutral result for a manual session compaction request.</summary>
public sealed record PiSharpCompactionResult(
    PiSharpCompactionStatus Status,
    string? Error = null);

/// <summary>Options for selecting a prior conversation branch from an extension command.</summary>
public sealed record PiSharpTreeNavigationOptions(bool Summarize = false,
    string? CustomInstructions = null, bool ReplaceInstructions = false, string? Label = null);

/// <summary>Result of an extension-requested conversation tree navigation.</summary>
public sealed record PiSharpTreeNavigationResult(bool Cancelled = false);

/// <summary>
/// Host-owned operations made available on a live extension command context. The value contains
/// callbacks only; extensions receive the stable methods on <see cref="PiSharpExtensionCommandContext"/>.
/// </summary>
public sealed class PiSharpExtensionCommandControlOperations
{
    private readonly Func<IReadOnlyList<PiSharpExtensionCommandInfo>> _getCommands;
    private readonly Func<IReadOnlyList<string>> _getActiveTools;
    private readonly Func<IReadOnlyList<PiSharpExtensionToolInfo>> _getConfiguredTools;
    private readonly Func<PiSharpExtensionModelInfo?> _getModel;
    private readonly Action<IReadOnlyList<string>> _setActiveTools;
    private readonly Func<string, string, CancellationToken, Task<PiSharpModelSelectionResult>> _setModelAsync;
    private readonly Func<PiSharpThinkingLevel> _getThinkingLevel;
    private readonly Func<PiSharpThinkingLevel, CancellationToken, Task<PiSharpThinkingLevel>> _setThinkingLevelAsync;
    private readonly Action _abortCurrentRun;
    private readonly Func<CancellationToken, Task<PiSharpCompactionResult>> _compactCurrentSessionAsync;
    private readonly Func<string, PiSharpTreeNavigationOptions?, CancellationToken,
        Task<PiSharpTreeNavigationResult>>? _navigateTreeAsync;
    private readonly Action _requestShutdown;

    /// <summary>Creates the narrow control bridge used by a live CLI or protocol host.</summary>
    public PiSharpExtensionCommandControlOperations(
        Func<IReadOnlyList<PiSharpExtensionCommandInfo>> getCommands,
        Func<IReadOnlyList<string>> getActiveTools,
        Func<IReadOnlyList<PiSharpExtensionToolInfo>> getConfiguredTools,
        Func<PiSharpExtensionModelInfo?> getModel,
        Action<IReadOnlyList<string>> setActiveTools,
        Func<string, string, CancellationToken, Task<PiSharpModelSelectionResult>> setModelAsync,
        Func<PiSharpThinkingLevel> getThinkingLevel,
        Func<PiSharpThinkingLevel, CancellationToken, Task<PiSharpThinkingLevel>> setThinkingLevelAsync,
        Action abortCurrentRun,
        Func<CancellationToken, Task<PiSharpCompactionResult>> compactCurrentSessionAsync,
        Action requestShutdown,
        Func<string, PiSharpTreeNavigationOptions?, CancellationToken,
            Task<PiSharpTreeNavigationResult>>? navigateTreeAsync = null)
    {
        ArgumentNullException.ThrowIfNull(getCommands);
        ArgumentNullException.ThrowIfNull(getActiveTools);
        ArgumentNullException.ThrowIfNull(getConfiguredTools);
        ArgumentNullException.ThrowIfNull(getModel);
        ArgumentNullException.ThrowIfNull(setActiveTools);
        ArgumentNullException.ThrowIfNull(setModelAsync);
        ArgumentNullException.ThrowIfNull(getThinkingLevel);
        ArgumentNullException.ThrowIfNull(setThinkingLevelAsync);
        ArgumentNullException.ThrowIfNull(abortCurrentRun);
        ArgumentNullException.ThrowIfNull(compactCurrentSessionAsync);
        ArgumentNullException.ThrowIfNull(requestShutdown);

        _getCommands = getCommands;
        _getActiveTools = getActiveTools;
        _getConfiguredTools = getConfiguredTools;
        _getModel = getModel;
        _setActiveTools = setActiveTools;
        _setModelAsync = setModelAsync;
        _getThinkingLevel = getThinkingLevel;
        _setThinkingLevelAsync = setThinkingLevelAsync;
        _abortCurrentRun = abortCurrentRun;
        _compactCurrentSessionAsync = compactCurrentSessionAsync;
        _navigateTreeAsync = navigateTreeAsync;
        _requestShutdown = requestShutdown;
    }

    internal IReadOnlyList<PiSharpExtensionCommandInfo> GetCommands() => Snapshot(_getCommands());
    internal IReadOnlyList<string> GetActiveTools() => Snapshot(_getActiveTools());
    internal IReadOnlyList<PiSharpExtensionToolInfo> GetConfiguredTools() => Snapshot(_getConfiguredTools());
    internal PiSharpExtensionModelInfo? GetModel() => _getModel();
    internal void SetActiveTools(IEnumerable<string> toolNames)
    {
        ArgumentNullException.ThrowIfNull(toolNames);
        _setActiveTools(Array.AsReadOnly(toolNames.ToArray()));
    }
    internal Task<PiSharpModelSelectionResult> SetModelAsync(string providerId, string modelId,
        CancellationToken cancellationToken) => _setModelAsync(providerId, modelId, cancellationToken);
    internal PiSharpThinkingLevel GetThinkingLevel() => _getThinkingLevel();
    internal Task<PiSharpThinkingLevel> SetThinkingLevelAsync(PiSharpThinkingLevel level,
        CancellationToken cancellationToken) => _setThinkingLevelAsync(level, cancellationToken);
    internal void AbortCurrentRun() => _abortCurrentRun();
    internal Task<PiSharpCompactionResult> CompactCurrentSessionAsync(CancellationToken cancellationToken) =>
        _compactCurrentSessionAsync(cancellationToken);
    internal Task<PiSharpTreeNavigationResult> NavigateTreeAsync(string targetId,
        PiSharpTreeNavigationOptions? options, CancellationToken cancellationToken) =>
        _navigateTreeAsync is { } navigate
            ? navigate(targetId, options, cancellationToken)
            : Task.FromException<PiSharpTreeNavigationResult>(new InvalidOperationException(
                "Tree navigation is not available in this extension command context."));
    internal void RequestShutdown() => _requestShutdown();

    private static IReadOnlyList<T> Snapshot<T>(IReadOnlyList<T> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        return Array.AsReadOnly(values.ToArray());
    }
}

/// <summary>Compiled-extension controls for the host's current command and session catalogues.</summary>
public sealed partial class PiSharpExtensionCommandContext
{
    private PiSharpExtensionCommandControlOperations? _controlOperations;
    private int _controlContextActive = 1;
    private int _shutdownRequested;

    /// <summary>Creates a context with host-owned tool, model, thinking, and runtime operations.</summary>
    public PiSharpExtensionCommandContext(string workingDirectory, bool isIdle, bool isProjectTrusted,
        bool hasPendingMessages, Func<string, CancellationToken, Task> sendUserMessageAsync,
        Func<PiSharpCustomMessage, bool, PiSharpMessageDeliveryMode, CancellationToken, Task> sendCustomMessageAsync,
        PiSharpExtensionCommandControlOperations controlOperations)
        : this(workingDirectory, isIdle, isProjectTrusted, hasPendingMessages,
            sendUserMessageAsync, sendCustomMessageAsync)
    {
        _controlOperations = controlOperations ?? throw new ArgumentNullException(nameof(controlOperations));
    }

    /// <summary>Creates a mode-aware context with host-owned tool, model, thinking, and runtime operations.</summary>
    public PiSharpExtensionCommandContext(string workingDirectory, bool isIdle, bool isProjectTrusted,
        bool hasPendingMessages, Func<string, CancellationToken, Task> sendUserMessageAsync,
        Func<PiSharpCustomMessage, bool, PiSharpMessageDeliveryMode, CancellationToken, Task> sendCustomMessageAsync,
        string mode, bool hasUserInterface, IPiSharpExtensionUi? ui,
        PiSharpExtensionCommandControlOperations controlOperations)
        : this(workingDirectory, isIdle, isProjectTrusted, hasPendingMessages,
            sendUserMessageAsync, sendCustomMessageAsync, mode, hasUserInterface, ui)
    {
        _controlOperations = controlOperations ?? throw new ArgumentNullException(nameof(controlOperations));
    }

    /// <summary>Gets current built-in and dynamic commands with their source category.</summary>
    public IReadOnlyList<PiSharpExtensionCommandInfo> GetCommands()
    {
        EnsureControlContextActive();
        return RequireControlOperations().GetCommands();
    }

    /// <summary>Gets the names currently declared to the model.</summary>
    public IReadOnlyList<string> GetActiveTools()
    {
        EnsureControlContextActive();
        return RequireControlOperations().GetActiveTools();
    }

    /// <summary>Gets a read-only projection of configured tools and their exposure.</summary>
    public IReadOnlyList<PiSharpExtensionToolInfo> GetConfiguredTools()
    {
        EnsureControlContextActive();
        return RequireControlOperations().GetConfiguredTools();
    }

    /// <summary>Gets the selected model's stable identity and capabilities.</summary>
    public PiSharpExtensionModelInfo? GetModel()
    {
        EnsureControlContextActive();
        return RequireControlOperations().GetModel();
    }

    /// <summary>Replaces the tools declared on the next provider request; unknown and hidden tools are ignored.</summary>
    public void SetActiveTools(IEnumerable<string> toolNames)
    {
        EnsureControlContextActive();
        RequireControlOperations().SetActiveTools(toolNames);
    }

    /// <summary>Selects a session-local model by provider and model ID.</summary>
    public Task<PiSharpModelSelectionResult> SetModelAsync(string providerId, string modelId,
        CancellationToken cancellationToken = default)
    {
        EnsureControlContextActive();
        ArgumentException.ThrowIfNullOrWhiteSpace(providerId);
        ArgumentException.ThrowIfNullOrWhiteSpace(modelId);
        return RequireControlOperations().SetModelAsync(providerId, modelId, cancellationToken);
    }

    /// <summary>Gets the effective thinking level for the selected model.</summary>
    public PiSharpThinkingLevel GetThinkingLevel()
    {
        EnsureControlContextActive();
        return RequireControlOperations().GetThinkingLevel();
    }

    /// <summary>Changes the session-local thinking level, clamped to the selected model's capability.</summary>
    public Task<PiSharpThinkingLevel> SetThinkingLevelAsync(PiSharpThinkingLevel level,
        CancellationToken cancellationToken = default)
    {
        EnsureControlContextActive();
        if (!Enum.IsDefined(level)) throw new ArgumentOutOfRangeException(nameof(level));
        return RequireControlOperations().SetThinkingLevelAsync(level, cancellationToken);
    }

    /// <summary>Requests cancellation of the current model run. It is harmless while idle.</summary>
    public void AbortCurrentRun()
    {
        EnsureControlContextActive();
        RequireControlOperations().AbortCurrentRun();
    }

    /// <summary>Requests normal session compaction through the host's existing compaction path.</summary>
    public Task<PiSharpCompactionResult> CompactCurrentSessionAsync(CancellationToken cancellationToken = default)
    {
        EnsureControlContextActive();
        return RequireControlOperations().CompactCurrentSessionAsync(cancellationToken);
    }

    /// <summary>Selects a prior conversation branch, optionally summarizing the branch being left.</summary>
    public Task<PiSharpTreeNavigationResult> NavigateTreeAsync(string targetId,
        PiSharpTreeNavigationOptions? options = null, CancellationToken cancellationToken = default)
    {
        EnsureControlContextActive();
        ArgumentException.ThrowIfNullOrWhiteSpace(targetId);
        return RequireControlOperations().NavigateTreeAsync(targetId, options, cancellationToken);
    }

    /// <summary>Requests graceful host shutdown through its normal application path.</summary>
    public void RequestShutdown()
    {
        EnsureControlContextActive();
        if (Interlocked.Exchange(ref _shutdownRequested, 1) == 0)
            RequireControlOperations().RequestShutdown();
    }

    internal void DisposeControlContext() => Interlocked.Exchange(ref _controlContextActive, 0);

    private PiSharpExtensionCommandControlOperations RequireControlOperations() =>
        _controlOperations ?? throw new InvalidOperationException("Host control operations are not available in this extension context.");

    private void EnsureControlContextActive() =>
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _controlContextActive) == 0, this);
}
