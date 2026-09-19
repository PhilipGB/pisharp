using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace PiSharp.Core.Settings;

/// <summary>
/// Typed accessors and setters mirroring the pinned Pi <c>SettingsManager</c> getters and
/// defaults. Getters resolve the effective (merged) settings; setters write the global
/// scope unless explicitly project-scoped, using Pi's modified-field save semantics.
/// </summary>
public sealed partial class SettingsManager
{
    /// <summary>
    /// Environment lookup used by a few Pi getters (PI_HARDWARE_CURSOR, PI_CLEAR_ON_SHRINK,
    /// VISUAL/EDITOR). Swappable in tests for deterministic behaviour.
    /// </summary>
    internal static Func<string, string?> EnvironmentLookup { get; set; } = Environment.GetEnvironmentVariable;

    /// <summary>Pi's default HTTP idle timeout in milliseconds.</summary>
    public const long DefaultHttpIdleTimeoutMs = 300_000;

    /// <summary>The thinking levels Pi exposes, in cycle order.</summary>
    public static readonly IReadOnlyList<string> ThinkingLevels =
        ["off", "minimal", "low", "medium", "high", "xhigh", "max"];

    /// <summary>Pi's default thinking level.</summary>
    public const string DefaultThinkingLevel = "medium";

    // ------------------------------------------------------------------
    // Identity / session
    // ------------------------------------------------------------------

    /// <summary>Gets the version of the changelog already shown to the user.</summary>
    public string? GetLastChangelogVersion() => _effective.LastChangelogVersion;

    /// <summary>Records that the given changelog version was shown (global scope).</summary>
    public void SetLastChangelogVersion(string version)
    {
        _global.LastChangelogVersion = version;
        SetGlobalRaw("lastChangelogVersion", (JsonNode)version);
        MarkGlobalModified("lastChangelogVersion");
        RecomputeEffective();
    }

    /// <summary>Gets the session storage directory override with ~ expanded.</summary>
    public string? GetSessionDir()
    {
        var sessionDir = _effective.SessionDir;
        return string.IsNullOrEmpty(sessionDir) ? null : SettingsPaths.ExpandTilde(sessionDir);
    }

    /// <summary>Sets (or clears when null) the session storage directory (global scope).</summary>
    public void SetSessionDir(string? sessionDir)
    {
        _global.SessionDir = sessionDir;
        SetGlobalRaw("sessionDir", sessionDir is null ? null : (JsonNode)sessionDir);
        MarkGlobalModified("sessionDir");
        RecomputeEffective();
    }

    /// <summary>Gets the default provider identifier.</summary>
    public string? GetDefaultProvider() => _effective.DefaultProvider;

    /// <summary>Gets the default model id.</summary>
    public string? GetDefaultModel() => _effective.DefaultModel;

    /// <summary>Sets the default provider (global scope).</summary>
    public void SetDefaultProvider(string provider)
    {
        _global.DefaultProvider = provider;
        SetGlobalRaw("defaultProvider", (JsonNode)provider);
        MarkGlobalModified("defaultProvider");
        RecomputeEffective();
    }

    /// <summary>Sets the default model (global scope).</summary>
    public void SetDefaultModel(string modelId)
    {
        _global.DefaultModel = modelId;
        SetGlobalRaw("defaultModel", (JsonNode)modelId);
        MarkGlobalModified("defaultModel");
        RecomputeEffective();
    }

    /// <summary>Sets both the default provider and model (global scope).</summary>
    public void SetDefaultModelAndProvider(string provider, string modelId)
    {
        _global.DefaultProvider = provider;
        _global.DefaultModel = modelId;
        SetGlobalRaw("defaultProvider", (JsonNode)provider);
        SetGlobalRaw("defaultModel", (JsonNode)modelId);
        MarkGlobalModified("defaultProvider");
        MarkGlobalModified("defaultModel");
        RecomputeEffective();
    }

    // ------------------------------------------------------------------
    // Queue behaviour
    // ------------------------------------------------------------------

    /// <summary>Gets the steering drain mode (all/one-at-a-time, default one-at-a-time).</summary>
    public string GetSteeringMode() => ResolveQueueMode(_effective.SteeringMode);

    /// <summary>Gets the follow-up drain mode (all/one-at-a-time, default one-at-a-time).</summary>
    public string GetFollowUpMode() => ResolveQueueMode(_effective.FollowUpMode);

    private static string ResolveQueueMode(string? mode) =>
        mode is not null && mode is "all" or "one-at-a-time" ? mode : "one-at-a-time";

    /// <summary>Sets the steering drain mode (global scope).</summary>
    public void SetSteeringMode(string mode)
    {
        ValidateQueueMode(mode);
        _global.SteeringMode = mode;
        SetGlobalRaw("steeringMode", (JsonNode)mode);
        MarkGlobalModified("steeringMode");
        RecomputeEffective();
    }

    /// <summary>Sets the follow-up drain mode (global scope).</summary>
    public void SetFollowUpMode(string mode)
    {
        ValidateQueueMode(mode);
        _global.FollowUpMode = mode;
        SetGlobalRaw("followUpMode", (JsonNode)mode);
        MarkGlobalModified("followUpMode");
        RecomputeEffective();
    }

    private static void ValidateQueueMode(string mode)
    {
        if (mode is not ("all" or "one-at-a-time"))
        {
            throw new FormatException($"Invalid queue mode '{mode}'. Expected 'all' or 'one-at-a-time'.");
        }
    }

    // ------------------------------------------------------------------
    // Theme and transport
    // ------------------------------------------------------------------

