using PiSharp.Cli;

namespace PiSharp.Tests;

public sealed class TerminalSettingsPersistenceTests
{
    [Fact]
    public async Task NewInteractiveSettingsPersistInBothScopesAndProjectValuesOverrideUserValues()
    {
        var root = Path.Combine(Path.GetTempPath(), "pisharp-terminal-settings-" + Guid.NewGuid().ToString("N"));
        var userPath = Path.Combine(root, "agent", "settings.json");
        var projectPath = Path.Combine(root, "workspace", ".pi", "settings.json");
        try
        {
            await UserSettingsWriter.SetAsync(userPath, "images.autoResize", "false", userScope: true);
            await UserSettingsWriter.SetAsync(userPath, "showHardwareCursor", "true", userScope: true);
            await UserSettingsWriter.SetAsync(userPath, "editorPaddingX", "2", userScope: true);
            await UserSettingsWriter.SetAsync(userPath, "outputPad", "0", userScope: true);
            await UserSettingsWriter.SetAsync(userPath, "autocompleteMaxVisible", "7", userScope: true);
            await UserSettingsWriter.SetAsync(userPath, "terminal.clearOnShrink", "true", userScope: true);
            await UserSettingsWriter.SetAsync(userPath, "terminal.showTerminalProgress", "true", userScope: true);

            await UserSettingsWriter.SetAsync(projectPath, "images.autoResize", "true", userScope: false);
            await UserSettingsWriter.SetAsync(projectPath, "showHardwareCursor", "false", userScope: false);
            await UserSettingsWriter.SetAsync(projectPath, "editorPaddingX", "1", userScope: false);
            await UserSettingsWriter.SetAsync(projectPath, "outputPad", "1", userScope: false);
            await UserSettingsWriter.SetAsync(projectPath, "autocompleteMaxVisible", "10", userScope: false);
            await UserSettingsWriter.SetAsync(projectPath, "terminal.clearOnShrink", "false", userScope: false);
            await UserSettingsWriter.SetAsync(projectPath, "terminal.showTerminalProgress", "false", userScope: false);

            var user = await UserSettings.LoadAsync(Path.Combine(root, "agent"), _ => null);
            var project = await UserSettings.LoadProjectAsync(Path.Combine(root, "workspace"));
            var effective = user.Overlay(project);

            Assert.False(user.AutoResizeImages);
            Assert.True(user.ShowHardwareCursor);
            Assert.Equal(2, user.EditorPaddingX);
            Assert.Equal(0, user.OutputPad);
            Assert.Equal(7, user.AutocompleteMaxVisible);
            Assert.True(user.ClearOnShrink);
            Assert.True(user.ShowTerminalProgress);
            Assert.True(project.AutoResizeImages);
            Assert.False(project.ShowHardwareCursor);
            Assert.Equal(1, project.EditorPaddingX);
            Assert.Equal(1, project.OutputPad);
            Assert.Equal(10, project.AutocompleteMaxVisible);
            Assert.False(project.ClearOnShrink);
            Assert.False(project.ShowTerminalProgress);
            Assert.True(effective.AutoResizeImages);
            Assert.False(effective.ShowHardwareCursor);
            Assert.Equal(1, effective.EditorPaddingX);
            Assert.Equal(1, effective.OutputPad);
            Assert.Equal(10, effective.AutocompleteMaxVisible);
            Assert.False(effective.ClearOnShrink);
            Assert.False(effective.ShowTerminalProgress);

            await Assert.ThrowsAsync<ArgumentException>(() =>
                UserSettingsWriter.SetAsync(projectPath, "editorPaddingX", "4", userScope: false));
            await Assert.ThrowsAsync<ArgumentException>(() =>
                UserSettingsWriter.SetAsync(projectPath, "outputPad", "2", userScope: false));
            await Assert.ThrowsAsync<ArgumentException>(() =>
                UserSettingsWriter.SetAsync(projectPath, "autocompleteMaxVisible", "21", userScope: false));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }
}
