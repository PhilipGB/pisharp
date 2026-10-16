using PiSharp.Cli;

namespace PiSharp.Tests;

public sealed class PackageSettingsReaderTests
{
    [Fact]
    public async Task NpmCommandRemainsAnArgvArrayWithoutShellOrCharacterRestrictions()
    {
        var root = Path.Combine(Path.GetTempPath(), "pisharp-npm-command-settings-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var path = Path.Combine(root, "settings.json");
            var command = new[] { "wrapper", "exec", "runtime@22", "--", "npm", "line\nfeed", "" };
            await File.WriteAllTextAsync(path, System.Text.Json.JsonSerializer.Serialize(new { npmCommand = command }));

            var settings = await UserSettings.LoadAsync(root, _ => null);

            Assert.Equal(command, settings.NpmCommand);
            await File.WriteAllTextAsync(path, "{\"npmCommand\":[\"npm\",false]}");
            await Assert.ThrowsAsync<InvalidDataException>(() => UserSettings.LoadAsync(root, _ => null));
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}