    /// <summary>Gets the raw theme setting (name or "auto/..." selection).</summary>
    public string? GetThemeSetting() => _effective.Theme;

    /// <summary>Gets the fixed theme name, or null for automatic (slash-separated) selections.</summary>
    public string? GetTheme()
    {
        var theme = GetThemeSetting();
        return theme is not null && theme.Contains('/') ? null : theme;
    }

    /// <summary>Sets the theme (global scope).</summary>
    public void SetTheme(string theme)
    {
        _global.Theme = theme;
        SetGlobalRaw("theme", (JsonNode)theme);
        MarkGlobalModified("theme");
        RecomputeEffective();
    }

    /// <summary>Gets the transport preference (default auto).</summary>
    public string GetTransport() => _effective.Transport ?? "auto";

    /// <summary>Sets the transport (global scope).</summary>
    public void SetTransport(string transport)
    {
        if (transport is not ("auto" or "sse" or "websocket"))
        {
            throw new FormatException($"Invalid transport '{transport}'. Expected 'auto', 'sse', or 'websocket'.");
        }
        _global.Transport = transport;
        SetGlobalRaw("transport", (JsonNode)transport);
        MarkGlobalModified("transport");
        RecomputeEffective();
    }

    // ------------------------------------------------------------------
    // Thinking levels
    // ------------------------------------------------------------------

    /// <summary>Gets the default thinking level, or null when unset.</summary>
    public string? GetDefaultThinkingLevel() => _effective.DefaultThinkingLevel;

    /// <summary>Sets the default thinking level (global scope).</summary>
    public void SetDefaultThinkingLevel(string level)
    {
        ValidateThinkingLevel(level);
        _global.DefaultThinkingLevel = level;
        SetGlobalRaw("defaultThinkingLevel", (JsonNode)level);
        MarkGlobalModified("defaultThinkingLevel");
        RecomputeEffective();
    }

    /// <summary>Gets the per-model thinking level override for provider/modelId, if any.</summary>
    public string? GetModelThinkingLevel(string provider, string modelId) =>
        _effective.ModelThinkingLevels is { } levels &&
        levels.TryGetValue($"{provider}/{modelId}", out var level)
            ? level
            : null;

    /// <summary>Gets all per-model thinking level overrides keyed by "provider/modelId".</summary>
    public IReadOnlyDictionary<string, string> GetAllModelThinkingLevels() =>
        _effective.ModelThinkingLevels is { } levels
            ? new Dictionary<string, string>(levels)
            : new Dictionary<string, string>();

    /// <summary>Sets a per-model thinking level override (global scope).</summary>
    public void SetModelThinkingLevel(string provider, string modelId, string level)
    {
        ValidateThinkingLevel(level);
        if (_global.ModelThinkingLevels is null)
        {
            _global.ModelThinkingLevels = new Dictionary<string, string>();
        }
        _global.ModelThinkingLevels[$"{provider}/{modelId}"] = level;
        SetGlobalRaw("modelThinkingLevels", JsonFrom(_global.ModelThinkingLevels));
        MarkGlobalModified("modelThinkingLevels");
        RecomputeEffective();
    }

    /// <summary>Removes a per-model thinking level override (global scope).</summary>
    public void RemoveModelThinkingLevel(string provider, string modelId)
    {
        if (_global.ModelThinkingLevels is null ||
            !_global.ModelThinkingLevels.Remove($"{provider}/{modelId}"))
        {
            return;
        }
        if (_global.ModelThinkingLevels.Count == 0)
        {
            _global.ModelThinkingLevels = null;
            SetGlobalRaw("modelThinkingLevels", null);
        }
        else
        {
            SetGlobalRaw("modelThinkingLevels", JsonFrom(_global.ModelThinkingLevels));
        }
        MarkGlobalModified("modelThinkingLevels");
        RecomputeEffective();
    }

    internal static void ValidateThinkingLevel(string level)
    {
        if (!ThinkingLevels.Contains(level))
        {
            throw new FormatException(
                $"Invalid thinking level '{level}'. Expected one of: {string.Join(", ", ThinkingLevels)}.");
        }
    }

    // ------------------------------------------------------------------
    // Compaction
    // ------------------------------------------------------------------

    /// <summary>Gets whether automatic compaction is enabled (default true).</summary>
    public bool GetCompactionEnabled() => _effective.Compaction?.Enabled ?? true;

    /// <summary>Sets whether automatic compaction is enabled (global scope).</summary>
    public void SetCompactionEnabled(bool enabled)
    {
        if (_global.Compaction is null)
        {
            _global.Compaction = new PiCompactionSettings();
        }
        _global.Compaction.Enabled = enabled;
        SetGlobalRaw("compaction", (JsonNode)enabled, "enabled");
        MarkGlobalModified("compaction", "enabled");
        RecomputeEffective();
    }

    /// <summary>Sets the reserve token budget (global scope). Throws for invalid values.</summary>
    public void SetCompactionReserveTokens(long reserveTokens)
    {
        if (!IsSafeNonNegativeInteger(reserveTokens) || reserveTokens < 0)
        {
            throw new FormatException(
                $"Invalid compaction.reserveTokens value: {reserveTokens}. Expected a non-negative safe integer.");
        }
        if (_global.Compaction is null)
        {
            _global.Compaction = new PiCompactionSettings();
        }
        _global.Compaction.ReserveTokens = reserveTokens;
        SetGlobalRaw("compaction", (JsonNode)reserveTokens, "reserveTokens");
        MarkGlobalModified("compaction", "reserveTokens");
        RecomputeEffective();
    }

