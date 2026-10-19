using System.Collections.ObjectModel;
using System.Text.Json;

namespace PiSharp.Extensions.Abstractions;

/// <summary>Detached provider request payload presented at the final physical wire boundary.</summary>
public sealed record PiSharpProviderRequestEvent
{
    private readonly JsonElement _payload;

    /// <summary>Creates a detached provider request payload event.</summary>
    public PiSharpProviderRequestEvent(string providerId, string modelId, string protocol, JsonElement payload)
    {
        ProviderId = providerId;
        ModelId = modelId;
        Protocol = protocol;
        _payload = payload.Clone();
    }

    /// <summary>Selected physical provider identity.</summary>
    public string ProviderId { get; }
    /// <summary>Selected physical model identity.</summary>
    public string ModelId { get; }
    /// <summary>Physical request protocol.</summary>
    public string Protocol { get; }
    /// <summary>Detached final provider payload.</summary>
    public JsonElement Payload => _payload.Clone();
}

/// <summary>Final provider request headers before transport dispatch.</summary>
public sealed record PiSharpProviderHeadersEvent
{
    private readonly Dictionary<string, string?> _headers;

    /// <summary>Creates a detached final request headers event.</summary>
    public PiSharpProviderHeadersEvent(string providerId, string modelId, string protocol,
        IReadOnlyDictionary<string, string?> headers)
    {
        ProviderId = providerId;
        ModelId = modelId;
        Protocol = protocol;
        _headers = new Dictionary<string, string?>(headers, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Selected physical provider identity.</summary>
    public string ProviderId { get; }
    /// <summary>Selected physical model identity.</summary>
    public string ModelId { get; }
    /// <summary>Physical request protocol.</summary>
    public string Protocol { get; }
    /// <summary>
    /// Mutable, detached final headers. Changes take effect immediately, even if this handler throws.
    /// Remove a key or set its value to null to remove that header before dispatch.
    /// </summary>
    public IDictionary<string, string?> Headers => _headers;
}

/// <summary>HTTP response metadata exposed before the provider response body is consumed.</summary>
public sealed class PiSharpProviderResponseEvent
{
    /// <summary>Creates a detached provider response metadata event.</summary>
    public PiSharpProviderResponseEvent(string providerId, string modelId, string protocol,
        int statusCode, IReadOnlyDictionary<string, string> headers)
    {
        ProviderId = providerId;
        ModelId = modelId;
        Protocol = protocol;
        StatusCode = statusCode;
        Headers = new ReadOnlyDictionary<string, string>(
            new Dictionary<string, string>(headers, StringComparer.OrdinalIgnoreCase));
    }

    /// <summary>Selected physical provider identity.</summary>
    public string ProviderId { get; }
    /// <summary>Selected physical model identity.</summary>
    public string ModelId { get; }
    /// <summary>Physical request protocol.</summary>
    public string Protocol { get; }
    /// <summary>HTTP response status code.</summary>
    public int StatusCode { get; }
    /// <summary>Detached response headers.</summary>
    public IReadOnlyDictionary<string, string> Headers { get; }
}

/// <summary>One parsed raw provider event before PiSharp converts it to MEAI updates.</summary>
public sealed record PiSharpProviderStreamEvent
{
    private readonly JsonElement _data;

    /// <summary>Creates a detached raw provider stream event.</summary>
    public PiSharpProviderStreamEvent(string providerId, string modelId, string protocol,
        string? eventType, JsonElement data)
    {
        ProviderId = providerId;
        ModelId = modelId;
        Protocol = protocol;
        EventType = eventType;
        _data = data.Clone();
    }

    /// <summary>Selected physical provider identity.</summary>
    public string ProviderId { get; }
    /// <summary>Selected physical model identity.</summary>
    public string ModelId { get; }
    /// <summary>Physical request protocol.</summary>
    public string Protocol { get; }
    /// <summary>Provider event type when the transport has one.</summary>
    public string? EventType { get; }
    /// <summary>Detached parsed raw provider event.</summary>
    public JsonElement Data => _data.Clone();
}

/// <summary>Asynchronous mutation hook for a final physical provider payload.</summary>
public delegate ValueTask<JsonElement?> PiSharpBeforeProviderRequestHandler(
    PiSharpProviderRequestEvent eventData, PiSharpExtensionContext context, CancellationToken cancellationToken);

/// <summary>
/// Asynchronous mutation hook for final provider headers. Mutate the detached event headers
/// directly; mutations made before a handler throws remain effective for later handlers.
/// </summary>
public delegate ValueTask PiSharpBeforeProviderHeadersHandler(
    PiSharpProviderHeadersEvent eventData, PiSharpExtensionContext context, CancellationToken cancellationToken);

/// <summary>Asynchronous observer for provider HTTP response metadata.</summary>
public delegate ValueTask PiSharpAfterProviderResponseHandler(
    PiSharpProviderResponseEvent eventData, PiSharpExtensionContext context, CancellationToken cancellationToken);

/// <summary>Asynchronous observer for one raw provider stream event.</summary>
public delegate ValueTask PiSharpProviderStreamEventHandler(
    PiSharpProviderStreamEvent eventData, PiSharpExtensionContext context, CancellationToken cancellationToken);

/// <summary>Host-owned wire callbacks available to newly instrumented extension providers.</summary>
public interface IPiSharpExtensionProviderWireBridge
{
    /// <summary>Transforms a detached provider payload through the host's ordered request handlers.</summary>
    ValueTask<JsonElement> TransformPayloadAsync(JsonElement payload, CancellationToken cancellationToken = default);

    /// <summary>Transforms provider headers through the host's ordered header handlers.</summary>
    ValueTask<IReadOnlyDictionary<string, string?>> TransformHeadersAsync(
        IReadOnlyDictionary<string, string?> headers, CancellationToken cancellationToken = default);

    /// <summary>Reports physical response metadata through the host's response observers.</summary>
    ValueTask ObserveResponseAsync(int statusCode, IReadOnlyDictionary<string, string> headers,
        CancellationToken cancellationToken = default);

    /// <summary>Reports one parsed physical provider event through the host's stream observers.</summary>
    ValueTask ObserveStreamEventAsync(string? eventType, JsonElement data,
        CancellationToken cancellationToken = default);
}
