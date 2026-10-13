using PiSharp.Cli.Composition;

namespace PiSharp.Cli.Tui;

internal sealed class TerminalProviderCommands(ProviderModelRuntime providers, ModelRuntimeController controller,
    ActiveModelRuntime model, Func<TextWriter> output)
{
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
        if (argument.Length > 0)
            providers.SetScope(argument.Split([',', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        output().WriteLine(providers.Scope.Count == 0 ? "Scoped models: all" :
            "Scoped models: " + string.Join(", ", providers.Scope));
        foreach (var scoped in await providers.ListModelsAsync(providerFilter))
            output().WriteLine($"  {scoped.Provider}/{scoped.Id}{(scoped.Available ? "" : " · unavailable")}");
    }

    public async Task SetThinkingAsync(string argument, Func<ModelSelection, string, bool, bool, Task> replace)
    {
        if (argument.Length == 0)
        {
            var available = ThinkingLevels.AvailableForModel(model.Selection.Model.Reasoning,
                model.Selection.Model.ThinkingLevelMap);
            output().WriteLine($"Thinking: {model.Thinking}; available: " +
                (available.Count == 0 ? "none" : string.Join(", ", available)));
            return;
        }
        await replace(model.Selection, argument, false, true);
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
        var selected = await providers.ResolveAsync(null, argument, includeOutOfScope: true);
        var thinking = controller.ResolveThinkingLevelForModelSwitch(selected, model.Thinking);
        await apply(selected, thinking);
    }
}
