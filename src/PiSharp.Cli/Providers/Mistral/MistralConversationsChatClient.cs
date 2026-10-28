using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.AI;
using PiSharp.Runtime.Providers;
using PiSharp.Runtime.Sessions;

namespace PiSharp.Cli;

internal static class MistralConversationsChatClientFactory
{
    public static IChatClient Create(ModelSelection selection, int headerTimeoutMs)
    {
        var endpoint = selection.Connection.Endpoint ?? selection.Provider.Endpoint;
        var requestContext = new MistralChatRequestContext();
        var handler = new MistralChatCompatibilityHandler(requestContext,
            ProviderWireHookHandler.CreateWithActivity(selection, "mistral-conversations",
                new HttpClientHandler()));
        var http = new HttpClient(handler, disposeHandler: true) { Timeout = Timeout.InfiniteTimeSpan };
        var client = new MistralConversationsChatClient(http, BuildEndpoint(endpoint), selection.Model,
            selection.ApiKey, headerTimeoutMs);
        return new MistralChatOptionsClient(client, selection.Model, requestContext);
    }

    private static Uri BuildEndpoint(Uri endpoint)
    {
        var builder = new UriBuilder(endpoint);
        var path = builder.Path.TrimEnd('/');
        if (path.EndsWith("/chat/completions", StringComparison.Ordinal)) return builder.Uri;
        builder.Path = path.EndsWith("/v1", StringComparison.Ordinal)
            ? path + "/chat/completions"
            : path + "/v1/chat/completions";
        return builder.Uri;
    }
}

