using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;
using PiSharp.Cli;
using PiSharp.Extensions.Abstractions;
using PiSharp.Runtime.Extensions;
using PiSharp.Runtime.Providers;
using PiSharp.Runtime.Sessions;
using PiSharp.Runtime.VirtualModels;

namespace PiSharp.Tests;

public sealed class NativeExtensionProviderRuntimeTests
{
    [Fact]
    public async Task ExtensionModelsJoinNormalCatalogSelectionAndStreamingClientPath()
    {
        using var http = new HttpClient();
        var runtime = await ProviderModelRuntime.CreateAsync(Path.GetTempPath(), false, _ => null, http,
            offline: true);
        var clientContext = default(PiSharpExtensionProviderClientContext);
        var definition = Provider("fixture", [new("small", "Fixture small", 4096, 1024,
            ["text", "image"], Reasoning: true)], context =>
        {
            clientContext = context;
            return new RecordingClient("extension response");
        }, path: "/extensions/provider-fixture.dll");

        runtime.SetExtensionProviders([definition]);
        var catalog = await runtime.ListModelsAsync("fixture");
        var selection = await runtime.ResolveAsync("fixture", "small");
        var client = ProviderChatClientFactory.Create(selection);
        var updates = new List<ChatResponseUpdate>();
        await foreach (var update in client.GetStreamingResponseAsync(
                           [new ChatMessage(ChatRole.User, "hello")])) updates.Add(update);

        Assert.Equal("fixture", Assert.Single(runtime.Providers, provider => provider.Id == "fixture").Id);
        var model = Assert.Single(catalog);
        Assert.Equal("small", model.Id);
        Assert.Equal("Fixture small", model.Name);
        Assert.Equal(["text", "image"], model.Input);
        Assert.Equal(4096, model.ContextLength);
        Assert.Equal(1024, model.MaxOutputTokens);
        Assert.True(model.Reasoning);
        Assert.Equal("fixture", selection.Provider.Id);
        Assert.Equal("pisharp-extension", ProviderChatClientFactory.ResolveProtocol(selection));
        Assert.Equal("fixture", clientContext!.ProviderId);
        Assert.Equal("small", clientContext.ModelId);
        Assert.Null(clientContext.Endpoint);
        Assert.Equal("extension response", Assert.Single(updates).Text);
    }

