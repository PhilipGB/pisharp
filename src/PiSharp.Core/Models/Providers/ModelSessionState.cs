using PiSharp.Core.Models;
using PiSharp.Core.Settings;

namespace PiSharp.Core.Models.Providers;

/// <summary>Mutation options for model/thinking changes (pinned ModelMutationOptions).</summary>
public sealed record ModelMutationOptions(bool Persist = false);

/// <summary>Result of a direct model change (pinned AgentSession.setModel).</summary>
public sealed record SetModelResult(
    ModelInfo Model,
    string ThinkingLevel,
    bool ModelChanged,
    bool ThinkingChanged);

/// <summary>Result of a model cycle step (pinned AgentSession.cycleModel).</summary>
public sealed record ModelCycleResult(
    ModelInfo Model,
    string? ThinkingLevel,
    bool IsScoped);

/// <summary>Result of a thinking-level change (pinned AgentSession.setThinkingLevel).</summary>
public sealed record SetThinkingResult(
    string Requested,
    string Effective,
    bool Changed);

/// <summary>
/// Live model and thinking-level state for one agent session (ported from pinned
/// AgentSession: setModel, cycleModel, setThinkingLevel, cycleThinkingLevel).
///
/// The state is framework-neutral: it owns the current selection, applies Pi's clamping and
/// settings-persistence rules, and reports what changed so the owning session controller can
/// append the durable model_change / thinking_level_change entries. It never writes to the
/// session transcript itself.
/// </summary>
public sealed class ModelSessionState
{
    private readonly ModelRuntime _runtime;
    private readonly SettingsManager _settings;
    private int? _contextWindowOverride;
    private int? _maxOutputOverride;
    private CurrentModelSelection? _current;
    private IReadOnlyList<ScopedModel> _scopedModels = [];

    public ModelSessionState(
        ModelRuntime runtime,
        SettingsManager settings,
        CurrentModelSelection? current = null,
        IReadOnlyList<ScopedModel>? scopedModels = null,
        int? contextWindowOverride = null,
        int? maxOutputOverride = null)
    {
        _runtime = runtime;
        _settings = settings;
        _contextWindowOverride = contextWindowOverride;
        _maxOutputOverride = maxOutputOverride;
        _current = WithSessionOverrides(current);
        if (scopedModels is not null)
        {
            _scopedModels = scopedModels;
        }
    }

    /// <summary>
    /// Sets the session-wide overrides (CLI --context-tokens / --max-output-tokens or their
    /// env vars). Called once at startup before the model is applied; re-applies them to the
    /// current selection so the effective limits stay consistent.
    /// </summary>
    public void SetSessionOverrides(int? contextWindow, int? maxOutput)
    {
        _contextWindowOverride = contextWindow;
        _maxOutputOverride = maxOutput;
        _current = WithSessionOverrides(_current);
    }

    /// <summary>
    /// Applies the session-level overrides to a selection (the overrides are session-wide in
    /// PiSharp; pinned Pi always follows the model metadata).
    /// </summary>
    private CurrentModelSelection? WithSessionOverrides(CurrentModelSelection? selection) =>
        selection is null
            ? null
            : new CurrentModelSelection(
                selection.Model,
                selection.ThinkingLevel,
                _contextWindowOverride ?? selection.ContextWindowOverride,
                _maxOutputOverride ?? selection.MaxOutputOverride);

    /// <summary>The current selection (model + thinking level + session overrides).</summary>
    public CurrentModelSelection? Current => _current;

    /// <summary>The current model, if any.</summary>
    public ModelInfo? Model => _current?.Model;

    /// <summary>The current Pi thinking level, if any.</summary>
    public string? ThinkingLevel => _current?.ThinkingLevel;

    /// <summary>Scoped models for cycling (from --models / settings enabledModels).</summary>
    public IReadOnlyList<ScopedModel> ScopedModels => _scopedModels;

    /// <summary>Whether the current model supports thinking/reasoning (pinned supportsThinking).</summary>
    public bool SupportsThinking => _current?.SupportsThinking == true;

    /// <summary>Replaces the current selection wholesale (startup restore, /new, /resume).</summary>
    public void ApplySelection(CurrentModelSelection? selection) =>
        _current = WithSessionOverrides(selection);

    /// <summary>Sets the scoped model list used by cycling.</summary>
    public void SetScopedModels(IReadOnlyList<ScopedModel> models) => _scopedModels = models;

