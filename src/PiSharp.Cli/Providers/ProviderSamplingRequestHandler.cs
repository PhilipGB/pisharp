using System.Text.Json;
using System.Text.Json.Nodes;
using PiSharp.Runtime.Providers;

namespace PiSharp.Cli;

/// <summary>Applies resolved sampling parameters to supported OpenAI-compatible JSON requests.</summary>
internal sealed class ProviderSamplingRequestHandler(SamplingParametersRequestContext requestContext,
    HttpMessageHandler innerHandler) : DelegatingHandler(innerHandler)
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        if (request.Method == HttpMethod.Post && IsSupportedRequest(request.RequestUri) &&
            request.Content is { Headers.ContentType.MediaType: "application/json" } content &&
            requestContext.Current is { ValueKind: JsonValueKind.Object } parameters)
        {
            var body = await content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
            if (JsonNode.Parse(body) is JsonObject requestBody)
            {
                foreach (var parameter in parameters.EnumerateObject())
                    requestBody[parameter.Name] = JsonNode.Parse(parameter.Value.GetRawText());

                var replacement = new ByteArrayContent(JsonSerializer.SerializeToUtf8Bytes(requestBody));
                foreach (var header in content.Headers)
                {
                    if (!header.Key.Equals("Content-Length", StringComparison.OrdinalIgnoreCase))
                        replacement.Headers.TryAddWithoutValidation(header.Key, header.Value);
                }
                request.Content = replacement;
                content.Dispose();
            }
        }

        return await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
    }

    private static bool IsSupportedRequest(Uri? uri) => uri is not null &&
        (uri.AbsolutePath.EndsWith("/chat/completions", StringComparison.Ordinal) ||
         uri.AbsolutePath.EndsWith("/responses", StringComparison.Ordinal));
}
