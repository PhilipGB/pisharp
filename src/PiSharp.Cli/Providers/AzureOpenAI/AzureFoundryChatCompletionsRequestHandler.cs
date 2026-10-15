using System.Text.Json;
using System.Text.Json.Nodes;

namespace PiSharp.Cli;

/// <summary>Maps catalog model IDs to Azure Foundry deployments on its OpenAI v1 Completions route.</summary>
internal sealed class AzureFoundryChatCompletionsRequestHandler(
    AzureOpenAiProviderOptions options, HttpMessageHandler innerHandler) : DelegatingHandler(innerHandler)
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        if (request.Method == HttpMethod.Post &&
            request.RequestUri?.AbsolutePath.EndsWith("/chat/completions", StringComparison.Ordinal) == true &&
            request.Content is { Headers.ContentType.MediaType: "application/json" } content)
        {
            var body = await content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
            if (JsonNode.Parse(body) is JsonObject requestBody && requestBody["model"] is JsonValue modelValue &&
                modelValue.TryGetValue<string>(out var modelId) &&
                options.DeploymentNames.TryGetValue(modelId, out var deploymentName))
            {
                requestBody["model"] = deploymentName;
                var replacement = new ByteArrayContent(JsonSerializer.SerializeToUtf8Bytes(requestBody));
                foreach (var header in content.Headers)
                    if (!header.Key.Equals("Content-Length", StringComparison.OrdinalIgnoreCase))
                        replacement.Headers.TryAddWithoutValidation(header.Key, header.Value);
                request.Content = replacement;
                content.Dispose();
            }
        }

        return await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
    }
}