    /// <summary>Sets the keep-recent token budget (global scope). Throws for invalid values.</summary>
    public void SetCompactionKeepRecentTokens(long keepRecentTokens)
    {
        if (!IsSafeNonNegativeInteger(keepRecentTokens) || keepRecentTokens < 0)
        {
            throw new FormatException(
                $"Invalid compaction.keepRecentTokens value: {keepRecentTokens}. Expected a non-negative safe integer.");
        }
        if (_global.Compaction is null)
        {
            _global.Compaction = new PiCompactionSettings();
        }
        _global.Compaction.KeepRecentTokens = keepRecentTokens;
        SetGlobalRaw("compaction", (JsonNode)keepRecentTokens, "keepRecentTokens");
        MarkGlobalModified("compaction", "keepRecentTokens");
        RecomputeEffective();
    }

    /// <summary>
    /// Gets the reserve token budget for a model: per-model override, ordinary setting,
    /// then the Pi default of 16384. Throws for invalid configured values (Pi behaviour).
    /// </summary>
    public long GetCompactionReserveTokens(string? provider = null, string? modelId = null) =>
        GetCompactionTokenSetting("reserveTokens", provider, modelId, 16_384);

    /// <summary>
    /// Gets the keep-recent token budget for a model: per-model override, ordinary setting,
    /// then the Pi default of 20000. Throws for invalid configured values (Pi behaviour).
    /// </summary>
    public long GetCompactionKeepRecentTokens(string? provider = null, string? modelId = null) =>
        GetCompactionTokenSetting("keepRecentTokens", provider, modelId, 20_000);

    private long GetCompactionTokenSetting(string field, string? provider, string? modelId, long defaultValue)
    {
        var compaction = _effective.Compaction;
        var ordinary = field == "reserveTokens" ? compaction?.ReserveTokens : compaction?.KeepRecentTokens;
        if (ordinary is { } ordinaryValue && (!IsSafeNonNegativeInteger(ordinaryValue) || ordinaryValue < 0))
        {
            throw new FormatException(
                $"Invalid compaction.{field} setting: {ordinaryValue}. Expected a non-negative safe integer.");
        }

        // Per-model override entries are keyed by exact "provider/modelId"; empty entries
        // simply contribute no overrides.
        var modelKey = provider is { } p && modelId is { } m ? $"{p}/{m}" : null;
        var entry = modelKey is null ? null : compaction?.ModelOverrides?.GetValueOrDefault(modelKey);
        var overrideValue = entry is null
            ? null
            : field == "reserveTokens" ? entry.ReserveTokens : entry.KeepRecentTokens;
        if (overrideValue is { } overrideVal && !IsSafeNonNegativeInteger(overrideVal))
        {
            throw new FormatException(
                $"Invalid compaction.modelOverrides[\"{modelKey}\"].{field} setting: {overrideVal}. " +
                "Expected a non-negative safe integer.");
        }

        return overrideValue ?? ordinary ?? defaultValue;
    }

    private static bool IsSafeNonNegativeInteger(long value) =>
        value >= 0 && value <= int.MaxValue;

    /// <summary>
    /// Resolves the effective compaction budgets for a model identity into the
    /// application-owned <see cref="CompactionSettings"/> record used by the planner.
    /// </summary>
    public CompactionSettings ResolveCompactionSettings(string? provider = null, string? modelId = null) =>
        new(
            enabled: GetCompactionEnabled(),
            reserveTokens: (int)GetCompactionReserveTokens(provider, modelId),
            keepRecentTokens: (int)GetCompactionKeepRecentTokens(provider, modelId));

    // ------------------------------------------------------------------
    // Branch summary
    // ------------------------------------------------------------------

    /// <summary>Gets the branch summary reserve tokens (default 16384).</summary>
    public long GetBranchSummaryReserveTokens() => _effective.BranchSummary?.ReserveTokens ?? 16_384;

    /// <summary>Gets whether the branch summary prompt is skipped (default false).</summary>
    public bool GetBranchSummarySkipPrompt() => _effective.BranchSummary?.SkipPrompt ?? false;

    // ------------------------------------------------------------------
    // Retries
    // ------------------------------------------------------------------

    /// <summary>Gets whether agent retries are enabled (default true).</summary>
    public bool GetRetryEnabled() => _effective.Retry?.Enabled ?? true;

    /// <summary>Sets whether agent retries are enabled (global scope).</summary>
    public void SetRetryEnabled(bool enabled)
    {
        if (_global.Retry is null)
        {
            _global.Retry = new PiRetrySettings();
        }
        _global.Retry.Enabled = enabled;
        SetGlobalRaw("retry", (JsonNode)enabled, "enabled");
        MarkGlobalModified("retry", "enabled");
        RecomputeEffective();
    }

    /// <summary>Sets the agent retry attempt count (global scope).</summary>
    public void SetRetryMaxRetries(int maxRetries)
    {
        if (maxRetries < 0 || maxRetries > 10)
        {
            throw new FormatException($"Invalid retry.maxRetries value: {maxRetries}. Expected 0-10.");
        }
        if (_global.Retry is null)
        {
            _global.Retry = new PiRetrySettings();
        }
        _global.Retry.MaxRetries = maxRetries;
        SetGlobalRaw("retry", (JsonNode)maxRetries, "maxRetries");
        MarkGlobalModified("retry", "maxRetries");
        RecomputeEffective();
    }

