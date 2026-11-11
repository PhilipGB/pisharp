using System.Text.Json;
using PiSharp.Cli;

namespace PiSharp.Tests;

/// <summary>Settings contract for `treeFilterMode`, pinned to Pi's SettingsManager getTreeFilterMode.</summary>
public sealed class TreeFilterModeSettingTests
{
    private static string NewAgentDirectory()
    {
        var directory = Path.Combine(Path.GetTempPath(), "pisharp-tree-filter-settings-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }

    private static async Task<UserSettings> LoadAsync(string directory, string json)
    {
        await File.WriteAllTextAsync(Path.Combine(directory, "settings.json"), json);
        return await UserSettings.LoadAsync(directory, _ => null);
    }

    [Fact]
    public async Task UnsetResolvesTheDefaultFilterMode()
    {
        var directory = NewAgentDirectory();
        try
        {
            var settings = await LoadAsync(directory, "{\"quietStartup\":true}");
            Assert.Null(settings.TreeFilterMode);
            Assert.Equal("default", settings.EffectiveTreeFilterMode);
            Assert.Equal("default", new UserSettings().EffectiveTreeFilterMode);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Theory]
    [InlineData("default", "default")]
    [InlineData("no-tools", "no-tools")]
    [InlineData("user-only", "user-only")]
    [InlineData("labeled-only", "labeled-only")]
    [InlineData("all", "all")]
    public async Task EveryPinnedValueRoundTripsThroughLoad(string configured, string expected)
    {
        var directory = NewAgentDirectory();
        try
        {
            var settings = await LoadAsync(directory, JsonSerializer.Serialize(
                new Dictionary<string, string> { ["treeFilterMode"] = configured }));
            Assert.Equal(configured, settings.TreeFilterMode);
            Assert.Equal(expected, settings.EffectiveTreeFilterMode);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Theory]
    [InlineData("\"bogus\"")]
    [InlineData("\"\"")]
    [InlineData("42")]
    [InlineData("null")]
    [InlineData("{\"mode\":\"all\"}")]
    public async Task UnrecognizedValuesResolveTheDefaultWithoutFailingToLoad(string storedValue)
    {
        // Pinned Pi keeps the raw settings value and getTreeFilterMode() falls back to its default.
        var directory = NewAgentDirectory();
        try
        {
            var settings = await LoadAsync(directory, "{\"treeFilterMode\":" + storedValue + "}");
            Assert.Equal("default", settings.EffectiveTreeFilterMode);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public void TrustedProjectValueOverridesTheUserValue()
    {
        var user = new UserSettings(TreeFilterMode: "user-only");
        var project = new UserSettings(TreeFilterMode: "all");

        Assert.Equal("all", user.Overlay(project).EffectiveTreeFilterMode);
        Assert.Equal("user-only", new UserSettings(TreeFilterMode: "user-only")
            .Overlay(new UserSettings()).EffectiveTreeFilterMode);
        Assert.Equal("default", new UserSettings().Overlay(new UserSettings()).EffectiveTreeFilterMode);
    }

    [Fact]
    public async Task WriterPersistsValidValuesAndResetRemovesOnlyThatKey()
    {
        var directory = NewAgentDirectory();
        try
        {
            var path = Path.Combine(directory, "settings.json");
            await File.WriteAllTextAsync(path,
                "{\"shellCommandPrefix\":\"preserved\",\"quietStartup\":true}\n");

            await UserSettingsWriter.SetAsync(path, "treeFilterMode", "labeled-only", userScope: true);
            var persisted = await UserSettings.LoadAsync(directory, _ => null);
            Assert.Equal("labeled-only", persisted.TreeFilterMode);
            Assert.Equal("labeled-only", persisted.EffectiveTreeFilterMode);

            using (var document = JsonDocument.Parse(await File.ReadAllTextAsync(path)))
            {
                Assert.Equal("preserved", document.RootElement.GetProperty("shellCommandPrefix").GetString());
                Assert.True(document.RootElement.GetProperty("quietStartup").GetBoolean());
                Assert.Equal("labeled-only", document.RootElement.GetProperty("treeFilterMode").GetString());
            }

            await UserSettingsWriter.SetAsync(path, "treeFilterMode", null, userScope: true);
            var reset = await UserSettings.LoadAsync(directory, _ => null);
            Assert.Null(reset.TreeFilterMode);
            Assert.Equal("default", reset.EffectiveTreeFilterMode);
            using (var document = JsonDocument.Parse(await File.ReadAllTextAsync(path)))
            {
                Assert.False(document.RootElement.TryGetProperty("treeFilterMode", out _));
                Assert.Equal("preserved", document.RootElement.GetProperty("shellCommandPrefix").GetString());
                Assert.True(document.RootElement.GetProperty("quietStartup").GetBoolean());
            }
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Theory]
    [InlineData("bogus")]
    [InlineData("Default")]
    [InlineData("all ")]
    [InlineData("")]
    public async Task WriterRejectsValuesOutsideThePinnedSet(string value)
    {
        var directory = NewAgentDirectory();
        try
        {
            var path = Path.Combine(directory, "settings.json");
            await Assert.ThrowsAsync<ArgumentException>(() =>
                UserSettingsWriter.SetAsync(path, "treeFilterMode", value, userScope: true));
            Assert.False(File.Exists(path));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }
}
