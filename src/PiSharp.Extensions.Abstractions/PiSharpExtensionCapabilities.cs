using System.Text.Json;

namespace PiSharp.Extensions.Abstractions;

/// <summary>
/// Safe, callback-scoped access to the current host state and operations already owned by PiSharp.
/// Every method checks that the callback scope is still active.
/// </summary>
public sealed class PiSharpExtensionCapabilities : IDisposable
{
    private readonly PiSharpExtensionCommandControlOperations? _controls;
    private readonly PiSharpExtensionSessionContext? _session;
    private readonly PiSharpExtensionSettingsFlagContext? _settingsAndFlags;
    private readonly Func<PiSharpExtensionContextUsage?>? _getContextUsage;
    private readonly Func<string?>? _getSystemPrompt;
    private readonly Func<string, CancellationToken, Task>? _sendUserMessageAsync;
    private readonly Func<PiSharpCustomMessage, bool, PiSharpMessageDeliveryMode, CancellationToken, Task>?
        _sendCustomMessageAsync;
    private readonly Func<PiSharpExtensionModelInfo?>? _getModel;
    private readonly Func<PiSharpThinkingLevel>? _getThinkingLevel;
    private readonly Func<IReadOnlyList<string>>? _getActiveTools;
    private readonly Func<IReadOnlyList<PiSharpExtensionToolInfo>>? _getConfiguredTools;
    private readonly Func<IReadOnlyList<PiSharpExtensionCommandInfo>>? _getCommands;
    private readonly CancellationTokenSource _lifetime = new();
    private int _isActive = 1;
    private int _shutdownRequested;

    /// <summary>Creates the host bridge used by live command and event callback contexts.</summary>
    /// <param name="controlOperations">Existing host-owned controls, when this host mode supplies them.</param>
    /// <param name="session">The callback-scoped canonical session bridge.</param>
    /// <param name="settingsAndFlags">The callback-scoped effective settings and extension flag reader.</param>
    /// <param name="getContextUsage">Current token usage, or null when unknown.</param>
    /// <param name="getSystemPrompt">The current effective system prompt, when the host can provide it.</param>
    /// <param name="sendUserMessageAsync">The normal host path for submitting user text.</param>
    /// <param name="sendCustomMessageAsync">The normal host path for submitting extension messages.</param>
    /// <param name="getModel">Current model projection for modes without command controls.</param>
    /// <param name="getThinkingLevel">Current thinking value for modes without command controls.</param>
    /// <param name="getActiveTools">Current active tool names for modes without command controls.</param>
    /// <param name="getConfiguredTools">Current tool projections for modes without command controls.</param>
    /// <param name="getCommands">Current command projections for modes without command controls.</param>
    public PiSharpExtensionCapabilities(
        PiSharpExtensionCommandControlOperations? controlOperations,
        PiSharpExtensionSessionContext? session,
        PiSharpExtensionSettingsFlagContext? settingsAndFlags,
        Func<PiSharpExtensionContextUsage?>? getContextUsage = null,
        Func<string?>? getSystemPrompt = null,
        Func<string, CancellationToken, Task>? sendUserMessageAsync = null,
        Func<PiSharpCustomMessage, bool, PiSharpMessageDeliveryMode, CancellationToken, Task>? sendCustomMessageAsync = null,
        Func<PiSharpExtensionModelInfo?>? getModel = null,
        Func<PiSharpThinkingLevel>? getThinkingLevel = null,
        Func<IReadOnlyList<string>>? getActiveTools = null,
        Func<IReadOnlyList<PiSharpExtensionToolInfo>>? getConfiguredTools = null,
        Func<IReadOnlyList<PiSharpExtensionCommandInfo>>? getCommands = null)
    {
        _controls = controlOperations;
        _session = session;
        _settingsAndFlags = settingsAndFlags;
        _getContextUsage = getContextUsage;
        _getSystemPrompt = getSystemPrompt;
        _sendUserMessageAsync = sendUserMessageAsync;
        _sendCustomMessageAsync = sendCustomMessageAsync;
        _getModel = getModel;
        _getThinkingLevel = getThinkingLevel;
        _getActiveTools = getActiveTools;
        _getConfiguredTools = getConfiguredTools;
        _getCommands = getCommands;
    }

    /// <summary>Returns a detached snapshot of the current session branch, when available.</summary>
    public PiSharpExtensionSessionSnapshot? GetSessionSnapshot()
    {
        EnsureActive();
        return _session?.Snapshot;
    }

    /// <summary>Gets the selected model's stable identity and capabilities.</summary>
    public PiSharpExtensionModelInfo? GetModel()
    {
        EnsureActive();
        return _controls?.GetModel() ?? _getModel?.Invoke();
    }

    /// <summary>Gets the effective thinking level.</summary>
    public PiSharpThinkingLevel GetThinkingLevel()
    {
        EnsureActive();
        return _controls?.GetThinkingLevel() ?? _getThinkingLevel?.Invoke() ??
            throw MissingCapability("thinking-level reads");
    }

    /// <summary>Gets names currently declared to the model.</summary>
    public IReadOnlyList<string> GetActiveTools()
    {
        EnsureActive();
        return _controls?.GetActiveTools() ?? Snapshot(_getActiveTools?.Invoke() ?? []);
    }

    /// <summary>Gets configured tools and their current exposure.</summary>
    public IReadOnlyList<PiSharpExtensionToolInfo> GetConfiguredTools()
    {
        EnsureActive();
        return _controls?.GetConfiguredTools() ?? Snapshot(_getConfiguredTools?.Invoke() ?? []);
    }