    /// <summary>Sets the exponential backoff base delay in milliseconds (global scope).</summary>
    public void SetRetryBaseDelayMs(long baseDelayMs)
    {
        if (baseDelayMs < 0)
        {
            throw new FormatException($"Invalid retry.baseDelayMs value: {baseDelayMs}. Expected a non-negative number.");
        }
        if (_global.Retry is null)
        {
            _global.Retry = new PiRetrySettings();
        }
        _global.Retry.BaseDelayMs = baseDelayMs;
        SetGlobalRaw("retry", (JsonNode)baseDelayMs, "baseDelayMs");
        MarkGlobalModified("retry", "baseDelayMs");
        RecomputeEffective();
    }

    /// <summary>
    /// Gets the resolved agent retry budget with Pi defaults: 3 attempts, 2000ms base
    /// delay, 60000ms maximum delay.
    /// </summary>
    public (bool Enabled, int MaxRetries, long BaseDelayMs, long MaxAgentDelayMs) GetRetrySettings()
    {
        var retry = _effective.Retry;
        return (
            GetRetryEnabled(),
            retry?.MaxRetries ?? 3,
            retry?.BaseDelayMs ?? 2_000,
            retry?.MaxAgentDelayMs ?? 60_000);
    }

    /// <summary>
    /// Maps the resolved retry budget onto the application retry policy used by the turn
    /// runner and summarizer.
    /// </summary>
    public RetryPolicyOptions GetRetryPolicy()
    {
        // The configured budget is preserved even when retries are disabled so callers
        // can display it (Pi consumers read the raw settings, enabled or not).
        var (enabled, maxRetries, baseDelayMs, maxAgentDelayMs) = GetRetrySettings();
        return new RetryPolicyOptions(
            enabled,
            maxRetries,
            TimeSpan.FromMilliseconds(baseDelayMs),
            TimeSpan.FromMilliseconds(maxAgentDelayMs));
    }

    /// <summary>Gets provider-level retry options with the Pi default 60000ms delay cap.</summary>
    public (long? TimeoutMs, int? MaxRetries, long MaxRetryDelayMs) GetProviderRetrySettings()
    {
        var provider = _effective.Retry?.Provider;
        return (provider?.TimeoutMs, provider?.MaxRetries, provider?.MaxRetryDelayMs ?? 60_000);
    }

    // ------------------------------------------------------------------
    // Shell
    // ------------------------------------------------------------------

    /// <summary>Gets the external editor command: setting, then VISUAL, then EDITOR, then platform default.</summary>
    public string GetExternalEditorCommand()
    {
        var configured = _effective.ExternalEditor;
        if (configured is { Length: > 0 } && configured.Trim().Length > 0)
        {
            return configured;
        }

        var environmentEditor = EnvironmentLookup("VISUAL") ?? EnvironmentLookup("EDITOR");
        if (environmentEditor is { Length: > 0 })
        {
            return environmentEditor;
        }

        return "nano";
    }

    /// <summary>Gets the custom shell path with ~ expanded.</summary>
    public string? GetShellPath()
    {
        var shellPath = _effective.ShellPath;
        return string.IsNullOrEmpty(shellPath) ? null : SettingsPaths.ExpandTilde(shellPath);
    }

    /// <summary>Sets (or clears when null) the custom shell path (global scope).</summary>
    public void SetShellPath(string? path)
    {
        _global.ShellPath = path;
        SetGlobalRaw("shellPath", path is null ? null : (JsonNode)path);
        MarkGlobalModified("shellPath");
        RecomputeEffective();
    }

    /// <summary>Gets the prefix prepended to every shell command.</summary>
    public string? GetShellCommandPrefix() => _effective.ShellCommandPrefix;

    /// <summary>Sets (or clears when null) the shell command prefix (global scope).</summary>
    public void SetShellCommandPrefix(string? prefix)
    {
        _global.ShellCommandPrefix = prefix;
        SetGlobalRaw("shellCommandPrefix", prefix is null ? null : (JsonNode)prefix);
        MarkGlobalModified("shellCommandPrefix");
        RecomputeEffective();
    }

    /// <summary>Gets the argv-style npm command override.</summary>
    public string[]? GetNpmCommand() => _effective.NpmCommand is { } command ? command.ToArray() : null;

    /// <summary>Sets (or clears when null) the npm command (global scope).</summary>
    public void SetNpmCommand(string[]? command)
    {
        _global.NpmCommand = command is null ? null : command.ToArray();
        SetGlobalRaw("npmCommand", command is null ? null : JsonFrom(command));
        MarkGlobalModified("npmCommand");
        RecomputeEffective();
    }

    // ------------------------------------------------------------------
    // Project trust default
    // ------------------------------------------------------------------

    /// <summary>
    /// Gets the default project trust decision from the global scope only; invalid or
    /// missing values resolve to Ask (Pi behaviour).
    /// </summary>
    public DefaultProjectTrust GetDefaultProjectTrust()
    {
        var value = _global.DefaultProjectTrust;
        return value is "always" || value is "never" ? Enum.Parse<DefaultProjectTrust>(value, true) : DefaultProjectTrust.Ask;
    }

    /// <summary>Sets the default project trust decision (global scope).</summary>
    public void SetDefaultProjectTrust(DefaultProjectTrust trust)
    {
        var value = trust switch
        {
            DefaultProjectTrust.Always => "always",
            DefaultProjectTrust.Never => "never",
            _ => "ask",
        };
        _global.DefaultProjectTrust = value;
        SetGlobalRaw("defaultProjectTrust", (JsonNode)value);
        MarkGlobalModified("defaultProjectTrust");
        RecomputeEffective();
    }

    // ------------------------------------------------------------------
    // Display / TUI preferences
    // ------------------------------------------------------------------

