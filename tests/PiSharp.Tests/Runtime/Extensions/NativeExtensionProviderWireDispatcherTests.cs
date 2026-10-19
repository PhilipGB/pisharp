using System.Text.Json;
using PiSharp.Extensions.Abstractions;
using PiSharp.Runtime.Extensions;
using PiSharp.Runtime.Resources;
using PiSharp.Runtime.Sessions;

namespace PiSharp.Tests;

public sealed class NativeExtensionProviderWireDispatcherTests
{
    [Fact]
    public async Task ProviderWireCallbacksPreserveOrderAndAttributeFailuresToTheirSource()
    {
        var registration = new ExtensionRegistration();
        var adapter = new NativeExtensionRegistrationAdapter(registration);
        var observed = new List<string>();
        registration.SetCurrentSourceInfo(Source("/extensions/handler-a.dll"));
        adapter.AddBeforeProviderRequestHandler((eventData, _, _) =>
        {
            observed.Add("payload-a");
            Assert.Equal("openai-responses", eventData.Protocol);
            return ValueTask.FromResult<JsonElement?>(JsonSerializer.SerializeToElement(new { stage = "a" }));
        });
        registration.SetCurrentSourceInfo(Source("/extensions/handler-b.dll"));
        adapter.AddBeforeProviderRequestHandler((_, _, _) =>
        {
            observed.Add("payload-b");
            throw new InvalidOperationException("fixture payload handler failure");
        });
        registration.SetCurrentSourceInfo(Source("/extensions/handler-c.dll"));
        JsonElement? payloadSeenByC = null;
        adapter.AddBeforeProviderRequestHandler((eventData, _, _) =>
        {
            observed.Add("payload-c");
            payloadSeenByC = eventData.Payload;
            return ValueTask.FromResult<JsonElement?>(null);
        });
        adapter.AddBeforeProviderHeadersHandler((eventData, _, _) =>
        {
            Assert.Equal("fixture-provider", eventData.ProviderId);
            return ValueTask.FromResult<IReadOnlyDictionary<string, string?>?>(
                new Dictionary<string, string?> { ["x-wire-test"] = "visible" });
        });
        adapter.AddAfterProviderResponseHandler((eventData, _, _) =>
        {
            observed.Add($"response-{eventData.StatusCode}");
            return ValueTask.CompletedTask;
        });
        adapter.AddProviderStreamEventHandler((eventData, _, _) =>
        {
            observed.Add($"stream-{eventData.EventType}");
            return ValueTask.CompletedTask;
        });
        registration.SetCurrentSourceInfo(null);
        adapter.Close();

        var diagnostics = new List<AgentLifecycleEvent>();
        var dispatcher = new NativeExtensionEventDispatcher(registration, CreateContext(), diagnostics.Add);
        using var originalPayload = JsonDocument.Parse("{\"original\":true}");
        var payload = await dispatcher.DispatchProviderRequestAsync(new("fixture-provider", "fixture-model",
            "openai-responses", originalPayload.RootElement), CancellationToken.None);
        Assert.Equal("a", payload.GetProperty("stage").GetString());
        Assert.Equal("a", payloadSeenByC!.Value.GetProperty("stage").GetString());

        var headers = await dispatcher.DispatchProviderHeadersAsync(new("fixture-provider", "fixture-model",
            "openai-responses", new Dictionary<string, string?>()), CancellationToken.None);
        Assert.Equal("visible", headers["x-wire-test"]);
        await dispatcher.DispatchProviderResponseAsync(new("fixture-provider", "fixture-model",
            "openai-responses", 200, new Dictionary<string, string>()), CancellationToken.None);
        using var streamData = JsonDocument.Parse("{\"type\":\"response.output_text.delta\"}");
        await dispatcher.DispatchProviderStreamEventAsync(new("fixture-provider", "fixture-model",
            "openai-responses", "response.output_text.delta", streamData.RootElement), CancellationToken.None);

        Assert.Equal(["payload-a", "payload-b", "payload-c", "response-200", "stream-response.output_text.delta"], observed);
        var diagnostic = Assert.Single(diagnostics, item => item.Type == "extension_error");
        Assert.Contains("handler-b.dll", JsonSerializer.Serialize(diagnostic.Details), StringComparison.Ordinal);
        Assert.Contains("before_provider_request", JsonSerializer.Serialize(diagnostic.Details), StringComparison.Ordinal);
        Assert.Contains("fixture payload handler failure", diagnostic.Error, StringComparison.Ordinal);
    }

    private static ResourceSourceInfo Source(string path) =>
        new(path, "native-package", "user", "package", "/extensions");

    private static PiSharp.Runtime.Extensions.PiSharpExtensionContext CreateContext() => new(
        new PiSharpExtensionContextConfiguration("/tmp", "test", HasUserInterface: false,
            IsProjectTrusted: true, new TestUi()), static () => null, static () => [], static () => true);

    private sealed class TestUi : PiSharp.Runtime.Extensions.IPiSharpExtensionUi
    {
        public Task<string?> SelectAsync(string title, IReadOnlyList<string> options,
            CancellationToken cancellationToken = default) => Task.FromResult<string?>(null);
        public Task<bool> ConfirmAsync(string title, string message,
            CancellationToken cancellationToken = default) => Task.FromResult(false);
        public Task<string?> InputAsync(string title, string? placeholder = null,
            CancellationToken cancellationToken = default) => Task.FromResult<string?>(null);
        public void Notify(string message, string type = "info") { }
    }
}
