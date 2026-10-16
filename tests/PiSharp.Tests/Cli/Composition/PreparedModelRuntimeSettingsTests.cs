using PiSharp.Cli;
using PiSharp.Runtime.Providers;

namespace PiSharp.Tests;

public sealed class PreparedModelRuntimeSettingsTests
{
    [Fact]
    public void AutoResizeImageSettingControlsPreparedModelResizeOptions()
    {
        var resize = new ModelImageResizeOptions(MaxWidth: 1200, MaxHeight: 900, MaxBytes: 4_000_000);
        var model = new ModelDescriptor("fixture-model", "Fixture", 32_000, "configured",
            Provider: "fixture", Api: "openai-completions", Input: ["text", "image"],
            InputLimits: new ModelInputLimits(new ModelImageInputLimits(resize)));
        var provider = new ProviderProfile("fixture", "Fixture", new Uri("https://fixture.example/v1"),
            AuthRequired: false, OAuthSupported: false, ApiKeyEnvironment: null, ConfiguredApiKey: null,
            Models: [model], Api: "openai-completions");
        var selection = new ModelSelection(provider, model, "fixture-key", Authenticated: true, "fixture");
        using var client = ProviderChatClientFactory.Create(selection);

        var enabled = new PreparedModelRuntime(selection, "medium", selection.Connection, 20_000, client,
            null, null, null, AutoResizeImages: true);
        var disabled = enabled with { AutoResizeImages = false };

        Assert.Equal(resize, enabled.ImageResizeOptions);
        Assert.Null(disabled.ImageResizeOptions);
    }
}
