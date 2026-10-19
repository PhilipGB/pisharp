using System.Text.Json;
using PiSharp.Extensions.Abstractions;

namespace PiSharp.Runtime.Extensions;

/// <summary>Ambient, request-scoped host dispatcher shared by physical adapters and extension providers.</summary>
internal sealed class ProviderWireInvocation(NativeExtensionEventDispatcher dispatcher,
    CancellationToken cancellationToken)
{
    private static readonly AsyncLocal<ProviderWireInvocation?> s_current = new();

    internal static ProviderWireInvocation? Current => s_current.Value;

    internal static IDisposable Enter(NativeExtensionEventDispatcher dispatcher, CancellationToken cancellationToken)
    {
        var previous = s_current.Value;
        s_current.Value = new ProviderWireInvocation(dispatcher, cancellationToken);
        return new Scope(previous);
    }

    internal async Task<JsonElement> TransformPayloadAsync(string providerId, string modelId, string protocol,
        JsonElement payload, CancellationToken requestCancellation)
    {
        using var linked = Link(requestCancellation);
        return await dispatcher.DispatchProviderRequestAsync(
            new(providerId, modelId, protocol, payload), linked.Token).ConfigureAwait(false);
    }

    internal async Task<IReadOnlyDictionary<string, string?>> TransformHeadersAsync(string providerId,
        string modelId, string protocol, IReadOnlyDictionary<string, string?> headers,
        CancellationToken requestCancellation)
    {
        using var linked = Link(requestCancellation);
        return await dispatcher.DispatchProviderHeadersAsync(
            new(providerId, modelId, protocol, headers), linked.Token).ConfigureAwait(false);
    }

    internal async Task ObserveResponseAsync(string providerId, string modelId, string protocol,
        int statusCode, IReadOnlyDictionary<string, string> headers, CancellationToken requestCancellation)
    {
        using var linked = Link(requestCancellation);
        await dispatcher.DispatchProviderResponseAsync(new(providerId, modelId, protocol, statusCode,
            new Dictionary<string, string>(headers, StringComparer.OrdinalIgnoreCase)), linked.Token)
            .ConfigureAwait(false);
    }

    internal async Task ObserveStreamEventAsync(string providerId, string modelId, string protocol,
        string? eventType, JsonElement data, CancellationToken requestCancellation)
    {
        using var linked = Link(requestCancellation);
        await dispatcher.DispatchProviderStreamEventAsync(
            new(providerId, modelId, protocol, eventType, data), linked.Token).ConfigureAwait(false);
    }

    private CancellationTokenSource Link(CancellationToken requestCancellation) =>
        CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, requestCancellation);

    private sealed class Scope(ProviderWireInvocation? previous) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0) s_current.Value = previous;
        }
    }
}

internal sealed class NativeExtensionProviderWireBridge(string providerId, string modelId, string protocol)
    : IPiSharpExtensionProviderWireBridge
{
    public async ValueTask<JsonElement> TransformPayloadAsync(JsonElement payload,
        CancellationToken cancellationToken = default)
    {
        var current = ProviderWireInvocation.Current;
        return current is null ? payload.Clone() : await current.TransformPayloadAsync(providerId, modelId,
            protocol, payload, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<IReadOnlyDictionary<string, string?>> TransformHeadersAsync(
        IReadOnlyDictionary<string, string?> headers, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(headers);
        var current = ProviderWireInvocation.Current;
        if (current is null)
            return new Dictionary<string, string?>(headers, StringComparer.OrdinalIgnoreCase);
        return await current.TransformHeadersAsync(providerId, modelId, protocol, headers,
            cancellationToken).ConfigureAwait(false);
    }

    public ValueTask ObserveResponseAsync(int statusCode, IReadOnlyDictionary<string, string> headers,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(headers);
        var current = ProviderWireInvocation.Current;
        return current is null ? ValueTask.CompletedTask : new(current.ObserveResponseAsync(providerId, modelId,
            protocol, statusCode, headers, cancellationToken));
    }

    public ValueTask ObserveStreamEventAsync(string? eventType, JsonElement data,
        CancellationToken cancellationToken = default)
    {
        var current = ProviderWireInvocation.Current;
        return current is null ? ValueTask.CompletedTask : new(current.ObserveStreamEventAsync(providerId,
            modelId, protocol, eventType, data, cancellationToken));
    }
}
