using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.AI;

namespace PiSharp.Cli;

internal static class PiMessagesRequestMapper
{
    internal const string MetadataPrefix = "pisharp.piMessages.";
    internal const string TextSignatureKey = MetadataPrefix + "textSignature";
    internal const string TextSignaturesKey = MetadataPrefix + "textSignatures";
    internal const string ThinkingSignaturesKey = MetadataPrefix + "thinkingSignatures";
    internal const string RedactedThinkingKey = MetadataPrefix + "redactedThinking";
    internal const string UsageKey = MetadataPrefix + "usage";

    private static readonly JsonSerializerOptions s_jsonOptions = new(JsonSerializerDefaults.Web);

    internal static JsonObject Build(string modelId, IEnumerable<ChatMessage> history, ChatOptions? options)
    {
        var messages = new JsonArray();
        var tools = GetTools(options);
        if (!string.IsNullOrEmpty(options?.Instructions) || tools.Count > 0)
        {
            var system = new JsonObject
            {
                ["role"] = "system",
                ["content"] = options?.Instructions ?? string.Empty,
                ["timestamp"] = 0
            };
            if (tools.Count > 0) system["toolsAdded"] = tools;
            messages.Add(system);
        }

        foreach (var message in history)
        {
            if (IsSystemMessage(message))
            {
                if (message.Contents.Any(content => content is not TextContent))
                    throw new NotSupportedException("Pi Messages system instructions must be text content.");
                messages.Add(new JsonObject
                {
                    ["role"] = "system",
                    ["content"] = message.Text,
                    ["timestamp"] = Timestamp(message, system: true)
                });
            }
            else if (message.Role == ChatRole.User)
            {
                messages.Add(BuildUserMessage(message));
            }
            else if (message.Role == ChatRole.Assistant)
            {
                messages.Add(BuildAssistantMessage(modelId, message));
            }
            else if (message.Role == ChatRole.Tool)
            {
                foreach (var result in message.Contents.OfType<FunctionResultContent>())
                    messages.Add(BuildToolResultMessage(message, result));
            }
            else
            {
                messages.Add(BuildUserMessage(message));
            }
        }

        var requestOptions = new JsonObject();
        Add(requestOptions, "temperature", options?.Temperature);
        Add(requestOptions, "maxTokens", options?.MaxOutputTokens);
        Add(requestOptions, "reasoning", ReadString(options?.AdditionalProperties, "piMessages.reasoning") ??
            MapReasoning(options?.Reasoning));
        Add(requestOptions, "cacheRetention", ResolveCacheRetention(options));
        Add(requestOptions, "sessionId", options?.ConversationId ??
            ReadString(options?.AdditionalProperties, "piMessages.sessionId"));
        Add(requestOptions, "toolChoice", MapToolChoice(options));

        return new JsonObject
        {
            ["model"] = modelId,
            ["context"] = new JsonObject { ["messages"] = messages },
            ["options"] = requestOptions
        };
    }

    private static JsonObject BuildUserMessage(ChatMessage message)
    {
        var contents = new JsonArray();
        foreach (var content in message.Contents)
        {
            switch (content)
            {
                case TextContent text:
                    contents.Add(new JsonObject { ["type"] = "text", ["text"] = text.Text });
                    break;
                case DataContent data:
                    var imageData = data.Data;
                    if (!data.MediaType.StartsWith("image/", StringComparison.OrdinalIgnoreCase) || imageData.IsEmpty)
                        throw new NotSupportedException("Pi Messages requires image content to be embedded data.");
                    contents.Add(new JsonObject
                    {
                        ["type"] = "image",
                        ["data"] = Convert.ToBase64String(imageData.Span),
                        ["mimeType"] = data.MediaType
                    });
                    break;
                default:
                    throw new NotSupportedException($"Pi Messages does not support user content '{content.GetType().Name}'.");
            }
        }

        JsonNode contentValue = contents.Count == 1 && contents[0] is JsonObject single &&
            single["type"]?.GetValue<string>() == "text"
                ? JsonValue.Create(single["text"]?.GetValue<string>() ?? string.Empty)!
                : contents;
        return new JsonObject
        {
            ["role"] = "user",
            ["content"] = contentValue,
            ["timestamp"] = Timestamp(message)
        };
    }

