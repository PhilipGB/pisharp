using System.Net;
using System.Text;
using PiSharp.Cli;
using PiSharp.Runtime.Providers;

namespace PiSharp.Tests;

public sealed class ModelCatalogTests
{
    [Fact]
    public async Task CustomModelDiscoveryUsesOnlyExplicitEndpointKeyAndParsesCapabilities()
    {
        var handler = new FixtureHandler("""
            {"data":[{"id":"local-one","name":"Local One","owned_by":"llamacpp","status":{"value":"loaded"},"context_length":16384,"max_tokens":2048,"input":["text","image"],"api":"openai-completions"},{"id":"local-one"},{"id":"local-two","context_length":-1}]}
            """);
        using var http = new HttpClient(handler);
        var settings = ConnectionSettings.Resolve(true, name => name switch
        {
            "PISHARP_BASE_URL" => "http://localhost:8000/v1",
            "PISHARP_API_KEY" => "local-token",
            "OPENAI_API_KEY" => "unrelated-cloud-secret",
            _ => null
        });
        var models = await ModelCatalog.ListAsync(http, settings.Endpoint, settings.ApiKey);
        Assert.Equal("http://localhost:8000/v1/models", handler.Url);
        Assert.Equal("Bearer local-token", handler.Authorization);
        Assert.Equal(2, models.Count);
        Assert.Equal("loaded", models[0].Status);
        Assert.Equal(16384, models[0].ContextLength);
        Assert.Equal("Local One", models[0].Name);
        Assert.Equal(2048, models[0].MaxOutputTokens);
        Assert.Equal(["text", "image"], models[0].Input);
        Assert.Equal("openai-completions", models[0].Api);
        Assert.Null(models[1].ContextLength);
        Assert.DoesNotContain("unrelated-cloud-secret", handler.Authorization!);
    }

    [Fact]
    public async Task ModelMetadataParsesContextReasoningAndBothPricingConventions()
    {
        var handler = new FixtureHandler("""
            {"data":[
              {"id":"pi-style","context_length":200000,"reasoning":true,"cost":{"input":3,"output":15,"cacheRead":0.3}},
              {"id":"router-style","max_output_tokens":4096,"architecture":{"input_modalities":["text","image"]},"pricing":{"prompt":"0.000002","completion":"0.00001","input_cache_read":"0.000001"}},
              {"id":"unknown-price","cost":{"input":-1,"output":2}},
              {"id":"tiered","cost":{"input":5,"output":30,"cacheRead":0.5,"tiers":[{"inputTokensAbove":272000,"input":10,"output":45,"cacheRead":1}]}}
            ]}
            """);
        using var http = new HttpClient(handler);

        var models = await ModelCatalog.ListAsync(http, new Uri("https://models.test/v1"), "token");

        Assert.Equal(200000, models[0].ContextLength);
        Assert.True(models[0].Reasoning);
        Assert.Equal(new PiSharp.Runtime.Sessions.ModelPricing(3m, 15m, 0.3m), models[0].Pricing);
        Assert.Equal(new PiSharp.Runtime.Sessions.ModelPricing(2m, 10m, 1m), models[1].Pricing);
        Assert.Equal(4096, models[1].MaxOutputTokens);
        Assert.Equal(["text", "image"], models[1].Input);
        Assert.Null(models[2].Pricing);
        Assert.Equal([new PiSharp.Runtime.Sessions.ModelPricingTier(272000, 10m, 45m, 1m)], models[3].Pricing!.Tiers);
        Assert.Equal(new PiSharp.Runtime.Sessions.ModelPricing(10m, 45m, 1m), models[3].Pricing!.ForInput(272001));
    }

    [Fact]
    public async Task OversizedOrMalformedCatalogFailsWithoutLeakingCredentials()
    {
        using var oversized = new HttpClient(new FixtureHandler(new string('a', 1024 * 1024 + 1)));
        await Assert.ThrowsAsync<InvalidDataException>(() => ModelCatalog.ListAsync(oversized, new Uri("https://localhost/v1"), "key"));
        using var malformed = new HttpClient(new FixtureHandler("{}"));
        await Assert.ThrowsAsync<InvalidDataException>(() => ModelCatalog.ListAsync(malformed, null, "key"));
    }

    private sealed class FixtureHandler(string json) : HttpMessageHandler
    {
        public string? Url { get; private set; }
        public string? Authorization { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Url = request.RequestUri?.ToString();
            Authorization = request.Headers.Authorization?.ToString();
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new StringContent(json, Encoding.UTF8, "application/json") });
        }
    }
}
