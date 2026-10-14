using PiSharp.Cli;

namespace PiSharp.Tests;

public sealed class ConfigurationSchemaMetadataTests
{
    [Fact]
    public async Task SettingsSchemaReferenceLoadsAndSurvivesSettingPersistence()
    {
        var directory = Path.Combine(Path.GetTempPath(), "pisharp-schema-reference-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, "settings.json");
            await File.WriteAllTextAsync(path, "{\"$schema\":\"https://pi.dev/schemas/settings.schema.json\",\"theme\":\"dark\"}");
            Assert.Equal("dark", (await UserSettings.LoadAsync(directory, _ => null)).Theme);
            await UserSettingsWriter.SetAsync(path, "theme", "light", true);
            Assert.Contains("https://pi.dev/schemas/settings.schema.json", await File.ReadAllTextAsync(path));
            Assert.Equal("light", (await UserSettings.LoadAsync(directory, _ => null)).Theme);
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task ModelsRejectAnInvalidSchemaReferenceBeforeLoadingProviders()
    {
        var path = Path.Combine(Path.GetTempPath(), "pisharp-schema-reference-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            await File.WriteAllTextAsync(path, "{\"$schema\":42,\"providers\":{}}");
            var error = await Assert.ThrowsAsync<InvalidDataException>(() =>
                ProviderProfileLoader.LoadConfiguredProvidersAsync(path, [], CancellationToken.None));
            Assert.Contains("$schema", error.Message);
        }
        finally { File.Delete(path); }
    }
}
