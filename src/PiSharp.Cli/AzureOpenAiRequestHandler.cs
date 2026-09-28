using System.Text.Json;

namespace PiSharp.Cli;

/// <summary>Applies Azure Responses authentication, API versioning, and deployment mapping.</summary>
internal sealed class AzureOpenAiRequestHandler : DelegatingHandler
{
    private readonly string _apiKey;
    private readonly string _apiVersion;
    private readonly IReadOnlyDictionary<string, string> _deploymentNames;
    private readonly AzureOpenAiRequestContext _requestContext;

    public AzureOpenAiRequestHandler(string apiKey, AzureOpenAiProviderOptions options,
        AzureOpenAiRequestContext requestContext, HttpMessageHandler innerHandler) : base(innerHandler)
    {
        _apiKey = apiKey;
        _apiVersion = options.ApiVersion;
        _deploymentNames = options.DeploymentNames;
        _requestContext = requestContext;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        request.Headers.Remove("Authorization");
        request.Headers.Remove("api-key");
        request.Headers.TryAddWithoutValidation("api-key", _apiKey);
        if (request.RequestUri is { } uri) request.RequestUri = AddApiVersion(uri, _apiVersion);
        await ApplyRequestCompatibilityAsync(request, cancellationToken);
        return await base.SendAsync(request, cancellationToken);
    }

    private static Uri AddApiVersion(Uri uri, string apiVersion)
    {
        var query = uri.Query.TrimStart('?');
        var hasApiVersion = query.Split('&', StringSplitOptions.RemoveEmptyEntries).Any(pair =>
        {
            var separator = pair.IndexOf('=');
            var name = separator < 0 ? pair : pair[..separator];
            return Uri.UnescapeDataString(name).Equals("api-version", StringComparison.OrdinalIgnoreCase);
        });
        if (hasApiVersion) return uri;
        var version = "api-version=" + Uri.EscapeDataString(apiVersion);
        return new UriBuilder(uri) { Query = string.IsNullOrEmpty(query) ? version : query + "&" + version }.Uri;
    }

    private async Task ApplyRequestCompatibilityAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (request.Content is not { } content) return;
        var body = await content.ReadAsByteArrayAsync(cancellationToken);
        using var document = JsonDocument.Parse(body);
        if (document.RootElement.ValueKind != JsonValueKind.Object) return;
        var requestMetadata = _requestContext.Current;
        var hasReasoning = document.RootElement.TryGetProperty("reasoning", out var reasoning) &&
            reasoning.ValueKind == JsonValueKind.Object;

        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            var storeWritten = false;
            var cacheKeyWritten = false;
            var includeWritten = false;
            foreach (var property in document.RootElement.EnumerateObject())
            {
                if (property.NameEquals("model") && property.Value.ValueKind == JsonValueKind.String &&
                    _deploymentNames.TryGetValue(property.Value.GetString()!, out var deploymentName))
                    writer.WriteString(property.Name, deploymentName);
                else if (property.NameEquals("store"))
                {
                    writer.WriteBoolean(property.Name, false);
                    storeWritten = true;
                }
                else if (property.NameEquals("prompt_cache_key") && requestMetadata?.PromptCacheKey is { } cacheKey)
                {
                    writer.WriteString(property.Name, ClampPromptCacheKey(cacheKey));
                    cacheKeyWritten = true;
                }
                else if (property.NameEquals("max_output_tokens") && property.Value.ValueKind == JsonValueKind.Number &&
                    property.Value.TryGetInt32(out var maxOutputTokens) && maxOutputTokens < 16)
                    writer.WriteNumber(property.Name, 16);
                else if (property.NameEquals("reasoning") && property.Value.ValueKind == JsonValueKind.Object)
                    WriteReasoning(writer, property.Value);
                else if (property.NameEquals("include") && hasReasoning && property.Value.ValueKind == JsonValueKind.Array)
                {
                    WriteInclude(writer, property.Value);
                    includeWritten = true;
                }
                else property.WriteTo(writer);
            }
            if (!storeWritten) writer.WriteBoolean("store", false);
            if (!cacheKeyWritten && requestMetadata?.PromptCacheKey is { Length: > 0 } promptCacheKey)
                writer.WriteString("prompt_cache_key", ClampPromptCacheKey(promptCacheKey));
            if (hasReasoning && !includeWritten) WriteInclude(writer, null);
            writer.WriteEndObject();
        }

        var mapped = new ByteArrayContent(buffer.ToArray());
        foreach (var header in content.Headers)
            if (!header.Key.Equals("Content-Length", StringComparison.OrdinalIgnoreCase))
                mapped.Headers.TryAddWithoutValidation(header.Key, header.Value);
        request.Content = mapped;
        content.Dispose();
    }

    private static void WriteReasoning(Utf8JsonWriter writer, JsonElement reasoning)
    {
        writer.WritePropertyName("reasoning");
        writer.WriteStartObject();
        foreach (var property in reasoning.EnumerateObject())
            if (!property.NameEquals("summary")) property.WriteTo(writer);
        writer.WriteString("summary", "auto");
        writer.WriteEndObject();
    }

    private static void WriteInclude(Utf8JsonWriter writer, JsonElement? current)
    {
        writer.WritePropertyName("include");
        writer.WriteStartArray();
        var hasEncryptedContent = false;
        if (current is { ValueKind: JsonValueKind.Array } values)
        {
            foreach (var value in values.EnumerateArray())
            {
                if (value.ValueKind == JsonValueKind.String && value.GetString() == "reasoning.encrypted_content")
                    hasEncryptedContent = true;
                value.WriteTo(writer);
            }
        }
        if (!hasEncryptedContent) writer.WriteStringValue("reasoning.encrypted_content");
        writer.WriteEndArray();
    }

    private static string ClampPromptCacheKey(string value) => value.Length <= 64 ? value : value[..64];
}