    /// <summary>Gets whether thinking blocks are hidden (default false).</summary>
    public bool GetHideThinkingBlock() => _effective.HideThinkingBlock ?? false;

    /// <summary>Sets whether thinking blocks are hidden (global scope).</summary>
    public void SetHideThinkingBlock(bool hidden)
    {
        _global.HideThinkingBlock = hidden;
        SetGlobalRaw("hideThinkingBlock", (JsonNode)hidden);
        MarkGlobalModified("hideThinkingBlock");
        RecomputeEffective();
    }

    /// <summary>Gets whether cache miss notices are shown (default false).</summary>
    public bool GetShowCacheMissNotices() => _effective.ShowCacheMissNotices ?? false;

    /// <summary>Sets whether cache miss notices are shown (global scope).</summary>
    public void SetShowCacheMissNotices(bool shown)
    {
        _global.ShowCacheMissNotices = shown;
        SetGlobalRaw("showCacheMissNotices", (JsonNode)shown);
        MarkGlobalModified("showCacheMissNotices");
        RecomputeEffective();
    }

    /// <summary>Gets whether startup output is quiet (default false).</summary>
    public bool GetQuietStartup() => _effective.QuietStartup ?? false;

    /// <summary>Sets quiet startup (global scope).</summary>
    public void SetQuietStartup(bool quiet)
    {
        _global.QuietStartup = quiet;
        SetGlobalRaw("quietStartup", (JsonNode)quiet);
        MarkGlobalModified("quietStartup");
        RecomputeEffective();
    }

    /// <summary>Gets whether the changelog is condensed after updates (default false).</summary>
    public bool GetCollapseChangelog() => _effective.CollapseChangelog ?? false;

    /// <summary>Sets condensed changelog behaviour (global scope).</summary>
    public void SetCollapseChangelog(bool collapse)
    {
        _global.CollapseChangelog = collapse;
        SetGlobalRaw("collapseChangelog", (JsonNode)collapse);
        MarkGlobalModified("collapseChangelog");
        RecomputeEffective();
    }

    /// <summary>Gets whether install telemetry pings are enabled (default true).</summary>
    public bool GetEnableInstallTelemetry() => _effective.EnableInstallTelemetry ?? true;

    /// <summary>Sets install telemetry (global scope).</summary>
    public void SetEnableInstallTelemetry(bool enabled)
    {
        _global.EnableInstallTelemetry = enabled;
        SetGlobalRaw("enableInstallTelemetry", (JsonNode)enabled);
        MarkGlobalModified("enableInstallTelemetry");
        RecomputeEffective();
    }

    /// <summary>Gets whether analytics sharing is enabled (default false).</summary>
    public bool GetEnableAnalytics() => _effective.EnableAnalytics ?? false;

    /// <summary>Gets the analytics tracking identifier.</summary>
    public string? GetTrackingId() => _effective.TrackingId;

    /// <summary>
    /// Sets the analytics opt-in; generates a tracking identifier on first opt-in
    /// (Pi behaviour).
    /// </summary>
    public void SetEnableAnalytics(bool enabled)
    {
        _global.EnableAnalytics = enabled;
        SetGlobalRaw("enableAnalytics", (JsonNode)enabled);
        MarkGlobalModified("enableAnalytics");
        if (enabled && string.IsNullOrEmpty(_global.TrackingId))
        {
            _global.TrackingId = Guid.NewGuid().ToString();
            SetGlobalRaw("trackingId", (JsonNode)_global.TrackingId);
            MarkGlobalModified("trackingId");
        }
        RecomputeEffective();
    }

    /// <summary>Gets the action for double-escape with an empty editor (default tree).</summary>
    public string GetDoubleEscapeAction()
    {
        var action = _effective.DoubleEscapeAction;
        return action is "fork" or "tree" or "none" ? action : "tree";
    }

    /// <summary>Sets the double-escape action (global scope).</summary>
    public void SetDoubleEscapeAction(string action)
    {
        if (action is not ("fork" or "tree" or "none"))
        {
            throw new FormatException($"Invalid doubleEscapeAction '{action}'. Expected 'fork', 'tree', or 'none'.");
        }
        _global.DoubleEscapeAction = action;
        SetGlobalRaw("doubleEscapeAction", (JsonNode)action);
        MarkGlobalModified("doubleEscapeAction");
        RecomputeEffective();
    }

    /// <summary>Gets the default tree filter (default: "default").</summary>
    public string GetTreeFilterMode()
    {
        var mode = _effective.TreeFilterMode;
        return mode is "default" or "no-tools" or "user-only" or "labeled-only" or "all" ? mode : "default";
    }

    /// <summary>Sets the default tree filter (global scope).</summary>
    public void SetTreeFilterMode(string mode)
    {
        if (mode is not ("default" or "no-tools" or "user-only" or "labeled-only" or "all"))
        {
            throw new FormatException(
                $"Invalid treeFilterMode '{mode}'. Expected 'default', 'no-tools', 'user-only', 'labeled-only', or 'all'.");
        }
        _global.TreeFilterMode = mode;
        SetGlobalRaw("treeFilterMode", (JsonNode)mode);
        MarkGlobalModified("treeFilterMode");
        RecomputeEffective();
    }

    /// <summary>Gets whether the hardware cursor is shown (default: PI_HARDWARE_CURSOR=1).</summary>
    public bool GetShowHardwareCursor() =>
        _effective.ShowHardwareCursor ?? EnvironmentLookup("PI_HARDWARE_CURSOR") == "1";

