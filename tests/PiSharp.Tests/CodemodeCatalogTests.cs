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
                {"id":"one","type":"classifier","api":"typesafe-system-one","cost":{"input":3,"output":4}}]}}}
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
            Assert.Equal(3, classifier.GetProperty("cost").GetProperty("input").GetDecimal());
            Assert.DoesNotContain("host-key", chat.GetRawText() + classifier.GetRawText());
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}
