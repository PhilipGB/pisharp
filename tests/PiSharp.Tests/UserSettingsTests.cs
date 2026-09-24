using PiSharp.Cli;

namespace PiSharp.Tests;

public sealed class UserSettingsTests
{
    [Fact]
    public async Task UserSettingsProvideValidatedDefaultsBelowCliAndEnvironmentOverrides()
    {
        var root = Path.Combine(Path.GetTempPath(), "pisharp-settings-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            await File.WriteAllTextAsync(Path.Combine(root, "settings.json"),
                "{\"defaultProvider\":\"mistral\",\"defaultModel\":\"mistral-large-latest\",\"defaultThinkingLevel\":\"HIGH\",\"defaultTools\":[\"read\",\"grep\"]}");
            var settings = await UserSettings.LoadAsync(root, _ => null);
            var defaults = settings.ApplyDefaults(CliArguments.Parse(["--print", "hello"]), _ => null);
            Assert.Equal("mistral", defaults.Provider);
            Assert.Equal("mistral-large-latest", defaults.ModelOverride);
            Assert.Equal("high", defaults.Thinking);
            Assert.Equal(["read", "grep"], defaults.Tools);

            var explicitOptions = settings.ApplyDefaults(CliArguments.Parse(["--provider", "openai", "--model", "gpt-custom", "--thinking", "low", "--tools", "write"]), _ => null);
            Assert.Equal("openai", explicitOptions.Provider);
            Assert.Equal("gpt-custom", explicitOptions.ModelOverride);
            Assert.Equal("low", explicitOptions.Thinking);
            Assert.Equal(["write"], explicitOptions.Tools);
            var disabledTools = settings.ApplyDefaults(CliArguments.Parse(["--no-tools"]), _ => null);
            Assert.Null(disabledTools.Tools);

            var continuation = settings.ApplyDefaults(CliArguments.Parse(["--continue"]), _ => null, preserveSessionModel: true);
            Assert.Null(continuation.Provider);
            Assert.Null(continuation.ModelOverride);
            Assert.Equal("high", continuation.Thinking);
            var listing = settings.ApplyDefaults(CliArguments.Parse(["--list-models"]), _ => null, preserveSessionModel: true);
            Assert.Null(listing.Provider);
            Assert.Null(listing.ModelOverride);

            var environmentOverride = settings.ApplyDefaults(CliArguments.Parse(["--provider", "mistral"]),
                name => name == "PISHARP_MISTRAL_MODEL" ? "mistral-small-latest" : null);
            Assert.Null(environmentOverride.ModelOverride);
        }
        finally { Directory.Delete(root, true); }
    }

    [Theory]
    [InlineData("{\"defaultThinkingLevel\":\"ultra\"}")]
    [InlineData("{\"unknown\":\"value\"}")]
    [InlineData("{\"defaultModel\":42}")]
    [InlineData("{\"defaultTools\":\"read\"}")]
    [InlineData("{\"defaultTools\":[\"unknown\"]}")]
    [InlineData("{\"defaultTools\":[\"read\",\"read\"]}")]
    [InlineData("{\"defaultModel\":\"a\",\"defaultModel\":\"b\"}")]
    public async Task InvalidSettingsFailClosed(string json)
    {
        var root = Path.Combine(Path.GetTempPath(), "pisharp-settings-invalid-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            await File.WriteAllTextAsync(Path.Combine(root, "settings.json"), json);
            await Assert.ThrowsAnyAsync<Exception>(() => UserSettings.LoadAsync(root, _ => null));
        }
        finally { Directory.Delete(root, true); }
    }
}
