using System.Text.Json;
using PiSharp.Cli;
using PiSharp.Runtime.Providers;
using PiSharp.Runtime.Sessions;

namespace PiSharp.Tests;

public sealed class MistralModelCatalogTests
{
    [Fact]
    public async Task ListsCurrentPiMistralModelsWithReasoningAndImageMetadataWithoutDiscoveryRequest()
    {
        var root = Path.Combine(Path.GetTempPath(), "pisharp-mistral-catalog-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var environment = new Dictionary<string, string>
            {
                ["MISTRAL_API_KEY"] = "mistral-key",
                ["PISHARP_MISTRAL_MODEL"] = "mistral-medium-latest"
            };
            using var http = new HttpClient(new UnexpectedModelDiscoveryHandler());
            var runtime = await ProviderModelRuntime.CreateAsync(root, false,
                name => environment.GetValueOrDefault(name), http);

            var models = await runtime.ListModelsAsync("mistral");
            Assert.Equal(33, models.Count);
            Assert.Equal("mistral-medium-latest", models[0].Id);

            var small = models.Single(model => model.Id == "mistral-small-latest");
            Assert.Equal("Mistral Small (latest)", small.Name);
            Assert.Equal("mistral-conversations", small.Api);
            Assert.True(small.Reasoning);
            Assert.Equal(256000, small.ContextLength);
            Assert.Equal(256000, small.MaxOutputTokens);
            Assert.Contains("image", small.Input!);
            Assert.Equal(new ModelPricing(0.15m, 0.6m, 0.015m, CachedWrite: 0m), small.Pricing);
            Assert.Equal("none", small.ThinkingLevelMap?.GetProperty("off").GetString());
            Assert.Equal("high", small.ThinkingLevelMap?.GetProperty("high").GetString());
            Assert.Equal(JsonValueKind.Null, small.ThinkingLevelMap?.GetProperty("low").ValueKind);

            var large = models.Single(model => model.Id == "mistral-large-latest");
            Assert.Equal(new ModelImageResizeOptions(2000, 2000, 4718592, 80), large.InputLimits?.Images?.Resize);

            var magistral = models.Single(model => model.Id == "magistral-medium-latest");
            Assert.True(magistral.Reasoning);
            Assert.Null(magistral.ThinkingLevelMap);

            var glm = models.Single(model => model.Id == "zai-glm-5-2");
            Assert.Equal("max", glm.ThinkingLevelMap?.GetProperty("max").GetString());
            Assert.Equal("mistral-medium-latest", (await runtime.ResolveAsync("mistral", null)).Model.Id);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task UnknownConfiguredDefaultStaysSelectableAlongsidePinnedModels()
    {
        var root = Path.Combine(Path.GetTempPath(), "pisharp-mistral-configured-catalog-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var environment = new Dictionary<string, string>
            {
                ["MISTRAL_API_KEY"] = "mistral-key",
                ["PISHARP_MISTRAL_MODEL"] = "mistral-preview"
            };
            using var http = new HttpClient(new UnexpectedModelDiscoveryHandler());
            var runtime = await ProviderModelRuntime.CreateAsync(root, false,
                name => environment.GetValueOrDefault(name), http);

            var models = await runtime.ListModelsAsync("mistral");
            Assert.Equal(34, models.Count);
            Assert.Equal("mistral-preview", models[0].Id);
            Assert.Equal("mistral-conversations", models[0].Api);
            Assert.Contains(models, model => model.Id == "mistral-small-latest");
            Assert.Equal("mistral-preview", (await runtime.ResolveAsync("mistral", null)).Model.Id);
        }
        finally { Directory.Delete(root, true); }
    }

    private sealed class UnexpectedModelDiscoveryHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException($"Static Mistral catalogue unexpectedly requested '{request.RequestUri}'.");
    }
}
