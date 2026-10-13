using PiSharp.Cli.Composition;

namespace PiSharp.Cli.Tui;

internal sealed class TerminalModelSelectionCommands(ProviderModelRuntime modelRuntime, ModelRuntimeController modelRuntimeController,
    ActiveProjectRuntime projectBinding, ActiveModelRuntime modelBinding, ConversationModelTransitions modelTransitions,
    TerminalPresentation presentation, TerminalSettingsCommands settingsCommands)
{
    private readonly TerminalModelPicker? _picker = presentation.Editor is null ? null : new TerminalModelPicker(modelRuntime, presentation.Editor,
        () => projectBinding.Settings.DefaultProvider is { } provider && projectBinding.Settings.DefaultModel is { } model
            ? provider + "\0" + model : null);
    public void ShowModelChangeStatus(bool persistAsDefault = false)
    {
        var label = persistAsDefault
            ? $"Default model: {modelBinding.Selection.Provider.Id}/{modelBinding.Selection.Model.Id}"
            : $"Model: {modelBinding.Selection.Model.Id}";
        if (presentation.Screen is { IsActive: true } screen)
            screen.ShowModelChangeStatus(presentation.IdleFooter(), label);
        else Console.WriteLine($"Model: {modelBinding.Selection.Provider.Id}/{modelBinding.Selection.Model.Id} · thinking {modelBinding.Thinking}");
    }
    public async Task SelectModelAsync()
    {
        if (_picker is null) return;
        var picked = await _picker.ShowAsync(modelBinding.Selection);
        if (picked is null) return;
        var nextThinking = modelRuntimeController.ResolveThinkingLevelForModelSwitch(picked.Selection, modelBinding.Thinking);
        await modelTransitions.ApplySelectionAsync(picked.Selection, nextThinking);
        if (picked.PersistAsDefault)
        {
            await settingsCommands.SaveSettingAsync(projectScope: false, "defaultProvider", modelBinding.Selection.Provider.Id);
            await settingsCommands.SaveSettingAsync(projectScope: false, "defaultModel", modelBinding.Selection.Model.Id);
        }
        ShowModelChangeStatus(picked.PersistAsDefault);
    }

    private readonly TerminalModelNavigation _navigation = new(modelRuntime, () => Console.Out);

    public async Task CycleThinkingAsync()
    {
        var nextThinking = _navigation.NextThinkingLevel(modelBinding.Selection, modelBinding.Thinking);
        if (nextThinking is null) return;
        await modelTransitions.ReplaceAsync(modelBinding.Selection, nextThinking, recordModelChange: false);
        Console.WriteLine($"Thinking level: {modelBinding.Thinking}");
    }

    public async Task CycleModelAsync(int direction)
    {
        var nextSelection = await _navigation.NextModelAsync(() => modelBinding.Selection, projectBinding.Arguments.Provider, direction);
        if (nextSelection is null) return;
        var compatibleThinking = modelRuntimeController.ResolveThinkingLevelForModelSwitch(nextSelection, modelBinding.Thinking);
        await modelTransitions.ApplySelectionAsync(nextSelection, compatibleThinking);
        ShowModelChangeStatus();
    }
}
