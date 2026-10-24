using PiSharp.Cli.Composition;
using PiSharp.Runtime.Providers;
using PiSharp.Runtime.Sessions;

namespace PiSharp.Cli.Tui;

internal sealed class TerminalSettingsCommands(string agentDirectory, ActiveProjectRuntime projectBinding,
    ActiveModelRuntime modelBinding, ActiveConversationRuntime conversationBinding, ConversationModelTransitions modelTransitions,
    ModelRuntimeController modelRuntimeController, ProviderModelRuntime modelRuntime,
    TerminalPresentation presentation, TerminalConversationExecution execution)
{
    private readonly TerminalSettingsPicker? _picker = presentation.Editor is null ? null :
        new TerminalSettingsPicker(presentation.Editor, modelRuntime, () => presentation.Themes.GetAvailableNames());

    public async Task<(UserSettings User, UserSettings? Project)> SaveSettingAsync(bool projectScope, string setting,
        string? value) => await SaveSettingCoreAsync(projectScope, setting, value, announce: true);

    public async Task SaveModelThinkingLevelAsync(string level) =>
        await SaveModelThinkingLevelAsync(modelBinding.Selection.Model, level);

    public async Task<(UserSettings User, UserSettings? Project)> SaveModelThinkingLevelAsync(ModelDescriptor model,
        string? level)
    {
        var savedSettings = await UserSettingsPersistence.SaveModelThinkingLevelAsync(agentDirectory,
            model.Provider!, model.Id, level,
            projectBinding.BaseUserSettings, projectBinding.ProjectSettings, Environment.GetEnvironmentVariable);
        projectBinding.SetSettings(savedSettings.User, savedSettings.Project, savedSettings.Effective);
        projectBinding.PublishSettingsConfiguration();
        Console.WriteLine($"{(level is null ? "Removed" : "Saved")} thinking default for {model.Provider}/{model.Id}{(level is null ? "." : $": {level}.")}");
        if (!modelBinding.Selection.Provider.Id.Equals(model.Provider, StringComparison.OrdinalIgnoreCase) ||
            !modelBinding.Selection.Model.Id.Equals(model.Id, StringComparison.OrdinalIgnoreCase))
            return (projectBinding.BaseUserSettings, projectBinding.ProjectSettings);

        var fallback = projectBinding.Arguments.Thinking ?? projectBinding.Settings.DefaultThinkingLevel ?? ThinkingLevels.Default;
        var nextThinking = modelRuntimeController.ResolveThinkingLevelForModelSwitch(modelBinding.Selection, fallback);
        if (nextThinking.Equals(modelBinding.Thinking, StringComparison.Ordinal))
            return (projectBinding.BaseUserSettings, projectBinding.ProjectSettings);
        if (conversationBinding.Run.IsActive)
            await modelTransitions.SetRpcThinkingLevelDuringRunAsync(nextThinking, CancellationToken.None);
        else
            await modelTransitions.SelectThinkingLevelAsync(nextThinking);
        return (projectBinding.BaseUserSettings, projectBinding.ProjectSettings);
    }

    public async Task<(UserSettings User, UserSettings? Project)> SaveSettingCoreAsync(bool projectScope, string setting,
        string? value, bool announce)
    {
        var savedSettings = await UserSettingsPersistence.SaveAsync(agentDirectory, projectBinding.WorkingDirectory,
            projectScope, projectBinding.Trusted, setting, value, projectBinding.BaseUserSettings, projectBinding.ProjectSettings, Environment.GetEnvironmentVariable);
        projectBinding.SetSettings(savedSettings.User, savedSettings.Project, savedSettings.Effective);
        presentation.ApplyTuiSettings(projectBinding.Settings);
        if (setting == "hideThinkingBlock")
        {
            presentation.SetHideThinking(projectBinding.Settings.HideThinkingBlock == true);
            execution.ActiveTranscript?.SetHideThinkingBlock(presentation.HideThinking);
        }
        if (setting == "theme") presentation.ClearThemeOverride();
        projectBinding.PublishSettingsConfiguration();
        if (setting == "steeringMode")
            conversationBinding.Run.SetSteeringMode(projectBinding.Settings.SteeringMode ?? PromptDeliveryMode.OneAtATime);
        else if (setting == "followUpMode")
            conversationBinding.Run.SetFollowUpMode(projectBinding.Settings.FollowUpMode ?? PromptDeliveryMode.OneAtATime);
        if (setting is "images.blockImages" or "compaction.enabled" or "retry.provider.maxRetries" or
            "retry.provider.maxRetryDelayMs" or "retry.provider.timeoutMs" or "httpIdleTimeoutMs" or "images.autoResize")
            await modelTransitions.ReplaceAsync(modelBinding.Selection, modelBinding.Thinking, recordModelChange: false);
        if (setting is "theme" or "terminal.trueColor")
        {
            presentation.RefreshTerminalTheme();
        }
        else if (setting == "markdown.codeBlockIndent")
            presentation.Screen?.SetMarkdownCodeBlockIndent(projectBinding.Settings.MarkdownCodeBlockIndent ?? UserSettings.DefaultMarkdownCodeBlockIndent);
        if (announce)
        {
            var displayValue = setting == "httpProxy" ? value is null ? "(default)" : "(configured)" : value ?? "(default)";
            Console.WriteLine($"Saved {(projectScope ? "project" : "user")} setting {setting} = {displayValue}.");
        }
        return (projectBinding.BaseUserSettings, projectBinding.ProjectSettings);
    }

    public async Task SelectSettingsAsync()
    {
        if (_picker is null) return;
        await _picker.ShowAsync(projectBinding.BaseUserSettings, projectBinding.ProjectSettings, SaveSettingAsync,
            activeThemeSetting: presentation.ActiveThemeSetting,
            saveModelThinkingLevel: SaveModelThinkingLevelAsync,
            currentModel: modelBinding.Selection.Model);
        Console.WriteLine("Settings closed.");
    }

    public async Task ToggleThinkingAsync()
    {
        var hide = !presentation.HideThinking;
        await SaveSettingCoreAsync(projectScope: false, "hideThinkingBlock", hide ? "true" : "false",
            announce: false);
        presentation.SetHideThinking(hide);
        execution.ActiveTranscript?.SetHideThinkingBlock(hide);
        presentation.Screen?.AppendThemedStatusLine($"Thinking blocks: {(hide ? "hidden" : "visible")}");
    }
}