    /// <summary>Gets built-in and dynamic commands with their source category.</summary>
    public IReadOnlyList<PiSharpExtensionCommandInfo> GetCommands()
    {
        EnsureActive();
        return _controls?.GetCommands() ?? Snapshot(_getCommands?.Invoke() ?? []);
    }

    /// <summary>Returns a fresh detached JSON snapshot of effective settings.</summary>
    public JsonElement GetSettings()
    {
        EnsureActive();
        return (_settingsAndFlags ?? throw MissingCapability("settings access")).GetSettings();
    }

    /// <summary>Gets this extension's registered flag value, or null when unset or unregistered.</summary>
    public PiSharpExtensionFlagValue? GetFlag(string name)
    {
        EnsureActive();
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        return (_settingsAndFlags ?? throw MissingCapability("extension flag access")).GetFlag(name);
    }

    /// <summary>Gets context usage when known; null means the host cannot currently estimate it.</summary>
    public PiSharpExtensionContextUsage? GetContextUsage()
    {
        EnsureActive();
        return _getContextUsage?.Invoke();
    }

    /// <summary>Gets the current effective system prompt when the host can provide it.</summary>
    public string? GetSystemPrompt()
    {
        EnsureActive();
        return _getSystemPrompt?.Invoke();
    }

    /// <summary>Changes the tool declarations used by the next provider request.</summary>
    public void SetActiveTools(IEnumerable<string> toolNames)
    {
        EnsureActive();
        RequireControls().SetActiveTools(toolNames);
    }

    /// <summary>Selects a session-local model by stable provider and model identity.</summary>
    public async Task<PiSharpModelSelectionResult> SetModelAsync(string providerId, string modelId,
        CancellationToken cancellationToken = default)
    {
        EnsureActive();
        using var linked = Link(cancellationToken);
        return await RequireControls().SetModelAsync(providerId, modelId, linked.Token).ConfigureAwait(false);
    }

    /// <summary>Changes the effective thinking level, clamped to model capability by the host.</summary>
    public async Task<PiSharpThinkingLevel> SetThinkingLevelAsync(PiSharpThinkingLevel level,
        CancellationToken cancellationToken = default)
    {
        EnsureActive();
        using var linked = Link(cancellationToken);
        return await RequireControls().SetThinkingLevelAsync(level, linked.Token).ConfigureAwait(false);
    }

    /// <summary>Submits text through the host's normal user-turn path.</summary>
    public async Task SendUserMessageAsync(string prompt, CancellationToken cancellationToken = default)
    {
        EnsureActive();
        ArgumentException.ThrowIfNullOrWhiteSpace(prompt);
        using var linked = Link(cancellationToken);
        await (_sendUserMessageAsync ?? throw MissingCapability("user-message delivery"))(prompt, linked.Token)
            .ConfigureAwait(false);
    }

    /// <summary>Sends an extension message through the host's normal queue and run path.</summary>
    public async Task SendCustomMessageAsync(PiSharpCustomMessage message, bool triggerTurn = true,
        PiSharpMessageDeliveryMode deliveryMode = PiSharpMessageDeliveryMode.Steer,
        CancellationToken cancellationToken = default)
    {
        EnsureActive();
        ArgumentNullException.ThrowIfNull(message);
        ArgumentException.ThrowIfNullOrWhiteSpace(message.CustomType);
        ArgumentNullException.ThrowIfNull(message.Content);
        if (!Enum.IsDefined(deliveryMode)) throw new ArgumentOutOfRangeException(nameof(deliveryMode));
        using var linked = Link(cancellationToken);
        await (_sendCustomMessageAsync ?? throw MissingCapability("custom-message delivery"))(
            message, triggerTurn, deliveryMode, linked.Token).ConfigureAwait(false);
    }

    /// <summary>Requests cancellation of the current run.</summary>
    public void AbortCurrentRun()
    {
        EnsureActive();
        RequireControls().AbortCurrentRun();
    }

    /// <summary>Requests compaction through the host's canonical session compactor.</summary>
    public async Task<PiSharpCompactionResult> CompactCurrentSessionAsync(
        CancellationToken cancellationToken = default)
    {
        EnsureActive();
        using var linked = Link(cancellationToken);
        return await RequireControls().CompactCurrentSessionAsync(linked.Token).ConfigureAwait(false);
    }

    /// <summary>Requests graceful shutdown through the host's normal application path.</summary>
    public void RequestShutdown()
    {
        EnsureActive();
        if (Interlocked.Exchange(ref _shutdownRequested, 1) == 0)
            RequireControls().RequestShutdown();
    }

    /// <summary>Revokes all live operations after the owning callback ends.</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _isActive, 0) == 0) return;
        try { _lifetime.Cancel(); }
        finally
        {
            _lifetime.Dispose();
            _settingsAndFlags?.Dispose();
        }
    }

    private PiSharpExtensionCommandControlOperations RequireControls()
    {
        EnsureActive();
        return _controls ?? throw MissingCapability("host controls");
    }

    private CancellationTokenSource Link(CancellationToken cancellationToken)
    {
        EnsureActive();
        return CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
    }

    private void EnsureActive() => ObjectDisposedException.ThrowIf(Volatile.Read(ref _isActive) == 0, this);

    private static InvalidOperationException MissingCapability(string capability) =>
        new($"The host did not provide {capability} for this callback.");

    private static IReadOnlyList<T> Snapshot<T>(IReadOnlyList<T> values) =>
        Array.AsReadOnly(values.ToArray());
}
