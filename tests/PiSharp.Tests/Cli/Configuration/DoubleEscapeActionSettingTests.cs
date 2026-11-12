using System.Text.Json;
using PiSharp.Cli;

namespace PiSharp.Tests;

/// <summary>
/// Settings contract for `doubleEscapeAction`, pinned to Pi's SettingsManager getDoubleEscapeAction.
/// Unlike `treeFilterMode`, the pinned getter does not validate the merged value: it only falls back
/// when the value is nullish, and the interactive handler forks for anything that is not exactly
/// "tree" or "none".
/// </summary>
public sealed class DoubleEscapeActionSettingTests
{
    private static string NewAgentDirectory()
    {
        var directory = Path.Combine(Path.GetTempPath(), "pisharp-double-escape-settings-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }

    private static async Task<UserSettings> LoadAsync(string directory, string json)
    {
        await File.WriteAllTextAsync(Path.Combine(directory, "settings.json"), json);
        return await UserSettings.LoadAsync(directory, _ => null);
    }

    [Fact]
    public async Task UnsetResolvesTheDefaultAction()
    {
        var directory = NewAgentDirectory();
        try
        {
            var settings = await LoadAsync(directory, "{\"quietStartup\":true}");
            Assert.Null(settings.DoubleEscapeAction);
            Assert.Equal("tree", settings.EffectiveDoubleEscapeAction);
            Assert.Equal("tree", new UserSettings().EffectiveDoubleEscapeAction);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Theory]
    [InlineData("tree")]
    [InlineData("fork")]
    [InlineData("none")]
    public async Task EveryPinnedValueRoundTripsThroughLoad(string configured)
    {
        var directory = NewAgentDirectory();
        try
        {
            var settings = await LoadAsync(directory, JsonSerializer.Serialize(
                new Dictionary<string, string> { ["doubleEscapeAction"] = configured }));
            Assert.Equal(configured, settings.DoubleEscapeAction?.Raw);
            Assert.Equal(configured, settings.EffectiveDoubleEscapeAction);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Theory]
    // Pinned Pi returns the stored value unchanged; the handler forks for anything but tree/none.
    [InlineData("\"bogus\"", "bogus")]
    [InlineData("\"\"", "")]
    [InlineData("\"TREE\"", "TREE")]
    [InlineData("42", "42")]
    [InlineData("false", "false")]
    [InlineData("{}", "{}")]
    [InlineData("[]", "[]")]
    public async Task UnrecognizedValuesLoadAndStayInEffect(string storedValue, string effective)
    {
        var directory = NewAgentDirectory();
        try
        {
            var settings = await LoadAsync(directory, "{\"doubleEscapeAction\":" + storedValue + "}");
            Assert.NotNull(settings.DoubleEscapeAction);
            Assert.Equal(effective, settings.EffectiveDoubleEscapeAction);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Theory]
    [InlineData("{\"doubleEscapeAction\":null}")]
    [InlineData("{\"quietStartup\":true}")]
    public async Task NullishValuesResolveTheDefaultAction(string json)
    {
        var directory = NewAgentDirectory();
        try
        {
            var settings = await LoadAsync(directory, json);
            Assert.Equal("tree", settings.EffectiveDoubleEscapeAction);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public async Task TrustedProjectValueOverridesTheUserValue()
    {
        var directory = NewAgentDirectory();
        try
        {
            var projectDirectory = Path.Combine(directory, "project");
            Directory.CreateDirectory(Path.Combine(projectDirectory, ".pi"));
            await File.WriteAllTextAsync(Path.Combine(projectDirectory, ".pi", "settings.json"),
                "{\"doubleEscapeAction\":\"fork\"}");
            await File.WriteAllTextAsync(Path.Combine(directory, "settings.json"),
                "{\"doubleEscapeAction\":\"none\"}");

            var user = await UserSettings.LoadAsync(directory, _ => null);
            var project = await UserSettings.LoadProjectAsync(projectDirectory);
            Assert.Equal("none", user.EffectiveDoubleEscapeAction);
            Assert.Equal("fork", user.Overlay(project).EffectiveDoubleEscapeAction);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public async Task UntrustedProjectValueDoesNotAffectTheUserValue()
    {
        // ProjectRuntimeContext.CreateAsync loads project settings only after trust succeeds,
        // so an untrusted project contributes no entry at all.
        var directory = NewAgentDirectory();
        try
        {
            var projectDirectory = Path.Combine(directory, "project");
            Directory.CreateDirectory(Path.Combine(projectDirectory, ".pi"));
            await File.WriteAllTextAsync(Path.Combine(projectDirectory, ".pi", "settings.json"),
                "{\"doubleEscapeAction\":\"fork\"}");
            await File.WriteAllTextAsync(Path.Combine(directory, "settings.json"),
                "{\"doubleEscapeAction\":\"none\"}");

            var user = await UserSettings.LoadAsync(directory, _ => null);
            Assert.Equal("none", user.Overlay(new UserSettings()).EffectiveDoubleEscapeAction);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Theory]
    [InlineData("42", "42")]
    [InlineData("\"bogus\"", "bogus")]
    [InlineData("\"\"", "")]
    [InlineData("false", "false")]
    [InlineData("{}", "{}")]
    [InlineData("[]", "[]")]
    [InlineData("null", "tree")]
    public async Task MalformedTrustedProjectValueReplacesTheValidUserValue(string storedValue, string effective)
    {
        // Pinned Pi merges scopes by presence and never validates the merged value, so a project
        // entry outside the declared union still replaces the user value.
        var directory = NewAgentDirectory();
        try
        {
            var projectDirectory = Path.Combine(directory, "project");
            Directory.CreateDirectory(Path.Combine(projectDirectory, ".pi"));
            await File.WriteAllTextAsync(Path.Combine(projectDirectory, ".pi", "settings.json"),
                "{\"doubleEscapeAction\":" + storedValue + "}");
            await File.WriteAllTextAsync(Path.Combine(directory, "settings.json"),
                "{\"doubleEscapeAction\":\"none\"}");

            var user = await UserSettings.LoadAsync(directory, _ => null);
            var project = await UserSettings.LoadProjectAsync(projectDirectory);
            Assert.NotNull(project.DoubleEscapeAction);
            Assert.Equal(effective, user.Overlay(project).EffectiveDoubleEscapeAction);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public async Task AbsentProjectSettingsKeepTheUserValueEffective()
    {
        var directory = NewAgentDirectory();
        try
        {
            var projectDirectory = Path.Combine(directory, "project");
            Directory.CreateDirectory(Path.Combine(projectDirectory, ".pi"));
            await File.WriteAllTextAsync(Path.Combine(projectDirectory, ".pi", "settings.json"),
                "{\"doubleEscapeAction\":\"fork\"}");
            await File.WriteAllTextAsync(Path.Combine(directory, "settings.json"),
                "{\"doubleEscapeAction\":\"none\"}");

            var user = await UserSettings.LoadAsync(directory, _ => null);
            Assert.Equal("none", user.Overlay(new UserSettings()).EffectiveDoubleEscapeAction);

            // The same file wins once trust loads it.
            var trusted = await UserSettings.LoadProjectAsync(projectDirectory);
            Assert.Equal("fork", user.Overlay(trusted).EffectiveDoubleEscapeAction);
        }
        finally { Directory.Delete(directory, recursive: true); }
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

            await UserSettingsWriter.SetAsync(path, "doubleEscapeAction", "fork", userScope: true);
            var persisted = await UserSettings.LoadAsync(directory, _ => null);
            Assert.Equal("fork", persisted.DoubleEscapeAction?.Raw);
            Assert.Equal("fork", persisted.EffectiveDoubleEscapeAction);

            using (var document = JsonDocument.Parse(await File.ReadAllTextAsync(path)))
            {
                Assert.Equal("preserved", document.RootElement.GetProperty("shellCommandPrefix").GetString());
                Assert.True(document.RootElement.GetProperty("quietStartup").GetBoolean());
                Assert.Equal("fork", document.RootElement.GetProperty("doubleEscapeAction").GetString());
            }

            await UserSettingsWriter.SetAsync(path, "doubleEscapeAction", null, userScope: true);
            var reset = await UserSettings.LoadAsync(directory, _ => null);
            Assert.Null(reset.DoubleEscapeAction);
            Assert.Equal("tree", reset.EffectiveDoubleEscapeAction);
            using (var document = JsonDocument.Parse(await File.ReadAllTextAsync(path)))
            {
                Assert.False(document.RootElement.TryGetProperty("doubleEscapeAction", out _));
                Assert.Equal("preserved", document.RootElement.GetProperty("shellCommandPrefix").GetString());
                Assert.True(document.RootElement.GetProperty("quietStartup").GetBoolean());
            }
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Theory]
    [InlineData("bogus")]
    [InlineData("Tree")]
    [InlineData("none ")]
    [InlineData("")]
    public async Task WriterRejectsValuesOutsideThePinnedSet(string value)
    {
        var directory = NewAgentDirectory();
        try
        {
            var path = Path.Combine(directory, "settings.json");
            await Assert.ThrowsAsync<ArgumentException>(() =>
                UserSettingsWriter.SetAsync(path, "doubleEscapeAction", value, userScope: true));
            Assert.False(File.Exists(path));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }
}
