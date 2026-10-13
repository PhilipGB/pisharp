using System.Text.Json;
using System.Text.Json.Nodes;

namespace PiSharp.Cli;

/// <summary>Removes adapter-generated schema defaults that current Pi omits from tool declarations.</summary>
internal sealed class OpenAiToolSchemaCompatibilityHandler(HttpMessageHandler innerHandler) : DelegatingHandler(innerHandler)
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        if (request.RequestUri?.AbsolutePath.EndsWith("/chat/completions", StringComparison.Ordinal) == true &&
            request.Content?.Headers.ContentType?.MediaType == "application/json")
        {
            var content = request.Content;
            var bytes = await content.ReadAsByteArrayAsync(cancellationToken);
            var projected = ProjectToolSchemas(bytes);
            if (!ReferenceEquals(projected, bytes))
            {
                var replacement = new ByteArrayContent(projected);
                foreach (var header in content.Headers)
                {
                    if (!string.Equals(header.Key, "Content-Length", StringComparison.OrdinalIgnoreCase))
                        replacement.Headers.TryAddWithoutValidation(header.Key, header.Value);
                }
                request.Content = replacement;
                content.Dispose();
            }
        }

        return await base.SendAsync(request, cancellationToken);
    }

    internal static byte[] ProjectToolSchemas(byte[] requestBody)
    {
        var root = JsonNode.Parse(requestBody);
        if (root is not JsonObject request || request["tools"] is not JsonArray tools) return requestBody;

        var changed = false;
        foreach (var tool in tools)
        {
            if (tool is not JsonObject toolObject || toolObject["function"] is not JsonObject function ||
                function["parameters"] is not JsonObject parameters)
                continue;

            if (parameters["required"] is JsonArray required && required.Count == 0)
            {
                parameters.Remove("required");
                changed = true;
            }

            if (function["name"]?.GetValue<string>() is ("read" or "bash" or "write" or "edit") &&
                parameters["additionalProperties"] is JsonValue additionalProperties &&
                additionalProperties.TryGetValue<bool>(out var allowAdditionalProperties) && !allowAdditionalProperties)
            {
                parameters.Remove("additionalProperties");
                changed = true;
            }
        }

        return changed ? JsonSerializer.SerializeToUtf8Bytes(root) : requestBody;
    }
}
