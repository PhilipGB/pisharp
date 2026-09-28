using System.Net;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.AI;
using PiSharp.Runtime.Providers;

namespace PiSharp.Cli;

internal static class GoogleGenAiChatClientFactory
{
    public static IChatClient Create(ModelSelection selection)
    {
        var handler = new ProviderWireActivityHandler(new HttpClientHandler { AllowAutoRedirect = false });
        var http = new HttpClient(handler, disposeHandler: true) { Timeout = Timeout.InfiniteTimeSpan };
        return new GoogleGenAiChatClient(http, selection.Connection.Endpoint ?? selection.Provider.Endpoint,
            selection.ApiKey, selection.Model);
    }
}

/// <summary>Maps MAF chat history to Google's generateContent SSE protocol.</summary>
internal sealed class GoogleGenAiChatClient(HttpClient http, Uri endpoint, string apiKey, ModelDescriptor model)
    : IChatClient
{
    private const string SignatureKey = "pisharp.google.thoughtSignature";
    private const string SignatureProviderKey = "pisharp.google.signatureProvider";
    private const string SignatureModelKey = "pisharp.google.signatureModel";
    private static readonly JsonSerializerOptions s_jsonOptions = new(JsonSerializerDefaults.Web);
    private readonly string _modelId = model.Id;

    public async Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var updates = GetStreamingResponseAsync(messages, options, cancellationToken);
        return await updates.ToChatResponseAsync(cancellationToken).ConfigureAwait(false);
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
        ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var request = BuildRequest(messages.ToArray(), options);
        using var message = new HttpRequestMessage(HttpMethod.Post, BuildStreamUri(endpoint, _modelId));
        message.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        message.Headers.TryAddWithoutValidation("x-goog-api-key", apiKey);
        message.Headers.TryAddWithoutValidation("User-Agent", "PiSharp");
        message.Content = new StringContent(request.ToJsonString(), Encoding.UTF8, "application/json");

        using var response = await http.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw await CreateProviderExceptionAsync(response, apiKey, cancellationToken).ConfigureAwait(false);

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: false,
            bufferSize: 4096, leaveOpen: true);
        var eventData = new StringBuilder();
        string? line;
        var sawResponse = false;
        var responseId = Guid.NewGuid().ToString("N");
        while ((line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false)) is not null)
        {
            if (line.Length == 0)
            {
                if (eventData.Length > 0)
                {
                    var json = eventData.ToString();
                    eventData.Clear();
                    if (TryParseUpdate(json, responseId, out var update))
                    {
                        sawResponse = true;
                        responseId = update.ResponseId ?? responseId;
                        yield return update;
                    }
                }
                continue;
            }
            if (line[0] == ':' || !line.StartsWith("data:", StringComparison.Ordinal)) continue;
            var data = line.AsSpan(5);
            if (!data.IsEmpty && data[0] == ' ') data = data[1..];
            if (data.SequenceEqual("[DONE]")) continue;
            if (eventData.Length > 0) eventData.Append('\n');
            eventData.Append(data);
        }

        if (eventData.Length > 0 && TryParseUpdate(eventData.ToString(), responseId, out var lastUpdate))
        {
            sawResponse = true;
            yield return lastUpdate;
        }
        if (!sawResponse)
            throw new InvalidDataException("Google GenAI returned an empty streaming response.");
    }

    public object? GetService(Type serviceType, object? serviceKey = null) =>
        serviceType.IsInstanceOfType(this) ? this : null;

    public void Dispose() => http.Dispose();

    internal JsonObject BuildRequest(IReadOnlyList<ChatMessage> messages, ChatOptions? options)
    {
        var contents = new JsonArray();
        var systemParts = new List<string>();
        if (!string.IsNullOrWhiteSpace(options?.Instructions))
            systemParts.Add(SanitizeSurrogates(options.Instructions));
        var callNames = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var message in messages)
        {
            foreach (var call in message.Contents.OfType<FunctionCallContent>())
                callNames[NormalizeCallId(call.CallId)] = call.Name;
        }

        foreach (var message in messages)
        {
            if (message.Role == ChatRole.System)
            {
                var text = string.Join("", message.Contents.OfType<TextContent>().Select(item => item.Text));
                if (!string.IsNullOrEmpty(text)) systemParts.Add(SanitizeSurrogates(text));
                continue;
            }

            var functionResults = message.Contents.OfType<FunctionResultContent>().ToArray();
            if (functionResults.Length > 0)
            {
                var last = contents.LastOrDefault() as JsonObject;
                var lastParts = last?["parts"] as JsonArray;
                var canAppend = last?["role"]?.GetValue<string>() == "user" &&
                    lastParts?.Any(part => part is JsonObject item && item["functionResponse"] is not null) == true;
                var userContent = canAppend ? last! : new JsonObject { ["role"] = "user", ["parts"] = new JsonArray() };
                var parts = (JsonArray)userContent["parts"]!;
                var imageTurns = new List<JsonObject>();
                foreach (var result in functionResults)
                {
                    var images = message.Contents.OfType<DataContent>().Where(IsImage).ToArray();
                    var responseText = GetResultText(result.Result);
                    if (responseText.Length == 0 && images.Length > 0) responseText = "(see attached image)";
                    var functionResponse = new JsonObject
                    {
                        ["name"] = callNames.GetValueOrDefault(NormalizeCallId(result.CallId), "unknown"),
                        ["response"] = new JsonObject
                        {
                            [result.Exception is null ? "output" : "error"] = result.Exception is null
                                ? JsonValue.Create(responseText)
                                : JsonValue.Create(result.Exception.Message)
                        }
                    };
                    if (RequiresToolCallId(_modelId)) functionResponse["id"] = NormalizeCallId(result.CallId);
                    if (images.Length > 0 && SupportsMultimodalFunctionResponse(_modelId))
                    {
                        var imageParts = new JsonArray();
                        foreach (var image in images) imageParts.Add(ToInlineData(image));
                        functionResponse["parts"] = imageParts;
                    }
                    parts.Add(new JsonObject { ["functionResponse"] = functionResponse });

                    if (images.Length > 0 && !SupportsMultimodalFunctionResponse(_modelId))
                    {
                        var imageTurnParts = new JsonArray { new JsonObject { ["text"] = "Tool result image:" } };
                        foreach (var image in images) imageTurnParts.Add(ToInlineData(image));
                        imageTurns.Add(new JsonObject { ["role"] = "user", ["parts"] = imageTurnParts });
                    }
                }
                if (!canAppend) contents.Add(userContent);
                foreach (var imageTurn in imageTurns) contents.Add(imageTurn);
                continue;
            }

            var partsForMessage = new JsonArray();
            foreach (var item in message.Contents)
            {
                switch (item)
                {
                    case TextContent text when message.Role == ChatRole.Assistant:
                        var textSignature = ReadSignature(text);
                        if (string.IsNullOrWhiteSpace(text.Text) && textSignature is null) break;
                        var textPart = new JsonObject { ["text"] = SanitizeSurrogates(text.Text) };
                        if (textSignature is not null) textPart["thoughtSignature"] = textSignature;
                        partsForMessage.Add(textPart);
                        break;
                    case TextContent userText:
                        partsForMessage.Add(new JsonObject { ["text"] = SanitizeSurrogates(userText.Text) });
                        break;
                    case TextReasoningContent reasoning when message.Role == ChatRole.Assistant:
                        var signature = IsSameModel(reasoning.AdditionalProperties) ? ReadObjectString(reasoning.ProtectedData) : null;
                        if (string.IsNullOrWhiteSpace(reasoning.Text) && !IsValidSignature(signature)) break;
                        var thinkingPart = new JsonObject { ["text"] = SanitizeSurrogates(reasoning.Text) };
                        if (IsSameModel(reasoning.AdditionalProperties)) thinkingPart["thought"] = true;
                        if (IsValidSignature(signature)) thinkingPart["thoughtSignature"] = signature;
                        partsForMessage.Add(thinkingPart);
                        break;
                    case DataContent data when IsImage(data):
                        partsForMessage.Add(ToInlineData(data));
                        break;
                    case FunctionCallContent call:
                        var callId = NormalizeCallId(call.CallId);
                        var functionCall = new JsonObject
                        {
                            ["name"] = call.Name,
                            ["args"] = JsonSerializer.SerializeToNode(call.Arguments ?? new Dictionary<string, object?>(), s_jsonOptions)
                                ?? new JsonObject()
                        };
                        if (RequiresToolCallId(_modelId)) functionCall["id"] = callId;
                        var callPart = new JsonObject { ["functionCall"] = functionCall };
                        var callSignature = ReadSignature(call);
                        if (callSignature is not null) callPart["thoughtSignature"] = callSignature;
                        partsForMessage.Add(callPart);
                        break;
                }
            }

            if (partsForMessage.Count == 0) continue;
            var role = message.Role == ChatRole.Assistant ? "model" : "user";
            contents.Add(new JsonObject { ["role"] = role, ["parts"] = partsForMessage });
        }

        var root = new JsonObject { ["contents"] = contents };
        if (systemParts.Count > 0)
            root["systemInstruction"] = new JsonObject
            {
                ["parts"] = new JsonArray(new JsonObject { ["text"] = string.Join("\n", systemParts) })
            };

        if (options?.Temperature is not null || options?.MaxOutputTokens is not null)
        {
            var generation = new JsonObject();
            if (options?.Temperature is { } temperature) generation["temperature"] = temperature;
            if (options?.MaxOutputTokens is { } maxOutputTokens) generation["maxOutputTokens"] = maxOutputTokens;
            root["generationConfig"] = generation;
        }

        AddThinkingConfig(root, options?.Reasoning);
        AddTools(root, options);
        return root;
    }

    private bool TryParseUpdate(string json, string responseId, out ChatResponseUpdate update)
    {
        update = null!;
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (root.TryGetProperty("error", out var error))
            throw new InvalidDataException("Google GenAI stream error: " + ReadString(error, "message"));

        if (root.TryGetProperty("responseId", out var id) && id.ValueKind == JsonValueKind.String)
            responseId = id.GetString() ?? responseId;
        var contents = new List<AIContent>();
        var candidate = root.TryGetProperty("candidates", out var candidates) && candidates.ValueKind == JsonValueKind.Array && candidates.GetArrayLength() > 0
            ? candidates[0] : default;
        if (candidate.ValueKind == JsonValueKind.Object && candidate.TryGetProperty("content", out var content) &&
            content.TryGetProperty("parts", out var responseParts) && responseParts.ValueKind == JsonValueKind.Array)
        {
            foreach (var part in responseParts.EnumerateArray())
            {
                var thought = part.TryGetProperty("thought", out var thoughtValue) && thoughtValue.ValueKind == JsonValueKind.True;
                var signature = ReadString(part, "thoughtSignature");
                if (part.TryGetProperty("text", out var textValue) && textValue.ValueKind == JsonValueKind.String)
                {
                    AIContent textContent = thought
                        ? new TextReasoningContent(textValue.GetString() ?? "") { ProtectedData = signature }
                        : new TextContent(textValue.GetString() ?? "");
                    MarkSignatureSource(textContent, signature);
                    contents.Add(textContent);
                }
                if (part.TryGetProperty("functionCall", out var call) && call.ValueKind == JsonValueKind.Object)
                {
                    var name = ReadString(call, "name") ?? "";
                    var callId = ReadString(call, "id");
                    if (string.IsNullOrWhiteSpace(callId)) callId = $"{name}_{Guid.NewGuid():N}";
                    var arguments = call.TryGetProperty("args", out var args) && args.ValueKind == JsonValueKind.Object
                        ? ToObjectDictionary(args) : new Dictionary<string, object?>();
                    var functionCall = new FunctionCallContent(callId, name, arguments);
                    MarkSignatureSource(functionCall, signature);
                    contents.Add(functionCall);
                }
            }
        }

        var updateCandidate = new ChatResponseUpdate(ChatRole.Assistant, contents)
        {
            ResponseId = responseId,
            MessageId = responseId,
            ModelId = _modelId
        };
        var hasUsage = false;
        if (root.TryGetProperty("usageMetadata", out var usage) && usage.ValueKind == JsonValueKind.Object)
        {
            var prompt = ReadLong(usage, "promptTokenCount");
            var cached = ReadLong(usage, "cachedContentTokenCount");
            var candidatesTokens = ReadLong(usage, "candidatesTokenCount");
            var thoughts = ReadLong(usage, "thoughtsTokenCount");
            var total = ReadLong(usage, "totalTokenCount");
            updateCandidate.Contents.Add(new UsageContent(new UsageDetails
            {
                InputTokenCount = prompt,
                OutputTokenCount = candidatesTokens + thoughts,
                CachedInputTokenCount = cached,
                ReasoningTokenCount = thoughts,
                TotalTokenCount = total
            }));
            hasUsage = true;
        }

        if (candidate.ValueKind == JsonValueKind.Object && candidate.TryGetProperty("finishReason", out var reason) &&
            reason.ValueKind == JsonValueKind.String)
        {
            var rawReason = reason.GetString() ?? "";
            var hasCalls = contents.Any(item => item is FunctionCallContent);
            updateCandidate.FinishReason = rawReason switch
            {
                "STOP" when hasCalls => ChatFinishReason.ToolCalls,
                "STOP" => ChatFinishReason.Stop,
                "MAX_TOKENS" => ChatFinishReason.Length,
                "SAFETY" or "PROHIBITED_CONTENT" or "SPII" or "IMAGE_SAFETY" or "IMAGE_PROHIBITED_CONTENT" or "IMAGE_RECITATION" => ChatFinishReason.ContentFilter,
                _ => new ChatFinishReason("error")
            };
            updateCandidate.AdditionalProperties ??= new AdditionalPropertiesDictionary();
            updateCandidate.AdditionalProperties["pisharp.google.rawFinishReason"] = rawReason;
        }

        if (contents.Count == 0 && !hasUsage && updateCandidate.FinishReason is null)
            return false;
        update = updateCandidate;
        return true;
    }

    private void AddThinkingConfig(JsonObject root, ReasoningOptions? reasoning)
    {
        if (model.Reasoning != true) return;
        var enabled = reasoning is not null;
        var requested = reasoning?.Effort switch
        {
            ReasoningEffort.Low => "low",
            ReasoningEffort.Medium => "medium",
            ReasoningEffort.High => "high",
            ReasoningEffort.ExtraHigh => "max",
            _ => "off"
        };
        if (model.ThinkingLevelMap is { ValueKind: JsonValueKind.Object } map)
        {
            var level = ThinkingLevels.ValidateForModel(enabled ? requested : "off", model.Reasoning, model.ThinkingLevelMap);
            var mapped = map.TryGetProperty(level, out var mappedValue) && mappedValue.ValueKind == JsonValueKind.String
                ? mappedValue.GetString()?.ToLowerInvariant() : level;
            var config = new JsonObject { ["includeThoughts"] = enabled };
            if (UsesThinkingLevel(_modelId)) config["thinkingLevel"] = ToApiThinkingLevel(mapped);
            else config["thinkingBudget"] = enabled ? GetThinkingBudget(_modelId, mapped ?? requested) : 0;
            AddThinkingConfigNode(root, config);
        }
        else if (UsesThinkingLevel(_modelId))
        {
            var level = enabled ? requested : "minimal";
            AddThinkingConfigNode(root, new JsonObject
            {
                ["includeThoughts"] = enabled,
                ["thinkingLevel"] = ToApiThinkingLevel(level)
            });
        }
        else
        {
            AddThinkingConfigNode(root, new JsonObject
            {
                ["includeThoughts"] = enabled,
                ["thinkingBudget"] = enabled ? GetThinkingBudget(_modelId, requested) : 0
            });
        }
    }

    private static void AddThinkingConfigNode(JsonObject root, JsonObject config)
    {
        var generation = root["generationConfig"] as JsonObject;
        if (generation is null)
        {
            generation = new JsonObject();
            root["generationConfig"] = generation;
        }
        generation["thinkingConfig"] = config;
    }

    private void AddTools(JsonObject root, ChatOptions? options)
    {
        var tools = options?.Tools?.OfType<AIFunctionDeclaration>().ToArray() ?? [];
        if (tools.Length == 0) return;
        var declarations = new JsonArray();
        foreach (var tool in tools)
        {
            var schema = tool.JsonSchema is { } jsonSchema
                ? JsonNode.Parse(jsonSchema.GetRawText())
                : new JsonObject { ["type"] = "object", ["properties"] = new JsonObject() };
            StripSchemaMetadata(schema);
            declarations.Add(new JsonObject
            {
                ["name"] = tool.Name,
                ["description"] = tool.Description,
                ["parametersJsonSchema"] = schema
            });
        }
        root["tools"] = new JsonArray(new JsonObject { ["functionDeclarations"] = declarations });
        var mode = options?.ToolMode is NoneChatToolMode ? "NONE" :
            options?.ToolMode is RequiredChatToolMode ? "ANY" : null;
        if (mode is not null)
            root["toolConfig"] = new JsonObject { ["functionCallingConfig"] = new JsonObject { ["mode"] = mode } };
    }

    private string? ReadSignature(AIContent content)
    {
        if (!IsSameModel(content.AdditionalProperties)) return null;
        return TryReadString(content.AdditionalProperties, SignatureKey, out var value) && IsValidSignature(value)
            ? value : null;
    }

    private void MarkSignatureSource(AIContent content, string? signature)
    {
        if (!IsValidSignature(signature)) return;
        content.AdditionalProperties ??= new AdditionalPropertiesDictionary();
        content.AdditionalProperties[SignatureKey] = signature!;
        content.AdditionalProperties[SignatureProviderKey] = "google";
        content.AdditionalProperties[SignatureModelKey] = _modelId;
    }

    private bool IsSameModel(AdditionalPropertiesDictionary? properties) =>
        TryReadString(properties, SignatureProviderKey, out var provider) && provider == "google" &&
        TryReadString(properties, SignatureModelKey, out var modelId) && modelId == _modelId;

    private static bool TryReadString(AdditionalPropertiesDictionary? properties, string key, out string value)
    {
        value = "";
        if (properties?.TryGetValue(key, out var raw) != true) return false;
        if (raw is string text) { value = text; return true; }
        if (raw is JsonElement { ValueKind: JsonValueKind.String } element)
        {
            value = element.GetString() ?? "";
            return true;
        }
        if (raw is JsonValue jsonValue && jsonValue.TryGetValue<string>(out var jsonText))
        {
            value = jsonText ?? "";
            return true;
        }
        return false;
    }

    private static string? ReadObjectString(object? value) => value switch
    {
        string text => text,
        JsonElement { ValueKind: JsonValueKind.String } element => element.GetString(),
        JsonValue jsonValue when jsonValue.TryGetValue<string>(out var text) => text,
        _ => null
    };

    private static JsonObject ToInlineData(DataContent content) => new()
    {
        ["inlineData"] = new JsonObject
        {
            ["mimeType"] = content.MediaType,
            ["data"] = Convert.ToBase64String(content.Data.Span)
        }
    };

    private static bool IsImage(DataContent content) => content.MediaType.StartsWith("image/", StringComparison.OrdinalIgnoreCase);

    private static bool RequiresToolCallId(string modelId) =>
        GetGeminiMajorVersion(modelId) is >= 3 || modelId.StartsWith("claude-", StringComparison.OrdinalIgnoreCase) ||
        modelId.StartsWith("gpt-oss-", StringComparison.OrdinalIgnoreCase);

    private static bool SupportsMultimodalFunctionResponse(string modelId) => GetGeminiMajorVersion(modelId) is >= 3;

    private static int? GetGeminiMajorVersion(string modelId)
    {
        const string prefix = "gemini-";
        if (!modelId.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return null;
        var digits = new string(modelId[prefix.Length..].TakeWhile(char.IsAsciiDigit).ToArray());
        return int.TryParse(digits, out var major) ? major : null;
    }

    private static string NormalizeCallId(string id)
    {
        var sanitized = new string(id.Select(character => char.IsAsciiLetterOrDigit(character) || character is '_' or '-'
            ? character : '_').Take(64).ToArray());
        return sanitized.Length == 0 ? "call" : sanitized;
    }

    private static void StripSchemaMetadata(JsonNode? schema)
    {
        if (schema is JsonObject obj)
        {
            foreach (var key in obj.Select(item => item.Key).Where(key => key is "$schema" or "$id" or "$anchor" or
                         "$dynamicAnchor" or "$vocabulary" or "$comment" or "$defs" or "definitions").ToArray())
                obj.Remove(key);
            foreach (var child in obj.Select(item => item.Value)) StripSchemaMetadata(child);
        }
        else if (schema is JsonArray array)
        {
            foreach (var child in array) StripSchemaMetadata(child);
        }
    }

    private static bool IsValidSignature(string? signature) => !string.IsNullOrEmpty(signature) &&
        signature.Length % 4 == 0 && signature.All(character => char.IsAsciiLetterOrDigit(character) || character is '+' or '/' or '=');

    private static string ToApiThinkingLevel(string? level) => level?.ToLowerInvariant() switch
    {
        "minimal" => "MINIMAL",
        "low" => "LOW",
        "medium" => "MEDIUM",
        "high" => "HIGH",
        _ => "THINKING_LEVEL_UNSPECIFIED"
    };

    private static int GetThinkingBudget(string modelId, string level)
    {
        if (modelId.Contains("2.5-pro", StringComparison.OrdinalIgnoreCase))
            return level switch { "minimal" => 128, "low" => 2048, "medium" => 8192, "high" or "max" => 32768, _ => -1 };
        if (modelId.Contains("2.5-flash-lite", StringComparison.OrdinalIgnoreCase))
            return level switch { "minimal" => 512, "low" => 2048, "medium" => 8192, "high" or "max" => 24576, _ => -1 };
        if (modelId.Contains("2.5-flash", StringComparison.OrdinalIgnoreCase))
            return level switch { "minimal" => 128, "low" => 2048, "medium" => 8192, "high" or "max" => 24576, _ => -1 };
        return -1;
    }

    private static bool UsesThinkingLevel(string modelId) =>
        modelId.Equals("gemini-flash-latest", StringComparison.OrdinalIgnoreCase) ||
        modelId.Equals("gemini-flash-lite-latest", StringComparison.OrdinalIgnoreCase) ||
        System.Text.RegularExpressions.Regex.IsMatch(modelId, "^gemini-3(?:\\.\\d+)?-(?:pro|flash)",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant) ||
        System.Text.RegularExpressions.Regex.IsMatch(modelId, "^gemma-?4", System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    private static string GetResultText(object? value) => value switch
    {
        null => "",
        string text => text,
        JsonElement { ValueKind: JsonValueKind.String } element => element.GetString() ?? "",
        JsonElement element => element.GetRawText(),
        _ => JsonSerializer.Serialize(value, s_jsonOptions)
    };

    private static Dictionary<string, object?> ToObjectDictionary(JsonElement element) => element.EnumerateObject()
        .ToDictionary(property => property.Name, property => ConvertJsonValue(property.Value), StringComparer.Ordinal);

    private static object? ConvertJsonValue(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.Object => ToObjectDictionary(element),
        JsonValueKind.Array => element.EnumerateArray().Select(ConvertJsonValue).ToArray(),
        JsonValueKind.String => element.GetString(),
        JsonValueKind.Number when element.TryGetInt64(out var integer) => integer,
        JsonValueKind.Number => element.GetDouble(),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        _ => null
    };

    private static long? ReadLong(JsonElement parent, string property) =>
        parent.TryGetProperty(property, out var value) && value.TryGetInt64(out var number) ? number : null;

    private static string? ReadString(JsonElement parent, string property) =>
        parent.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static Uri BuildStreamUri(Uri baseUri, string modelId)
    {
        var path = baseUri.AbsolutePath.TrimEnd('/') + "/models/" + Uri.EscapeDataString(modelId) + ":streamGenerateContent";
        return new UriBuilder(baseUri) { Path = path, Query = "alt=sse" }.Uri;
    }

    private static async Task<HttpRequestException> CreateProviderExceptionAsync(HttpResponseMessage response,
        string secret, CancellationToken cancellationToken)
    {
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        string detail;
        try
        {
            using var document = JsonDocument.Parse(body);
            var error = document.RootElement.TryGetProperty("error", out var value) ? value : document.RootElement;
            detail = ReadString(error, "message") ?? body;
        }
        catch (JsonException) { detail = body; }
        detail = detail.Replace(secret, "[redacted]", StringComparison.Ordinal);
        if (detail.Length > 2048) detail = detail[..2048];
        return new HttpRequestException($"Google GenAI request failed with HTTP {(int)response.StatusCode}: {detail}",
            null, response.StatusCode);
    }

    private static string SanitizeSurrogates(string text)
    {
        var builder = new StringBuilder(text.Length);
        for (var index = 0; index < text.Length; index++)
        {
            var character = text[index];
            if (!char.IsSurrogate(character)) { builder.Append(character); continue; }
            if (char.IsHighSurrogate(character) && index + 1 < text.Length && char.IsLowSurrogate(text[index + 1]))
            {
                builder.Append(character).Append(text[++index]);
                continue;
            }
            builder.Append('\uFFFD');
        }
        return builder.ToString();
    }
}
