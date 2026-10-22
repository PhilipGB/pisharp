using PiSharp.Cli;
using PiSharp.Runtime.Sessions;

namespace PiSharp.Tests;

public sealed class UserSettingsWriterTests
{
    [Fact]
    public async Task SavesModelSpecificThinkingDefaultWithoutDroppingExistingSettings()
    {
        var root = Path.Combine(Path.GetTempPath(), "pisharp-model-thinking-write-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var path = Path.Combine(root, "settings.json");
            await UserSettingsWriter.SetAsync(path, "defaultThinkingLevel", "medium", userScope: true);
            await UserSettingsWriter.SetModelThinkingLevelAsync(path, "openai", "gpt-5", "low");
            await UserSettingsWriter.SetModelThinkingLevelAsync(path, "llama.cpp", "Qwen3.8-27B", "xhigh");

            var settings = await UserSettings.LoadAsync(root, _ => null);

            Assert.Equal("medium", settings.DefaultThinkingLevel);
            Assert.Equal("low", settings.GetModelThinkingLevel("openai", "gpt-5"));
            Assert.Equal("xhigh", settings.GetModelThinkingLevel("llama.cpp", "Qwen3.8-27B"));
            await Assert.ThrowsAsync<ArgumentException>(() =>
                UserSettingsWriter.SetModelThinkingLevelAsync(path, "llama.cpp", "Qwen3.8-27B", "unsupported"));
            Assert.Equal("xhigh", (await UserSettings.LoadAsync(root, _ => null))
                .GetModelThinkingLevel("llama.cpp", "Qwen3.8-27B"));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task TuiModeIsValidatedPersistedAndInheritedOnlyFromUserSettings()
    {
        var root = Path.Combine(Path.GetTempPath(), "pisharp-tui-mode-write-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var path = Path.Combine(root, "settings.json");
            await UserSettingsWriter.SetAsync(path, "tuiMode", "regular", userScope: true);

            var user = await UserSettings.LoadAsync(root, _ => null);
            var effective = user.Overlay(new UserSettings(TuiMode: "fullscreen"));
            Assert.Equal("regular", effective.TuiMode);

            await UserSettingsWriter.SetAsync(path, "tuiMode", "fullscreen", userScope: true);
            Assert.Equal("fullscreen", (await UserSettings.LoadAsync(root, _ => null)).TuiMode);

            await Assert.ThrowsAsync<ArgumentException>(() =>
                UserSettingsWriter.SetAsync(path, "tuiMode", "windowed", userScope: true));
            await Assert.ThrowsAsync<ArgumentException>(() =>
                UserSettingsWriter.SetAsync(path, "tuiMode", "regular", userScope: false));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task UpdatesSupportedValuesAndPreservesOtherSettingsAtomically()
    {
        var root = Path.Combine(Path.GetTempPath(), "pisharp-settings-write-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var path = Path.Combine(root, "settings.json");
            await File.WriteAllTextAsync(path, "{\"quietStartup\":true,\"shellCommandPrefix\":\"export PISHARP_PREFIX=kept\",\"compaction\":{\"reserveTokens\":2048}}\n");

            await UserSettingsWriter.SetAsync(path, "hideThinkingBlock", "true", userScope: true);
            await UserSettingsWriter.SetAsync(path, "images.blockImages", "false", userScope: true);
            await UserSettingsWriter.SetAsync(path, "compaction.enabled", "false", userScope: true);
            await UserSettingsWriter.SetAsync(path, "retry.enabled", "false", userScope: true);
            await UserSettingsWriter.SetAsync(path, "steeringMode", "all", userScope: true);
            await UserSettingsWriter.SetAsync(path, "followUpMode", "one-at-a-time", userScope: true);
            await UserSettingsWriter.SetAsync(path, "retry.provider.maxRetries", "2", userScope: true);
            await UserSettingsWriter.SetAsync(path, "retry.provider.maxRetryDelayMs", "2500.5", userScope: true);
            await UserSettingsWriter.SetAsync(path, "retry.provider.timeoutMs", "120000", userScope: true);
            await UserSettingsWriter.SetAsync(path, "httpIdleTimeoutMs", "disabled", userScope: true);
            await UserSettingsWriter.SetAsync(path, "markdown.codeBlockIndent", ">>", userScope: true);
            await UserSettingsWriter.SetAsync(path, "terminal.trueColor", "true", userScope: true);
            await UserSettingsWriter.SetAsync(path, "enableSkillCommands", "false", userScope: true);
            await UserSettingsWriter.SetAsync(path, "thinkingBudgets.low", "4096", userScope: true);
            await UserSettingsWriter.SetAsync(path, "thinkingBudgets.high", "32768", userScope: true);
            await UserSettingsWriter.SetAsync(path, "fullscreenWheelScrollLines", "3", userScope: true);
            var settings = await UserSettings.LoadAsync(root, _ => null);

            Assert.True(settings.HideThinkingBlock);
            Assert.False(settings.BlockImages);
            Assert.Equal(QuietStartupMode.Silent, settings.QuietStartup);
            Assert.Equal("export PISHARP_PREFIX=kept", settings.ShellCommandPrefix);
            Assert.False(settings.Compaction?.Enabled);
            Assert.Equal(2048, settings.Compaction?.ReserveTokens);
            Assert.False(settings.Retry?.Enabled);
            Assert.Equal(PromptDeliveryMode.All, settings.SteeringMode);
            Assert.Equal(PromptDeliveryMode.OneAtATime, settings.FollowUpMode);
            Assert.Equal(2, settings.Retry?.Provider?.MaxRetries);
            Assert.Equal(2_500.5, settings.Retry?.Provider?.MaxRetryDelayMs);
            Assert.Equal(120_000, settings.Retry?.Provider?.TimeoutMs);
            Assert.Equal(0, settings.HttpIdleTimeoutMs);
            Assert.Equal(">>", settings.MarkdownCodeBlockIndent);
            Assert.Equal("true", settings.TerminalTrueColor);
            Assert.True(settings.TerminalTrueColorOverride);
            Assert.False(settings.SkillCommandsEnabled);
            Assert.Equal(4096, settings.ThinkingBudgets!["low"]);
            Assert.Equal(32768, settings.ThinkingBudgets["high"]);
            Assert.Equal(FullscreenWheelScrollSetting.Fixed(3), settings.FullscreenWheelScrollLines);

            await UserSettingsWriter.SetAsync(path, "images.blockImages", null, userScope: true);
            await UserSettingsWriter.SetAsync(path, "retry.enabled", null, userScope: true);
            await UserSettingsWriter.SetAsync(path, "steeringMode", null, userScope: true);
            await UserSettingsWriter.SetAsync(path, "retry.provider.maxRetries", null, userScope: true);
            await UserSettingsWriter.SetAsync(path, "retry.provider.maxRetryDelayMs", null, userScope: true);
            await UserSettingsWriter.SetAsync(path, "retry.provider.timeoutMs", null, userScope: true);
            await UserSettingsWriter.SetAsync(path, "httpIdleTimeoutMs", null, userScope: true);
            await UserSettingsWriter.SetAsync(path, "markdown.codeBlockIndent", null, userScope: true);
            await UserSettingsWriter.SetAsync(path, "terminal.trueColor", "auto", userScope: true);
            Assert.Equal("auto", (await UserSettings.LoadAsync(root, _ => null)).TerminalTrueColor);
            await UserSettingsWriter.SetAsync(path, "terminal.trueColor", null, userScope: true);
            await UserSettingsWriter.SetAsync(path, "enableSkillCommands", null, userScope: true);
            await UserSettingsWriter.SetAsync(path, "thinkingBudgets.low", null, userScope: true);
            await UserSettingsWriter.SetAsync(path, "fullscreenWheelScrollLines", "auto", userScope: true);
            Assert.Equal(FullscreenWheelScrollSetting.Automatic,
                (await UserSettings.LoadAsync(root, _ => null)).FullscreenWheelScrollLines);
            await UserSettingsWriter.SetAsync(path, "fullscreenWheelScrollLines", null, userScope: true);
            Assert.Null((await UserSettings.LoadAsync(root, _ => null)).BlockImages);
            var cleared = await UserSettings.LoadAsync(root, _ => null);
            Assert.Null(cleared.Retry);
            Assert.Null(cleared.SteeringMode);
            Assert.Null(cleared.Retry);
            Assert.Null(cleared.MarkdownCodeBlockIndent);
            Assert.Null(cleared.TerminalTrueColor);
            Assert.Null(cleared.EnableSkillCommands);
            Assert.Null(cleared.FullscreenWheelScrollLines);
            Assert.True(cleared.SkillCommandsEnabled);
            await Assert.ThrowsAsync<ArgumentException>(() =>
                UserSettingsWriter.SetAsync(path, "followUpMode", "sometimes", userScope: true));
            await Assert.ThrowsAsync<ArgumentException>(() =>
                UserSettingsWriter.SetAsync(path, "retry.provider.maxRetries", "21", userScope: true));
            await Assert.ThrowsAsync<ArgumentException>(() =>
                UserSettingsWriter.SetAsync(path, "retry.provider.timeoutMs", "-1", userScope: true));
            await Assert.ThrowsAsync<ArgumentException>(() =>
                UserSettingsWriter.SetAsync(path, "thinkingBudgets.low", "-1", userScope: true));
            await Assert.ThrowsAsync<ArgumentException>(() =>
                UserSettingsWriter.SetAsync(path, "thinkingBudgets.high", "1.5", userScope: true));
            await Assert.ThrowsAsync<ArgumentException>(() =>
                UserSettingsWriter.SetAsync(path, "retry.provider.maxRetryDelayMs", "-1", userScope: true));
            await Assert.ThrowsAsync<ArgumentException>(() =>
                UserSettingsWriter.SetAsync(path, "retry.provider.maxRetryDelayMs", "NaN", userScope: true));
            await Assert.ThrowsAsync<ArgumentException>(() =>
                UserSettingsWriter.SetAsync(path, "httpIdleTimeoutMs", "sometimes", userScope: true));
            await Assert.ThrowsAsync<ArgumentException>(() =>
                UserSettingsWriter.SetAsync(path, "terminal.trueColor", "sometimes", userScope: true));
            await Assert.ThrowsAsync<ArgumentException>(() =>
                UserSettingsWriter.SetAsync(path, "fullscreenWheelScrollLines", "101", userScope: true));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void FullscreenWheelSettingParsesPiNumericValuesAndProjectSettingOverridesUserSetting()
    {
        using var document = System.Text.Json.JsonDocument.Parse("2.9");
        Assert.Equal(FullscreenWheelScrollSetting.Fixed(2),
            FullscreenWheelScrollSetting.Parse(document.RootElement));
        using var highDocument = System.Text.Json.JsonDocument.Parse("1000");
        Assert.Equal(FullscreenWheelScrollSetting.Fixed(100),
            FullscreenWheelScrollSetting.Parse(highDocument.RootElement));

        var user = new UserSettings(FullscreenWheelScrollLines: FullscreenWheelScrollSetting.Fixed(2));
        var project = new UserSettings(FullscreenWheelScrollLines: FullscreenWheelScrollSetting.Fixed(5));

        Assert.Equal(FullscreenWheelScrollSetting.Fixed(5), user.Overlay(project).FullscreenWheelScrollLines);
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
            await UserSettingsWriter.SetAsync(path, "terminal.trueColor", "false", userScope: false);
            await UserSettingsWriter.SetAsync(path, "enableSkillCommands", "true", userScope: false);

            var settings = await UserSettings.LoadProjectAsync(root);
            Assert.True(settings.Compaction?.Enabled);
            Assert.Equal("high", settings.DefaultThinkingLevel);
            Assert.Equal("code --wait", settings.ExternalEditor);
            Assert.Equal("false", settings.TerminalTrueColor);
            Assert.True(settings.SkillCommandsEnabled);
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

    [Fact]
    public async Task HttpProxyWriterEnforcesUserOnlyScopeAndRemovesTheSetting()
    {
        var root = Path.Combine(Path.GetTempPath(), "pisharp-http-proxy-write-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var path = Path.Combine(root, "settings.json");
            await UserSettingsWriter.SetAsync(path, "httpProxy", "https://proxy.example:8443", userScope: true);
            Assert.Equal("https://proxy.example:8443", (await UserSettings.LoadAsync(root, _ => null)).HttpProxy);

            await Assert.ThrowsAsync<ArgumentException>(() =>
                UserSettingsWriter.SetAsync(Path.Combine(root, "project", "settings.json"), "httpProxy",
                    "http://proxy.example:8080", userScope: false));
            await Assert.ThrowsAsync<ArgumentException>(() =>
                UserSettingsWriter.SetAsync(path, "httpProxy", "ftp://proxy.example:21", userScope: true));

            await UserSettingsWriter.SetAsync(path, "httpProxy", null, userScope: true);
            Assert.Null((await UserSettings.LoadAsync(root, _ => null)).HttpProxy);
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}
