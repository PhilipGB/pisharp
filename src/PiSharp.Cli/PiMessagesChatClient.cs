using System.Net;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.AI;
using PiSharp.Runtime.Providers;

namespace PiSharp.Cli;

internal static class PiMessagesChatClientFactory
{
    public static IChatClient Create(ModelSelection selection, HttpMessageHandler? innerHandler = null)
    {
        var endpoint = selection.Model.BaseUrl ?? selection.Connection.Endpoint?.ToString() ??
            selection.Provider.Endpoint.ToString();
        var baseUri = ProviderProfileLoader.ParseEndpoint(endpoint, "Pi Messages base URL");
        var handler = new ProviderWireActivityHandler(innerHandler ?? new HttpClientHandler { AllowAutoRedirect = false });
        var http = new HttpClient(handler, disposeHandler: true) { Timeout = Timeout.InfiniteTimeSpan };
        var apiKey = selection.ApiKey is "not-needed" or "not-configured" ? string.Empty : selection.ApiKey;
        return new PiMessagesChatClient(http, baseUri, selection.Model, apiKey,
            selection.OAuthCredentialResolver);
    }
}

/// <summary>Implements Pi's JSON request and SSE response protocol over the MAF chat boundary.</summary>
internal sealed class PiMessagesChatClient(HttpClient http, Uri baseUri, ModelDescriptor model, string apiKey,
    Func<CancellationToken, Task<(string Access, string AccountId)>>? oauthCredentialResolver = null)
    : IChatClient
{
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
        var requestApiKey = apiKey;
        if (oauthCredentialResolver is not null)
        {
            string? credentialError = null;
            try
            {
                requestApiKey = (await oauthCredentialResolver(cancellationToken).ConfigureAwait(false)).Access;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception error)
            {
                credentialError = SecretRedactor.Redact(error.Message, apiKey);
            }
            if (credentialError is not null)
            {
                yield return CreateFailureUpdate(credentialError, null);
                yield break;
            }
        }

        if (string.IsNullOrWhiteSpace(requestApiKey))
        {
            yield return CreateFailureUpdate($"No API key provided for provider '{model.Provider ?? "custom"}'.", null);
            yield break;
        }

        var requestUri = BuildRequestUri(options);
        using var request = new HttpRequestMessage(HttpMethod.Post, requestUri);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", requestApiKey);
        request.Content = new StringContent(
            PiMessagesRequestMapper.Build(_modelId, messages, options).ToJsonString(s_jsonOptions),
            Encoding.UTF8, "application/json");

        HttpResponseMessage? response = null;
        Exception? requestError = null;
        try
        {
            response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception error)
        {
            requestError = error;
        }

        if (requestError is not null)
        {
            if (requestError is OperationCanceledException && cancellationToken.IsCancellationRequested)
                throw requestError;
            yield return CreateFailureUpdate(SecretRedactor.Redact(requestError.Message, requestApiKey), null);
            yield break;
        }

        using var responseLease = response!;
        ProviderRetryResponseCapture.Observe(responseLease);
        var responseHeaders = ReadHeaders(responseLease);
        if (!responseLease.IsSuccessStatusCode)
        {
            string body;
            Exception? bodyError = null;
            try
            {
                body = await ReadBoundedBodyAsync(responseLease.Content, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception error)
            {
                body = string.Empty;
                bodyError = error;
            }
            if (bodyError is OperationCanceledException && cancellationToken.IsCancellationRequested)
                throw bodyError;

            yield return CreateHttpFailureUpdate(responseLease, requestUri, body, responseHeaders, requestApiKey);
            yield break;
        }

        Stream responseStream;
        Exception? streamOpenError = null;
        try
        {
            responseStream = await responseLease.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception error)
        {
            responseStream = Stream.Null;
            streamOpenError = error;
        }
        if (streamOpenError is not null)
        {
            if (streamOpenError is OperationCanceledException && cancellationToken.IsCancellationRequested)
                throw streamOpenError;
            yield return CreateFailureUpdate(SecretRedactor.Redact(streamOpenError.Message, requestApiKey), responseHeaders);
            yield break;
        }

        var state = new StreamState();
        Exception? readError = null;
        var terminal = false;
        using (responseStream)
        using (var reader = new StreamReader(responseStream, Encoding.UTF8, detectEncodingFromByteOrderMarks: false,
                   bufferSize: 4096, leaveOpen: true))
        {
            var dataLines = new List<string>();
            while (true)
            {
                string? line;
                try
                {
                    line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (Exception error)
                {
                    readError = error;
                    break;
                }
                if (line is null)
                {
                    if (dataLines.Count > 0)
                    {
                        var frameResult = ConvertFrame(string.Join('\n', dataLines), state, responseHeaders,
                            out var finalUpdate, out terminal);
                        if (frameResult && finalUpdate is not null) yield return finalUpdate;
                    }
                    break;
                }

                if (line.Length == 0)
                {
                    if (dataLines.Count == 0) continue;
                    var frame = string.Join('\n', dataLines);
                    dataLines.Clear();
                    bool parsed;
                    ChatResponseUpdate? update;
                    try
                    {
                        parsed = ConvertFrame(frame, state, responseHeaders, out update, out terminal);
                    }
                    catch (Exception error)
                    {
                        readError = error;
                        break;
                    }
                    if (parsed && update is not null) yield return update;
                    if (terminal) break;
                    continue;
                }

                if (line.StartsWith("data:", StringComparison.Ordinal))
                {
                    dataLines.Add(line);
                }
            }
        }

        if (readError is not null)
        {
            if (readError is OperationCanceledException && cancellationToken.IsCancellationRequested)
                throw readError;
            yield return CreateFailureUpdate(SecretRedactor.Redact(readError.Message, requestApiKey), responseHeaders);
            yield break;
        }

        if (!terminal)
            yield return CreateFailureUpdate($"{model.Provider ?? "provider"} stream ended without a terminal event.",
                responseHeaders);
    }

    public object? GetService(Type serviceType, object? serviceKey = null) =>
        serviceType.IsInstanceOfType(this) ? this : null;

    public void Dispose() => http.Dispose();

    private Uri BuildRequestUri(ChatOptions? options)
    {
        var builder = new UriBuilder(new Uri(baseUri.ToString().TrimEnd('/') + "/messages"));
        if (PiMessagesRequestMapper.ReadBoolean(options?.AdditionalProperties, "piMessages.debug") == true)
            builder.Query = "debug=1";
        return builder.Uri;
    }

    private bool ConvertFrame(string frame, StreamState state, IReadOnlyDictionary<string, string> headers,
        out ChatResponseUpdate? update, out bool terminal)
    {
        update = null;
        terminal = false;
        var dataLine = frame.Split('\n').FirstOrDefault(line => line.StartsWith("data:", StringComparison.Ordinal));
        if (dataLine is null) return false;
        var data = dataLine.AsSpan(5).Trim();
        if (data.IsEmpty || data.SequenceEqual("[DONE]")) return false;

        using var document = JsonDocument.Parse(data.ToString());
        var root = document.RootElement;
        var type = ReadString(root, "type") ?? throw new InvalidDataException("Pi Messages event has no type.");
        var rawEvent = root.Clone();
        switch (type)
        {
            case "start":
                return false;
            case "text_start":
            case "thinking_start":
                {
                    var index = ReadIndex(root);
                    state.Blocks[index] = new BlockState(type == "text_start");
                    return false;
                }
            case "text_delta":
                {
                    var index = ReadIndex(root);
                    var delta = ReadString(root, "delta") ?? string.Empty;
                    if (!state.Blocks.TryGetValue(index, out var block)) state.Blocks[index] = block = new BlockState(text: true);
                    block.Content.Append(delta);
                    update = CreateUpdate([new TextContent(delta)], headers);
                    return true;
                }
            case "thinking_delta":
                {
                    var index = ReadIndex(root);
                    var delta = ReadString(root, "delta") ?? string.Empty;
                    if (!state.Blocks.TryGetValue(index, out var block)) state.Blocks[index] = block = new BlockState(text: false);
                    block.Content.Append(delta);
                    update = CreateUpdate([new TextReasoningContent(delta)], headers);
                    return true;
                }
            case "text_end":
                {
                    var index = ReadIndex(root);
                    var signature = ReadString(root, "contentSignature");
                    if (signature is not null) state.TextSignatures[index.ToString(System.Globalization.CultureInfo.InvariantCulture)] = signature;
                    return false;
                }
            case "thinking_end":
                {
                    var index = ReadIndex(root);
                    var signature = ReadString(root, "contentSignature");
                    var redacted = ReadBoolean(root, "redacted");
                    if (signature is not null) state.ThinkingSignatures[index.ToString(System.Globalization.CultureInfo.InvariantCulture)] = signature;
                    if (redacted) state.RedactedThinking[index.ToString(System.Globalization.CultureInfo.InvariantCulture)] = true;
                    if (redacted && (!state.Blocks.TryGetValue(index, out var block) || block.Content.Length == 0))
                        update = CreateUpdate([new TextReasoningContent("[Reasoning redacted]")
                    {
                        ProtectedData = signature,
                        AdditionalProperties = new AdditionalPropertiesDictionary
                        {
                            [PiMessagesRequestMapper.RedactedThinkingKey] = true
                        }
                    }], headers);
                    return update is not null;
                }
            case "toolcall_start":
                {
                    var index = ReadIndex(root);
                    state.ToolCalls[index] = new ToolCallState(ReadString(root, "id") ?? string.Empty,
                        ReadString(root, "toolName") ?? string.Empty);
                    return false;
                }
            case "toolcall_delta":
                {
                    var index = ReadIndex(root);
                    if (!state.ToolCalls.TryGetValue(index, out var call))
                        state.ToolCalls[index] = call = new ToolCallState(string.Empty, string.Empty);
                    call.Arguments.Append(ReadString(root, "delta"));
                    return false;
                }
            case "toolcall_end":
                {
                    var index = ReadIndex(root);
                    if (!root.TryGetProperty("toolCall", out var toolCall) || toolCall.ValueKind != JsonValueKind.Object)
                        throw new InvalidDataException("Pi Messages toolcall_end event has no toolCall object.");
                    state.ToolCalls.TryGetValue(index, out var started);
                    var id = ReadString(toolCall, "id") ?? started?.Id ?? string.Empty;
                    var name = ReadString(toolCall, "name") ?? started?.Name ?? string.Empty;
                    var arguments = toolCall.TryGetProperty("arguments", out var args) && args.ValueKind == JsonValueKind.Object
                        ? JsonSerializer.Deserialize<Dictionary<string, object?>>(args.GetRawText(), s_jsonOptions) ?? []
                        : new Dictionary<string, object?>();
                    state.ToolCalls.Remove(index);
                    update = CreateUpdate([new FunctionCallContent(id, name, arguments)], headers);
                    return true;
                }
            case "done":
            case "error":
                terminal = true;
                update = CreateTerminalUpdate(root, type == "error", state, headers);
                return true;
            default:
                update = CreateUpdate([], headers);
                update.AdditionalProperties ??= new AdditionalPropertiesDictionary();
                update.AdditionalProperties["pisharp.piMessages.event"] = rawEvent;
                return true;
        }
    }

    private ChatResponseUpdate CreateTerminalUpdate(JsonElement root, bool isError, StreamState state,
        IReadOnlyDictionary<string, string> headers)
    {
        var responseId = ReadString(root, "responseId");
        var reason = ReadString(root, "reason") ?? (isError ? "error" : "stop");
        var usage = root.TryGetProperty("usage", out var usageValue) && usageValue.ValueKind == JsonValueKind.Object
            ? usageValue.Clone() : JsonDocument.Parse("{}").RootElement.Clone();
        var contents = new List<AIContent> { CreateUsageContent(usage) };
        if (isError)
        {
            var errorMessage = ReadString(root, "errorMessage") ?? "Pi Messages backend returned an error.";
            contents.Insert(0, new ErrorContent(errorMessage));
        }

        var update = CreateUpdate(contents, headers, responseId);
        update.FinishReason = MapFinishReason(reason);
        update.AdditionalProperties ??= new AdditionalPropertiesDictionary();
        update.AdditionalProperties[PiMessagesRequestMapper.UsageKey] = usage;
        update.AdditionalProperties["pisharp.piMessages.api"] = "pi-messages";
        update.AdditionalProperties["pisharp.piMessages.provider"] = model.Provider ?? string.Empty;
        update.AdditionalProperties["pisharp.piMessages.model"] = model.Id;
        update.AdditionalProperties["pisharp.piMessages.stopReason"] = reason;
        update.AdditionalProperties[PiMessagesRequestMapper.TextSignaturesKey] =
            new JsonObject(state.TextSignatures.ToDictionary(pair => pair.Key, pair => (JsonNode?)JsonValue.Create(pair.Value)));
        update.AdditionalProperties[PiMessagesRequestMapper.ThinkingSignaturesKey] =
            new JsonObject(state.ThinkingSignatures.ToDictionary(pair => pair.Key, pair => (JsonNode?)JsonValue.Create(pair.Value)));
        update.AdditionalProperties[PiMessagesRequestMapper.RedactedThinkingKey] =
            new JsonObject(state.RedactedThinking.ToDictionary(pair => pair.Key, pair => (JsonNode?)JsonValue.Create(pair.Value)));
        if (responseId is not null) update.ResponseId = responseId;
        if (responseId is not null)
            update.AdditionalProperties["pisharp.piMessages.responseId"] = responseId;
        if (ReadString(root, "providerThinkingLevel") is { } thinkingLevel)
            update.AdditionalProperties["pisharp.piMessages.providerThinkingLevel"] = thinkingLevel;
        if (root.TryGetProperty("rewrite", out var rewrite))
            update.AdditionalProperties["pisharp.piMessages.rewrite"] = rewrite.Clone();
        if (isError && root.TryGetProperty("errorMessage", out var errorValue))
            update.AdditionalProperties["pisharp.piMessages.errorMessage"] = errorValue.Clone();
        return update;
    }

    private ChatResponseUpdate CreateHttpFailureUpdate(HttpResponseMessage response, Uri requestUri, string body,
        IReadOnlyDictionary<string, string> headers, string requestApiKey)
    {
        var safeBody = SecretRedactor.Redact(body, requestApiKey);
        string? code = null;
        string? providerMessage = null;
        JsonElement? errorDetails = null;
        try
        {
            using var parsed = JsonDocument.Parse(safeBody);
            if (parsed.RootElement.TryGetProperty("error", out var errorObject) && errorObject.ValueKind == JsonValueKind.Object)
            {
                errorDetails = errorObject.Clone();
                code = ReadString(errorObject, "code");
                providerMessage = ReadString(errorObject, "message");
            }
        }
        catch (JsonException) { }

        var suffix = providerMessage ?? safeBody;
        var codeSuffix = code is null ? string.Empty : $" ({code})";
        var message = $"{(int)response.StatusCode} {response.ReasonPhrase}: {suffix}{codeSuffix}";
        var errorContent = new ErrorContent(message) { ErrorCode = code };
        if (errorDetails is { } details) errorContent.Details = details.GetRawText();
        var update = CreateUpdate([errorContent], headers);
        update.FinishReason = new ChatFinishReason("error");
        update.AdditionalProperties ??= new AdditionalPropertiesDictionary();
        update.AdditionalProperties["pisharp.piMessages.responseFailure"] = new JsonObject
        {
            ["version"] = 1,
            ["provider"] = model.Provider,
            ["model"] = model.Id,
            ["url"] = requestUri.ToString(),
            ["status"] = (int)response.StatusCode,
            ["statusText"] = response.ReasonPhrase,
            ["error"] = errorDetails is { } error ? JsonNode.Parse(error.GetRawText()) : null,
            ["body"] = errorDetails is null ? TruncateDiagnostic(safeBody) : null,
            ["timestampMs"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
        };
        update.AdditionalProperties["pisharp.piMessages.api"] = "pi-messages";
        update.AdditionalProperties["pisharp.piMessages.provider"] = model.Provider ?? string.Empty;
        update.AdditionalProperties["pisharp.piMessages.model"] = model.Id;
        update.AdditionalProperties["pisharp.piMessages.stopReason"] = "error";
        update.AdditionalProperties["pisharp.piMessages.errorMessage"] = message;
        return update;
    }

    private ChatResponseUpdate CreateFailureUpdate(string message, IReadOnlyDictionary<string, string>? headers)
    {
        var update = CreateUpdate([new ErrorContent(message)], headers);
        update.FinishReason = new ChatFinishReason("error");
        return update;
    }

    private ChatResponseUpdate CreateUpdate(IList<AIContent> contents, IReadOnlyDictionary<string, string>? headers,
        string? responseId = null)
    {
        var update = new ChatResponseUpdate(ChatRole.Assistant, contents)
        {
            ResponseId = responseId,
            MessageId = responseId,
            ModelId = _modelId,
            CreatedAt = DateTimeOffset.UtcNow
        };
        if (headers is not null)
        {
            update.AdditionalProperties ??= new AdditionalPropertiesDictionary();
            update.AdditionalProperties["pisharp.piMessages.responseHeaders"] =
                new Dictionary<string, string>(headers, StringComparer.OrdinalIgnoreCase);
        }
        return update;
    }

    private static UsageContent CreateUsageContent(JsonElement usage)
    {
        static long Count(JsonElement root, string property) =>
            root.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.Number &&
            value.TryGetInt64(out var count) ? count : 0;

        var details = new UsageDetails
        {
            InputTokenCount = Count(usage, "input"),
            OutputTokenCount = Count(usage, "output"),
            CachedInputTokenCount = Count(usage, "cacheRead"),
            TotalTokenCount = Count(usage, "totalTokens")
        };
        var cacheWrite = Count(usage, "cacheWrite");
        if (cacheWrite > 0)
            details.AdditionalCounts = new AdditionalPropertiesDictionary<long> { ["piMessages.cacheWrite"] = cacheWrite };
        var content = new UsageContent(details)
        {
            AdditionalProperties = new AdditionalPropertiesDictionary
            {
                [PiMessagesRequestMapper.UsageKey] = usage.Clone()
            }
        };
        return content;
    }

    private static ChatFinishReason MapFinishReason(string reason) => reason switch
    {
        "stop" => ChatFinishReason.Stop,
        "length" => ChatFinishReason.Length,
        "toolUse" => ChatFinishReason.ToolCalls,
        _ => new ChatFinishReason(reason)
    };

    private static async Task<string> ReadBoundedBodyAsync(HttpContent content, CancellationToken cancellationToken)
    {
        const int maxBytes = 1024 * 1024;
        await using var stream = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        var chunk = new byte[8192];
        while (buffer.Length <= maxBytes)
        {
            var remaining = (int)Math.Min(chunk.Length, maxBytes + 1 - buffer.Length);
            var read = await stream.ReadAsync(chunk.AsMemory(0, remaining), cancellationToken).ConfigureAwait(false);
            if (read == 0) break;
            buffer.Write(chunk, 0, read);
        }
        return Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length);
    }

    private static IReadOnlyDictionary<string, string> ReadHeaders(HttpResponseMessage response)
    {
        var headers = response.Headers.Concat(response.Content.Headers)
            .ToDictionary(pair => pair.Key.ToLowerInvariant(), pair => string.Join(",", pair.Value),
                StringComparer.OrdinalIgnoreCase);
        return headers;
    }

    private static int ReadIndex(JsonElement value) =>
        value.TryGetProperty("contentIndex", out var index) && index.TryGetInt32(out var parsed) ? parsed : 0;

    private static string? ReadString(JsonElement value, string property) =>
        value.TryGetProperty(property, out var propertyValue) && propertyValue.ValueKind == JsonValueKind.String
            ? propertyValue.GetString() : null;

    private static bool ReadBoolean(JsonElement value, string property) =>
        value.TryGetProperty(property, out var propertyValue) && propertyValue.ValueKind == JsonValueKind.True;

    private static string TruncateDiagnostic(string value) => value.Length > 8192 ? value[..8192] + "…" : value;

    private sealed class StreamState
    {
        public Dictionary<int, BlockState> Blocks { get; } = [];
        public Dictionary<int, ToolCallState> ToolCalls { get; } = [];
        public Dictionary<string, string> TextSignatures { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, string> ThinkingSignatures { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, bool> RedactedThinking { get; } = new(StringComparer.Ordinal);
    }

    private sealed class BlockState(bool text)
    {
        public bool IsText { get; } = text;
        public StringBuilder Content { get; } = new();
    }

    private sealed class ToolCallState(string id, string name)
    {
        public string Id { get; } = id;
        public string Name { get; } = name;
        public StringBuilder Arguments { get; } = new();
    }
}
