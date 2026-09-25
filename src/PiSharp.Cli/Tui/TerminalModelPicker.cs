using PiSharp.Cli;
using PiSharp.Runtime.Providers;

namespace PiSharp.Cli.Tui;

/// <summary>Adapts the reusable list overlay to the provider model catalogue.</summary>
internal sealed class TerminalModelPicker(ProviderModelRuntime modelRuntime, TerminalEditor editor)
{
    public async Task<ModelSelection?> ShowAsync(ModelSelection current, string? providerFilter = null,
        CancellationToken cancellationToken = default)
    {
        var listed = await modelRuntime.ListModelsAsync(providerFilter, cancellationToken, includeOutOfScope: true);
        var models = listed.ToList();
        if (models.All(model => !SameModel(model, current.Provider.Id, current.Model.Id)))
            models.Add(current.Model with { Provider = current.Provider.Id });

        var ordered = models.OrderByDescending(model => SameModel(model, current.Provider.Id, current.Model.Id))
            .ThenBy(model => model.Provider, StringComparer.OrdinalIgnoreCase)
            .ThenBy(model => model.Id, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var options = ordered.Select(model => ToOption(model, current)).ToArray();
        var scopedOptions = modelRuntime.Scope.Count == 0 ? null : options.Where(option => IsInScope(option.Value)).ToArray();
        var selectedKey = Key(current.Provider.Id, current.Model.Id);
        var chosen = editor.ShowSelectionList("Select model", options, selectedKey, scopedOptions,
            allLabel: "All models", scopedLabel: "Scoped models", emptyMessage: "No matching models");
        if (chosen is null) return null;
        var selected = chosen.Option.Value;
        var provider = selected.Provider ?? current.Provider.Id;
        return await modelRuntime.ResolveAsync(provider, selected.Id, cancellationToken,
            includeOutOfScope: !chosen.IsScoped);
    }

    private TerminalSelectionOption<ModelDescriptor> ToOption(ModelDescriptor model, ModelSelection current)
    {
        var provider = model.Provider ?? current.Provider.Id;
        var metadata = new List<string>();
        metadata.Add(model.Available ? model.Status ?? "available" : model.UnavailableReason ?? model.Status ?? "unavailable");
        if (model.Reasoning == true) metadata.Add("reasoning");
        if (model.ContextLength is { } contextLength) metadata.Add($"{contextLength} tokens");
        var isCurrent = SameModel(model, current.Provider.Id, current.Model.Id);
        var searchText = $"{provider} {provider}/{model.Id} {provider} {model.Id} {model.Name}";
        return new(Key(provider, model.Id), model, $"{model.Id} [{provider}]", string.Join(" · ", metadata),
            searchText, isCurrent);
    }

    private bool IsInScope(ModelDescriptor model)
    {
        var provider = model.Provider ?? "";
        return modelRuntime.Scope.Any(pattern => ModelScopeGlob.Matches(pattern, $"{provider}/{model.Id}") ||
            ModelScopeGlob.Matches(pattern, model.Id));
    }

    private static bool SameModel(ModelDescriptor model, string provider, string id) =>
        model.Provider?.Equals(provider, StringComparison.OrdinalIgnoreCase) == true &&
        model.Id.Equals(id, StringComparison.OrdinalIgnoreCase);

    private static string Key(string provider, string id) => $"{provider}\0{id}";
}
