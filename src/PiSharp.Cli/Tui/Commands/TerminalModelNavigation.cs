namespace PiSharp.Cli.Tui;

internal sealed class TerminalModelNavigation(ProviderModelRuntime models, Func<TextWriter> output)
{
    public string? NextThinkingLevel(ModelSelection selection, string thinking)
    {
        if (selection.Model.Reasoning != true)
        {
            output().WriteLine("Current model does not support thinking.");
            return null;
        }
        var levels = ThinkingLevels.AvailableForModel(selection.Model.Reasoning, selection.Model.ThinkingLevelMap);
        if (levels.Count == 0)
        {
            output().WriteLine("Current model has no available thinking levels.");
            return null;
        }
        var index = Array.FindIndex(levels.ToArray(), level => level.Equals(thinking, StringComparison.OrdinalIgnoreCase));
        return levels[(index + 1 + levels.Count) % levels.Count];
    }

    public async Task<ModelSelection?> NextModelAsync(Func<ModelSelection> getSelection, string? providerFilter, int direction)
    {
        var available = (await models.ListModelsAsync(providerFilter)).Where(model => model.Available).ToArray();
        if (available.Length == 0)
        {
            output().WriteLine("No available models in the current scope.");
            return null;
        }
        if (available.Length == 1)
        {
            output().WriteLine(models.HasScope ? "Only one model in scope." : "Only one model available.");
            return null;
        }
        var selection = getSelection();
        var index = Array.FindIndex(available, model =>
            model.Provider?.Equals(selection.Provider.Id, StringComparison.OrdinalIgnoreCase) == true &&
            model.Id.Equals(selection.Model.Id, StringComparison.OrdinalIgnoreCase));
        if (index < 0) index = 0;
        var next = available[(index + direction + available.Length) % available.Length];
        if (string.IsNullOrWhiteSpace(next.Provider))
            throw new InvalidOperationException("The model catalogue did not identify the selected provider.");
        return await models.ResolveAsync(next.Provider, next.Id);
    }
}
