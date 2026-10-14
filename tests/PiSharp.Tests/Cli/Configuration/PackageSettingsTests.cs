using PiSharp.Cli;

namespace PiSharp.Tests;

public sealed class PackageSettingsTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("[]")]
    [InlineData("[\"../package\"]")]
    [InlineData("[{\"source\":\"../package\",\"skills\":[],\"prompts\":[\"prompts/review.md\"]}]")]
    public async Task ValidPackageDeclarationsAllowOtherStartupSettingsToLoad(string? packages)
    {
        var root = Path.Combine(Path.GetTempPath(), "pisharp-package-settings-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var property = packages is null ? "" : ",\"packages\":" + packages;
            await File.WriteAllTextAsync(Path.Combine(root, "settings.json"),
                "{\"defaultProvider\":\"fixture\",\"defaultModel\":\"fixture-model\"" + property + "}");

            var settings = await UserSettings.LoadAsync(root, _ => null);

            Assert.Equal("fixture", settings.DefaultProvider);
            Assert.Equal("fixture-model", settings.DefaultModel);
        }
        finally { Directory.Delete(root, true); }
    }
}
