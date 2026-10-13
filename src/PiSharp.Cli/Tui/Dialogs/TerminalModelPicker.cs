using PiSharp.Cli;
using PiSharp.Runtime.Providers;

namespace PiSharp.Cli.Tui;

internal sealed record TerminalModelSelection(ModelSelection Selection, bool PersistAsDefault);

/// <summary>Adapts the reusable list overlay to the provider model catalogue.</summary>
internal sealed class TerminalModelPicker(ProviderModelRuntime modelRuntime, TerminalEditor editor,
    Func<string?> getDefaultModelKey)
{
    public async Task<TerminalModelSelection?> ShowAsync(ModelSelection current, string? providerFilter = null,
        CancellationToken cancellationToken = default)
    {
        var defaultModelKey = getDefaultModelKey();
        var snapshot = modelRuntime.GetAvailableModelSnapshot();
        var initial = CreateOptions(snapshot, current, defaultModelKey, providerFilter);
        var selectedKey = Key(current.Provider.Id, current.Model.Id);
        var hint = "Only showing models from configured providers. Use /login to add providers.";
        var chosen = editor.ShowModelSelectionList(initial.Options, selectedKey, hint,
            model => model.Name ?? model.Id, initial.ScopedOptions,
            token => RefreshOptionsAsync(current, defaultModelKey, providerFilter, initial.Options, token),
            cancellationToken);
        if (chosen is null) return null;
        var selected = chosen.Option.Value;
        var provider = selected.Provider ?? current.Provider.Id;
        var selection = await modelRuntime.ResolveAsync(provider, selected.Id, cancellationToken,
            includeOutOfScope: !chosen.IsScoped);
        return new(selection, chosen.SetAsDefault);
    }

    private async Task<TerminalSelectionRefresh<ModelDescriptor>> RefreshOptionsAsync(ModelSelection current,
        string? defaultModelKey, string? providerFilter,
        IReadOnlyList<TerminalSelectionOption<ModelDescriptor>> cached, CancellationToken cancellationToken)
    {
        try
        {
            var listed = await modelRuntime.ListModelsAsync(providerFilter, cancellationToken,
                includeOutOfScope: true).ConfigureAwait(false);
            var failures = listed.Where(model => model.UnavailableReason?.StartsWith("catalog unavailable:",
                    StringComparison.Ordinal) == true)
                .Select(model => model.Provider).Where(provider => provider is not null)
                .ToHashSet(StringComparer.Ordinal);
            if (failures.Count > 0)
            {
                var refreshedKeys = listed.Where(model => model.Available).Select(model => Key(model.Provider ?? "", model.Id))
                    .ToHashSet(StringComparer.Ordinal);
                listed = listed.Concat(cached.Select(option => option.Value).Where(model =>
                    failures.Contains(model.Provider) && refreshedKeys.Add(Key(model.Provider ?? "", model.Id)))).ToArray();
            }
            var options = CreateOptions(listed, current, defaultModelKey, providerFilter);
            var failedProviders = failures.Order(StringComparer.Ordinal).ToArray();
            var status = failedProviders.Length switch
            {
                0 => "Model catalogs refreshed.",
                1 => $"Could not refresh {failedProviders[0]}; showing cached models.",
                _ => $"Could not refresh {failedProviders.Length} model catalogs ({string.Join(", ", failedProviders)}); showing cached models."
            };
            return new(options.Options, options.ScopedOptions, status, failedProviders.Length == 0);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception error)
        {
            var safe = SecretRedactor.Redact(error.Message);
            return new(cached, modelRuntime.Scope.Count == 0 ? null : cached.Where(option => IsInScope(option.Value)).ToArray(),
                $"Could not refresh model catalogs: {safe}", IsSuccess: false);
        }
    }

    private TerminalSelectionRefresh<ModelDescriptor> CreateOptions(IEnumerable<ModelDescriptor> listed,
        ModelSelection current, string? defaultModelKey, string? providerFilter)
    {
        // Pi's interactive selector is fed from the available model snapshot. Do not fill it with
        // every provider catalogue entry that cannot be selected until the user configures auth.
        var models = listed.Where(model => model.Available &&
                (providerFilter is null || string.Equals(model.Provider, providerFilter, StringComparison.OrdinalIgnoreCase)))
            .ToList();
        if (models.All(model => !SameModel(model, current.Provider.Id, current.Model.Id)))
            models.Add(current.Model with { Provider = current.Provider.Id });

        // Match Pi's current/default/provider priorities while preserving each provider's catalog order.
        var ordered = models.OrderByDescending(model => SameModel(model, current.Provider.Id, current.Model.Id))
            .ThenByDescending(model => Key(model.Provider ?? current.Provider.Id, model.Id)
                .Equals(defaultModelKey, StringComparison.OrdinalIgnoreCase))
            .ThenBy(model => model.Provider, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var options = ordered.Select(model => ToOption(model, current, defaultModelKey)).ToArray();
        var scopedOptions = modelRuntime.Scope.Count == 0 ? null : options.Where(option => IsInScope(option.Value)).ToArray();
        return new(options, scopedOptions, "", IsSuccess: true);
    }

    private TerminalSelectionOption<ModelDescriptor> ToOption(ModelDescriptor model, ModelSelection current,
        string? defaultModelKey)
    {
        var provider = model.Provider ?? current.Provider.Id;
        var isCurrent = SameModel(model, current.Provider.Id, current.Model.Id);
        var searchText = $"{provider} {provider}/{model.Id} {provider} {model.Id} {model.Name}";
        return new(Key(provider, model.Id), model, model.Id, provider, searchText, isCurrent,
            IsDefault: Key(provider, model.Id).Equals(defaultModelKey, StringComparison.OrdinalIgnoreCase));
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
