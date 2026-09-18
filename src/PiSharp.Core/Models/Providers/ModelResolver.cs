using System.Text.RegularExpressions;

namespace PiSharp.Core.Models.Providers;

/// <summary>A model with an optional explicit thinking level from its pattern.</summary>
public sealed record ScopedModel(ModelInfo Model, string? ThinkingLevel);

/// <summary>A model scope diagnostic (pinned Pi: ModelScopeDiagnostic).</summary>
public sealed record ModelScopeDiagnostic(
    string Type, string Code, string Message, string Pattern);

/// <summary>Result of resolving model scope patterns.</summary>
public sealed record ResolveModelScopeResult(
    IReadOnlyList<ScopedModel> ScopedModels,
    IReadOnlyList<ModelScopeDiagnostic> Diagnostics);

/// <summary>Result of CLI model resolution (pinned Pi: ResolveCliModelResult).</summary>
public sealed record ResolveCliModelResult(
    ModelInfo? Model,
    string? ThinkingLevel,
    string? Warning,
    string? Error);

/// <summary>Result of initial model selection (pinned Pi: InitialModelResult).</summary>
public sealed record InitialModelResult(
    ModelInfo? Model,
    string ThinkingLevel,
    string? FallbackMessage,
    string? Error);

/// <summary>
/// Model resolution, scoping, and initial selection (pinned Pi: model-resolver.ts).
/// </summary>
public static class ModelResolver
{
    /// <summary>Default model IDs for each known provider (pinned defaultModelPerProvider).</summary>
    public static readonly IReadOnlyDictionary<string, string> DefaultModelPerProvider =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["amazon-bedrock"] = "us.anthropic.claude-opus-4-6-v1",
            ["ant-ling"] = "Ring-2.6-1T",
            ["anthropic"] = "claude-opus-4-8",
            ["openai"] = "gpt-5.5",
            ["azure-openai-responses"] = "gpt-5.4",
            ["openai-codex"] = "gpt-5.5",
            ["radius"] = "balanced",
            ["nvidia"] = "nvidia/nemotron-3-super-120b-a12b",
            ["deepseek"] = "deepseek-v4-pro",
            ["google"] = "gemini-3.1-pro-preview",
            ["google-vertex"] = "gemini-3.1-pro-preview",
            ["github-copilot"] = "gpt-5.4",
            ["openrouter"] = "moonshotai/kimi-k2.6",
            ["vercel-ai-gateway"] = "zai/glm-5.1",
            ["xai"] = "grok-4.6",
            ["groq"] = "openai/gpt-oss-120b",
            ["cerebras"] = "gpt-oss-120b",
            ["zai"] = "glm-5.3",
            ["zai-coding-cn"] = "glm-5.3",
            ["mistral"] = "devstral-medium-latest",
            ["minimax"] = "MiniMax-M2.7",
            ["minimax-cn"] = "MiniMax-M2.7",
            ["moonshotai"] = "kimi-k2.6",
            ["moonshotai-cn"] = "kimi-k2.6",
            ["huggingface"] = "moonshotai/Kimi-K2.6",
            ["fireworks"] = "accounts/fireworks/models/kimi-k2p6",
            ["together"] = "moonshotai/Kimi-K2.6",
            ["baseten"] = "zai-org/GLM-5.2",
            ["opencode"] = "kimi-k2.6",
            ["opencode-go"] = "kimi-k2.6",
            ["kimi-coding"] = "kimi-for-coding",
            ["cloudflare-workers-ai"] = "@cf/moonshotai/kimi-k2.6",
            ["cloudflare-ai-gateway"] = "workers-ai/@cf/moonshotai/kimi-k2.6",
            ["qwen-token-plan"] = "qwen3.7-max",
            ["qwen-token-plan-cn"] = "qwen3.7-max",
            ["qwen-token-plan-individual"] = "qwen3.8-max",
            ["xiaomi"] = "mimo-v2.5-pro",
            ["xiaomi-token-plan-cn"] = "mimo-v2.5-pro",
            ["xiaomi-token-plan-ams"] = "mimo-v2.5-pro",
            ["xiaomi-token-plan-sgp"] = "mimo-v2.5-pro",
        };

    /// <summary>
    /// Whether a model ID looks like an alias (no date suffix): ends in -latest or
    /// does not end in -YYYYMMDD (pinned isAlias).
    /// </summary>
    public static bool IsAlias(string id)
        => id.EndsWith("-latest", StringComparison.Ordinal) ||
           !Regex.IsMatch(id, @"-\d{8}$");

    /// <summary>
    /// Finds an exact model reference match: canonical provider/modelId, then
    /// provider-prefixed id, then a unique bare id (pinned findExactModelReferenceMatch).
    /// </summary>
    public static ModelInfo? FindExactModelReferenceMatch(
        string modelReference, IReadOnlyList<ModelInfo> availableModels)
    {
        var trimmed = modelReference.Trim();
        if (trimmed.Length == 0)
        {
            return null;
        }

        var normalized = trimmed.ToLowerInvariant();

        var canonicalMatches = availableModels
            .Where(model => $"{model.Provider}/{model.Id}".ToLowerInvariant() == normalized)
            .ToList();
        if (canonicalMatches.Count == 1)
        {
            return canonicalMatches[0];
        }

        if (canonicalMatches.Count > 1)
        {
            return null;
        }

        var slashIndex = trimmed.IndexOf('/');
        if (slashIndex != -1)
        {
            var providerPart = trimmed[..slashIndex].Trim();
            var modelIdPart = trimmed[(slashIndex + 1)..].Trim();
            if (providerPart.Length > 0 && modelIdPart.Length > 0)
            {
                var providerMatches = availableModels
                    .Where(model =>
                        model.Provider.ToLowerInvariant() == providerPart.ToLowerInvariant() &&
                        model.Id.ToLowerInvariant() == modelIdPart.ToLowerInvariant())
                    .ToList();
                if (providerMatches.Count == 1)
                {
                    return providerMatches[0];
                }

                if (providerMatches.Count > 1)
                {
                    return null;
                }
            }
        }

        var idMatches = availableModels
            .Where(model => model.Id.ToLowerInvariant() == normalized)
            .ToList();
        return idMatches.Count == 1 ? idMatches[0] : null;
    }

    /// <summary>
    /// Matches a pattern to a model: exact reference first, then partial id/name with
    /// alias preference and locale-descending tie-break (pinned tryMatchModel).
    /// </summary>
    public static ModelInfo? TryMatchModel(string modelPattern, IReadOnlyList<ModelInfo> availableModels)
    {
        var exactMatch = FindExactModelReferenceMatch(modelPattern, availableModels);
        if (exactMatch is not null)
        {
            return exactMatch;
        }

        var lower = modelPattern.ToLowerInvariant();
        var matches = availableModels
            .Where(m => m.Id.ToLowerInvariant().Contains(lower, StringComparison.Ordinal)
                || (m.Name?.ToLowerInvariant().Contains(lower, StringComparison.Ordinal) ?? false))
            .ToList();

        if (matches.Count == 0)
        {
            return null;
        }

        var aliases = matches.Where(m => IsAlias(m.Id)).ToList();
        var datedVersions = matches.Where(m => !IsAlias(m.Id)).ToList();

        var pool = aliases.Count > 0 ? aliases : datedVersions;
        return pool.OrderByDescending(m => m.Id, StringComparer.CurrentCulture).FirstOrDefault();
    }

    /// <summary>
    /// Parses a pattern to extract model and thinking level, handling colons in model
    /// IDs (pinned parseModelPattern).
    /// </summary>
    public static (ModelInfo? Model, string? ThinkingLevel, string? Warning) ParseModelPattern(
        string pattern,
        IReadOnlyList<ModelInfo> availableModels,
        bool allowInvalidThinkingLevelFallback = true)
    {
        var exactMatch = TryMatchModel(pattern, availableModels);
        if (exactMatch is not null)
        {
            return (exactMatch, null, null);
        }

        var lastColonIndex = pattern.LastIndexOf(':');
        if (lastColonIndex == -1)
        {
            return (null, null, null);
        }

        var prefix = pattern[..lastColonIndex];
        var suffix = pattern[(lastColonIndex + 1)..];

        if (ThinkingLevel.IsValid(suffix))
        {
            var (model, _, warning) = ParseModelPattern(prefix, availableModels, allowInvalidThinkingLevelFallback);
            if (model is not null)
            {
                // Only use this thinking level if no warning from inner recursion.
                return (model, warning is null ? suffix : null, warning);
            }

            return (null, null, warning);
        }

        if (!allowInvalidThinkingLevelFallback)
        {
            // Strict mode (CLI --model parsing): treat as part of the model id and fail.
            return (null, null, null);
        }

        var (fallbackModel, _, fallbackWarning) = ParseModelPattern(prefix, availableModels, allowInvalidThinkingLevelFallback);
        if (fallbackModel is not null)
        {
            return (
                fallbackModel,
                null,
                $"Invalid thinking level \"{suffix}\" in pattern \"{pattern}\". Using default instead.");
        }

        return (null, null, fallbackWarning);
    }

    /// <summary>
    /// Resolves scope patterns (pinned resolveModelScopeFromModels). Glob patterns
    /// match "provider/modelId" or the bare model id; non-glob patterns use
    /// parseModelPattern.
    /// </summary>
    public static ResolveModelScopeResult ResolveModelScopeFromModels(
        IReadOnlyList<string> patterns, IReadOnlyList<ModelInfo> models)
    {
        var scopedModels = new List<ScopedModel>();
        var diagnostics = new List<ModelScopeDiagnostic>();

        foreach (var pattern in patterns)
        {
            if (ContainsGlobCharacters(pattern))
            {
                var colonIdx = pattern.LastIndexOf(':');
                var globPattern = pattern;
                string? thinkingLevel = null;
                if (colonIdx != -1)
                {
                    var suffix = pattern[(colonIdx + 1)..];
                    if (ThinkingLevel.IsValid(suffix))
                    {
                        thinkingLevel = suffix;
                        globPattern = pattern[..colonIdx];
                    }
                }

                var exactMatch = FindExactModelReferenceMatch(globPattern, models);
                if (exactMatch is not null)
                {
                    AddIfNew(scopedModels, exactMatch, thinkingLevel);
                    continue;
                }

                var matchingModels = models
                    .Where(m => GlobMatches($"{m.Provider}/{m.Id}", globPattern) || GlobMatches(m.Id, globPattern))
                    .ToList();

                if (matchingModels.Count == 0)
                {
                    diagnostics.Add(new ModelScopeDiagnostic(
                        "warning", "no-match", $"No models match pattern \"{pattern}\"", pattern));
                    continue;
                }

                foreach (var model in matchingModels)
                {
                    AddIfNew(scopedModels, model, thinkingLevel);
                }

                continue;
            }

            var (matchedModel, patternThinking, warning) = ParseModelPattern(pattern, models);
            if (warning is not null)
            {
                diagnostics.Add(new ModelScopeDiagnostic("warning", "invalid-thinking-level", warning, pattern));
            }

            if (matchedModel is null)
            {
                diagnostics.Add(new ModelScopeDiagnostic(
                    "warning", "no-match", $"No models match pattern \"{pattern}\"", pattern));
                continue;
            }

            AddIfNew(scopedModels, matchedModel, patternThinking);
        }

        return new ResolveModelScopeResult(scopedModels, diagnostics);
    }

    private static void AddIfNew(List<ScopedModel> scopedModels, ModelInfo model, string? thinkingLevel)
    {
        if (!scopedModels.Any(sm => ModelInfo.AreEqual(sm.Model, model)))
        {
            scopedModels.Add(new ScopedModel(model, thinkingLevel));
        }
    }

    private static bool ContainsGlobCharacters(string pattern)
        => pattern.Contains('*', StringComparison.Ordinal)
            || pattern.Contains('?', StringComparison.Ordinal)
            || pattern.Contains('[', StringComparison.Ordinal);

    private static readonly Dictionary<char, char> GlobEscapeMap = new();

    /// <summary>
    /// Minimatch-style glob matching over the full pattern (case-insensitive; *, ?,
    /// [seq], [!seq]). Ported semantics for the small character set used by scopes.
    /// </summary>
    public static bool GlobMatches(string input, string pattern)
    {
        var regex = new Regex("^" + GlobToRegex(pattern) + "$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        return regex.IsMatch(input);
    }

    private static string GlobToRegex(string pattern)
    {
        var sb = new System.Text.StringBuilder();
        for (var i = 0; i < pattern.Length; i++)
        {
            var c = pattern[i];
            switch (c)
            {
                case '*':
                    sb.Append(".*");
                    break;
                case '?':
                    sb.Append('.');
                    break;
                case '[':
                    var end = pattern.IndexOf(']', i + 1);
                    if (end == -1)
                    {
                        sb.Append(Regex.Escape("["));
                    }
                    else
                    {
                        var range = pattern[(i + 1)..end];
                        if (range.Length > 0 && range[0] == '!')
                        {
                            range = "^" + range[1..];
                        }

                        sb.Append('[').Append(range).Append(']');
                        i = end;
                    }

                    break;
                default:
                    sb.Append(Regex.Escape(c.ToString()));
                    break;
            }
        }

        return sb.ToString();
    }

    /// <summary>
    /// Resolves a single model from CLI flags (pinned resolveCliModel). Uses ALL
    /// models (not just authed) so first-time --api-key setup works.
    /// </summary>
    public static ResolveCliModelResult ResolveCliModel(
        string? cliProvider,
        string? cliModel,
        string? cliThinking,
        ModelRuntime modelRuntime)
    {
        var result = ResolveCliModelCore(cliProvider, cliModel, cliThinking, modelRuntime);

        // Selection boundary (item 3): an explicit CLI model on a wire API this build cannot
        // execute fails with a diagnostic instead of being silently selected and failing at
        // request time.
        if (result.Model is { } model && !ModelExecutionSupport.CanExecute(model))
        {
            return new ResolveCliModelResult(
                null, null, null,
                $"Model \"{model.Reference}\" cannot be executed by this build: {ModelExecutionSupport.InexecutableReason(model)}.");
        }

        return result;
    }

    private static ResolveCliModelResult ResolveCliModelCore(
        string? cliProvider,
        string? cliModel,
        string? cliThinking,
        ModelRuntime modelRuntime)
    {
        if (cliModel is null)
        {
            return new ResolveCliModelResult(null, null, null, null);
        }

        var availableModels = modelRuntime.GetModels().ToArray();
        if (availableModels.Length == 0)
        {
            return new ResolveCliModelResult(
                null, null, null,
                "No models available. Check your installation or add models to models.json.");
        }

        var providerMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var m in availableModels)
        {
            providerMap[m.Provider] = m.Provider;
        }

        var provider = cliProvider is not null && providerMap.TryGetValue(cliProvider, out var canonical)
            ? canonical
            : null;
        if (cliProvider is not null && provider is null)
        {
            return new ResolveCliModelResult(
                null, null, null,
                $"Unknown provider \"{cliProvider}\". Use --list-models to see available providers/models.");
        }

        var pattern = cliModel;
        var inferredProvider = false;

        if (provider is null)
        {
            var slashIndex = cliModel.IndexOf('/');
            if (slashIndex != -1)
            {
                var maybeProvider = cliModel[..slashIndex];
                if (providerMap.TryGetValue(maybeProvider, out var canonicalProvider))
                {
                    provider = canonicalProvider;
                    pattern = cliModel[(slashIndex + 1)..];
                    inferredProvider = true;
                }
            }
        }

        if (provider is null)
        {
            var lower = cliModel.ToLowerInvariant();
            var exactMatches = availableModels
                .Where(m => m.Id.ToLowerInvariant() == lower
                    || $"{m.Provider}/{m.Id}".ToLowerInvariant() == lower)
                .ToList();
            if (exactMatches.Count == 1)
            {
                return new ResolveCliModelResult(exactMatches[0], null, null, null);
            }

            if (exactMatches.Count > 1)
            {
                var authenticatedExactMatches = exactMatches
                    .Where(m => modelRuntime.HasConfiguredAuth(m.Provider))
                    .ToList();
                if (authenticatedExactMatches.Count == 1)
                {
                    return new ResolveCliModelResult(authenticatedExactMatches[0], null, null, null);
                }

                var matches = string.Join(", ", exactMatches
                    .Select(m => $"{m.Provider}/{m.Id}")
                    .OrderBy(x => x, StringComparer.Ordinal));
                var authHint = authenticatedExactMatches.Count == 0
                    ? "No matching provider is authenticated."
                    : "More than one matching provider is authenticated.";
                return new ResolveCliModelResult(
                    null, null, null,
                    $"Model \"{cliModel}\" is ambiguous across providers: {matches}. {authHint} Use --provider or provider/model.");
            }
        }

        if (cliProvider is not null && provider is not null)
        {
            var prefix = provider + "/";
            if (cliModel.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                pattern = cliModel[prefix.Length..];
            }
        }

        IReadOnlyList<ModelInfo> candidates = provider is not null
            ? availableModels.Where(m => m.Provider == provider).ToArray()
            : availableModels;
        var (model, thinkingLevel, warning) = ParseModelPattern(pattern, candidates, false);

        if (model is not null)
        {
            if (inferredProvider)
            {
                var rawExactMatches = availableModels
                    .Where(m => m.Id.ToLowerInvariant() == cliModel.ToLowerInvariant()
                        && !ModelInfo.AreEqual(m, model))
                    .ToList();
                if (rawExactMatches.Count > 0 && !modelRuntime.HasConfiguredAuth(model.Provider))
                {
                    var authenticatedRawMatches = rawExactMatches
                        .Where(m => modelRuntime.HasConfiguredAuth(m.Provider))
                        .ToList();
                    if (authenticatedRawMatches.Count == 1)
                    {
                        return new ResolveCliModelResult(authenticatedRawMatches[0], null, null, null);
                    }
                }
            }

            return new ResolveCliModelResult(model, thinkingLevel, warning, null);
        }

        if (inferredProvider)
        {
            var lower = cliModel.ToLowerInvariant();
            var exact = availableModels
                .FirstOrDefault(m => m.Id.ToLowerInvariant() == lower
                    || $"{m.Provider}/{m.Id}".ToLowerInvariant() == lower);
            if (exact is not null)
            {
                return new ResolveCliModelResult(exact, null, null, null);
            }

            var (fallbackModel, fallbackThinking, fallbackWarning) = ParseModelPattern(cliModel, availableModels, false);
            if (fallbackModel is not null)
            {
                return new ResolveCliModelResult(fallbackModel, fallbackThinking, fallbackWarning, null);
            }
        }

        if (provider is not null)
        {
            var fallbackPattern = pattern;
            string? fallbackThinking = null;
            if (cliThinking is null)
            {
                var lastColon = pattern.LastIndexOf(':');
                if (lastColon != -1)
                {
                    var suffix = pattern[(lastColon + 1)..];
                    if (ThinkingLevel.IsValid(suffix))
                    {
                        fallbackPattern = pattern[..lastColon];
                        fallbackThinking = suffix;
                    }
                }
            }

            var fallbackModel = BuildFallbackModel(provider, fallbackPattern, availableModels);
            if (fallbackModel is not null)
            {
                var requestedThinking = cliThinking ?? fallbackThinking;
                var finalModel = requestedThinking is not null && requestedThinking != "off"
                    ? fallbackModel with { Reasoning = true }
                    : fallbackModel;
                var fallbackWarning = warning is not null
                    ? $"{warning} Model \"{fallbackPattern}\" not found for provider \"{provider}\". Using custom model id."
                    : $"Model \"{fallbackPattern}\" not found for provider \"{provider}\". Using custom model id.";
                return new ResolveCliModelResult(finalModel, fallbackThinking, fallbackWarning, null);
            }
        }

        var display = provider is not null ? $"{provider}/{pattern}" : cliModel;
        return new ResolveCliModelResult(
            null, null, warning,
            $"Model \"{display}\" not found. Use --list-models to see available models.");
    }

    private static ModelInfo? BuildFallbackModel(
        string provider, string modelId, IReadOnlyList<ModelInfo> availableModels)
    {
        var providerModels = availableModels.Where(m => m.Provider == provider).ToList();
        if (providerModels.Count == 0)
        {
            return null;
        }

        var defaultId = DefaultModelPerProvider.GetValueOrDefault(provider);
        var baseModel = defaultId is not null
            ? providerModels.FirstOrDefault(m => m.Id == defaultId) ?? providerModels[0]
            : providerModels[0];

        return baseModel with { Id = modelId, Name = modelId };
    }

    /// <summary>
    /// Finds the initial model (pinned findInitialModel), in priority order: CLI args,
    /// scoped models, saved default, first available.
    /// </summary>
    public static InitialModelResult FindInitialModel(
        string? cliProvider,
        string? cliModel,
        IReadOnlyList<ScopedModel> scopedModels,
        bool isContinuing,
        string? defaultProvider,
        string? defaultModelId,
        string? defaultThinkingLevel,
        IReadOnlyDictionary<string, string>? modelThinkingLevels,
        ModelRuntime modelRuntime)
    {
        var thinkingLevel = ThinkingLevel.Default;

        // 1. CLI args take priority.
        if (cliProvider is not null && cliModel is not null)
        {
            var resolved = ResolveCliModel(cliProvider, cliModel, null, modelRuntime);
            if (resolved.Error is not null)
            {
                return new InitialModelResult(null, thinkingLevel, null, resolved.Error);
            }

            if (resolved.Model is not null)
            {
                return new InitialModelResult(resolved.Model, thinkingLevel, null, null);
            }
        }

        // 2. First model from scoped models (skip when continuing/resuming).
        if (scopedModels.Count > 0 && !isContinuing)
        {
            var scopedModel = scopedModels[0];
            var key = $"{scopedModel.Model.Provider}/{scopedModel.Model.Id}";
            var perModel = modelThinkingLevels?.GetValueOrDefault(key);
            return new InitialModelResult(
                scopedModel.Model,
                scopedModel.ThinkingLevel ?? perModel ?? defaultThinkingLevel ?? thinkingLevel,
                null, null);
        }

        // 3. Saved default from settings when auth is configured.
        if (defaultProvider is not null && defaultModelId is not null)
        {
            var found = modelRuntime.GetModel(defaultProvider, defaultModelId);
            if (found is not null && modelRuntime.HasConfiguredAuth(found.Provider))
            {
                var key = $"{defaultProvider}/{defaultModelId}";
                var perModel = modelThinkingLevels?.GetValueOrDefault(key);
                var level = perModel ?? defaultThinkingLevel ?? thinkingLevel;
                return new InitialModelResult(found, level, null, null);
            }
        }

        // 4. First available model, preferring known-provider defaults. Only models this
        // build can execute are candidates (selection boundary, item 3); when the
        // authenticated catalogue holds only inexecutable models the caller's fallback
        // message reports the exclusion (ModelStartupResolver).
        var availableModels = modelRuntime.GetAvailableSnapshot()
            .Where(ModelExecutionSupport.CanExecute)
            .ToArray();
        if (availableModels.Length > 0)
        {
            foreach (var (provider, defaultId) in DefaultModelPerProvider)
            {
                var match = availableModels.FirstOrDefault(m => m.Provider == provider && m.Id == defaultId);
                if (match is not null)
                {
                    return new InitialModelResult(match, thinkingLevel, null, null);
                }
            }

            return new InitialModelResult(availableModels[0], thinkingLevel, null, null);
        }

        // 5. No model found.
        return new InitialModelResult(null, thinkingLevel, null, null);
    }

    /// <summary>
    /// Restores a model from a session with fallback (pinned restoreModelFromSession).
    /// <paramref name="emit"/> receives informational/warning lines for the caller to
    /// display (Core never writes to the console).
    /// </summary>
    public static (ModelInfo? Model, string? FallbackMessage) RestoreModelFromSession(
        string savedProvider,
        string savedModelId,
        ModelInfo? currentModel,
        Action<string>? emit,
        ModelRuntime modelRuntime)
    {
        var restoredModel = modelRuntime.GetModel(savedProvider, savedModelId);
        var hasConfiguredAuth = restoredModel is not null
            && modelRuntime.HasConfiguredAuth(restoredModel.Provider);
        var executable = restoredModel is not null && ModelExecutionSupport.CanExecute(restoredModel);

        if (restoredModel is not null && hasConfiguredAuth && executable)
        {
            emit?.Invoke($"Restored model: {savedProvider}/{savedModelId}");
            return (restoredModel, null);
        }

        var reason = restoredModel is null
            ? "model no longer exists"
            : !executable
                ? ModelExecutionSupport.InexecutableReason(restoredModel)!
                : "no auth configured";
        emit?.Invoke($"Warning: Could not restore model {savedProvider}/{savedModelId} ({reason}).");

        if (currentModel is not null)
        {
            emit?.Invoke($"Falling back to: {currentModel.Provider}/{currentModel.Id}");
            return (
                currentModel,
                $"Could not restore model {savedProvider}/{savedModelId} ({reason}). Using {currentModel.Provider}/{currentModel.Id}.");
        }

        // The fallback is the first executable available model (item 3).
        var availableModels = modelRuntime.GetAvailableSnapshot()
            .Where(ModelExecutionSupport.CanExecute)
            .ToArray();
        if (availableModels.Length > 0)
        {
            ModelInfo? fallbackModel = null;
            foreach (var (provider, defaultId) in DefaultModelPerProvider)
            {
                var match = availableModels.FirstOrDefault(m => m.Provider == provider && m.Id == defaultId);
                if (match is not null)
                {
                    fallbackModel = match;
                    break;
                }
            }

            fallbackModel ??= availableModels[0];
            emit?.Invoke($"Falling back to: {fallbackModel.Provider}/{fallbackModel.Id}");
            return (
                fallbackModel,
                $"Could not restore model {savedProvider}/{savedModelId} ({reason}). Using {fallbackModel.Provider}/{fallbackModel.Id}.");
        }

        return (null, null);
    }
}