    private static JsonObject BuildAssistantMessage(string modelId, ChatMessage message)
    {
        var content = new JsonArray();
        var textSignatures = GetStoredJson(message.AdditionalProperties, TextSignaturesKey) as JsonObject;
        var thinkingSignatures = GetStoredJson(message.AdditionalProperties, ThinkingSignaturesKey) as JsonObject;
        var redactedThinking = GetStoredJson(message.AdditionalProperties, RedactedThinkingKey) as JsonObject;
        var contentIndex = 0;
        foreach (var item in message.Contents)
        {
            switch (item)
            {
                case TextContent text:
                    {
                        var block = new JsonObject { ["type"] = "text", ["text"] = text.Text };
                        Add(block, "textSignature", ReadString(text.AdditionalProperties, TextSignatureKey) ??
                            ReadIndexedString(textSignatures, contentIndex));
                        content.Add(block);
                        contentIndex++;
                        break;
                    }
                case TextReasoningContent reasoning:
                    {
                        var block = new JsonObject { ["type"] = "thinking", ["thinking"] = reasoning.Text };
                        Add(block, "thinkingSignature", reasoning.ProtectedData ??
                            ReadIndexedString(thinkingSignatures, contentIndex));
                        if (ReadBoolean(reasoning.AdditionalProperties, RedactedThinkingKey) == true ||
                            ReadIndexedBoolean(redactedThinking, contentIndex))
                            block["redacted"] = true;
                        content.Add(block);
                        contentIndex++;
                        break;
                    }
                case FunctionCallContent call:
                    content.Add(new JsonObject
                    {
                        ["type"] = "toolCall",
                        ["id"] = call.CallId,
                        ["name"] = call.Name,
                        ["arguments"] = ToJsonObject(call.Arguments)
                    });
                    contentIndex++;
                    break;
            }
        }

        var usage = GetStoredJson(message.AdditionalProperties, UsageKey) ?? BuildUsage(message);
        var assistantMessage = new JsonObject
        {
            ["role"] = "assistant",
            ["content"] = content,
            ["api"] = ReadString(message.AdditionalProperties, MetadataPrefix + "api") ?? "pi-messages",
            ["provider"] = ReadString(message.AdditionalProperties, MetadataPrefix + "provider") ?? string.Empty,
            ["model"] = ReadString(message.AdditionalProperties, MetadataPrefix + "model") ?? modelId,
            ["usage"] = usage,
            ["stopReason"] = ReadString(message.AdditionalProperties, MetadataPrefix + "stopReason") ??
                (message.Contents.OfType<FunctionCallContent>().Any() ? "toolUse" : "stop"),
            ["timestamp"] = Timestamp(message)
        };
        Add(assistantMessage, "responseId", ReadString(message.AdditionalProperties, MetadataPrefix + "responseId"));
        Add(assistantMessage, "responseModel", ReadString(message.AdditionalProperties, MetadataPrefix + "responseModel"));
        Add(assistantMessage, "providerThinkingLevel", ReadString(message.AdditionalProperties,
            MetadataPrefix + "providerThinkingLevel"));
        Add(assistantMessage, "errorMessage", ReadString(message.AdditionalProperties, MetadataPrefix + "errorMessage"));
        if (GetStoredJson(message.AdditionalProperties, MetadataPrefix + "rewrite") is { } rewrite)
        {
            assistantMessage["diagnostics"] = new JsonArray(new JsonObject
            {
                ["type"] = "pi_messages_rewrite",
                ["timestamp"] = Timestamp(message),
                ["details"] = rewrite
            });
        }
        return assistantMessage;
    }

    private static JsonObject BuildToolResultMessage(ChatMessage message, FunctionResultContent result)
    {
        var content = new JsonArray();
        foreach (var item in message.Contents)
        {
            if (ReferenceEquals(item, result)) continue;
            switch (item)
            {
                case TextContent text:
                    content.Add(new JsonObject { ["type"] = "text", ["text"] = text.Text });
                    break;
                case DataContent data when data.MediaType.StartsWith("image/", StringComparison.OrdinalIgnoreCase):
                    var imageData = data.Data;
                    if (imageData.IsEmpty)
                        throw new NotSupportedException("Pi Messages requires image content to be embedded data.");
                    content.Add(new JsonObject
                    {
                        ["type"] = "image",
                        ["data"] = Convert.ToBase64String(imageData.Span),
                        ["mimeType"] = data.MediaType
                    });
                    break;
                case ErrorContent error:
                    content.Add(new JsonObject { ["type"] = "text", ["text"] = error.Message });
                    break;
            }
        }

        JsonNode? details = null;
        if (result.Result is string textResult)
            content.Add(new JsonObject { ["type"] = "text", ["text"] = textResult });
        else if (result.Result is not null)
        {
            details = ToJsonNode(result.Result);
            content.Add(new JsonObject { ["type"] = "text", ["text"] = details!.ToJsonString(s_jsonOptions) });
        }

        var messageNode = new JsonObject
        {
            ["role"] = "toolResult",
            ["toolCallId"] = result.CallId,
            ["toolName"] = message.AuthorName ?? string.Empty,
            ["content"] = content,
            ["isError"] = result.Exception is not null,
            ["timestamp"] = Timestamp(message)
        };
        if (details is JsonObject or JsonArray) messageNode["details"] = details;
        return messageNode;
    }

