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
            request.Content is { Headers.ContentType.MediaType: "application/json" } content)
        {
            var body = await content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
            if (JsonNode.Parse(body) is JsonObject requestBody)
            {
                var changed = false;
                var extendedReasoningEffort = GetExtendedReasoningEffort();
                if (requestContext.Current is { ValueKind: JsonValueKind.Object } parameters)
                {
                    foreach (var parameter in parameters.EnumerateObject())
                        requestBody[parameter.Name] = JsonNode.Parse(parameter.Value.GetRawText());
                    changed = true;
                }
                if (extendedReasoningEffort is { } reasoningEffort &&
                    request.RequestUri?.AbsolutePath.EndsWith("/responses", StringComparison.Ordinal) == true &&
                    requestBody["reasoning"] is JsonObject reasoning)
                {
                    reasoning["effort"] = reasoningEffort;
                    changed = true;
                }
                if (extendedReasoningEffort is { } completionReasoningEffort &&
                    request.RequestUri?.AbsolutePath.EndsWith("/chat/completions", StringComparison.Ordinal) == true &&
                    requestBody.ContainsKey("reasoning_effort"))
                {
                    requestBody["reasoning_effort"] = completionReasoningEffort;
                    changed = true;
                }
                if (requestContext.ThinkingLevel == "off" && requestContext.Model is { Reasoning: true } model &&
                    request.RequestUri?.AbsolutePath.EndsWith("/responses", StringComparison.Ordinal) == true &&
                    GetThinkingOffValue(model) is { } offValue)
                {
                    requestBody["reasoning"] = new JsonObject { ["effort"] = offValue };
                    changed = true;
                }
                if (changed)
                {
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
        }

        return await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
    }

    private static bool IsSupportedRequest(Uri? uri) => uri is not null &&
        (uri.AbsolutePath.EndsWith("/chat/completions", StringComparison.Ordinal) ||
         uri.AbsolutePath.EndsWith("/responses", StringComparison.Ordinal));

    private static string? GetThinkingOffValue(ModelDescriptor model)
    {
        if (model.ThinkingLevelMap is not { ValueKind: JsonValueKind.Object } map ||
            !map.TryGetProperty("off", out var value)) return "none";
        return value.ValueKind == JsonValueKind.Null ? null : value.ValueKind == JsonValueKind.String
            ? value.GetString() : "none";
    }

    private string? GetExtendedReasoningEffort()
    {
        var requestedLevel = requestContext.ThinkingLevel;
        if (requestedLevel is not ("xhigh" or "max") ||
            requestContext.Model is not { Reasoning: true } model)
            return null;

        var effectiveLevel = ThinkingLevels.ValidateForModel(requestedLevel, model.Reasoning,
            model.ThinkingLevelMap);
        if (effectiveLevel == "off") return null;
        if (model.ThinkingLevelMap is { ValueKind: JsonValueKind.Object } map &&
            map.TryGetProperty(effectiveLevel, out var mapped) && mapped.ValueKind == JsonValueKind.String)
            return mapped.GetString();
        return effectiveLevel;
    }

}

internal sealed class AnthropicThinkingRequestHandler(SamplingParametersRequestContext requestContext)
    : DelegatingHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        if (request.Method == HttpMethod.Post &&
            request.RequestUri?.AbsolutePath.EndsWith("/messages", StringComparison.Ordinal) == true &&
            request.Content is { Headers.ContentType.MediaType: "application/json" } content &&
            requestContext.ThinkingLevel == "off" && requestContext.Model is { Reasoning: true } model &&
            !HasThinkingOffDisabled(model))
        {
            var body = await content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
            if (JsonNode.Parse(body) is JsonObject requestBody)
            {
                requestBody["thinking"] = new JsonObject { ["type"] = "disabled" };
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

    private static bool HasThinkingOffDisabled(ModelDescriptor model) =>
        model.ThinkingLevelMap is { ValueKind: JsonValueKind.Object } map &&
        map.TryGetProperty("off", out var value) && value.ValueKind == JsonValueKind.Null;
}
