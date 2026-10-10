using System.Text.Json;
using System.Text.Json.Nodes;
using PiSharp.Runtime.Providers;

namespace PiSharp.Cli;

/// <summary>Projects the OpenAI Completions request defaults used by current Pi.</summary>
internal sealed class OpenAiCompletionsRequestCompatibilityHandler : DelegatingHandler
{
    private readonly ModelDescriptor _model;
    private readonly bool _supportsStore;
    private readonly string _maxTokensField;

    public OpenAiCompletionsRequestCompatibilityHandler(ModelDescriptor model, string providerId, Uri endpoint,
        JsonElement? providerCompatibility, HttpMessageHandler innerHandler) : base(innerHandler)
    {
        _model = model;
        var detected = DetectCompatibility(providerId, endpoint);
        _supportsStore = ReadBoolean(model.Compatibility, "supportsStore") ??
            ReadBoolean(providerCompatibility, "supportsStore") ?? detected.SupportsStore;
        _maxTokensField = ReadString(model.Compatibility, "maxTokensField") ??
            ReadString(providerCompatibility, "maxTokensField") ?? detected.MaxTokensField;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        if (request.Method == HttpMethod.Post &&
            request.RequestUri?.AbsolutePath.EndsWith("/chat/completions", StringComparison.Ordinal) == true &&
            request.Content?.Headers.ContentType?.MediaType == "application/json")
        {
            var content = request.Content;
            var body = await content.ReadAsByteArrayAsync(cancellationToken);
            var projected = ProjectRequest(body, _model, _supportsStore, _maxTokensField);
            if (!ReferenceEquals(projected, body))
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

    internal static byte[] ProjectRequest(byte[] body, ModelDescriptor model, bool supportsStore,
        string maxTokensField)
    {
        if (JsonNode.Parse(body) is not JsonObject request) return body;
        var changed = false;

        if (request["messages"] is JsonArray messages)
        {
            foreach (var message in messages)
            {
                if (message is not JsonObject messageObject ||
                    !HasStringValue(messageObject["role"], "user") ||
                    messageObject["content"] is not JsonValue content ||
                    !content.TryGetValue<string>(out var text))
                    continue;

                messageObject["content"] = new JsonArray(new JsonObject
                {
                    ["type"] = "text",
                    ["text"] = text
                });
                changed = true;
            }
        }

        if (supportsStore && !HasBooleanValue(request["store"], false))
        {
            request["store"] = false;
            changed = true;
        }

        if (HasStringValue(request["tool_choice"], "auto"))
        {
            request.Remove("tool_choice");
            changed = true;
        }

        var outputTokens = request["max_completion_tokens"]?.DeepClone() ?? request["max_tokens"]?.DeepClone();
        if (outputTokens is null && model.MaxOutputTokens is > 0)
            outputTokens = JsonValue.Create(model.MaxOutputTokens.Value);

        if (outputTokens is not null)
        {
            var target = maxTokensField == "max_tokens" ? "max_tokens" : "max_completion_tokens";
            var other = target == "max_tokens" ? "max_completion_tokens" : "max_tokens";
            if (request[other] is not null || request[target] is null)
            {
                request.Remove(other);
                request[target] = outputTokens;
                changed = true;
            }
        }

        return changed ? JsonSerializer.SerializeToUtf8Bytes(request) : body;
    }

    private static (bool SupportsStore, string MaxTokensField) DetectCompatibility(string providerId, Uri endpoint)
    {
        var provider = providerId.ToLowerInvariant();
        var baseUrl = endpoint.ToString().ToLowerInvariant();
        var zai = provider is "zai" or "zai-coding-cn" || Contains(baseUrl, "api.z.ai") ||
            Contains(baseUrl, "open.bigmodel.cn");
        var together = provider == "together" || Contains(baseUrl, "api.together.ai") ||
            Contains(baseUrl, "api.together.xyz");
        var moonshot = provider is "moonshotai" or "moonshotai-cn" || Contains(baseUrl, "api.moonshot.");
        var cloudflareWorkers = provider == "cloudflare-workers-ai" || Contains(baseUrl, "api.cloudflare.com");
        var cloudflareGateway = provider == "cloudflare-ai-gateway" ||
            Contains(baseUrl, "gateway.ai.cloudflare.com");
        var nvidia = provider == "nvidia" || Contains(baseUrl, "integrate.api.nvidia.com");
        var antLing = provider == "ant-ling" || Contains(baseUrl, "api.ant-ling.com");
        var cerebras = provider == "cerebras" || Contains(baseUrl, "cerebras.ai");
        var deepSeek = provider == "deepseek" || Contains(baseUrl, "deepseek.com");
        var grok = provider == "xai" || Contains(baseUrl, "api.x.ai");
        var chutes = Contains(baseUrl, "chutes.ai");
        var openCode = provider == "opencode" || Contains(baseUrl, "opencode.ai");

        var nonstandard = nvidia || cerebras || grok || together || chutes || deepSeek || zai || moonshot ||
            openCode || cloudflareWorkers || cloudflareGateway || antLing;
        var maxTokensField = chutes || deepSeek || moonshot || cloudflareGateway || together || nvidia || antLing || zai
            ? "max_tokens" : "max_completion_tokens";
        return (!nonstandard, maxTokensField);
    }

    private static bool Contains(string value, string fragment) => value.Contains(fragment, StringComparison.Ordinal);

    private static bool? ReadBoolean(JsonElement? compatibility, string property)
    {
        if (compatibility is not { ValueKind: JsonValueKind.Object } json ||
            !json.TryGetProperty(property, out var value) ||
            value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            return null;
        return value.GetBoolean();
    }

    private static string? ReadString(JsonElement? compatibility, string property)
    {
        if (compatibility is not { ValueKind: JsonValueKind.Object } json ||
            !json.TryGetProperty(property, out var value) || value.ValueKind != JsonValueKind.String)
            return null;
        return value.GetString();
    }

    private static bool HasStringValue(JsonNode? node, string expected) =>
        node is JsonValue value && value.TryGetValue<string>(out var actual) &&
        string.Equals(actual, expected, StringComparison.Ordinal);

    private static bool HasBooleanValue(JsonNode? node, bool expected) =>
        node is JsonValue value && value.TryGetValue<bool>(out var actual) && actual == expected;
}
