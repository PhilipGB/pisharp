namespace PiSharp.Cli;

internal sealed record PersistedUserSettings(UserSettings User, UserSettings? Project, UserSettings Effective);

internal static class UserSettingsPersistence
{
    public static async Task<PersistedUserSettings> SaveAsync(string agentDirectory, string workingDirectory,
        bool projectScope, bool trusted, string setting, string? value, UserSettings user, UserSettings? project,
        Func<string, string?> environment, CancellationToken cancellationToken = default)
    {
        if (projectScope && !trusted)
            throw new InvalidOperationException("Trust this project before editing its settings.");
        var path = projectScope
            ? Path.Combine(workingDirectory, ".pi", "settings.json")
            : UserSettings.GetSettingsPath(agentDirectory, environment);
        await UserSettingsWriter.SetAsync(path, setting, value, userScope: !projectScope, cancellationToken);
        if (projectScope)
            project = await UserSettings.LoadProjectAsync(workingDirectory, cancellationToken);
        else
            user = await UserSettings.LoadAsync(agentDirectory, environment, cancellationToken);
        return new(user, project, user.Overlay(project ?? new UserSettings()));
    }
}
