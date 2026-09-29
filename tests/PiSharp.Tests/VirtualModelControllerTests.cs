using System.Text.Json;
using Microsoft.Extensions.AI;
using PiSharp.Cli;
using PiSharp.Runtime.Providers;
using PiSharp.Runtime.Sessions;
using PiSharp.Runtime.VirtualModels;

namespace PiSharp.Tests;

public sealed class VirtualModelControllerTests
{
    [Theory]
    [InlineData("direct", false)]
    [InlineData("user", true)]
    [InlineData("continuation", true)]
    [InlineData("retry", true)]
    public async Task ProjectsLogicalModelStatePreviousSuccessAndPhysicalPolicy(string reason, bool hasState)
    {
        using var fixture = await Fixture.CreateAsync();
        VirtualModelRouteRequest? seen = null;
        fixture.Registry.Register(new("router", "auto", "Auto", (request, _) =>
        {
            seen = request;
            return Task.FromResult(new VirtualModelRoute("physical", "small", "high"));
        }, ThinkingLevels: ["low", "high"]), "test");
        var router = await fixture.RouterAsync();
        var session = new ConversationSession(fixture.Root, "auto", null, "router");
        session.AppendVirtualModelState("router", "auto", JsonSerializer.SerializeToElement(new { phase = 1 }));
        var previous = new ChatMessage(ChatRole.Assistant, "success")
        {
            AdditionalProperties = new()
            {
                ["pisharp.provider"] = JsonSerializer.SerializeToElement("physical"),
                ["pisharp.model"] = JsonSerializer.SerializeToElement("large"),
                ["pisharp.thinkingLevel"] = JsonSerializer.SerializeToElement("medium")
            }
        };
        var failed = new ChatMessage(ChatRole.Assistant, "failure")
        {
            AdditionalProperties = new()
            {
                ["pisharp.provider"] = "physical",
                ["pisharp.model"] = "small",
                ["pisharp.stopReason"] = "error"
            }
        };
        var route = await router(new(session, [previous, failed, new(ChatRole.User, "next")], reason, "low", null, default));
        Assert.Equal("auto", seen!.Model.Id);
        Assert.Equal("pi-virtual", seen.Model.Api);
        Assert.Equal("low", seen.ThinkingLevel);
        Assert.Equal("large", seen.PreviousModel!.Id);
        Assert.Equal("medium", seen.PreviousThinkingLevel);
        Assert.Equal(hasState, seen.State.HasValue);
        Assert.Equal("small", route.Model.Id);
        Assert.Equal("off", route.ThinkingLevel);
        Assert.Null(route.Reasoning);
        Assert.Equal(32000, route.Model.ContextLength);
        Assert.NotNull(route.ContextPolicy);
        Assert.Equal(2m, route.Pricing!.Input);
    }

    [Theory]
    [InlineData("physical", "missing")]
    [InlineData("router", "auto")]
    [InlineData("unavailable", "small")]
    [InlineData("", "small")]
    public async Task RejectsInvalidVirtualOrUnauthenticatedPhysicalTargets(string provider, string model)
    {
        using var fixture = await Fixture.CreateAsync();
        fixture.Registry.Register(new("router", "auto", "Auto", (_, _) =>
            Task.FromResult(new VirtualModelRoute(provider, model, "off"))), "test");
        var router = await fixture.RouterAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => router(new(
            new ConversationSession(fixture.Root, "auto", null, "router"), [], "user", "off", null, default)));
    }

    [Fact]
    public void VirtualCatalogOffersOnlyDeclaredThinkingLevels()
    {
        var registry = new VirtualModelRegistry();
        registry.Register(new("router", "auto", "Auto", (_, _) =>
            Task.FromResult(new VirtualModelRoute("physical", "large", "high")), ThinkingLevels: ["low", "high"]), "test");
        var model = registry.Get("router", "auto")!.Model;
        Assert.Equal(new[] { "low", "high" }, ThinkingLevels.AvailableForModel(model.Reasoning, model.ThinkingLevelMap));
    }

    private sealed class Fixture(string root, HttpClient http, ProviderModelRuntime providers, VirtualModelRegistry registry) : IDisposable
    {
        public string Root => root;
        public VirtualModelRegistry Registry => registry;
        public static async Task<Fixture> CreateAsync()
        {
            var root = Path.Combine(Path.GetTempPath(), "pisharp-virtual-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            await File.WriteAllTextAsync(Path.Combine(root, "models.json"), """
                {"providers":{
                  "physical":{"baseUrl":"http://localhost:12345/v1","apiKey":"fixture-key","api":"openai-completions","models":[
                    {"id":"small","contextWindow":32000,"maxTokens":1000,"reasoning":false,"input":["text"],"cost":{"input":2,"output":10}},
                    {"id":"large","contextWindow":64000,"reasoning":true}]},
                  "unavailable":{"baseUrl":"http://localhost:12346/v1","api":"openai-completions","models":[{"id":"small"}]}
                }}
                """);
            var http = new HttpClient();
            var providers = await ProviderModelRuntime.CreateAsync(root, false, _ => null, http, offline: true);
            var registry = new VirtualModelRegistry();
            providers.SetVirtualModelRegistry(registry);
            return new(root, http, providers, registry);
        }
        public async Task<VirtualModelRequestRouter> RouterAsync()
        {
            var controller = new ModelRuntimeController(providers, () => new UserSettings(), _ => null);
            return controller.CreateVirtualModelRouter(await providers.ResolveAsync("router", "auto"))!;
        }
        public void Dispose() { http.Dispose(); Directory.Delete(root, recursive: true); }
    }
}
