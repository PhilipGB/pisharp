using PiSharp.Core.Models;
using PiSharp.Core.Settings;

namespace PiSharp.Core.Models.Providers;

/// <summary>
/// Startup inputs for model/thinking resolution (pinned main.ts buildSessionOptions plus
/// sdk.ts createAgentSession model selection).
/// </summary>
public sealed record ModelStartupInput(
    string? CliProvider,
    string? CliModel,
    string? CliThinking,
    IReadOnlyList<ScopedModel> ScopedModels,
    bool HasExistingSession,
    (string Provider, string ModelId)? SavedSessionModel,
    string? SavedSessionThinking,
    bool SavedSessionHasThinkingEntry);

/// <summary>
/// Deterministic startup resolution result. <see cref="Model"/> is null when no model could
/// be resolved (the host must surface <see cref="ModelFallbackMessage"/> or <see cref="Error"/>
/// and stop); <see cref="Error"/> is set for a failed explicit CLI model request.
/// </summary>
public sealed record ModelStartupResult(
    ModelInfo? Model,
    string ThinkingLevel,
    string? Error,
    string? ModelFallbackMessage,
    IReadOnlyList<string> Warnings,
    bool CliThinkingOverride);

/// <summary>
/// Single deterministic startup model/thinking resolver (pinned Pi: main.ts
/// buildSessionOptions + sdk.ts createAgentSession). Precedence:
///
/// model: 1. CLI --model/--provider, 2. scoped models (new sessions only; saved default wins
/// when it is in scope), 3. session restore (resume/continue), 4. settings default, 5. first
/// available model.
///
/// thinking: 1. CLI --thinking, 2. model-pattern/scoped-pattern level, 3. existing session
/// (last thinking_level_change when present, else settings default), 4. per-model override,
/// 5. settings default (medium). The result is clamped to the model's supported levels; with
/// no model the level is "off".
/// </summary>
public static class ModelStartupResolver
{
    private const string NoModelsAvailableMessage =
        "No models available. Set an API key, use /login, or add models to models.json.";

    /// <summary>Resolves the startup model and thinking level (see type documentation).</summary>
    public static ModelStartupResult Resolve(
        ModelStartupInput input,
        SettingsManager settings,
        ModelRuntime runtime)
    {
        var warnings = new List<string>();
        ModelInfo? model = null;
        string? patternLevel = null;
        var cliThinkingFromModel = false;
        string? fallbackMessage = null;

        // 1. CLI --model (with --provider / provider-prefixed forms).
        if (input.CliModel is not null)
        {
            var resolved = ModelResolver.ResolveCliModel(input.CliProvider, input.CliModel, input.CliThinking, runtime);
            if (resolved.Warning is not null)
            {
                warnings.Add(resolved.Warning);
            }

            if (resolved.Error is not null)
            {
                return new ModelStartupResult(null, "off", resolved.Error, null, warnings, false);
            }

            if (resolved.Model is not null)
            {
                model = resolved.Model;
                // "--model <pattern>:<thinking>" shorthand; explicit --thinking wins (step 6).
                if (input.CliThinking is null && resolved.ThinkingLevel is not null)
                {
                    patternLevel = resolved.ThinkingLevel;
                    cliThinkingFromModel = true;
                }
            }
        }

        // 2. Scoped models: new sessions only. The saved default wins when it is in scope.
        if (model is null && input.ScopedModels.Count > 0 && !input.HasExistingSession)
        {
            var savedProvider = settings.GetDefaultProvider();
            var savedModelId = settings.GetDefaultModel();
            var savedModel = savedProvider is not null && savedModelId is not null
                ? runtime.GetModel(savedProvider, savedModelId)
                : null;
            var savedInScope = savedModel is null
                ? null
                : input.ScopedModels.FirstOrDefault(scoped => ModelInfo.AreEqual(scoped.Model, savedModel));

            var selected = savedInScope ?? input.ScopedModels[0];
            model = selected.Model;
            if (input.CliThinking is null && selected.ThinkingLevel is not null)
            {
                patternLevel = selected.ThinkingLevel;
            }
        }

        // 3. Session restore for resume/continue.
        if (model is null && input.HasExistingSession && input.SavedSessionModel is { } saved)
        {
            var (restored, restoreFallback) = ModelResolver.RestoreModelFromSession(
                saved.Provider,
                saved.ModelId,
                currentModel: null,
                message =>
                {
                    if (message.StartsWith("Warning:", StringComparison.Ordinal))
                    {
                        warnings.Add(message);
                    }
                },
                runtime);
            model = restored;
            if (restoreFallback is not null)
            {
                fallbackMessage = restoreFallback;
            }
        }

        // 4. Settings default, then first available model (scoped handling already done).
        if (model is null)
        {
            var initial = ModelResolver.FindInitialModel(
                null,
                null,
                [],
                isContinuing: input.HasExistingSession,
                settings.GetDefaultProvider(),
                settings.GetDefaultModel(),
                settings.GetDefaultThinkingLevel(),
                settings.GetAllModelThinkingLevels(),
                runtime);
            model = initial.Model;
            if (model is null)
            {
                fallbackMessage = fallbackMessage is null
                    ? NoModelsAvailableMessage
                    : $"{fallbackMessage} {NoModelsAvailableMessage}";
            }
            else if (fallbackMessage is not null)
            {
                fallbackMessage += $". Using {model.Provider}/{model.Id}";
            }
        }

        // 5. Thinking level cascade (pinned sdk.ts).
        var thinkingLevel = input.CliThinking ?? patternLevel;
        if (thinkingLevel is null && input.HasExistingSession)
        {
            thinkingLevel = input.SavedSessionHasThinkingEntry
                ? input.SavedSessionThinking
                : (settings.GetDefaultThinkingLevel() ?? ThinkingLevel.Default);
        }

        if (thinkingLevel is null && model is not null)
        {
            var perModel = settings.GetModelThinkingLevel(model.Provider, model.Id);
            if (perModel is not null)
            {
                thinkingLevel = perModel;
            }
        }

        thinkingLevel ??= settings.GetDefaultThinkingLevel() ?? ThinkingLevel.Default;

        // 6. Clamp to model capabilities; "off" when there is no model.
        var effectiveThinking = model is null
            ? "off"
            : ThinkingLevelSupport.Clamp(model, thinkingLevel);

        // Pinned main.ts: an explicit CLI thinking override (--thinking or the model-pattern
        // shorthand) is persisted as a thinking_level_change entry at startup.
        var cliThinkingOverride = (input.CliThinking is not null || cliThinkingFromModel) && model is not null;

        return new ModelStartupResult(
            model,
            effectiveThinking,
            null,
            fallbackMessage,
            warnings,
            cliThinkingOverride);
    }
}
