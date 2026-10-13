using PiSharp.Cli;

namespace PiSharp.Tests;

public sealed class ConnectionSettingsTests
{
    private static Func<string, string?> Environment(params (string Key, string Value)[] values) =>
        key => values.FirstOrDefault(v => v.Key == key).Value;

    [Fact]
    public void LocalProfileUsesRequestedEndpointAndModelWithoutOpenAiSecret()
    {
        var settings = ConnectionSettings.Resolve(true, Environment(("OPENAI_API_KEY", "sensitive-openai-key")));
        Assert.Equal("http://192.168.0.97:8000/v1", settings.Endpoint?.ToString().TrimEnd('/'));
        Assert.Equal("Qwen3.8-27B-GGUF", settings.Model);
        Assert.Equal("not-needed", settings.ApiKey);
    }

    [Fact]
    public void OverridesUseEndpointSpecificKeyAndModel()
    {
        var settings = ConnectionSettings.Resolve(true, Environment(
            ("PISHARP_BASE_URL", "http://localhost:8080/v1"), ("PISHARP_MODEL", "other"),
            ("PISHARP_API_KEY", "local-key"), ("OPENAI_API_KEY", "cloud-key")));
        Assert.Equal("other", settings.Model);
        Assert.Equal("http://localhost:8080/v1", settings.Endpoint?.ToString().TrimEnd('/'));
        Assert.Equal("local-key", settings.ApiKey);
    }

    [Fact]
    public void CloudRequiresKeyButCustomEndpointDoesNot()
    {
        Assert.Throws<ArgumentException>(() => ConnectionSettings.Resolve(false, Environment()));
        var cloud = ConnectionSettings.Resolve(false, Environment(("OPENAI_API_KEY", "cloud-key")));
        Assert.Null(cloud.Endpoint);
        Assert.Equal("cloud-key", cloud.ApiKey);
        Assert.Equal("gpt-4o-mini", cloud.Model);
        Assert.Equal("not-needed", ConnectionSettings.Resolve(false,
            Environment(("PISHARP_BASE_URL", "http://localhost:8000/v1"))).ApiKey);
    }

    [Fact]
    public void RejectsInvalidEndpoint()
    {
        Assert.Throws<ArgumentException>(() => ConnectionSettings.Resolve(true,
            Environment(("PISHARP_BASE_URL", "ftp://localhost/v1"))));
    }
}
