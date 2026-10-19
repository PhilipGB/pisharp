using System.Text.Json;
using System.Text.Json.Nodes;
using PiSharp.Extensions.Abstractions;
using PiSharp.Runtime.Extensions;
using PiSharp.Runtime.Resources;
using PiSharp.Runtime.Sessions;

namespace PiSharp.Tests;

internal sealed class ProviderWireTestSupport
{
    private readonly NativeExtensionEventDispatcher _dispatcher;

    public ProviderWireTestSupport(string sourcePath = "/extensions/wire-probe.dll",
        bool attemptSigV4ReservedHeaderChanges = false, bool mutateHeadersBeforeFailure = false,
        bool removeHeader = false)
    {
        Registration = new ExtensionRegistration();
        var adapter = new NativeExtensionRegistrationAdapter(Registration);
        Registration.SetCurrentSourceInfo(new(sourcePath, "native-package", "user", "package", "/extensions"));
        if (removeHeader)
            adapter.AddBeforeProviderHeadersHandler((eventData, _, _) =>
            {
                eventData.Headers["X-Remove"] = null;
                return ValueTask.CompletedTask;
            });
        if (mutateHeadersBeforeFailure)
        {
            Registration.SetCurrentSourceInfo(new("/extensions/header-a.dll", "native-package", "user", "package",
                "/extensions"));
            adapter.AddBeforeProviderHeadersHandler((eventData, _, _) =>
            {
                eventData.Headers["X-A"] = "one";
                eventData.Headers["x-remove"] = null;
                throw new InvalidOperationException("fixture header handler failure");
            });
            Registration.SetCurrentSourceInfo(new("/extensions/header-b.dll", "native-package", "user", "package",
                "/extensions"));
            adapter.AddBeforeProviderHeadersHandler((eventData, _, _) =>
            {
                HeaderAfterFailure = eventData.Headers["x-a"];
                eventData.Headers["X-B"] = "two";
                return ValueTask.CompletedTask;
            });
            Registration.SetCurrentSourceInfo(new(sourcePath, "native-package", "user", "package", "/extensions"));
        }
        adapter.AddBeforeProviderRequestHandler((eventData, _, _) =>
        {
            Events.Add("payload:" + eventData.Protocol);
            ProviderIds.Add(eventData.ProviderId);
            var payload = JsonNode.Parse(eventData.Payload.GetRawText())?.AsObject() ??
                throw new InvalidDataException("The fixture adapter did not produce an object payload.");
            payload["wire_probe"] = true;
            return ValueTask.FromResult<JsonElement?>(JsonSerializer.SerializeToElement(payload));
        });
        adapter.AddBeforeProviderHeadersHandler((eventData, _, _) =>
        {
            Events.Add("headers:" + eventData.Protocol);
            ProviderIds.Add(eventData.ProviderId);
            eventData.Headers["x-pisharp-wire-probe"] = "transformed";
            if (attemptSigV4ReservedHeaderChanges)
            {
                eventData.Headers["authorization"] = "Bearer forged";
                eventData.Headers["host"] = "forged.example";
                eventData.Headers["x-amz-date"] = "forged-date";
            }
            return ValueTask.CompletedTask;
        });
        adapter.AddAfterProviderResponseHandler((eventData, _, _) =>
        {
            Events.Add($"response:{eventData.Protocol}:{eventData.StatusCode}");
            ProviderIds.Add(eventData.ProviderId);
            ResponseObserved.TrySetResult();
            return ValueTask.CompletedTask;
        });
        adapter.AddProviderStreamEventHandler((eventData, _, _) =>
        {
            ProviderIds.Add(eventData.ProviderId);
            var type = eventData.EventType ?? (eventData.Data.ValueKind == JsonValueKind.Object &&
                eventData.Data.TryGetProperty("type", out var dataType) ? dataType.GetString() : null);
            Events.Add($"stream:{eventData.Protocol}:{type ?? "<none>"}");
            return ValueTask.CompletedTask;
        });
        Registration.SetCurrentSourceInfo(null);
        adapter.Close();
        _dispatcher = new NativeExtensionEventDispatcher(Registration, CreateContext(), Diagnostics.Add);
    }

    public ExtensionRegistration Registration { get; }
    public List<string> Events { get; } = [];
    public List<string> ProviderIds { get; } = [];
    public List<AgentLifecycleEvent> Diagnostics { get; } = [];
    public string? HeaderAfterFailure { get; private set; }
    public TaskCompletionSource ResponseObserved { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public IDisposable Enter(CancellationToken cancellationToken) =>
        ProviderWireInvocation.Enter(_dispatcher, cancellationToken);

    public void AssertHooks(string protocol, bool responseExposed)
    {
        Assert.Contains("payload:" + protocol, Events);
        Assert.Contains("headers:" + protocol, Events);
        Assert.Contains(Events, item => item.StartsWith("stream:" + protocol + ":", StringComparison.Ordinal));
        Assert.Equal(responseExposed ? 1 : 0,
            Events.Count(item => item.StartsWith("response:" + protocol + ":", StringComparison.Ordinal)));
        Assert.Empty(Diagnostics);
    }

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