    /// <summary>Sets the hardware cursor preference (global scope).</summary>
    public void SetShowHardwareCursor(bool enabled)
    {
        _global.ShowHardwareCursor = enabled;
        SetGlobalRaw("showHardwareCursor", (JsonNode)enabled);
        MarkGlobalModified("showHardwareCursor");
        RecomputeEffective();
    }

    /// <summary>Gets the editor horizontal padding (default 0).</summary>
    public int GetEditorPaddingX() => _effective.EditorPaddingX ?? 0;

    /// <summary>Sets the editor horizontal padding (global scope).</summary>
    public void SetEditorPaddingX(int padding)
    {
        _global.EditorPaddingX = padding;
        SetGlobalRaw("editorPaddingX", (JsonNode)padding);
        MarkGlobalModified("editorPaddingX");
        RecomputeEffective();
    }

    /// <summary>Gets the chat output padding (0 or 1, default 1).</summary>
    public int GetOutputPad() => _effective.OutputPad == 0 ? 0 : 1;

    /// <summary>Sets the chat output padding (global scope).</summary>
    public void SetOutputPad(int padding)
    {
        if (padding is not (0 or 1))
        {
            throw new FormatException($"Invalid outputPad '{padding}'. Expected 0 or 1.");
        }
        _global.OutputPad = padding;
        SetGlobalRaw("outputPad", (JsonNode)padding);
        MarkGlobalModified("outputPad");
        RecomputeEffective();
    }

    /// <summary>Gets the autocomplete dropdown size (default 5).</summary>
    public int GetAutocompleteMaxVisible() => _effective.AutocompleteMaxVisible ?? 5;

    /// <summary>Sets the autocomplete dropdown size, clamped to 3..20 (global scope).</summary>
    public void SetAutocompleteMaxVisible(int maxVisible)
    {
        _global.AutocompleteMaxVisible = Math.Clamp(maxVisible, 3, 20);
        SetGlobalRaw("autocompleteMaxVisible", (JsonNode)_global.AutocompleteMaxVisible.Value);
        MarkGlobalModified("autocompleteMaxVisible");
        RecomputeEffective();
    }

    /// <summary>Gets the TUI mode (regular/fullscreen, default regular).</summary>
    public string GetTuiMode() => _effective.TuiMode == "fullscreen" ? "fullscreen" : "regular";

    /// <summary>Sets the TUI mode (global scope).</summary>
    public void SetTuiMode(string mode)
    {
        if (mode is not ("regular" or "fullscreen"))
        {
            throw new FormatException($"Invalid tuiMode '{mode}'. Expected 'regular' or 'fullscreen'.");
        }
        _global.TuiMode = mode;
        SetGlobalRaw("tuiMode", (JsonNode)mode);
        MarkGlobalModified("tuiMode");
        RecomputeEffective();
    }

    // ------------------------------------------------------------------
    // Terminal / images
    // ------------------------------------------------------------------

    /// <summary>Gets whether inline images are shown (default true).</summary>
    public bool GetShowImages() => _effective.Terminal?.ShowImages ?? true;

    /// <summary>Sets inline image display (global scope).</summary>
    public void SetShowImages(bool show)
    {
        if (_global.Terminal is null)
        {
            _global.Terminal = new PiTerminalSettings();
        }
        _global.Terminal.ShowImages = show;
        SetGlobalRaw("terminal", (JsonNode)show, "showImages");
        MarkGlobalModified("terminal", "showImages");
        RecomputeEffective();
    }

    /// <summary>Gets the preferred inline image width in cells (default 60, minimum 1).</summary>
    public int GetImageWidthCells()
    {
        var width = _effective.Terminal?.ImageWidthCells;
        return width is { } w && w is > 0 ? Math.Max(1, (int)Math.Truncate((double)w)) : 60;
    }

    /// <summary>Sets the inline image width, clamped to a minimum of 1 (global scope).</summary>
    public void SetImageWidthCells(int width)
    {
        if (_global.Terminal is null)
        {
            _global.Terminal = new PiTerminalSettings();
        }
        _global.Terminal.ImageWidthCells = Math.Max(1, width);
        SetGlobalRaw("terminal", (JsonNode)Math.Max(1, width), "imageWidthCells");
        MarkGlobalModified("terminal", "imageWidthCells");
        RecomputeEffective();
    }

    /// <summary>
    /// Gets whether empty rows are cleared when content shrinks: setting, then
    /// PI_CLEAR_ON_SHRINK=1, then false (Pi behaviour).
    /// </summary>
    public bool GetClearOnShrink() =>
        _effective.Terminal?.ClearOnShrink
        ?? EnvironmentLookup("PI_CLEAR_ON_SHRINK") == "1";

    /// <summary>Sets the clear-on-shrink preference (global scope).</summary>
    public void SetClearOnShrink(bool enabled)
    {
        if (_global.Terminal is null)
        {
            _global.Terminal = new PiTerminalSettings();
        }
        _global.Terminal.ClearOnShrink = enabled;
        SetGlobalRaw("terminal", (JsonNode)enabled, "clearOnShrink");
        MarkGlobalModified("terminal", "clearOnShrink");
        RecomputeEffective();
    }

    /// <summary>Gets whether terminal progress indicators are shown (default false).</summary>
    public bool GetShowTerminalProgress() => _effective.Terminal?.ShowTerminalProgress ?? false;

    /// <summary>Sets terminal progress display (global scope).</summary>
    public void SetShowTerminalProgress(bool enabled)
    {
        if (_global.Terminal is null)
        {
            _global.Terminal = new PiTerminalSettings();
        }
        _global.Terminal.ShowTerminalProgress = enabled;
        SetGlobalRaw("terminal", (JsonNode)enabled, "showTerminalProgress");
        MarkGlobalModified("terminal", "showTerminalProgress");
        RecomputeEffective();
    }

