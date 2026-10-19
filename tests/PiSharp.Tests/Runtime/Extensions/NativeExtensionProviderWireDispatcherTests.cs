using System.Text.Json;
using PiSharp.Extensions.Abstractions;
using PiSharp.Runtime.Extensions;
using PiSharp.Runtime.Resources;
using PiSharp.Runtime.Sessions;
using NativeExtensions = PiSharp.Extensions.Abstractions;

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
            eventData.Headers["x-wire-test"] = "visible";
            return ValueTask.CompletedTask;
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

    [Fact]
    public async Task ProviderHeaderMutationsBeforeFailureRemainVisibleAndReachTheTransportMap()
    {
        var registration = new ExtensionRegistration();
        var adapter = new NativeExtensionRegistrationAdapter(registration);
        IDictionary<string, string?>? headerMapSeenByA = null;
        registration.SetCurrentSourceInfo(Source("/extensions/header-a.dll"));
        adapter.AddBeforeProviderHeadersHandler((eventData, _, _) =>
        {
            headerMapSeenByA = eventData.Headers;
            eventData.Headers["X-A"] = "one";
            eventData.Headers["x-remove"] = null;
            throw new InvalidOperationException("fixture header handler failure");
        });
        registration.SetCurrentSourceInfo(Source("/extensions/header-b.dll"));
        string? seenByB = null;
        adapter.AddBeforeProviderHeadersHandler((eventData, _, _) =>
        {
            Assert.Same(headerMapSeenByA, eventData.Headers);
            seenByB = eventData.Headers["x-a"];
            Assert.True(eventData.Headers.ContainsKey("x-remove"));
            Assert.Null(eventData.Headers["X-REMOVE"]);
            eventData.Headers["X-Remove"] = "restored";
            eventData.Headers["X-B"] = "two";
            return ValueTask.CompletedTask;
        });
        registration.SetCurrentSourceInfo(null);
        adapter.Close();

        var diagnostics = new List<AgentLifecycleEvent>();
        var dispatcher = new NativeExtensionEventDispatcher(registration, CreateContext(), diagnostics.Add);
        var headers = await dispatcher.DispatchProviderHeadersAsync(new("fixture-provider", "fixture-model",
            "openai-responses", new Dictionary<string, string?>
            {
                ["x-remove"] = "present",
                ["X-Existing"] = "kept"
            }), CancellationToken.None);

        Assert.Equal("one", seenByB);
        Assert.Equal("one", headers["x-a"]);
        Assert.Equal("two", headers["X-B"]);
        Assert.Equal("kept", headers["x-existing"]);
        Assert.Equal("restored", headers["X-REMOVE"]);
        var diagnostic = Assert.Single(diagnostics, item => item.Type == "extension_error");
        Assert.Contains("header-a.dll", JsonSerializer.Serialize(diagnostic.Details), StringComparison.Ordinal);
        Assert.Contains("before_provider_headers", JsonSerializer.Serialize(diagnostic.Details), StringComparison.Ordinal);
        Assert.Contains("fixture header handler failure", diagnostic.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ProviderHeaderRemovalRemainsDistinctFromPresentNullForLaterHandlers()
    {
        var registration = new ExtensionRegistration();
        var adapter = new NativeExtensionRegistrationAdapter(registration);
        IDictionary<string, string?>? headerMapSeenByA = null;
        adapter.AddBeforeProviderHeadersHandler((eventData, _, _) =>
        {
            headerMapSeenByA = eventData.Headers;
            eventData.Headers.Remove("X-A");
            eventData.Headers["X-B"] = null;
            return ValueTask.CompletedTask;
        });
        adapter.AddBeforeProviderHeadersHandler((eventData, _, _) =>
        {
            Assert.Same(headerMapSeenByA, eventData.Headers);
            Assert.False(eventData.Headers.ContainsKey("x-a"));
            Assert.True(eventData.Headers.ContainsKey("x-b"));
            Assert.Null(eventData.Headers["X-B"]);
            return ValueTask.CompletedTask;
        });
        adapter.Close();

        var diagnostics = new List<AgentLifecycleEvent>();
        var dispatcher = new NativeExtensionEventDispatcher(registration, CreateContext(), diagnostics.Add);
        var headers = await dispatcher.DispatchProviderHeadersAsync(new("fixture-provider", "fixture-model",
            "openai-responses", new Dictionary<string, string?> { ["X-A"] = "a", ["X-B"] = "b" }),
            CancellationToken.None);

        Assert.DoesNotContain("X-A", headers.Keys, StringComparer.OrdinalIgnoreCase);
        Assert.True(headers.ContainsKey("x-b"));
        Assert.Null(headers["X-B"]);
        Assert.Empty(diagnostics);
    }

    [Fact]
    public async Task InvalidProviderHeaderMutationsAreRevertedWithoutDroppingValidState()
    {
        var registration = new ExtensionRegistration();
        var adapter = new NativeExtensionRegistrationAdapter(registration);
        registration.SetCurrentSourceInfo(Source("/extensions/invalid-header.dll"));
        adapter.AddBeforeProviderHeadersHandler((eventData, _, _) =>
        {
            eventData.Headers["X-Good"] = "bad\r\nvalue";
            eventData.Headers["X-New"] = "bad\nvalue";
            return ValueTask.CompletedTask;
        });
        registration.SetCurrentSourceInfo(Source("/extensions/header-observer.dll"));
        adapter.AddBeforeProviderHeadersHandler((eventData, _, _) =>
        {
            Assert.Equal("before", eventData.Headers["x-good"]);
            Assert.False(eventData.Headers.ContainsKey("x-new"));
            return ValueTask.CompletedTask;
        });
        registration.SetCurrentSourceInfo(null);
        adapter.Close();

        var diagnostics = new List<AgentLifecycleEvent>();
        var dispatcher = new NativeExtensionEventDispatcher(registration, CreateContext(), diagnostics.Add);
        var headers = await dispatcher.DispatchProviderHeadersAsync(new("fixture-provider", "fixture-model",
            "openai-responses", new Dictionary<string, string?> { ["X-Good"] = "before" }),
            CancellationToken.None);

        Assert.Equal("before", headers["X-Good"]);
        Assert.DoesNotContain("X-New", headers.Keys, StringComparer.OrdinalIgnoreCase);
        var diagnostic = Assert.Single(diagnostics, item => item.Type == "extension_error");
        Assert.Contains("invalid-header.dll", JsonSerializer.Serialize(diagnostic.Details), StringComparison.Ordinal);
        Assert.Contains("before_provider_headers", JsonSerializer.Serialize(diagnostic.Details), StringComparison.Ordinal);
    }

    [Fact]
    public async Task CancellationDetachesPendingProviderRequestAndRevokesItsPublicContext()
    {
        var registration = new ExtensionRegistration();
        var adapter = new NativeExtensionRegistrationAdapter(registration);
        var started = new TaskCompletionSource<(NativeExtensions.PiSharpExtensionContext Context, CancellationToken Token)>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handlerFinished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        adapter.AddBeforeProviderRequestHandler(async (_, context, token) =>
        {
            if (Interlocked.Increment(ref calls) == 1)
            {
                started.TrySetResult((context, token));
                try { await release.Task; }
                finally { handlerFinished.TrySetResult(); }
                return null;
            }
            return null;
        });
        adapter.Close();

        var runtimeContext = CreateRevocableContext();
        var dispatcher = new NativeExtensionEventDispatcher(registration, runtimeContext, _ => { });
        using var cancellation = new CancellationTokenSource();
        using var invocation = ProviderWireInvocation.Enter(dispatcher, cancellation.Token);
        var bridge = new NativeExtensionProviderWireBridge("fixture-provider", "fixture-model", "openai-responses");
        using var payload = JsonDocument.Parse("{\"input\":\"hello\"}");
        var operation = bridge.TransformPayloadAsync(payload.RootElement).AsTask();
        var callback = await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var session = callback.Context.Session!;
        var capabilities = callback.Context.Capabilities!;

        try
        {
            cancellation.Cancel();

            Assert.True(callback.Token.IsCancellationRequested);
            Assert.Throws<ObjectDisposedException>(() => { _ = session.Snapshot; });
            Assert.Throws<ObjectDisposedException>(() => { _ = capabilities.GetSessionSnapshot(); });
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operation.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.False(handlerFinished.Task.IsCompleted);
        }
        finally
        {
            release.TrySetResult();
            await handlerFinished.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }

        using var nextInvocation = ProviderWireInvocation.Enter(dispatcher, CancellationToken.None);
        var next = await bridge.TransformPayloadAsync(payload.RootElement).AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("hello", next.GetProperty("input").GetString());
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task CancellationDetachesPendingProviderResponseAndRevokesItsPublicContext()
    {
        var registration = new ExtensionRegistration();
        var adapter = new NativeExtensionRegistrationAdapter(registration);
        var started = new TaskCompletionSource<(NativeExtensions.PiSharpExtensionContext Context, CancellationToken Token)>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handlerFinished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        adapter.AddAfterProviderResponseHandler(async (_, context, token) =>
        {
            started.TrySetResult((context, token));
            try { await release.Task; }
            finally { handlerFinished.TrySetResult(); }
        });
        adapter.Close();

        var dispatcher = new NativeExtensionEventDispatcher(registration, CreateRevocableContext(), _ => { });
        using var cancellation = new CancellationTokenSource();
        using var invocation = ProviderWireInvocation.Enter(dispatcher, cancellation.Token);
        var bridge = new NativeExtensionProviderWireBridge("fixture-provider", "fixture-model", "openai-responses");
        var operation = bridge.ObserveResponseAsync(200, new Dictionary<string, string>()).AsTask();
        var callback = await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var session = callback.Context.Session!;
        var capabilities = callback.Context.Capabilities!;

        try
        {
            cancellation.Cancel();

            Assert.True(callback.Token.IsCancellationRequested);
            Assert.Throws<ObjectDisposedException>(() => { _ = session.Snapshot; });
            Assert.Throws<ObjectDisposedException>(() => { _ = capabilities.GetSessionSnapshot(); });
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operation.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.False(handlerFinished.Task.IsCompleted);
        }
        finally
        {
            release.TrySetResult();
            await handlerFinished.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    private static ResourceSourceInfo Source(string path) =>
        new(path, "native-package", "user", "package", "/extensions");

    private static PiSharp.Runtime.Extensions.PiSharpExtensionContext CreateContext() => new(
        new PiSharpExtensionContextConfiguration("/tmp", "test", HasUserInterface: false,
            IsProjectTrusted: true, new TestUi()), static () => null, static () => [], static () => true);

    private static PiSharp.Runtime.Extensions.PiSharpExtensionContext CreateRevocableContext()
    {
        var context = CreateContext();
        context.SetSessionContextFactory(_ => new PiSharpExtensionSessionContext(
            () => new("session", null, null, []),
            (_, _, _) => throw new InvalidOperationException("revoked session operation")));
        context.SetCapabilitiesFactory((_, session) => new PiSharpExtensionCapabilities(null, session, null));
        return context;
    }

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
