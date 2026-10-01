using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace PiSharp.Runtime.Mcp;

/// <summary>Normalizes MCP HTTP edge cases that the pinned client SDK treats more strictly than Pi.</summary>
internal sealed class McpProtocolCompatibilityHandler(Uri serverUrl, HttpMessageHandler innerHandler)
    : DelegatingHandler(innerHandler)
{
    private static readonly HashSet<string> s_paginatedMethods =
        ["tools/list", "resources/list", "resources/templates/list"];

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        var paginatedMethods = await ReadPaginatedMethodsAsync(request, cancellationToken).ConfigureAwait(false);
        var response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode || response.Content is null) return response;

        if (request.Method == HttpMethod.Get && IsProtectedResourceMetadataRequest(request.RequestUri))
            return await NormalizeResourceMetadataAsync(response, cancellationToken).ConfigureAwait(false);
        if (paginatedMethods.Count > 0)
            return await NormalizePaginationAsync(response, paginatedMethods, cancellationToken).ConfigureAwait(false);
        return response;
    }

    private static async Task<HashSet<string>> ReadPaginatedMethodsAsync(HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        var methods = new HashSet<string>(StringComparer.Ordinal);
        if (request.Method != HttpMethod.Post || request.Content is null ||
            !string.Equals(request.Content.Headers.ContentType?.MediaType, "application/json",
                StringComparison.OrdinalIgnoreCase)) return methods;

        var original = request.Content;
        var bytes = await original.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
        var replacement = new ByteArrayContent(bytes);
        foreach (var header in original.Headers)
            if (!header.Key.Equals("Content-Length", StringComparison.OrdinalIgnoreCase))
                replacement.Headers.TryAddWithoutValidation(header.Key, header.Value);
        request.Content = replacement;
        original.Dispose();

        try
        {
            using var document = JsonDocument.Parse(bytes);
            if (document.RootElement.ValueKind == JsonValueKind.Array)
            {
                foreach (var message in document.RootElement.EnumerateArray()) AddMethod(message, methods);
            }
            else AddMethod(document.RootElement, methods);
        }
        catch (JsonException) { }
        return methods;
    }

    private static void AddMethod(JsonElement message, HashSet<string> methods)
    {
        if (message.ValueKind == JsonValueKind.Object && message.TryGetProperty("method", out var method) &&
            method.ValueKind == JsonValueKind.String && s_paginatedMethods.Contains(method.GetString()!))
            methods.Add(method.GetString()!);
    }

    private async Task<HttpResponseMessage> NormalizePaginationAsync(HttpResponseMessage response,
        HashSet<string> methods, CancellationToken cancellationToken)
    {
        var mediaType = response.Content.Headers.ContentType?.MediaType;
        var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
        byte[]? normalized = mediaType?.Equals("text/event-stream", StringComparison.OrdinalIgnoreCase) == true
            ? NormalizeEventStream(bytes, methods)
            : NormalizeJson(bytes, NormalizeListResponse);
        return normalized is null ? response : ReplaceContent(response, normalized);
    }

    private async Task<HttpResponseMessage> NormalizeResourceMetadataAsync(HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        if (response.StatusCode == HttpStatusCode.NotFound) return response;
        var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
        JsonNode? metadata;
        try { metadata = JsonNode.Parse(bytes); }
        catch (JsonException) { return ReplaceContent(response, SerializeFallbackMetadata()); }
        if (metadata is not JsonObject document) return ReplaceContent(response, SerializeFallbackMetadata());
        return NormalizeResourceMetadata(document)
            ? ReplaceContent(response, JsonSerializer.SerializeToUtf8Bytes(document))
            : response;
    }

    private byte[]? NormalizeEventStream(byte[] bytes, HashSet<string> methods)
    {
        var source = Encoding.UTF8.GetString(bytes);
        var newline = source.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var lines = source.Split('\n');
        var changed = false;
        for (var index = 0; index < lines.Length; index++)
        {
            var line = lines[index].TrimEnd('\r');
            if (!line.StartsWith("data:", StringComparison.Ordinal)) continue;
            var prefix = line.StartsWith("data: ", StringComparison.Ordinal) ? "data: " : "data:";
            var payload = line[prefix.Length..];
            var normalized = NormalizeJson(Encoding.UTF8.GetBytes(payload), NormalizeListResponse);
            if (normalized is null) continue;
            lines[index] = prefix + Encoding.UTF8.GetString(normalized);
            changed = true;
        }
        return changed ? Encoding.UTF8.GetBytes(string.Join(newline, lines)) : null;
    }

    private static byte[]? NormalizeJson(byte[] bytes, Func<JsonNode?, bool> normalize)
    {
        JsonNode? document;
        try { document = JsonNode.Parse(bytes); }
        catch (JsonException) { return null; }
        if (!normalize(document)) return null;
        return JsonSerializer.SerializeToUtf8Bytes(document);
    }

    private static bool NormalizeListResponse(JsonNode? response)
    {
        var changed = false;
        if (response is JsonArray batch)
        {
            foreach (var message in batch) changed |= NormalizeListResult(message);
        }
        else changed = NormalizeListResult(response);
        return changed;

        bool NormalizeListResult(JsonNode? message)
        {
            if (message is not JsonObject jsonRpc || jsonRpc["result"] is not JsonObject result ||
                !result.TryGetPropertyValue("nextCursor", out var cursor)) return false;
            var endOfPages = cursor is null || cursor is JsonValue value &&
                value.TryGetValue<string>(out var text) && text.Length == 0;
            if (!endOfPages) return false;
            result.Remove("nextCursor");
            return true;
        }
    }

    private bool NormalizeResourceMetadata(JsonObject document)
    {
        var resourceIsValid = document["resource"] is JsonValue resource &&
            resource.TryGetValue<string>(out var resourceUrl) && IsSafeAbsoluteUrl(resourceUrl);
        if (!resourceIsValid)
        {
            document["resource"] = serverUrl.AbsoluteUri;
            document["authorization_servers"] = FallbackAuthorizationServers();
            return true;
        }

        if (!document.TryGetPropertyValue("authorization_servers", out var servers) || servers is null)
        {
            document["authorization_servers"] = FallbackAuthorizationServers();
            return true;
        }
        if (servers is not JsonArray array || array.Count == 0 ||
            array.Any(server => server is not JsonValue value || !value.TryGetValue<string>(out var url) ||
                !IsSafeAbsoluteUrl(url)))
        {
            document["authorization_servers"] = FallbackAuthorizationServers();
            return true;
        }
        return false;
    }

    private byte[] SerializeFallbackMetadata() => JsonSerializer.SerializeToUtf8Bytes(new JsonObject
    {
        ["resource"] = serverUrl.AbsoluteUri,
        ["authorization_servers"] = FallbackAuthorizationServers()
    });

    private JsonArray FallbackAuthorizationServers() =>
        new(JsonValue.Create(serverUrl.GetLeftPart(UriPartial.Authority)));

    private static bool IsSafeAbsoluteUrl(string value) => Uri.TryCreate(value, UriKind.Absolute, out var uri) &&
        (string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) ||
         string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)) &&
        !string.IsNullOrEmpty(uri.Host);

    private static bool IsProtectedResourceMetadataRequest(Uri? uri) => uri is not null &&
        uri.AbsolutePath.Contains("/.well-known/oauth-protected-resource", StringComparison.Ordinal);

    private static HttpResponseMessage ReplaceContent(HttpResponseMessage response, byte[] bytes)
    {
        var previous = response.Content;
        var content = new ByteArrayContent(bytes);
        foreach (var header in previous.Headers)
            if (!header.Key.Equals("Content-Length", StringComparison.OrdinalIgnoreCase))
                content.Headers.TryAddWithoutValidation(header.Key, header.Value);
        response.Content = content;
        previous.Dispose();
        return response;
    }
}