    private static JsonObject BuildUsage(ChatMessage message)
    {
        var details = message.Contents.OfType<UsageContent>().LastOrDefault()?.Details;
        var usage = new JsonObject
        {
            ["input"] = details?.InputTokenCount ?? 0,
            ["output"] = details?.OutputTokenCount ?? 0,
            ["cacheRead"] = details?.CachedInputTokenCount ?? 0,
            ["cacheWrite"] = ReadAdditionalCount(details, "piMessages.cacheWrite") ?? 0,
            ["totalTokens"] = details?.TotalTokenCount ?? 0,
            ["cost"] = new JsonObject
            {
                ["input"] = 0,
                ["output"] = 0,
                ["cacheRead"] = 0,
                ["cacheWrite"] = 0,
                ["total"] = 0
            }
        };
        return usage;
    }

    private static JsonArray GetTools(ChatOptions? options)
    {
        var tools = new JsonArray();
        foreach (var declaration in options?.Tools?.OfType<AIFunctionDeclaration>() ?? [])
        {
            var parameters = declaration.JsonSchema is { } schema
                ? JsonNode.Parse(schema.GetRawText()) as JsonObject
                : new JsonObject { ["type"] = "object", ["properties"] = new JsonObject() };
            tools.Add(new JsonObject
            {
                ["name"] = declaration.Name,
                ["description"] = declaration.Description,
                ["parameters"] = parameters ?? new JsonObject { ["type"] = "object" }
            });
        }
        return tools;
    }

    private static bool IsSystemMessage(ChatMessage message) => message.Role == ChatRole.System ||
        string.Equals(message.Role.Value, "developer", StringComparison.OrdinalIgnoreCase);

    private static long Timestamp(ChatMessage message, bool system = false) =>
        system ? message.CreatedAt?.ToUnixTimeMilliseconds() ?? 0 :
        message.CreatedAt?.ToUnixTimeMilliseconds() ?? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    private static string? MapReasoning(ReasoningOptions? reasoning) => reasoning?.Effort switch
    {
        ReasoningEffort.Low => "low",
        ReasoningEffort.Medium => "medium",
        ReasoningEffort.High => "high",
        ReasoningEffort.ExtraHigh => "xhigh",
        _ => null
    };

    private static string? ResolveCacheRetention(ChatOptions? options)
    {
        var explicitValue = ReadString(options?.AdditionalProperties, "piMessages.cacheRetention");
        if (explicitValue is "none" or "short" or "long") return explicitValue;
        return Environment.GetEnvironmentVariable("PI_CACHE_RETENTION") == "long" ? "long" : null;
    }

    private static string? MapToolChoice(ChatOptions? options) => options?.ToolMode switch
    {
        null => null,
        NoneChatToolMode => "none",
        RequiredChatToolMode => "required",
        _ => "auto"
    };

    private static void Add(JsonObject target, string name, string? value)
    {
        if (value is not null) target[name] = value;
    }

    private static void Add(JsonObject target, string name, float? value)
    {
        if (value is not null) target[name] = value.Value;
    }

    private static void Add(JsonObject target, string name, int? value)
    {
        if (value is not null) target[name] = value.Value;
    }

    internal static string? ReadString(AdditionalPropertiesDictionary? properties, string name)
    {
        if (properties?.TryGetValue(name, out var value) != true) return null;
        return value switch
        {
            string text => text,
            JsonElement { ValueKind: JsonValueKind.String } element => element.GetString(),
            JsonValue jsonValue when jsonValue.TryGetValue<string>(out var text) => text,
            _ => null
        };
    }

    internal static bool? ReadBoolean(AdditionalPropertiesDictionary? properties, string name)
    {
        if (properties?.TryGetValue(name, out var value) != true) return null;
        return value switch
        {
            bool boolean => boolean,
            JsonElement { ValueKind: JsonValueKind.True } => true,
            JsonElement { ValueKind: JsonValueKind.False } => false,
            JsonValue jsonValue when jsonValue.TryGetValue<bool>(out var boolean) => boolean,
            _ => null
        };
    }

    internal static JsonNode? GetStoredJson(AdditionalPropertiesDictionary? properties, string name)
    {
        if (properties?.TryGetValue(name, out var value) != true || value is null) return null;
        return ToJsonNode(value);
    }

    private static long? ReadAdditionalCount(UsageDetails? details, string name) =>
        details?.AdditionalCounts?.TryGetValue(name, out var value) == true ? value : null;

    private static string? ReadIndexedString(JsonObject? values, int index) =>
        values?[index.ToString(System.Globalization.CultureInfo.InvariantCulture)] is JsonValue value &&
        value.TryGetValue<string>(out var text) ? text : null;

    private static bool ReadIndexedBoolean(JsonObject? values, int index) =>
        values?[index.ToString(System.Globalization.CultureInfo.InvariantCulture)] is JsonValue value &&
        value.TryGetValue<bool>(out var boolean) && boolean;

    private static JsonObject ToJsonObject(object? value) => ToJsonNode(value) as JsonObject ?? new JsonObject();

    internal static JsonNode? ToJsonNode(object? value)
    {
        if (value is null) return null;
        if (value is JsonNode node) return node.DeepClone();
        if (value is JsonElement element) return JsonNode.Parse(element.GetRawText());
        return JsonSerializer.SerializeToNode(value, value.GetType(), s_jsonOptions);
    }
}