    [Fact]
    public async Task ExtensionProviderConflictsAndRemovalAreDeterministic()
    {
        using var http = new HttpClient();
        var runtime = await ProviderModelRuntime.CreateAsync(Path.GetTempPath(), false, _ => null, http,
            offline: true);
        var builtInConflict = Assert.Throws<InvalidOperationException>(() => runtime.SetExtensionProviders(
            [Provider("openai", [new("fixture")], _ => new RecordingClient("unused"), "/extensions/openai.dll")]));
        Assert.Contains("/extensions/openai.dll", builtInConflict.Message, StringComparison.Ordinal);
        Assert.Contains("openai", builtInConflict.Message, StringComparison.Ordinal);

        var first = Provider("fixture", [new("first")], _ => new RecordingClient("first"), "/extensions/first.dll");
        var duplicate = Provider("fixture", [new("second")], _ => new RecordingClient("second"), "/extensions/second.dll");
        var duplicateError = Assert.Throws<InvalidOperationException>(() => runtime.SetExtensionProviders([first, duplicate]));
        Assert.Contains("fixture", duplicateError.Message, StringComparison.Ordinal);
        Assert.Contains("/extensions/second.dll", duplicateError.Message, StringComparison.Ordinal);

        runtime.SetExtensionProviders([first]);
        Assert.Equal("fixture", (await runtime.ResolveAsync("fixture", "first")).Provider.Id);
        runtime.SetExtensionProviders([]);
        var removed = await Assert.ThrowsAsync<ArgumentException>(() => runtime.ResolveAsync("fixture", "first"));
        Assert.Contains("Unknown provider", removed.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task VirtualModelsCannotClaimAnExtensionPhysicalModel()
    {
        using var http = new HttpClient();
        var runtime = await ProviderModelRuntime.CreateAsync(Path.GetTempPath(), false, _ => null, http,
            offline: true);
        runtime.SetExtensionProviders([Provider("router", [new("physical")],
            _ => new RecordingClient("unused"), "/extensions/owner.dll")]);
        var virtualModels = new VirtualModelRegistry();
        virtualModels.Register(new("router", "physical", "Conflict", (_, _) =>
            Task.FromResult(new VirtualModelRoute("fixture", "physical", "off"))), "/extensions/router.dll");

        var error = Assert.Throws<InvalidOperationException>(() => runtime.SetVirtualModelRegistry(virtualModels));

        Assert.Contains("router/physical", error.Message, StringComparison.Ordinal);
        Assert.Contains("/extensions/router.dll", error.Message, StringComparison.Ordinal);
        Assert.Contains("/extensions/owner.dll", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task VirtualRouteThinkingIsClampedThroughThePhysicalModelCapabilities()
    {
        using var http = new HttpClient();
        var runtime = await ProviderModelRuntime.CreateAsync(Path.GetTempPath(), false, _ => null, http,
            offline: true);
        runtime.SetExtensionProviders([Provider("fixture", [new("small", Reasoning: false)],
            _ => new RecordingClient("physical response"), "/extensions/physical.dll")]);
        var virtualModels = new VirtualModelRegistry();
        virtualModels.Register(new("router", "auto", "Auto", (_, _) =>
            Task.FromResult(new VirtualModelRoute("fixture", "small", "high")), ["off", "high"]),
            "/extensions/router.dll");
        runtime.SetVirtualModelRegistry(virtualModels);
        var controller = new ModelRuntimeController(runtime, () => new UserSettings(), _ => null);
        var selection = await runtime.ResolveAsync("router", "auto");
        var route = controller.CreateVirtualModelRouter(selection)!;

        var physical = await route(new(new ConversationSession(Path.GetTempPath(), "auto", null, "router"),
            [new ChatMessage(ChatRole.User, "hello")], "user", "high", null, CancellationToken.None));

        Assert.Equal("fixture", physical.Provider);
        Assert.Equal("small", physical.Model.Id);
        Assert.Equal("off", physical.ThinkingLevel);
    }

    [Fact]
    public async Task VirtualRouteRejectsUnknownPhysicalModelsAndInvalidThinkingLevels()
    {
        using var http = new HttpClient();
        var runtime = await ProviderModelRuntime.CreateAsync(Path.GetTempPath(), false, _ => null, http,
            offline: true);
        runtime.SetExtensionProviders([Provider("fixture", [new("small", Reasoning: true)],
            _ => new RecordingClient("physical response"), "/extensions/physical.dll")]);
        var virtualModels = new VirtualModelRegistry();
        virtualModels.Register(new("missing-model-router", "auto", "Missing model router", (_, _) =>
            Task.FromResult(new VirtualModelRoute("fixture", "missing", "low")), ["off", "low"]),
            "/extensions/missing-model-router.dll");
        virtualModels.Register(new("invalid-thinking-router", "auto", "Invalid thinking router", (_, _) =>
            Task.FromResult(new VirtualModelRoute("fixture", "small", "ultra")), ["off", "low"]),
            "/extensions/invalid-thinking-router.dll");
        runtime.SetVirtualModelRegistry(virtualModels);
        var controller = new ModelRuntimeController(runtime, () => new UserSettings(), _ => null);
        var conversation = new ConversationSession(Path.GetTempPath(), "auto", null, "missing-model-router");
        var messages = new[] { new ChatMessage(ChatRole.User, "hello") };

        var missingSelection = await runtime.ResolveAsync("missing-model-router", "auto");
        var missingRoute = controller.CreateVirtualModelRouter(missingSelection)!;
        var missing = await Assert.ThrowsAsync<InvalidOperationException>(() => missingRoute(new(
            conversation, messages, "user", "low", null, CancellationToken.None)));
        Assert.Contains("fixture/missing", missing.Message, StringComparison.Ordinal);
        Assert.Contains("not an available physical model", missing.Message, StringComparison.Ordinal);

        var thinkingSelection = await runtime.ResolveAsync("invalid-thinking-router", "auto");
        var thinkingRoute = controller.CreateVirtualModelRouter(thinkingSelection)!;
        var invalidThinking = await Assert.ThrowsAsync<ArgumentException>(() => thinkingRoute(new(
            conversation, messages, "user", "low", null, CancellationToken.None)));
        Assert.Contains("Thinking level must be off, minimal, low, medium, high, xhigh, or max",
            invalidThinking.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ProviderFactoryAndRequestFailuresDoNotLeakResolvedCredentials()
    {
        const string secret = "extension-provider-secret";
        using var http = new HttpClient();
        var runtime = await ProviderModelRuntime.CreateAsync(Path.GetTempPath(), false,
            name => name == "FIXTURE_PROVIDER_KEY" ? secret : null, http, offline: true);
        var factoryFailure = Provider("factory-failure", [new("small")], _ =>
            throw new InvalidOperationException("factory failed with " + secret), "/extensions/factory.dll",
            "FIXTURE_PROVIDER_KEY", authRequired: true);
        runtime.SetExtensionProviders([factoryFailure]);
        var factorySelection = await runtime.ResolveAsync("factory-failure", "small");
        var factoryError = Assert.Throws<InvalidOperationException>(() => ProviderChatClientFactory.Create(factorySelection));
        Assert.Contains("/extensions/factory.dll", factoryError.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(secret, factoryError.Message, StringComparison.Ordinal);

        var requestFailure = Provider("request-failure", [new("small")], _ => new RecordingClient("unused", secret),
            "/extensions/request.dll", "FIXTURE_PROVIDER_KEY", authRequired: true);
        runtime.SetExtensionProviders([requestFailure]);
        var requestSelection = await runtime.ResolveAsync("request-failure", "small");
        var client = ProviderChatClientFactory.Create(requestSelection);
        var requestError = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            client.GetResponseAsync([new ChatMessage(ChatRole.User, "hello")]));
        Assert.DoesNotContain(secret, requestError.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReplacedProviderDrainsAnActiveStreamAndRejectsFutureRequests()
    {
        using var http = new HttpClient();
        var runtime = await ProviderModelRuntime.CreateAsync(Path.GetTempPath(), false, _ => null, http,
            offline: true);
        var inner = new BlockingStreamClient();
        runtime.SetExtensionProviders([Provider("fixture", [new("small")], _ => inner, "/extensions/stream.dll")]);
        var selection = await runtime.ResolveAsync("fixture", "small");
        var client = ProviderChatClientFactory.Create(selection);
        await using var stream = client.GetStreamingResponseAsync([new ChatMessage(ChatRole.User, "hello")])
            .GetAsyncEnumerator();

        Assert.True(await stream.MoveNextAsync());
        runtime.SetExtensionProviders([]);
        Assert.False(inner.Disposed);
        inner.Complete();
        Assert.True(await stream.MoveNextAsync());
        Assert.False(await stream.MoveNextAsync());
        Assert.True(inner.Disposed);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            client.GetResponseAsync([new ChatMessage(ChatRole.User, "after removal")]));
        Assert.Contains("removed or replaced", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FailedSessionReplacementRestoresTheExactPriorProviderSnapshot()
    {
        using var http = new HttpClient();
        var runtime = await ProviderModelRuntime.CreateAsync(Path.GetTempPath(), false, _ => null, http,
            offline: true);
        var v1 = Provider("fixture", [new("v1")], _ => new RecordingClient("v1"), "/extensions/v1.dll");
        var v2 = Provider("fixture", [new("v2")], _ => new RecordingClient("v2"), "/extensions/v2.dll");
        runtime.SetExtensionProviders([v1]);
        var snapshot = runtime.CaptureExtensionProviderSnapshot();
        var originalClient = ProviderChatClientFactory.Create(await runtime.ResolveAsync("fixture", "v1"));

        runtime.SetExtensionProviders([v2], deactivateReplaced: false);
        runtime.RestoreExtensionProviderSnapshot(snapshot);

        Assert.Equal("v1", Assert.Single(await runtime.ListModelsAsync("fixture")).Id);
        Assert.Equal("v1", (await originalClient.GetResponseAsync([new ChatMessage(ChatRole.User, "hello")])).Text);
        Assert.DoesNotContain(await runtime.ListModelsAsync("fixture"), model => model.Id == "v2");
    }

    private static RegisteredExtensionProvider Provider(string id, IReadOnlyList<PiSharpExtensionProviderModel> models,
        Func<PiSharpExtensionProviderClientContext, IChatClient> factory, string path,
        string? apiKeyEnvironment = null, bool authRequired = false) =>
        new(new PiSharpExtensionProviderDefinition(id, "Fixture " + id, models, factory)
        {
            AuthRequired = authRequired,
            ApiKeyEnvironment = apiKeyEnvironment
        }, path);

    private sealed class RecordingClient(string response, string? failure = null) : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            if (failure is not null) throw new InvalidOperationException("provider request failed with " + failure);
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, response)));
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            var response = await GetResponseAsync(messages, options, cancellationToken);
            foreach (var update in response.ToChatResponseUpdates()) yield return update;
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    private sealed class BlockingStreamClient : IChatClient
    {
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool Disposed { get; private set; }

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default) => Task.FromResult(
            new ChatResponse(new ChatMessage(ChatRole.Assistant, "done")));

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            yield return new ChatResponseUpdate(ChatRole.Assistant, [new TextContent("first")]);
            await _release.Task.WaitAsync(cancellationToken);
            yield return new ChatResponseUpdate(ChatRole.Assistant, [new TextContent("second")]);
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() => Disposed = true;
        public void Complete() => _release.TrySetResult();
    }
}