/// <summary>Streams Mistral's provider-specific Chat Completions wire format without OpenAI SDK projection.</summary>
internal sealed class MistralConversationsChatClient(HttpClient http, Uri endpoint, ModelDescriptor model,
    string apiKey, int headerTimeoutMs) : IChatClient
{
    private const int MaxErrorBodyChars = 4000;

    public async Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var updates = GetStreamingResponseAsync(messages, options, cancellationToken);
        return await updates.ToChatResponseAsync(cancellationToken).ConfigureAwait(false);
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
        ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var payload = MistralConversationsRequestMapper.Build(model, messages, options);
        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        request.Headers.UserAgent.ParseAdd(BuildPiUserAgent());
        if (options?.ConversationId is { Length: > 0 } conversationId)
            request.Headers.TryAddWithoutValidation("x-affinity", conversationId);
        request.Content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json");

        HttpResponseMessage response;
        using (var headerTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
        {
            headerTimeout.CancelAfter(headerTimeoutMs);
            try
            {
                response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, headerTimeout.Token)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException error) when (!cancellationToken.IsCancellationRequested && headerTimeout.IsCancellationRequested)
            {
                throw new TimeoutException($"Mistral response headers timed out after {headerTimeoutMs}ms", error);
            }
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                var detail = string.IsNullOrWhiteSpace(body) ? response.ReasonPhrase ?? "Request failed" : body.Trim();
                if (detail.Length > MaxErrorBodyChars)
                    detail = $"{detail[..MaxErrorBodyChars]}... [truncated {detail.Length - MaxErrorBodyChars} chars]";
                throw new HttpRequestException(
                    $"Mistral API error ({(int)response.StatusCode}): {detail}", null, response.StatusCode);
            }
            if (response.Content is null)
                throw new InvalidDataException("Mistral response has no body");

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            using var reader = new StreamReader(stream, new UTF8Encoding(false), detectEncodingFromByteOrderMarks: true,
                bufferSize: 1024, leaveOpen: true);
            var responseId = "";
            string? finishReason = null;
            string? modelId = null;
            UsageDetails? finalUsage = null;
            var toolCalls = new SortedDictionary<int, ToolCallAccumulator>();
            var data = new StringBuilder();
            string? line;
            var streamDone = false;
            while ((line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false)) is not null)
            {
                if (line.Length == 0)
                {
                    foreach (var update in ProcessEvent(data, toolCalls, ref responseId, ref modelId, ref finishReason,
                        ref streamDone, cancellationToken))
                    {
                        if (update.Contents.OfType<UsageContent>().FirstOrDefault() is { } usage)
                            finalUsage = usage.Details;
                        else
                            yield return update;
                    }
                    data.Clear();
                    if (streamDone) break;
                    continue;
                }
                if (line.StartsWith("data:", StringComparison.Ordinal))
                {
                    if (data.Length > 0) data.Append('\n');
                    data.Append(line.AsSpan(5).TrimStart());
                }
            }
            if (data.Length > 0)
            {
                foreach (var update in ProcessEvent(data, toolCalls, ref responseId, ref modelId, ref finishReason,
                    ref streamDone, cancellationToken))
                {
                    if (update.Contents.OfType<UsageContent>().FirstOrDefault() is { } usage)
                        finalUsage = usage.Details;
                    else
                        yield return update;
                }
            }

            foreach (var call in toolCalls.Values)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var function = new FunctionCallContent(call.Id, call.Name, new Dictionary<string, object?>());
                ProviderToolArgumentChatClient.Normalize(function, call.Arguments.ToString());
                yield return CreateUpdate(responseId, modelId, [function]);
            }

            if (finishReason is null)
                throw new InvalidDataException("Mistral stream ended without a finish reason");
            if (finishReason == "error")
                throw new HttpRequestException("Provider stopped with: error (server error)");
            if (finishReason is not ("stop" or "length" or "model_length" or "tool_calls"))
                throw new InvalidDataException($"Provider stopped with: {finishReason}");

            if (finalUsage is not null)
                yield return CreateUpdate(responseId, modelId, [new UsageContent(finalUsage)]);
            var final = CreateUpdate(responseId, modelId, []);
            final.FinishReason = finishReason switch
            {
                "stop" => ChatFinishReason.Stop,
                "length" or "model_length" => ChatFinishReason.Length,
                "tool_calls" => ChatFinishReason.ToolCalls,
                _ => ChatFinishReason.Stop
            };
            final.AdditionalProperties ??= new AdditionalPropertiesDictionary();
            final.AdditionalProperties["pisharp.mistral.rawFinishReason"] = finishReason;
            yield return final;
        }
    }

    public object? GetService(Type serviceType, object? serviceKey = null) =>
        serviceType.IsInstanceOfType(this) ? this : null;

    public void Dispose() => http.Dispose();

    private IReadOnlyList<ChatResponseUpdate> ProcessEvent(StringBuilder data,
        IDictionary<int, ToolCallAccumulator> toolCalls, ref string responseId, ref string? modelId,
        ref string? finishReason, ref bool streamDone, CancellationToken cancellationToken)
    {
        var updates = new List<ChatResponseUpdate>();
        var raw = data.ToString().Trim();
        if (raw.Length == 0) return updates;
        if (raw == "[DONE]")
        {
            streamDone = true;
            return updates;
        }

        using var document = JsonDocument.Parse(raw);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("choices", out var choices) ||
            choices.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("Invalid Mistral streaming event");
        if (responseId.Length == 0 && ReadString(root, "id") is { Length: > 0 } id) responseId = id;
        if (ReadString(root, "model") is { Length: > 0 } returnedModel) modelId = returnedModel;

        if (root.TryGetProperty("usage", out var usage) && usage.ValueKind == JsonValueKind.Object)
        {
            var prompt = ReadInt64(usage, "prompt_tokens");
            var cached = Math.Clamp(ReadCachedTokens(usage), 0, prompt);
            var output = ReadInt64(usage, "completion_tokens");
            var total = ReadInt64(usage, "total_tokens");
            var details = new UsageDetails
            {
                InputTokenCount = prompt,
                OutputTokenCount = output,
                CachedInputTokenCount = cached,
                TotalTokenCount = total > 0 ? total : prompt + output,
                AdditionalCounts = new AdditionalPropertiesDictionary<long> { ["cacheWriteTokens"] = 0 }
            };
            updates.Add(CreateUpdate(responseId, modelId, [new UsageContent(details)]));
        }

        var choice = choices.EnumerateArray().FirstOrDefault();
        if (choice.ValueKind != JsonValueKind.Object) return updates;
        var finish = ReadString(choice, "finish_reason");
        if (!string.IsNullOrEmpty(finish)) finishReason = finish;
        if (!choice.TryGetProperty("delta", out var delta) || delta.ValueKind != JsonValueKind.Object) return updates;

        if (delta.TryGetProperty("content", out var content) && content.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined))
        {
            if (content.ValueKind == JsonValueKind.String)
            {
                var text = content.GetString();
                if (!string.IsNullOrEmpty(text)) updates.Add(CreateUpdate(responseId, modelId, [new TextContent(text)]));
            }
            else if (content.ValueKind == JsonValueKind.Array)
            {
                foreach (var part in content.EnumerateArray())
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var type = ReadString(part, "type");
                    if (type == "text" && ReadString(part, "text") is { Length: > 0 } textPart)
                        updates.Add(CreateUpdate(responseId, modelId, [new TextContent(textPart)]));
                    else if (type == "thinking" && ReadThinking(part) is { Length: > 0 } thinking)
                        updates.Add(CreateUpdate(responseId, modelId, [new TextReasoningContent(thinking)]));
                }
            }
        }
        if (delta.TryGetProperty("tool_calls", out var calls) && calls.ValueKind == JsonValueKind.Array)
        {
            foreach (var call in calls.EnumerateArray())
            {
                cancellationToken.ThrowIfCancellationRequested();
                var index = call.TryGetProperty("index", out var indexValue) && indexValue.TryGetInt32(out var parsedIndex)
                    ? parsedIndex : toolCalls.Count;
                if (!toolCalls.TryGetValue(index, out var accumulator))
                    toolCalls[index] = accumulator = new(index);
                if (ReadString(call, "id") is { Length: > 0 } callId && callId != "null") accumulator.Id = callId;
                if (!call.TryGetProperty("function", out var function) || function.ValueKind != JsonValueKind.Object) continue;
                if (ReadString(function, "name") is { Length: > 0 } name) accumulator.Name = name;
                if (function.TryGetProperty("arguments", out var arguments))
                {
                    if (arguments.ValueKind == JsonValueKind.String) accumulator.Arguments.Append(arguments.GetString());
                    else if (arguments.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined))
                        accumulator.Arguments.Append(arguments.GetRawText());
                }
            }
        }
        return updates;
    }

    private ChatResponseUpdate CreateUpdate(string responseId, string? returnedModel, IList<AIContent> contents) =>
        new(ChatRole.Assistant, contents)
        {
            ModelId = returnedModel ?? model.Id,
            MessageId = responseId,
            ResponseId = responseId
        };

    private static string? ReadString(JsonElement parent, string property) =>
        parent.ValueKind == JsonValueKind.Object && parent.TryGetProperty(property, out var value) &&
        value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static string BuildPiUserAgent()
    {
        var platform = OperatingSystem.IsWindows() ? "win32" : OperatingSystem.IsMacOS() ? "darwin" :
            OperatingSystem.IsLinux() ? "linux" : OperatingSystem.IsFreeBSD() ? "freebsd" : "unknown";
        var release = Environment.OSVersion.Version.ToString();
        if (OperatingSystem.IsLinux())
        {
            try { release = File.ReadAllText("/proc/sys/kernel/osrelease").Trim(); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        var architecture = RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.X86 => "ia32",
            Architecture.X64 => "x64",
            Architecture.Arm => "arm",
            Architecture.Arm64 => "arm64",
            _ => RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant()
        };
        return $"pi ({platform} {release}; {architecture})";
    }

    private static long ReadInt64(JsonElement parent, string property) =>
        parent.ValueKind == JsonValueKind.Object && parent.TryGetProperty(property, out var value) &&
        value.TryGetInt64(out var count) && count > 0 ? count : 0;

    private static long ReadCachedTokens(JsonElement usage)
    {
        foreach (var (container, name) in new[]
        {
            ("prompt_tokens_details", "cached_tokens"), ("prompt_token_details", "cached_tokens"),
            ("promptTokensDetails", "cachedTokens"), ("promptTokenDetails", "cachedTokens")
        })
            if (usage.TryGetProperty(container, out var details) && ReadInt64(details, name) is var cached && cached > 0)
                return cached;
        var camelCase = ReadInt64(usage, "numCachedTokens");
        return camelCase > 0 ? camelCase : ReadInt64(usage, "num_cached_tokens");
    }

    private static string ReadThinking(JsonElement content)
    {
        if (!content.TryGetProperty("thinking", out var parts)) return "";
        if (parts.ValueKind == JsonValueKind.String) return parts.GetString() ?? "";
        if (parts.ValueKind != JsonValueKind.Array) return "";
        return string.Concat(parts.EnumerateArray().Select(part => ReadString(part, "text") ?? ""));
    }

    private sealed class ToolCallAccumulator(int index)
    {
        public string Id { get; set; } = MistralChatCompatibilityHandler.CreateToolCallId($"toolcall:{index}");
        public string Name { get; set; } = "";
        public StringBuilder Arguments { get; } = new();
    }
}