    /// <summary>
    /// Thinking levels available for the current model (pinned getAvailableThinkingLevels).
    /// Non-reasoning models support only "off"; without a model the selectable options apply.
    /// </summary>
    public IReadOnlyList<string> GetAvailableThinkingLevels() =>
        _current?.Model is { } model
            ? ThinkingLevelSupport.GetSupportedLevels(model)
            : PiSharp.Core.Models.ThinkingLevel.Options;

    /// <summary>
    /// Sets the model directly (pinned AgentSession.setModel). Validates auth for the target
    /// provider and throws when none is configured. Applies the thinking level for the new
    /// model (per-model override, else global default, else current level). Persists the new
    /// default only when <paramref name="options"/> requests it.
    /// </summary>
    public async Task<SetModelResult> SetModelAsync(
        ModelInfo model,
        ModelMutationOptions options,
        CancellationToken cancellationToken = default)
    {
        if (await _runtime.CheckAuthAsync(model.Provider, cancellationToken) is null)
        {
            throw new InvalidOperationException($"No API key for {model.Provider}/{model.Id}");
        }

        var modelChanged = !ModelInfo.AreEqual(_current?.Model, model);
        var thinkingLevel = GetThinkingLevelForModelSwitch(model);
        _current = new CurrentModelSelection(
            model,
            _current?.ThinkingLevel ?? PiSharp.Core.Models.ThinkingLevel.Default,
            _current?.ContextWindowOverride,
            _current?.MaxOutputOverride);
        if (options.Persist)
        {
            _settings.SetDefaultModelAndProvider(model.Provider, model.Id);
            AddPersistedDefaultToNonEmptyScope(model);
        }

        // Apply thinking level for the new model. Per-model overrides take priority over the
        // global default; model persistence does not implicitly rewrite the thinking default.
        var thinking = SetThinkingLevel(thinkingLevel, new ModelMutationOptions());
        return new SetModelResult(model, _current!.ThinkingLevel, modelChanged, thinking.Changed);
    }

    /// <summary>
    /// Cycles to the next/previous model (pinned AgentSession.cycleModel). Uses scoped models
    /// when present, otherwise all available models. Returns null when cycling is a no-op
    /// (only one candidate). The candidate comes from the authenticated snapshot, so no
    /// separate auth check is performed (pinned behavior).
    /// </summary>
    public ModelCycleResult? CycleModel(string direction, ModelMutationOptions options)
    {
        if (_scopedModels.Count > 0)
        {
            return CycleScopedModel(direction, options);
        }

        return CycleAvailableModel(direction, options);
    }

    private ModelCycleResult? CycleScopedModel(string direction, ModelMutationOptions options)
    {
        var availableIds = new HashSet<(string, string)>(
            _runtime.GetAvailableSnapshot().Select(model => (model.Provider, model.Id)));
        var scopedModels = _scopedModels
            .Where(scoped => availableIds.Contains((scoped.Model.Provider, scoped.Model.Id)))
            .ToList();
        if (scopedModels.Count <= 1)
        {
            return null;
        }

        var currentIndex = IndexOf(scopedModels, scoped => ModelInfo.AreEqual(scoped.Model, _current?.Model));
        if (currentIndex == -1)
        {
            currentIndex = 0;
        }

        var nextIndex = direction == "backward"
            ? (currentIndex - 1 + scopedModels.Count) % scopedModels.Count
            : (currentIndex + 1) % scopedModels.Count;
        var next = scopedModels[nextIndex];
        return ApplyCycleTarget(next.Model, GetThinkingLevelForModelSwitch(next.Model, next.ThinkingLevel), options, isScoped: true);
    }

    private ModelCycleResult? CycleAvailableModel(string direction, ModelMutationOptions options)
    {
        var availableModels = _runtime.GetAvailableSnapshot();
        if (availableModels.Count <= 1)
        {
            return null;
        }

        var currentIndex = IndexOf(availableModels, model => ModelInfo.AreEqual(model, _current?.Model));
        if (currentIndex == -1)
        {
            currentIndex = 0;
        }

        var nextIndex = direction == "backward"
            ? (currentIndex - 1 + availableModels.Count) % availableModels.Count
            : (currentIndex + 1) % availableModels.Count;
        var nextModel = availableModels[nextIndex];
        return ApplyCycleTarget(nextModel, GetThinkingLevelForModelSwitch(nextModel), options, isScoped: false);
    }

