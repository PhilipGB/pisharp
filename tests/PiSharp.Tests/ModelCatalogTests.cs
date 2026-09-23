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
            {"data":[{"id":"local-one","owned_by":"llamacpp","status":{"value":"loaded"},"context_length":16384},{"id":"local-one"},{"id":"local-two","context_length":-1}]}
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
        Assert.Null(models[1].ContextLength);
        Assert.DoesNotContain("unrelated-cloud-secret", handler.Authorization!);
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
