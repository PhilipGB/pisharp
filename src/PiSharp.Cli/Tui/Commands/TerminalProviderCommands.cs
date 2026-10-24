using PiSharp.Cli.Composition;

namespace PiSharp.Cli.Tui;

internal sealed class TerminalProviderCommands(ProviderModelRuntime providers, ModelRuntimeController controller,
    ActiveModelRuntime model, Func<TextWriter> output, TerminalThinkingSelector? thinkingSelector = null,
    TerminalEditor? editor = null, Func<string?, Task>? saveEnabledModels = null)
{
    private readonly TerminalScopedModelsSelector? _scopedModelsSelector = editor is not null && saveEnabledModels is not null
        ? new(providers, editor, saveEnabledModels) : null;

    public async Task ListModelsAsync(string filter, string? providerFilter)
    {
        foreach (var item in (await providers.ListModelsAsync(providerFilter)).Where(item => string.IsNullOrEmpty(filter) ||
            item.Id.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
            (item.Provider?.Contains(filter, StringComparison.OrdinalIgnoreCase) ?? false)))
            output().WriteLine($"{item.Provider}/{item.Id} · {(item.Available ? item.Status ?? "available" : item.UnavailableReason ?? "unavailable")}" +
                $"{(item.Reasoning == true ? " · reasoning" : "")}{(item.ContextLength is null ? "" : " · " + item.ContextLength + " tokens")}");
    }

    public async Task SetScopeAsync(string argument, string? providerFilter)
    {
        var interactive = false;
        if (argument.Length > 0)
            providers.SetScope(argument.Split([',', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        else if (_scopedModelsSelector is { } selector)
        {
            interactive = true;
            var catalog = await providers.ListModelsAsync(includeOutOfScope: true);
            await selector.ShowAsync(catalog);
        }
        if (interactive)
            output().WriteLine(!providers.HasScope ? "Scoped models: all" : $"Scoped models: {providers.Scope.Count} model references");
        else
        {
            output().WriteLine(!providers.HasScope ? "Scoped models: all" :
                "Scoped models: " + string.Join(", ", providers.Scope));
            foreach (var scoped in await providers.ListModelsAsync(argument.Length == 0 ? null : providerFilter))
                output().WriteLine($"  {scoped.Provider}/{scoped.Id}{(scoped.Available ? "" : " · unavailable")}");
        }
    }

    public async Task SetThinkingAsync(string argument, string? defaultLevel,
        Func<string, Task> saveDefaultLevel, Func<ModelSelection, string, bool, bool, Task> replace)
    {
        var available = ThinkingLevels.AvailableForModel(model.Selection.Model.Reasoning,
            model.Selection.Model.ThinkingLevelMap);
        if (argument.Length > 0)
        {
            var requested = ThinkingLevels.RequireAvailableForModel(argument,
                model.Selection.Model.Reasoning, model.Selection.Model.ThinkingLevelMap);
            await replace(model.Selection, requested, false, true);
            output().WriteLine($"Thinking: {model.Thinking}");
            return;
        }

        if (thinkingSelector is null) return;
        var selected = await thinkingSelector.ShowAsync(model.Selection, model.Thinking, defaultLevel);
        if (selected is null) return;
        if (!selected.Level.Equals(model.Thinking, StringComparison.Ordinal))
            await replace(model.Selection, selected.Level, false, true);
        if (selected.SetAsDefault) await saveDefaultLevel(selected.Level);
        output().WriteLine($"Thinking: {model.Thinking}");
    }

    public async Task LogoutAsync(string argument, Func<ModelSelection, string, bool, bool, Task> replace)
    {
        var provider = argument.Length == 0 ? model.Selection.Provider.Id : argument;
        var removed = await providers.LogoutAsync(provider);
        output().WriteLine(removed ? $"Logged out {provider}." : $"No stored credential for {provider}.");
        if (provider.Equals(model.Selection.Provider.Id, StringComparison.OrdinalIgnoreCase))
            await replace(await providers.ResolveAsync(model.Selection.Provider.Id, model.Selection.Model.Id),
                model.Thinking, false, false);
    }

    public async Task SelectNamedModelAsync(string argument, Func<ModelSelection, string, Task> apply)
    {
        var reference = argument.Trim();
        var slash = reference.IndexOf('/');
        var qualifiedProvider = slash > 0 && providers.Providers.Any(provider =>
            provider.Id.Equals(reference[..slash], StringComparison.OrdinalIgnoreCase));
        var currentProvider = model.Selection.Provider.Id;
        var currentMatch = currentProvider.Equals("llama.cpp", StringComparison.OrdinalIgnoreCase) && !qualifiedProvider
            ? providers.GetAvailableModelSnapshot().FirstOrDefault(candidate =>
                candidate.Available && candidate.Provider?.Equals(currentProvider, StringComparison.OrdinalIgnoreCase) == true &&
                candidate.Id.Equals(reference, StringComparison.OrdinalIgnoreCase))
            : null;
        var selected = currentMatch is null
            ? await providers.ResolveAsync(null, reference, includeOutOfScope: true)
            : await providers.ResolveAsync(currentProvider, currentMatch.Id, includeOutOfScope: true);
        var thinking = controller.ResolveThinkingLevelForModelSwitch(selected, model.Thinking);
        await apply(selected, thinking);
    }
}
