using System.Text.Json;
using PiSharp.Cli;
using PiSharp.Cli.Tui;

namespace PiSharp.Tests;

public sealed class SessionShareSettingsTests
{
    [Theory]
    [InlineData("\"auto\"", "auto")]
    [InlineData("\"gist\"", "gist")]
    [InlineData("\"radius\"", "radius")]
    [InlineData("\"unknown\"", "auto")]
    [InlineData("false", "auto")]
    [InlineData("null", "auto")]
    [InlineData("123", "auto")]
    [InlineData("[]", "auto")]
    [InlineData("{}", "auto")]
    public async Task DestinationUsesPiFallbackForInvalidValuesIncludingProjectOverrides(string raw, string expected)
    {
        var directory = Path.Combine(Path.GetTempPath(), "pisharp-share-settings-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(directory, ".pi"));
        try
        {
            Assert.Equal("auto", new UserSettings().ShareDestination);
            await File.WriteAllTextAsync(Path.Combine(directory, ".pi", "settings.json"), "{\"sessionShareDestination\":" + raw + "}");
            var project = await UserSettings.LoadProjectAsync(directory);
            Assert.Equal(expected, new UserSettings(SessionShareDestination: "gist").Overlay(project).ShareDestination);
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task DestinationPersistsAcrossRestartAndProjectClearRestoresTheUserChoice()
    {
        var directory = Path.Combine(Path.GetTempPath(), "pisharp-share-settings-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var user = await UserSettingsPersistence.SaveAsync(directory, directory, false, true,
                "sessionShareDestination", "gist", new(), null, _ => null);
            var project = await UserSettingsPersistence.SaveAsync(directory, directory, true, true,
                "sessionShareDestination", "radius", user.User, null, _ => null);
            Assert.Equal("radius", project.Effective.ShareDestination);
            var restarted = (await UserSettings.LoadAsync(directory, _ => null)).Overlay(await UserSettings.LoadProjectAsync(directory));
            Assert.Equal("radius", restarted.ShareDestination);
            var cleared = await UserSettingsPersistence.SaveAsync(directory, directory, true, true,
                "sessionShareDestination", null, project.User, project.Project, _ => null);
            Assert.Equal("gist", cleared.Effective.ShareDestination);
            var previous = await File.ReadAllTextAsync(Path.Combine(directory, ".pi", "settings.json"));
            await Assert.ThrowsAsync<InvalidOperationException>(() => UserSettingsPersistence.SaveAsync(directory, directory,
                true, false, "sessionShareDestination", "auto", cleared.User, cleared.Project, _ => null));
            Assert.Equal(previous, await File.ReadAllTextAsync(Path.Combine(directory, ".pi", "settings.json")));
            await Assert.ThrowsAsync<ArgumentException>(() => UserSettingsWriter.SetAsync(Path.Combine(directory, "settings.json"),
                "sessionShareDestination", "invalid", true));
            Assert.Equal("gist", (await UserSettings.LoadAsync(directory, _ => null)).ShareDestination);
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task PiShareDestinationConfigurationLoads()
    {
        var directory = Path.Combine(Path.GetTempPath(), "pisharp-share-settings-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            await File.WriteAllTextAsync(Path.Combine(directory, "settings.json"), "{\"sessionShareDestination\":\"gist\"}");
            Assert.Equal("gist", (await UserSettings.LoadAsync(directory, _ => null)).ShareDestination);
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task DestinationCanBePersistedWithoutReplacingOtherSettings()
    {
        var directory = Path.Combine(Path.GetTempPath(), "pisharp-share-settings-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, "settings.json");
            await File.WriteAllTextAsync(path, "{\"theme\":\"dark\"}");
            await UserSettingsWriter.SetAsync(path, "sessionShareDestination", "radius", userScope: true);
            using var result = JsonDocument.Parse(await File.ReadAllTextAsync(path));
            Assert.Equal("radius", result.RootElement.GetProperty("sessionShareDestination").GetString());
            Assert.Equal("dark", result.RootElement.GetProperty("theme").GetString());
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public void ShareAppearsInSlashCompletion()
    {
        var buffer = new EditorBuffer();
        buffer.SetText("/sha");
        Assert.Equal(["/share"], new EditorCompletion(Path.GetTempPath()).Complete(buffer));
    }
}
