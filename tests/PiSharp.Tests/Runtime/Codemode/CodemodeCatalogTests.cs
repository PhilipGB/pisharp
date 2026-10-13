using PiSharp.Cli;

namespace PiSharp.Tests;

public sealed class CodemodeCatalogTests
{
    [Fact]
    public async Task GuestChatCatalogUsesPiMetadataNamesAndKeepsClassifierIdentitySeparate()
    {
        var root = Path.Combine(Path.GetTempPath(), "pisharp-codemode-catalog-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            await File.WriteAllTextAsync(Path.Combine(root, "models.json"), """
                {"providers":{"fixture":{"baseUrl":"http://127.0.0.1:9999/v1","api":"openai-completions","apiKey":"host-key","models":[
                {"id":"one","name":"chat name","contextWindow":4096,"maxTokens":512,"reasoning":true,"cost":{"input":1,"output":2,"cacheRead":0.5}},
                {"id":"one","type":"classifier","name":"classifier name","api":"typesafe-system-one","cost":{"input":3,"output":4}},
                {"id":"one","type":"image","name":"image name","api":"openrouter-images","input":["text","image"],"output":["image"],"cost":{"input":5,"output":6}}]}}}
                """);
            using var http = new HttpClient();
            var providers = await ProviderModelRuntime.CreateAsync(root, false, _ => null, http, offline: true);
            var runtime = new ProviderCodemodeModels(providers, http);
            var chat = Assert.Single(runtime.GetModels("chat", "fixture"));
            Assert.True(chat.TryGetProperty("cost", out var cost));
            Assert.Equal(1, cost.GetProperty("input").GetDecimal());
            Assert.Equal(0.5m, cost.GetProperty("cacheRead").GetDecimal());
            Assert.Equal(512, chat.GetProperty("maxTokens").GetInt32());
            Assert.Equal(4096, chat.GetProperty("contextWindow").GetInt32());
            Assert.Equal("chat", chat.GetProperty("type").GetString());
            var classifier = Assert.Single(runtime.GetModels("classifier", "fixture"));
            Assert.Equal("classifier", classifier.GetProperty("type").GetString());
            Assert.Equal("classifier name", classifier.GetProperty("name").GetString());
            Assert.Equal("text", classifier.GetProperty("input")[0].GetString());
            Assert.Single(providers.GetProvider("fixture").Models);
            var image = Assert.Single(runtime.GetModels("image", "fixture"));
            Assert.Equal("image name", image.GetProperty("name").GetString());
            Assert.Equal("image", image.GetProperty("output")[0].GetString());
            Assert.Equal(5, image.GetProperty("cost").GetProperty("input").GetDecimal());
            Assert.Single(await runtime.GetAvailableAsync("image", "fixture", default));
            Assert.Equal(3, classifier.GetProperty("cost").GetProperty("input").GetDecimal());
            Assert.DoesNotContain("host-key", chat.GetRawText() + classifier.GetRawText() + image.GetRawText());
        }
        finally { Directory.Delete(root, recursive: true); }
    }
    [Fact]
    public async Task BuiltinImagesRetainTypedMetadataAndRequireTheirOwnCredentials()
    {
        var root = Path.Combine(Path.GetTempPath(), "pisharp-image-catalog-" + Guid.NewGuid().ToString("N"));
        using var http = new HttpClient();
        var providers = await ProviderModelRuntime.CreateAsync(root, false, _ => null, http, offline: true);
        var runtime = new ProviderCodemodeModels(providers, http);
        var models = runtime.GetModels("image");
        Assert.Equal(57, models.Count);
        Assert.All(models, model =>
        {
            Assert.Equal("image", model.GetProperty("type").GetString());
            Assert.Equal("openrouter", model.GetProperty("provider").GetString());
            Assert.Contains(model.GetProperty("output").EnumerateArray(), value => value.GetString() == "image");
        });
        var flux = Assert.Single(models, model => model.GetProperty("id").GetString() == "black-forest-labs/flux.2-flex");
        Assert.Equal(2000, flux.GetProperty("inputLimits").GetProperty("images").GetProperty("resize").GetProperty("maxWidth").GetInt32());
        Assert.Empty(await runtime.GetAvailableAsync("image", null, default));
        var authenticated = await ProviderModelRuntime.CreateAsync(root, false,
            key => key == "OPENROUTER_API_KEY" ? "owned-image-key" : null, http, offline: true);
        var available = await new ProviderCodemodeModels(authenticated, http).GetAvailableAsync("image", "openrouter", default);
        Assert.Equal(models.Count, available.Count);
        Assert.DoesNotContain("owned-image-key", string.Join("", available.Select(model => model.GetRawText())));
    }

    [Theory]
    [InlineData("{\"type\":\"other\"}")]
    [InlineData("{\"type\":\"image\",\"api\":\"openrouter-images\",\"output\":[\"text\"]}")]
    [InlineData("{\"type\":\"image\",\"api\":\"openrouter-images\",\"input\":[\"audio\"]}")]
    [InlineData("{\"type\":\"image\",\"api\":\"openrouter-images\",\"baseUrl\":\"http://unowned.invalid/v1\"}")]
    public async Task InvalidTypedModelsAreRejected(string metadata)
    {
        var root = Path.Combine(Path.GetTempPath(), "pisharp-invalid-image-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var model = System.Text.Json.Nodes.JsonNode.Parse(metadata)!;
            model["id"] = "one";
            var json = "{\"providers\":{\"fixture\":{\"baseUrl\":\"http://127.0.0.1:9999/v1\",\"models\":[" + model.ToJsonString() + "]}}}";
            await File.WriteAllTextAsync(Path.Combine(root, "models.json"), json);
            using var http = new HttpClient();
            await Assert.ThrowsAsync<InvalidDataException>(() => ProviderModelRuntime.CreateAsync(root, false, _ => null, http, offline: true));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

}
