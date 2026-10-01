using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace PiSharp.Runtime.Mcp;

internal sealed class McpOAuthMetadataHandler(Uri resourceUrl, Uri metadataUrl, HttpMessageHandler innerHandler)
    : DelegatingHandler(innerHandler)
{
    private readonly SemaphoreSlim _metadataGate = new(1, 1);
    private ConfiguredMetadata? _metadata;

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        if (request.Method == HttpMethod.Get && IsAuthorizationMetadataRequest(request.RequestUri))
        {
            var metadata = await GetMetadataAsync(cancellationToken).ConfigureAwait(false);
            return JsonResponse(request, HttpStatusCode.OK, metadata.Document);
        }

        var response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (request.Method != HttpMethod.Get || !IsResourceMetadataRequest(request.RequestUri))
            return response;

        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            response.Dispose();
            var metadata = await GetMetadataAsync(cancellationToken).ConfigureAwait(false);
            var resourceMetadata = JsonSerializer.SerializeToUtf8Bytes(new
            {
                resource = resourceUrl.AbsoluteUri,
                authorization_servers = new[] { metadata.Issuer }
            });
            return JsonResponse(request, HttpStatusCode.OK, resourceMetadata);
        }

        if (!response.IsSuccessStatusCode) return response;

        using (response)
        {
            var metadata = await GetMetadataAsync(cancellationToken).ConfigureAwait(false);
            var body = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
            var document = JsonNode.Parse(body) as JsonObject ??
                throw new InvalidDataException("MCP protected-resource metadata must be an object.");
            document["authorization_servers"] = new JsonArray(JsonValue.Create(metadata.Issuer));
            var normalized = JsonSerializer.SerializeToUtf8Bytes(document);
            return JsonResponse(request, response.StatusCode, normalized, response);
        }
    }

    private async Task<ConfiguredMetadata> GetMetadataAsync(CancellationToken cancellationToken)
    {
        if (Volatile.Read(ref _metadata) is { } cached) return cached;
        await _metadataGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_metadata is { } current) return current;
            using var request = new HttpRequestMessage(HttpMethod.Get, metadataUrl);
            using var response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                throw new HttpRequestException(
                    $"Configured MCP authorization-server metadata request failed ({(int)response.StatusCode}).");
            var document = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
            using var parsed = JsonDocument.Parse(document);
            var issuer = parsed.RootElement.ValueKind == JsonValueKind.Object &&
                parsed.RootElement.TryGetProperty("issuer", out var issuerValue) &&
                issuerValue.ValueKind == JsonValueKind.String ? issuerValue.GetString() : null;
            if (string.IsNullOrWhiteSpace(issuer) ||
                !Uri.TryCreate(issuer, UriKind.Absolute, out _))
                throw new InvalidDataException("Configured MCP authorization-server metadata has no absolute issuer.");
            current = new ConfiguredMetadata(document, issuer);
            Volatile.Write(ref _metadata, current);
            return current;
        }
        finally
        {
            _metadataGate.Release();
        }
    }

    private static bool IsAuthorizationMetadataRequest(Uri? uri)
    {
        if (uri is null) return false;
        var path = uri.AbsolutePath;
        return path.Contains("/.well-known/oauth-authorization-server", StringComparison.OrdinalIgnoreCase) ||
            path.Contains("/.well-known/openid-configuration", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsResourceMetadataRequest(Uri? uri) => uri is not null &&
        uri.AbsolutePath.Contains("/.well-known/oauth-protected-resource", StringComparison.OrdinalIgnoreCase);

    private static HttpResponseMessage JsonResponse(HttpRequestMessage request, HttpStatusCode status,
        byte[] body, HttpResponseMessage? source = null)
    {
        var response = new HttpResponseMessage(status)
        {
            RequestMessage = request,
            Content = new ByteArrayContent(body),
            Version = source?.Version ?? HttpVersion.Version11,
            ReasonPhrase = source?.ReasonPhrase
        };
        response.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
        if (source is not null)
            foreach (var header in source.Headers)
                response.Headers.TryAddWithoutValidation(header.Key, header.Value);
        return response;
    }

    private sealed record ConfiguredMetadata(byte[] Document, string Issuer);
}
