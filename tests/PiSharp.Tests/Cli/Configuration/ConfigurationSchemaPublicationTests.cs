using System.Text.Json;
using PiSharp.Cli;

namespace PiSharp.Tests;

public sealed class ConfigurationSchemaPublicationTests
{
    [Theory]
    [InlineData("models")]
    [InlineData("settings")]
    [InlineData("keybindings")]
    [InlineData("theme")]
    public void PublishedAndEmbeddedSchemasAreIdenticalCurrentDraftArtifacts(string name)
    {
        using var resource = ConfigSchemaCatalog.Open(name);
        using var bytes = new MemoryStream();
        resource.CopyTo(bytes);
        var output = Path.Combine(Path.GetDirectoryName(typeof(ConfigSchemaCatalog).Assembly.Location)!, "schemas", name + ".schema.json");
        Assert.Equal(bytes.ToArray(), File.ReadAllBytes(output));
        using var json = JsonDocument.Parse(bytes.ToArray());
        Assert.Equal("https://json-schema.org/draft/2020-12/schema", json.RootElement.GetProperty("$schema").GetString());
        Assert.Equal($"https://pi.dev/schemas/{name}.schema.json", json.RootElement.GetProperty("$id").GetString());
        Assert.Equal(JsonValueKind.Object, json.RootElement.GetProperty("properties").ValueKind);
        Assert.True(File.Exists(Path.Combine(Path.GetDirectoryName(output)!, "UPSTREAM-LICENSE.txt")));
    }

    [Fact]
    public void SettingsSchemaPublishesTheCurrentShareDestinationChoices()
    {
        using var resource = ConfigSchemaCatalog.Open("settings");
        using var json = JsonDocument.Parse(resource);
        var options = json.RootElement.GetProperty("properties").GetProperty("sessionShareDestination").GetProperty("anyOf");
        Assert.Equal(["auto", "gist", "radius"], options.EnumerateArray().Select(option => option.GetProperty("const").GetString()));
        Assert.Equal(["models", "settings", "keybindings", "theme"], ConfigSchemaCatalog.Names);
        Assert.Throws<ArgumentException>(() => ConfigSchemaCatalog.Open("../settings"));
    }
}