    private ModelCycleResult ApplyCycleTarget(
        ModelInfo nextModel,
        string thinkingLevel,
        ModelMutationOptions options,
        bool isScoped)
    {
        _current = new CurrentModelSelection(
            nextModel,
            _current?.ThinkingLevel ?? PiSharp.Core.Models.ThinkingLevel.Default,
            _contextWindowOverride ?? _current?.ContextWindowOverride,
            _maxOutputOverride ?? _current?.MaxOutputOverride);
        if (options.Persist)
        {
            _settings.SetDefaultModelAndProvider(nextModel.Provider, nextModel.Id);
            AddPersistedDefaultToNonEmptyScope(nextModel);
        }

        // Explicit scoped-model thinking levels override defaults; setThinkingLevel clamps to
        // the new model's capabilities.
        SetThinkingLevel(thinkingLevel, new ModelMutationOptions());
        return new ModelCycleResult(nextModel, _current!.ThinkingLevel, isScoped);
    }

    /// <summary>
    /// Sets the thinking level (pinned AgentSession.setThinkingLevel). Clamps to the current
    /// model's supported levels; persists the requested (not clamped) level to the global
    /// default only when <paramref name="options"/> requests it. The caller persists a
    /// thinking_level_change entry only when <see cref="SetThinkingResult.Changed"/> is true.
    /// </summary>
    public SetThinkingResult SetThinkingLevel(string level, ModelMutationOptions options)
    {
        var availableLevels = GetAvailableThinkingLevels();
        var effectiveLevel = availableLevels.Contains(level, StringComparer.Ordinal)
            ? level
            : _current?.Model is { } model
                ? ThinkingLevelSupport.Clamp(model, level)
                : "off";

        var previousLevel = _current?.ThinkingLevel;
        var changed = !string.Equals(effectiveLevel, previousLevel, StringComparison.Ordinal);
        _current = _current?.WithThinking(effectiveLevel)
                  ?? new CurrentModelSelection(
                        null, effectiveLevel, _contextWindowOverride, _maxOutputOverride);

        if (options.Persist)
        {
            // Pi persists the requested level: clamping is model-specific and re-evaluated on
            // every switch.
            _settings.SetDefaultThinkingLevel(level);
        }

        return new SetThinkingResult(level, effectiveLevel, changed);
    }

    /// <summary>
    /// Cycles to the next thinking level (pinned AgentSession.cycleThinkingLevel). Returns
    /// null when the current model does not support thinking.
    /// </summary>
    public string? CycleThinkingLevel(ModelMutationOptions options)
    {
        if (!SupportsThinking)
        {
            return null;
        }

        var levels = GetAvailableThinkingLevels();
        var currentIndex = IndexOf(levels, level => string.Equals(level, _current?.ThinkingLevel, StringComparison.Ordinal));
        var nextIndex = (currentIndex + 1) % levels.Count;
        var nextLevel = levels[nextIndex];
        SetThinkingLevel(nextLevel, options);
        return nextLevel;
    }

    /// <summary>
    /// Resolves the thinking level applied when switching models (pinned
    /// _getThinkingLevelForModelSwitch): explicit level, else per-model override, else global
    /// default, else the current level.
    /// </summary>
    public string GetThinkingLevelForModelSwitch(ModelInfo? targetModel, string? explicitLevel = null)
    {
        if (explicitLevel is not null)
        {
            return explicitLevel;
        }

        if (targetModel is not null)
        {
            var perModel = _settings.GetModelThinkingLevel(targetModel.Provider, targetModel.Id);
            if (perModel is not null)
            {
                return perModel;
            }
        }

        return _settings.GetDefaultThinkingLevel() ?? _current?.ThinkingLevel ?? PiSharp.Core.Models.ThinkingLevel.Default;
    }

    /// <summary>
    /// When a model is persisted as the default while a scope is active, adds it to the scope
    /// and to the enabled-model patterns (pinned _addPersistedDefaultToNonEmptyScope).
    /// </summary>
    private void AddPersistedDefaultToNonEmptyScope(ModelInfo model)
    {
        if (_scopedModels.Count == 0)
        {
            return;
        }

        if (_scopedModels.Any(scoped => ModelInfo.AreEqual(scoped.Model, model)))
        {
            return;
        }

        _scopedModels = [.. _scopedModels, new ScopedModel(model, null)];

        var enabledModels = _settings.GetEnabledModels();
        if (enabledModels is null || enabledModels.Count == 0)
        {
            return;
        }

        var modelReference = model.Reference;
        if (enabledModels.Any(pattern => string.Equals(pattern, modelReference, StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        _settings.SetEnabledModels([.. enabledModels, modelReference]);
    }
private static int IndexOf<T>(IReadOnlyList<T> items, Func<T, bool> predicate)
    {
        for (var i = 0; i < items.Count; i++)
        {
            if (predicate(items[i]))
            {
                return i;
            }
        }

        return -1;
    }
}