    /// <summary>Gets whether images are auto-resized before sending (default true).</summary>
    public bool GetImageAutoResize() => _effective.Images?.AutoResize ?? true;

    /// <summary>Sets image auto-resize (global scope).</summary>
    public void SetImageAutoResize(bool enabled)
    {
        if (_global.Images is null)
        {
            _global.Images = new PiImageSettings();
        }
        _global.Images.AutoResize = enabled;
        SetGlobalRaw("images", (JsonNode)enabled, "autoResize");
        MarkGlobalModified("images", "autoResize");
        RecomputeEffective();
    }

    /// <summary>Gets whether all images are blocked from providers.</summary>
    public bool GetBlockImages() => _effective.Images?.BlockImages ?? false;

    /// <summary>Sets the image block (global scope).</summary>
    public void SetBlockImages(bool blocked)
    {
        if (_global.Images is null)
        {
            _global.Images = new PiImageSettings();
        }
        _global.Images.BlockImages = blocked;
        SetGlobalRaw("images", (JsonNode)blocked, "blockImages");
        MarkGlobalModified("images", "blockImages");
        RecomputeEffective();
    }

    // ------------------------------------------------------------------
    // Markdown / warnings
    // ------------------------------------------------------------------

    /// <summary>Gets the code block indentation (default two spaces).</summary>
    public string GetMarkdownCodeBlockIndent() => _effective.Markdown?.CodeBlockIndent ?? "  ";

    /// <summary>Gets the mermaid rendering mode (default streaming).</summary>
    public string GetMermaidRenderingMode()
    {
        var mode = _effective.Markdown?.Mermaid;
        return mode is "off" or "final" or "streaming" ? mode : "streaming";
    }

    /// <summary>Gets whether Anthropic extra-usage warnings are enabled (default true).</summary>
    public bool GetAnthropicExtraUsageWarning() => _effective.Warnings?.AnthropicExtraUsage ?? true;

    // ------------------------------------------------------------------
    // Resources and packages
    // ------------------------------------------------------------------

    /// <summary>Gets whether skills register /skill:name commands (default true).</summary>
    public bool GetEnableSkillCommands() => _effective.EnableSkillCommands ?? true;

    /// <summary>Sets skill command registration (global scope).</summary>
    public void SetEnableSkillCommands(bool enabled)
    {
        _global.EnableSkillCommands = enabled;
        SetGlobalRaw("enableSkillCommands", (JsonNode)enabled);
        MarkGlobalModified("enableSkillCommands");
        RecomputeEffective();
    }

    /// <summary>Gets the configured package sources.</summary>
    public IReadOnlyList<PiPackageSource> GetPackages() =>
        _effective.Packages is { Length: > 0 } packages ? packages.ToArray() : [];

    /// <summary>Sets the package sources (global scope).</summary>
    public void SetPackages(IEnumerable<PiPackageSource> packages)
    {
        _global.Packages = packages.ToArray();
        SetGlobalRaw("packages", JsonFrom(_global.Packages));
        MarkGlobalModified("packages");
        RecomputeEffective();
    }

    /// <summary>Gets the configured local extension paths.</summary>
    public IReadOnlyList<string> GetExtensionPaths() => _effective.Extensions is { Length: > 0 } e ? e.ToArray() : [];

    /// <summary>Sets the local extension paths (global scope).</summary>
    public void SetExtensionPaths(IEnumerable<string> paths)
    {
        _global.Extensions = paths.ToArray();
        SetGlobalRaw("extensions", JsonFrom(_global.Extensions));
        MarkGlobalModified("extensions");
        RecomputeEffective();
    }

    /// <summary>Gets the configured local skill paths.</summary>
    public IReadOnlyList<string> GetSkillPaths() => _effective.Skills is { Length: > 0 } s ? s.ToArray() : [];

    /// <summary>Sets the local skill paths (global scope).</summary>
    public void SetSkillPaths(IEnumerable<string> paths)
    {
        _global.Skills = paths.ToArray();
        SetGlobalRaw("skills", JsonFrom(_global.Skills));
        MarkGlobalModified("skills");
        RecomputeEffective();
    }

    /// <summary>Gets the configured local prompt template paths.</summary>
    public IReadOnlyList<string> GetPromptTemplatePaths() => _effective.Prompts is { Length: > 0 } p ? p.ToArray() : [];

    /// <summary>Sets the local prompt template paths (global scope).</summary>
    public void SetPromptTemplatePaths(IEnumerable<string> paths)
    {
        _global.Prompts = paths.ToArray();
        SetGlobalRaw("prompts", JsonFrom(_global.Prompts));
        MarkGlobalModified("prompts");
        RecomputeEffective();
    }

    /// <summary>Gets the configured local theme paths.</summary>
    public IReadOnlyList<string> GetThemePaths() => _effective.Themes is { Length: > 0 } t ? t.ToArray() : [];

    /// <summary>Sets the local theme paths (global scope).</summary>
    public void SetThemePaths(IEnumerable<string> paths)
    {
        _global.Themes = paths.ToArray();
        SetGlobalRaw("themes", JsonFrom(_global.Themes));
        MarkGlobalModified("themes");
        RecomputeEffective();
    }

    /// <summary>Gets the model patterns enabled for cycling, or null when unset.</summary>
    public IReadOnlyList<string>? GetEnabledModels() =>
        _effective.EnabledModels is { } models ? models.ToArray() : null;

