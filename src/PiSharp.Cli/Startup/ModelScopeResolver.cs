using PiSharp.Runtime.Providers;

namespace PiSharp.Cli;

internal sealed record ResolvedScopedModel(ModelDescriptor Model, string? ThinkingLevel);
internal sealed record ModelScopeDiagnostic(string Code, string Pattern, string Message);
internal sealed record ModelScopeResolution(IReadOnlyList<ResolvedScopedModel> Models,
    IReadOnlyList<ModelScopeDiagnostic> Diagnostics);

/// <summary>Resolves ordered enabled-model patterns against one catalogue snapshot.</summary>
internal static class ModelScopeResolver
{
    public static ModelScopeResolution Resolve(IReadOnlyList<string> patterns,
        IReadOnlyList<ModelDescriptor> models)
    {
        var resolved = new List<ResolvedScopedModel>();
        var diagnostics = new List<ModelScopeDiagnostic>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var originalPattern in patterns)
        {
            var pattern = originalPattern.Trim();
            if (pattern.Length == 0) continue;
            var glob = ContainsGlob(pattern);
            var (basePattern, thinking, invalidThinking) = SplitThinking(pattern);
            IReadOnlyList<ModelDescriptor> matches;
            var exactAmbiguity = false;
            string? matchedThinking = null;
            if (glob)
            {
                var globPattern = invalidThinking is null ? basePattern : pattern;
                if (invalidThinking is null) matchedThinking = thinking;
                matches = FindExact(globPattern, models);
                exactAmbiguity = matches.Count > 1;
                if (matches.Count == 0)
                    matches = models.Where(model => ModelScopeGlob.Matches(globPattern, Qualified(model)) ||
                        ModelScopeGlob.Matches(globPattern, model.Id)).ToArray();
            }
            else
            {
                matches = FindExact(pattern, models);
                if (matches.Count == 0) matches = FindFuzzy(pattern, models);
                if (matches.Count == 0 && thinking is not null)
                {
                    matches = FindReference(basePattern, models);
                    if (matches.Count > 0) matchedThinking = thinking;
                }
                if (matches.Count == 0 && invalidThinking is not null)
                {
                    matches = FindReference(basePattern, models);
                    if (matches.Count > 0)
                        diagnostics.Add(new("invalid-thinking-level", pattern,
                            $"Invalid thinking level \"{invalidThinking}\" in pattern \"{pattern}\". Using default instead."));
                }
            }

            if (matches.Count == 0)
            {
                diagnostics.Add(new("no-match", pattern, $"No models match pattern \"{pattern}\""));
                continue;
            }

            if ((exactAmbiguity || !glob) &&
                matches.Select(model => model.Provider).Distinct(StringComparer.OrdinalIgnoreCase).Count() > 1)
            {
                var providers = matches.Select(model => model.Provider).Where(provider => provider is not null)
                    .Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase);
                diagnostics.Add(new("ambiguous", pattern,
                    $"Pattern \"{pattern}\" matches several providers: {string.Join(", ", providers)}. Use provider/id to choose one."));
            }

            foreach (var match in matches)
            {
                var key = Qualified(match);
                if (seen.Add(key)) resolved.Add(new(match, matchedThinking));
            }
        }
        return new(resolved, diagnostics);
    }

    private static IReadOnlyList<ModelDescriptor> FindExact(string reference,
        IReadOnlyList<ModelDescriptor> models)
    {
        var slash = reference.IndexOf('/');
        if (slash > 0)
        {
            var provider = models.Select(model => model.Provider).FirstOrDefault(value =>
                value is not null && value.Equals(reference[..slash], StringComparison.OrdinalIgnoreCase));
            if (provider is not null)
            {
                var named = models.Where(model => model.Provider!.Equals(provider, StringComparison.OrdinalIgnoreCase) &&
                    model.Id.Equals(reference[(slash + 1)..], StringComparison.OrdinalIgnoreCase)).ToArray();
                if (named.Length > 0) return named;
            }
        }
        return models.Where(model => model.Id.Equals(reference, StringComparison.OrdinalIgnoreCase)).ToArray();
    }

    private static IReadOnlyList<ModelDescriptor> FindFuzzy(string reference,
        IReadOnlyList<ModelDescriptor> models)
    {
        var slash = reference.IndexOf('/');
        if (slash > 0)
        {
            var provider = models.Select(model => model.Provider).FirstOrDefault(value =>
                value is not null && value.Equals(reference[..slash], StringComparison.OrdinalIgnoreCase));
            if (provider is not null)
            {
                var named = models.Where(model => model.Provider!.Equals(provider, StringComparison.OrdinalIgnoreCase) &&
                    IsFuzzyMatch(reference[(slash + 1)..], model)).ToArray();
                if (named.Length > 0) return RankFuzzy(named);
            }
        }
        var matches = models.Where(model => IsFuzzyMatch(reference, model)).ToArray();
        return matches.Length == 0 ? [] : RankFuzzy(matches);
    }

    private static IReadOnlyList<ModelDescriptor> FindReference(string reference,
        IReadOnlyList<ModelDescriptor> models)
    {
        var exact = FindExact(reference, models);
        return exact.Count > 0 ? exact : FindFuzzy(reference, models);
    }

    private static IReadOnlyList<ModelDescriptor> RankFuzzy(IReadOnlyList<ModelDescriptor> matches)
    {
        var aliases = matches.Where(model => !System.Text.RegularExpressions.Regex.IsMatch(model.Id,
            "-\\d{8}$", System.Text.RegularExpressions.RegexOptions.CultureInvariant) ||
            model.Id.EndsWith("-latest", StringComparison.OrdinalIgnoreCase)).ToArray();
        var ranked = (aliases.Length > 0 ? aliases : matches)
            .OrderByDescending(model => model.Id, StringComparer.OrdinalIgnoreCase).ToArray();
        var bestId = ranked[0].Id;
        return ranked.Where(model => model.Id.Equals(bestId, StringComparison.OrdinalIgnoreCase)).ToArray();
    }

    private static bool IsFuzzyMatch(string value, ModelDescriptor model) =>
        model.Id.Contains(value, StringComparison.OrdinalIgnoreCase) ||
        (model.Name?.Contains(value, StringComparison.OrdinalIgnoreCase) ?? false);

    private static bool ContainsGlob(string pattern) => pattern.Contains('*') || pattern.Contains('?') ||
        pattern.Contains('[');

    private static (string Base, string? Thinking, string? InvalidThinking) SplitThinking(string pattern)
    {
        var colon = pattern.LastIndexOf(':');
        if (colon <= 0) return (pattern, null, null);
        var suffix = pattern[(colon + 1)..];
        if (ThinkingLevels.IsValid(suffix)) return (pattern[..colon], ThinkingLevels.Normalize(suffix), null);
        return (pattern[..colon], null, suffix);
    }

    private static string Qualified(ModelDescriptor model) => $"{model.Provider}/{model.Id}";
}
