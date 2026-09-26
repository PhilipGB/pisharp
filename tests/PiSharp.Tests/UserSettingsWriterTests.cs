using PiSharp.Cli;
using PiSharp.Runtime.Sessions;

namespace PiSharp.Tests;

public sealed class UserSettingsWriterTests
{
    [Fact]
    public async Task UpdatesSupportedValuesAndPreservesOtherSettingsAtomically()
    {
        var root = Path.Combine(Path.GetTempPath(), "pisharp-settings-write-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var path = Path.Combine(root, "settings.json");
            await File.WriteAllTextAsync(path, "{\"quietStartup\":true,\"compaction\":{\"reserveTokens\":2048}}\n");

            await UserSettingsWriter.SetAsync(path, "hideThinkingBlock", "true", userScope: true);
            await UserSettingsWriter.SetAsync(path, "images.blockImages", "false", userScope: true);
            await UserSettingsWriter.SetAsync(path, "compaction.enabled", "false", userScope: true);
            await UserSettingsWriter.SetAsync(path, "retry.enabled", "false", userScope: true);
            await UserSettingsWriter.SetAsync(path, "steeringMode", "all", userScope: true);
            await UserSettingsWriter.SetAsync(path, "followUpMode", "one-at-a-time", userScope: true);
            var settings = await UserSettings.LoadAsync(root, _ => null);

            Assert.True(settings.HideThinkingBlock);
            Assert.False(settings.BlockImages);
            Assert.True(settings.QuietStartup);
            Assert.False(settings.Compaction?.Enabled);
            Assert.Equal(2048, settings.Compaction?.ReserveTokens);
            Assert.False(settings.Retry?.Enabled);
            Assert.Equal(PromptDeliveryMode.All, settings.SteeringMode);
            Assert.Equal(PromptDeliveryMode.OneAtATime, settings.FollowUpMode);

            await UserSettingsWriter.SetAsync(path, "images.blockImages", null, userScope: true);
            await UserSettingsWriter.SetAsync(path, "retry.enabled", null, userScope: true);
            await UserSettingsWriter.SetAsync(path, "steeringMode", null, userScope: true);
            Assert.Null((await UserSettings.LoadAsync(root, _ => null)).BlockImages);
            var cleared = await UserSettings.LoadAsync(root, _ => null);
            Assert.Null(cleared.Retry);
            Assert.Null(cleared.SteeringMode);
            await Assert.ThrowsAsync<ArgumentException>(() =>
                UserSettingsWriter.SetAsync(path, "followUpMode", "sometimes", userScope: true));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task NewUserSettingsFileIsPrivateAndProjectScopeCannotSetUserOnlyTrust()
    {
        var root = Path.Combine(Path.GetTempPath(), "pisharp-settings-mode-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var path = Path.Combine(root, "nested", "settings.json");
            await UserSettingsWriter.SetAsync(path, "defaultProjectTrust", "never", userScope: true);

            Assert.Equal("never", (await UserSettings.LoadAsync(root, _ => Path.Combine(root, "nested", "settings.json"))).DefaultProjectTrust);
            if (OperatingSystem.IsLinux())
                Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path));

            await Assert.ThrowsAsync<ArgumentException>(() =>
                UserSettingsWriter.SetAsync(path, "defaultProjectTrust", "always", userScope: false));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task UpdatesTrustedProjectScopeSettings()
    {
        var root = Path.Combine(Path.GetTempPath(), "pisharp-project-settings-write-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var path = Path.Combine(root, ".pi", "settings.json");
            await UserSettingsWriter.SetAsync(path, "compaction.enabled", "true", userScope: false);
            await UserSettingsWriter.SetAsync(path, "defaultThinkingLevel", "high", userScope: false);
            await UserSettingsWriter.SetAsync(path, "externalEditor", "code --wait", userScope: false);

            var settings = await UserSettings.LoadProjectAsync(root);
            Assert.True(settings.Compaction?.Enabled);
            Assert.Equal("high", settings.DefaultThinkingLevel);
            Assert.Equal("code --wait", settings.ExternalEditor);
            await Assert.ThrowsAsync<ArgumentException>(() =>
                UserSettingsWriter.SetAsync(path, "externalEditor", " bad ", userScope: false));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task ThemeSettingSupportsUserAndProjectOverrideAndValidatesThemePairs()
    {
        var root = Path.Combine(Path.GetTempPath(), "pisharp-theme-setting-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var userPath = Path.Combine(root, "agent", "settings.json");
            var projectPath = Path.Combine(root, "workspace", ".pi", "settings.json");
            Directory.CreateDirectory(Path.GetDirectoryName(projectPath)!);
            await UserSettingsWriter.SetAsync(userPath, "theme", "light/dark", userScope: true);
            await UserSettingsWriter.SetAsync(projectPath, "theme", "ocean", userScope: false);

            var user = await UserSettings.LoadAsync(root, _ => userPath);
            var project = await UserSettings.LoadProjectAsync(Path.Combine(root, "workspace"));
            Assert.Equal("light/dark", user.Theme);
            Assert.Equal("ocean", project.Theme);
            Assert.Equal("ocean", user.Overlay(project).Theme);
            await Assert.ThrowsAsync<ArgumentException>(() =>
                UserSettingsWriter.SetAsync(userPath, "theme", "light/dark/extra", userScope: true));
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}