    /// <summary>Sets (or clears when null) the enabled model patterns (global scope).</summary>
    public void SetEnabledModels(IEnumerable<string>? patterns)
    {
        _global.EnabledModels = patterns?.ToArray();
        SetGlobalRaw("enabledModels", patterns is null ? null : JsonFrom(patterns));
        MarkGlobalModified("enabledModels");
        RecomputeEffective();
    }

    /// <summary>Gets the initial built-in tool selection, or null when unset (empty lists are preserved).</summary>
    public IReadOnlyList<string>? GetDefaultTools() =>
        _effective.DefaultTools is { } tools ? tools.ToArray() : null;

    // ------------------------------------------------------------------
    // Project-scoped setters
    // ------------------------------------------------------------------

    private void AssertProjectTrustedForWrite()
    {
        if (!_projectTrusted)
        {
            throw new InvalidOperationException("Project is not trusted; refusing to write project settings");
        }
    }

    /// <summary>Sets the project package sources (project scope).</summary>
    public void SetProjectPackages(IEnumerable<PiPackageSource> packages)
    {
        AssertProjectTrustedForWrite();
        _project.Packages = packages.ToArray();
        SetProjectRaw("packages", JsonFrom(_project.Packages));
        MarkProjectModified("packages");
        RecomputeEffective();
    }

    /// <summary>Sets the project extension paths (project scope).</summary>
    public void SetProjectExtensionPaths(IEnumerable<string> paths)
    {
        AssertProjectTrustedForWrite();
        _project.Extensions = paths.ToArray();
        SetProjectRaw("extensions", JsonFrom(_project.Extensions));
        MarkProjectModified("extensions");
        RecomputeEffective();
    }

    /// <summary>Sets the project skill paths (project scope).</summary>
    public void SetProjectSkillPaths(IEnumerable<string> paths)
    {
        AssertProjectTrustedForWrite();
        _project.Skills = paths.ToArray();
        SetProjectRaw("skills", JsonFrom(_project.Skills));
        MarkProjectModified("skills");
        RecomputeEffective();
    }

    /// <summary>Sets the project prompt template paths (project scope).</summary>
    public void SetProjectPromptTemplatePaths(IEnumerable<string> paths)
    {
        AssertProjectTrustedForWrite();
        _project.Prompts = paths.ToArray();
        SetProjectRaw("prompts", JsonFrom(_project.Prompts));
        MarkProjectModified("prompts");
        RecomputeEffective();
    }

    /// <summary>Sets the project theme paths (project scope).</summary>
    public void SetProjectThemePaths(IEnumerable<string> paths)
    {
        AssertProjectTrustedForWrite();
        _project.Themes = paths.ToArray();
        SetProjectRaw("themes", JsonFrom(_project.Themes));
        MarkProjectModified("themes");
        RecomputeEffective();
    }

    // ------------------------------------------------------------------
    // HTTP / proxy
    // ------------------------------------------------------------------

    /// <summary>Gets the proxy URL applied to managed HTTP clients.</summary>
    public string? GetHttpProxy() => _effective.HttpProxy;

    /// <summary>
    /// Gets the HTTP idle timeout: configured value (number or "disabled"/numeric string),
    /// then the Pi default of 300000ms. Throws for invalid configured values (Pi behaviour).
    /// </summary>
    public long GetHttpIdleTimeoutMs()
    {
        var value = _effective.HttpIdleTimeoutMs;
        if (value is null)
        {
            return DefaultHttpIdleTimeoutMs;
        }

        var parsed = ParseTimeoutValue(value.Value);
        if (parsed is null)
        {
            throw new FormatException($"Invalid httpIdleTimeoutMs setting: {value.Value}");
        }
        return parsed.Value;
    }

    /// <summary>Sets the HTTP idle timeout in milliseconds (global scope).</summary>
    public void SetHttpIdleTimeoutMs(long timeoutMs)
    {
        if (timeoutMs < 0)
        {
            throw new FormatException($"Invalid httpIdleTimeoutMs setting: {timeoutMs}");
        }
        _global.HttpIdleTimeoutMs = JsonDocument.Parse(timeoutMs.ToString(CultureInfo.InvariantCulture)).RootElement;
        SetGlobalRaw("httpIdleTimeoutMs", (JsonNode)timeoutMs);
        MarkGlobalModified("httpIdleTimeoutMs");
        RecomputeEffective();
    }

    /// <summary>Gets the WebSocket connect timeout, or null when unset.</summary>
    public long? GetWebSocketConnectTimeoutMs()
    {
        var value = _effective.WebsocketConnectTimeoutMs;
        if (value is null)
        {
            return null;
        }
        var parsed = ParseTimeoutValue(value.Value);
        if (parsed is null)
        {
            throw new FormatException($"Invalid websocketConnectTimeoutMs setting: {value.Value}");
        }
        return parsed;
    }

    private static long? ParseTimeoutValue(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Number when value.TryGetInt64(out var number) && number is >= 0 and <= int.MaxValue => number,
        JsonValueKind.String => ParseTimeoutString(value.GetString()),
        _ => null,
    };

    private static long? ParseTimeoutString(string? raw)
    {
        var trimmed = raw?.Trim();
        if (string.Equals(trimmed, "disabled", StringComparison.OrdinalIgnoreCase))
        {
            return 0;
        }
        if (string.IsNullOrEmpty(trimmed))
        {
            return null;
        }
        return long.TryParse(trimmed, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) &&
               parsed is >= 0 and <= int.MaxValue
            ? parsed
            : null;
    }
}
