using PiSharp.Runtime.Providers;

namespace PiSharp.Cli.Tui;

internal sealed class TerminalModelNavigation(ProviderModelRuntime models, Func<TextWriter> output)
{
    public string? NextThinkingLevel(ModelSelection selection, string thinking) =>
        NextThinkingLevelFor(selection, thinking, output());

    internal static string? NextThinkingLevelFor(ModelSelection selection, string thinking, TextWriter output)
    {
        if (selection.Model.Reasoning != true)
        {
            output.WriteLine("Current model does not support thinking");
            return null;
        }
        var levels = ThinkingLevels.AvailableForModel(selection.Model.Reasoning, selection.Model.ThinkingLevelMap);
        if (levels.Count == 0)
        {
            output.WriteLine("Current model does not support thinking");
            return null;
        }
        var index = Array.FindIndex(levels.ToArray(), level => level.Equals(thinking, StringComparison.OrdinalIgnoreCase));
        return levels[(index + 1 + levels.Count) % levels.Count];
    }

    public async Task<ModelSelection?> NextModelAsync(Func<ModelSelection> getSelection, string? providerFilter, int direction)
    {
        var available = CycleCandidates(providerFilter);
        if (available.Count <= 1)
        {
            output().WriteLine(models.HasScope ? "Only one model in scope" : "Only one model available");
            return null;
        }
        var selection = getSelection();
        var index = available.ToList().FindIndex(model =>
            model.Provider?.Equals(selection.Provider.Id, StringComparison.OrdinalIgnoreCase) == true &&
            model.Id.Equals(selection.Model.Id, StringComparison.OrdinalIgnoreCase));
        if (index < 0) index = 0;
        var next = available[(index + direction + available.Count) % available.Count];
        if (string.IsNullOrWhiteSpace(next.Provider))
            throw new InvalidOperationException("The model catalogue did not identify the selected provider.");
        return await models.ResolveAsync(next.Provider, next.Id, includeOutOfScope: true);
    }

    private IReadOnlyList<ModelDescriptor> CycleCandidates(string? providerFilter)
    {
        // Pi cycles the cached available snapshot rather than refreshing provider catalogues, so a
        // keypress never performs a network request and follows the snapshot's stable order.
        var snapshot = models.GetAvailableModelSnapshot()
            .Where(model => model.Provider is not null && model.Available)
            .ToArray();
        // Configured providers authenticated with a literal key are not part of the
        // environment-key snapshot; include their configured models without a catalog refresh.
        var configured = models.Providers
            .Where(provider => provider.Models.Any(model => model.Available) &&
                (!provider.AuthRequired || !string.IsNullOrWhiteSpace(provider.ConfiguredApiKey) ||
                 provider.ApiKeyEnvironment is { } keyName &&
                 !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(keyName))))
            .SelectMany(provider => provider.Models.Where(model => model.Available)
                .Select(model => model with { Provider = provider.Id }))
            .GroupBy(Key, StringComparer.Ordinal).Select(group => group.First()).ToArray();
        if (configured.Length > 0)
        {
            var keys = snapshot.Select(Key).ToHashSet(StringComparer.Ordinal);
            snapshot = snapshot.Concat(configured.Where(model => keys.Add(Key(model)))).ToArray();
        }
        if (models.HasScope)
        {
            var availableByKey = snapshot.GroupBy(Key, StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => group.Last(), StringComparer.Ordinal);
            return ModelScopeResolver.Resolve(models.Scope, snapshot).Models
                .Select(item => availableByKey.GetValueOrDefault(Key(item.Model)))
                .Where(model => model is not null).Select(model => model!).ToArray();
        }
        return snapshot.Where(model => providerFilter is null ||
            model.Provider!.Equals(providerFilter, StringComparison.OrdinalIgnoreCase)).ToArray();
    }

    private static string Key(ModelDescriptor model) => $"{model.Provider}\0{model.Id}";
}