internal static class MistralConversationsRequestMapper
{
    public static JsonObject Build(ModelDescriptor model, IEnumerable<ChatMessage> history, ChatOptions? options)
    {
        var messages = new JsonArray();
        var toolNames = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!string.IsNullOrWhiteSpace(options?.Instructions))
            messages.Add(new JsonObject { ["role"] = "system", ["content"] = options.Instructions });
        foreach (var message in history)
        {
            if (message.Role == ChatRole.System)
            {
                var text = string.Concat(message.Contents.OfType<TextContent>().Select(content => content.Text));
                if (!string.IsNullOrWhiteSpace(text)) messages.Add(new JsonObject { ["role"] = "system", ["content"] = text });
            }
            else if (message.Role == ChatRole.User) messages.Add(BuildUserMessage(message, SupportsImages(model)));
            else if (message.Role == ChatRole.Assistant) AddAssistantMessage(messages, message, toolNames);
            else if (message.Role == ChatRole.Tool) AddToolResults(messages, message, SupportsImages(model), toolNames);
            else messages.Add(BuildUserMessage(message, SupportsImages(model)));
        }

        var payload = new JsonObject
        {
            ["model"] = options?.ModelId ?? model.Id,
            ["stream"] = true,
            ["messages"] = messages
        };
        if (options?.Temperature is { } temperature) payload["temperature"] = temperature;
        if (options?.TopP is { } topP) payload["top_p"] = topP;
        if (options?.MaxOutputTokens is { } maxTokens) payload["max_tokens"] = maxTokens;
        if (options?.ToolMode is NoneChatToolMode) payload["tool_choice"] = "none";
        else if (options?.ToolMode is RequiredChatToolMode) payload["tool_choice"] = "required";
        else if (options?.ToolMode is not null) payload["tool_choice"] = "auto";
        var tools = options?.Tools?.OfType<AIFunctionDeclaration>().ToArray() ?? [];
        if (tools.Length > 0)
        {
            payload["tools"] = new JsonArray(tools.Select(tool => new JsonObject
            {
                ["type"] = "function",
                ["function"] = new JsonObject
                {
                    ["name"] = tool.Name,
                    ["description"] = tool.Description,
                    ["parameters"] = JsonNode.Parse(tool.JsonSchema.GetRawText()),
                    ["strict"] = false
                }
            }).ToArray());
        }
        if (options?.ConversationId is { Length: > 0 } sessionId)
            payload["prompt_cache_key"] = sessionId;
        return payload;
    }

    private static JsonObject BuildUserMessage(ChatMessage message, bool supportsImages)
    {
        var contents = new JsonArray();
        var previousWasPlaceholder = false;
        var hadImageContent = false;
        foreach (var content in message.Contents)
        {
            if (content is TextContent text)
            {
                contents.Add(new JsonObject { ["type"] = "text", ["text"] = text.Text });
                previousWasPlaceholder = text.Text == "(image omitted: model does not support images)";
            }
            else if (content is DataContent data && data.MediaType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
            {
                hadImageContent = true;
                if (supportsImages)
                    contents.Add(new JsonObject
                    {
                        ["type"] = "image_url",
                        ["image_url"] = $"data:{data.MediaType};base64,{Convert.ToBase64String(data.Data.Span)}"
                    });
                else if (!previousWasPlaceholder)
                    contents.Add(new JsonObject { ["type"] = "text", ["text"] = "(image omitted: model does not support images)" });
                previousWasPlaceholder = !supportsImages;
            }
        }
        if (!hadImageContent && contents.Count == 1 && contents[0] is JsonObject one && one["type"]?.GetValue<string>() == "text")
            return new JsonObject { ["role"] = "user", ["content"] = one["text"]?.DeepClone() };
        return new JsonObject { ["role"] = "user", ["content"] = contents };
    }

    private static void AddAssistantMessage(JsonArray messages, ChatMessage message, IDictionary<string, string> toolNames)
    {
        var content = new JsonArray();
        var calls = new JsonArray();
        foreach (var part in message.Contents)
        {
            if (part is TextContent text && !string.IsNullOrWhiteSpace(text.Text))
                content.Add(new JsonObject { ["type"] = "text", ["text"] = text.Text });
            else if (part is TextReasoningContent reasoning && !string.IsNullOrWhiteSpace(reasoning.Text))
                content.Add(new JsonObject
                {
                    ["type"] = "thinking",
                    ["thinking"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = reasoning.Text })
                });
            else if (part is FunctionCallContent call)
            {
                toolNames[call.CallId] = call.Name;
                calls.Add(new JsonObject
                {
                    ["id"] = call.CallId,
                    ["type"] = "function",
                    ["function"] = new JsonObject
                    {
                        ["name"] = call.Name,
                        ["arguments"] = JsonSerializer.Serialize(call.Arguments ?? new Dictionary<string, object?>())
                    },
                    ["index"] = calls.Count
                });
            }
        }
        if (content.Count == 0 && calls.Count == 0) return;
        var assistant = new JsonObject { ["role"] = "assistant", ["prefix"] = false };
        if (content.Count > 0) assistant["content"] = content;
        if (calls.Count > 0) assistant["tool_calls"] = calls;
        messages.Add(assistant);
    }

    private static void AddToolResults(JsonArray messages, ChatMessage message, bool supportsImages,
        IReadOnlyDictionary<string, string> toolNames)
    {
        var images = message.Contents.OfType<DataContent>()
            .Where(data => data.MediaType.StartsWith("image/", StringComparison.OrdinalIgnoreCase)).ToArray();
        foreach (var result in message.Contents.OfType<FunctionResultContent>())
        {
            var text = ResultText(result, images.Length > 0, supportsImages);
            var content = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = text });
            if (supportsImages)
                foreach (var image in images)
                    content.Add(new JsonObject
                    {
                        ["type"] = "image_url",
                        ["image_url"] = $"data:{image.MediaType};base64,{Convert.ToBase64String(image.Data.Span)}"
                    });
            messages.Add(new JsonObject
            {
                ["role"] = "tool",
                ["tool_call_id"] = result.CallId,
                ["name"] = toolNames.TryGetValue(result.CallId, out var name) ? name : result.CallId,
                ["content"] = content
            });
        }
    }

    private static string ResultText(FunctionResultContent result, bool hasImages, bool supportsImages)
    {
        var text = result.Result switch
        {
            null => "",
            string value => value,
            JsonElement element => element.ValueKind == JsonValueKind.String ? element.GetString() ?? "" : element.GetRawText(),
            _ => JsonSerializer.Serialize(result.Result)
        };
        var trimmed = text.Trim();
        var errorPrefix = result.Exception is null ? "" : "[tool error] ";
        if (trimmed.Length > 0)
            return $"{errorPrefix}{trimmed}{(hasImages && !supportsImages ? "\n[tool image omitted: model does not support images]" : "")}";
        if (hasImages)
            return supportsImages ? $"{errorPrefix}(see attached image)" :
                $"{errorPrefix}(image omitted: model does not support images)";
        return $"{errorPrefix}(no tool output)";
    }

    private static bool SupportsImages(ModelDescriptor model) => model.Input is null || model.Input.Contains("image", StringComparer.Ordinal);
}
