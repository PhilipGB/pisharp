using PiSharp.Runtime.Sessions;

namespace PiSharp.Cli.Protocols;

internal sealed class RpcUserSettingsController(string agentDirectory, Func<string, string?> environment,
    Func<UserSettings?> getProjectSettings, Action<UserSettings, UserSettings> applySettings,
    Func<ConversationRun> currentRun)
{
    public async Task SetAutoRetryEnabledAsync(bool enabled, CancellationToken cancellationToken)
    {
        await SetGlobalSettingAsync("retry.enabled", enabled ? "true" : "false", cancellationToken);
        currentRun().SetAutoRetryEnabled(enabled);
    }

    public async Task SetPromptDeliveryModeAsync(bool steering, PromptDeliveryMode mode,
        CancellationToken cancellationToken)
    {
        var setting = steering ? "steeringMode" : "followUpMode";
        await SetGlobalSettingAsync(setting, mode.ToSettingValue(), cancellationToken);
        if (steering) currentRun().SetSteeringMode(mode);
        else currentRun().SetFollowUpMode(mode);
    }

    private async Task SetGlobalSettingAsync(string setting, string value, CancellationToken cancellationToken)
    {
        var path = UserSettings.GetSettingsPath(agentDirectory, environment);
        await UserSettingsWriter.SetAsync(path, setting, value, userScope: true, cancellationToken);
        var baseSettings = await UserSettings.LoadAsync(agentDirectory, environment, cancellationToken);
        applySettings(baseSettings, baseSettings.Overlay(getProjectSettings() ?? new UserSettings()));
    }
}
