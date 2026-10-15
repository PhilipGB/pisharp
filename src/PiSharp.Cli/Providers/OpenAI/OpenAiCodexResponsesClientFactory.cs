using System.ClientModel;
using System.ClientModel.Primitives;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using OpenAI;
using PiSharp.Runtime.Providers;

namespace PiSharp.Cli;

/// <summary>Builds the Responses SDK pipeline for the ChatGPT Codex backend's SSE protocol.</summary>
internal static class OpenAiCodexResponsesClientFactory
{
    public static IChatClient Create(ModelSelection selection, TimeSpan timeout)
    {
        var requestContext = new OpenAiCodexRequestContext();
        var options = new OpenAIClientOptions
        {
            Endpoint = selection.Connection.Endpoint ?? selection.Provider.Endpoint,
            NetworkTimeout = timeout,
            RetryPolicy = new ClientRetryPolicy(0),
            Transport = new HttpClientPipelineTransport(new HttpClient(
                new OpenAiCodexRequestHandler(selection.ApiKey, requestContext,
                    new ProviderWireActivityHandler(new OpenAiCodexWebSocketBridgeHandler(
                        requestContext, new HttpClientHandler { AllowAutoRedirect = false }, TimeSpan.FromSeconds(15))),
                    selection.OAuthCredentialResolver),
                disposeHandler: true))
        };
        var client = new OpenAIClient(new ApiKeyCredential(selection.ApiKey), options);
#pragma warning disable OPENAI001
        var chat = new StatelessResponsesChatClient(client.GetResponsesClient().AsIChatClient(selection.Model.Id));
#pragma warning restore OPENAI001
        return new OpenAiCodexChatOptionsClient(chat, requestContext,
            SupportsGrammarTools(selection.Model.Compatibility));
    }

    private static bool SupportsGrammarTools(JsonElement? compatibility) =>
        compatibility is { ValueKind: JsonValueKind.Object } value &&
        value.TryGetProperty("supportsOpenAIGrammarTools", out var supported) &&
        supported.ValueKind == JsonValueKind.True;
}

internal sealed record OpenAiCodexRequestMetadata(string? SessionId,
    IReadOnlyDictionary<string, OpenAiCodexGrammarTool> GrammarTools);

internal sealed record OpenAiCodexGrammarTool(string Name, string? Description, string Syntax,
    string Definition, string InputProperty);

internal sealed class OpenAiCodexGrammarCallState(OpenAiCodexGrammarTool grammar)
{
    private bool _started;
    private bool _closed;
    private string _input = string.Empty;

    public string? Append(string inputDelta)
    {
        if (_closed)
            throw new InvalidOperationException($"Grammar tool input for '{grammar.InputProperty}' changed after it was closed.");
        var nextInput = _input + inputDelta;
        return Advance(nextInput, close: false);
    }

    public string Complete(string input)
    {
        if (_closed && input != _input)
            throw new InvalidOperationException($"Grammar tool input for '{grammar.InputProperty}' changed after it was closed.");
        _ = Advance(input, close: true);
        return CreateArguments();
    }

    private string? Advance(string nextInput, bool close)
    {
        if (!nextInput.StartsWith(_input, StringComparison.Ordinal))
            throw new InvalidOperationException($"Grammar tool input for '{grammar.InputProperty}' changed non-monotonically.");
        var inputDelta = nextInput[_input.Length..];
        if (!close && inputDelta.Length == 0) return null;

        var delta = string.Empty;
        if (!_started)
        {
            delta = $"{{{JsonSerializer.Serialize(grammar.InputProperty)}:\"";
            _started = true;
        }
        delta += JsonSerializer.Serialize(inputDelta)[1..^1];
        _input = nextInput;
        if (close)
        {
            delta += "\"}";
            _closed = true;
        }
        return delta;
    }

    private string CreateArguments()
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString(grammar.InputProperty, _input);
            writer.WriteEndObject();
        }
        return Encoding.UTF8.GetString(buffer.ToArray());
    }
}

internal sealed class OpenAiCodexRequestContext
{
    private sealed record SessionId(string Value);

    private static readonly ConditionalWeakTable<AgentSession, SessionId> SessionIds = new();
    private readonly AsyncLocal<OpenAiCodexRequestMetadata?> _current = new();

    public OpenAiCodexRequestMetadata? Current => _current.Value;

    public static string? GetCurrentSessionId()
    {
        AgentRunContext? runContext;
        try { runContext = AIAgent.CurrentRunContext; }
        catch (InvalidOperationException) { return null; }
        return runContext?.Session is { } session
            ? SessionIds.GetValue(session, static _ => new(Guid.NewGuid().ToString("N"))).Value
            : null;
    }

    public IDisposable Push(OpenAiCodexRequestMetadata metadata)
    {
        var previous = _current.Value;
        _current.Value = metadata;
        return new Scope(this, previous);
    }

    private sealed class Scope(OpenAiCodexRequestContext owner, OpenAiCodexRequestMetadata? previous) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0) owner._current.Value = previous;
        }
    }
}

/// <summary>Passes stable session affinity to the Codex wire adapter without changing canonical history.</summary>
internal sealed class OpenAiCodexChatOptionsClient(IChatClient inner, OpenAiCodexRequestContext requestContext,
    bool supportsGrammarTools)
    : DelegatingChatClient(inner)
{
    public override async Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages,
        ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        using var scope = requestContext.Push(new(ResolveSessionId(options), ResolveGrammarTools(options)));
        var updates = base.GetStreamingResponseAsync(messages, StripConversationSessionId(options), cancellationToken);
        return await updates.ToChatResponseAsync(cancellationToken).ConfigureAwait(false);
    }

    public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        using var scope = requestContext.Push(new(ResolveSessionId(options), ResolveGrammarTools(options)));
        await foreach (var update in base.GetStreamingResponseAsync(messages,
            StripConversationSessionId(options), cancellationToken)
            .WithCancellation(cancellationToken).ConfigureAwait(false))
            yield return update;
    }

    private static string? ResolveSessionId(ChatOptions? options)
    {
        var conversationSessionId = options?.AdditionalProperties?.TryGetValue(
            ProviderSessionMetadata.ConversationSessionIdProperty, out var value) == true
            ? value as string : null;
        return ClampSessionId(conversationSessionId ?? options?.ConversationId ??
            OpenAiCodexRequestContext.GetCurrentSessionId());
    }

    private IReadOnlyDictionary<string, OpenAiCodexGrammarTool> ResolveGrammarTools(ChatOptions? options)
    {
        var grammarTools = new Dictionary<string, OpenAiCodexGrammarTool>(StringComparer.Ordinal);
        if (!supportsGrammarTools || options?.Tools is null) return grammarTools;

        foreach (var function in options.Tools.OfType<AIFunction>())
        {
            if (!function.AdditionalProperties.TryGetValue("constrainedSampling", out var configured) ||
                configured is not JsonElement policy || policy.ValueKind != JsonValueKind.Object ||
                !policy.TryGetProperty("type", out var type) || type.ValueKind != JsonValueKind.String ||
                type.GetString() != "grammar")
                continue;

            var variants = policy.TryGetProperty("variants", out var variantsElement) &&
                variantsElement.ValueKind == JsonValueKind.Object ? variantsElement : default;
            var lark = ReadNonEmptyString(variants, "openai_lark");
            var regex = ReadNonEmptyString(variants, "openai_regex");
            if (lark is null && regex is null)
                throw new InvalidOperationException($"Tool \"{function.Name}\" cannot use grammar constrained sampling: no supported grammar variant was provided.");

            var inputProperty = ResolveGrammarInputProperty(function);
            grammarTools[function.Name] = new(function.Name, function.Description,
                lark is not null ? "lark" : "regex", lark ?? regex!, inputProperty);
        }

        return grammarTools;
    }

    private static string? ReadNonEmptyString(JsonElement parent, string property) =>
        parent.ValueKind == JsonValueKind.Object && parent.TryGetProperty(property, out var value) &&
        value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(value.GetString())
            ? value.GetString()
            : null;

    private static string ResolveGrammarInputProperty(AIFunction function)
    {
        var schema = function.JsonSchema;
        if (schema.ValueKind != JsonValueKind.Object || !schema.TryGetProperty("type", out var type) ||
            type.ValueKind != JsonValueKind.String || type.GetString() != "object")
            throw new InvalidOperationException($"Tool \"{function.Name}\" cannot use grammar constrained sampling: grammar constrained sampling requires an object parameter schema.");

        if (!schema.TryGetProperty("required", out var required) || required.ValueKind != JsonValueKind.Array ||
            required.GetArrayLength() != 1 || required[0].ValueKind != JsonValueKind.String)
            throw new InvalidOperationException($"Tool \"{function.Name}\" cannot use grammar constrained sampling: grammar constrained sampling requires exactly one required string property.");

        var inputProperty = required[0].GetString()!;
        if (!schema.TryGetProperty("properties", out var properties) || properties.ValueKind != JsonValueKind.Object ||
            !properties.TryGetProperty(inputProperty, out var input) || input.ValueKind != JsonValueKind.Object)
            throw new InvalidOperationException($"Tool \"{function.Name}\" cannot use grammar constrained sampling: grammar constrained sampling requires a properties entry for {inputProperty}.");
        if (!input.TryGetProperty("type", out type) || type.ValueKind != JsonValueKind.String || type.GetString() != "string")
            throw new InvalidOperationException($"Tool \"{function.Name}\" cannot use grammar constrained sampling: grammar constrained sampling property {inputProperty} must have type string.");
        return inputProperty;
    }

    private static ChatOptions? StripConversationSessionId(ChatOptions? options)
    {
        if (options?.AdditionalProperties is not { } properties ||
            !properties.ContainsKey(ProviderSessionMetadata.ConversationSessionIdProperty)) return options;

        var requestOptions = options.Clone();
        var requestProperties = properties.Clone();
        requestProperties.Remove(ProviderSessionMetadata.ConversationSessionIdProperty);
        requestOptions.AdditionalProperties = requestProperties.Count == 0 ? null : requestProperties;
        return requestOptions;
    }

    private static string? ClampSessionId(string? value) => string.IsNullOrEmpty(value)
        ? null
        : value.Length <= 64 ? value : value[..64];
}

/// <summary>Adapts standard Responses requests to Codex's account-scoped SSE endpoint.</summary>
internal sealed class OpenAiCodexRequestHandler : DelegatingHandler
{
    private const string EncryptedReasoning = "reasoning.encrypted_content";
    private readonly string _accessToken;
    private readonly string _accountId;
    private readonly OpenAiCodexRequestContext _requestContext;
    private readonly Func<CancellationToken, Task<(string Access, string AccountId)>>? _credentialResolver;

    public OpenAiCodexRequestHandler(string accessToken, OpenAiCodexRequestContext requestContext,
        HttpMessageHandler innerHandler,
        Func<CancellationToken, Task<(string Access, string AccountId)>>? credentialResolver = null) : base(innerHandler)
    {
        _accessToken = accessToken;
        _accountId = ExtractAccountId(accessToken);
        _requestContext = requestContext;
        _credentialResolver = credentialResolver;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        if (request.RequestUri is { } requestUri) request.RequestUri = ResolveCodexUri(requestUri);

        var accessToken = _accessToken;
        var accountId = _accountId;
        if (_credentialResolver is not null)
        {
            var current = await _credentialResolver(cancellationToken).ConfigureAwait(false);
            accessToken = current.Access;
            accountId = current.AccountId;
        }
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        request.Headers.Remove("api-key");
        request.Headers.Remove("chatgpt-account-id");
        request.Headers.TryAddWithoutValidation("chatgpt-account-id", accountId);
        request.Headers.Remove("originator");
        request.Headers.TryAddWithoutValidation("originator", "pi");
        request.Headers.Remove("OpenAI-Beta");
        request.Headers.TryAddWithoutValidation("OpenAI-Beta", "responses=experimental");
        request.Headers.Remove("User-Agent");
        request.Headers.TryAddWithoutValidation("User-Agent", "PiSharp");
        request.Headers.Accept.Clear();
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));

        if (_requestContext.Current is { } metadata)
        {
            if (metadata.SessionId is { Length: > 0 } sessionId)
            {
                request.Headers.Remove("session-id");
                request.Headers.TryAddWithoutValidation("session-id", sessionId);
                request.Headers.Remove("x-client-request-id");
                request.Headers.TryAddWithoutValidation("x-client-request-id", sessionId);
            }
            await ProjectBodyAsync(request, metadata, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            await ProjectBodyAsync(request, null, cancellationToken).ConfigureAwait(false);
        }

        var response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (_requestContext.Current is { GrammarTools.Count: > 0 } responseMetadata &&
            response.Content.Headers.ContentType?.MediaType?.Equals("text/event-stream", StringComparison.OrdinalIgnoreCase) == true)
            await OpenAiCodexGrammarSseStream.ProjectAsync(response, responseMetadata.GrammarTools, cancellationToken)
                .ConfigureAwait(false);
        return response;
    }

    private static Uri ResolveCodexUri(Uri uri)
    {
        var path = uri.AbsolutePath.TrimEnd('/');
        if (path.EndsWith("/codex/responses", StringComparison.OrdinalIgnoreCase)) return uri;
        if (path.EndsWith("/responses", StringComparison.OrdinalIgnoreCase))
            path = path[..^"/responses".Length];
        path += "/codex/responses";
        return new UriBuilder(uri) { Path = path }.Uri;
    }

    private static async Task ProjectBodyAsync(HttpRequestMessage request, OpenAiCodexRequestMetadata? metadata,
        CancellationToken cancellationToken)
    {
        if (request.Content is not { } content) return;
        var sessionId = metadata?.SessionId;
        var grammarTools = metadata?.GrammarTools ?? new Dictionary<string, OpenAiCodexGrammarTool>(StringComparer.Ordinal);
        var bytes = await content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
        using var document = JsonDocument.Parse(bytes);
        if (document.RootElement.ValueKind != JsonValueKind.Object) return;

        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            var storeWritten = false;
            var cacheKeyWritten = false;
            var includeWritten = false;
            var instructionsWritten = false;
            foreach (var property in document.RootElement.EnumerateObject())
            {
                if (property.NameEquals("store"))
                {
                    writer.WriteBoolean("store", false);
                    storeWritten = true;
                }
                else if (property.NameEquals("prompt_cache_key"))
                {
                    if (sessionId is { Length: > 0 }) writer.WriteString("prompt_cache_key", sessionId);
                    cacheKeyWritten = true;
                }
                else if (property.NameEquals("include"))
                {
                    WriteInclude(writer, property.Value);
                    includeWritten = true;
                }
                else if (property.NameEquals("reasoning") && property.Value.ValueKind == JsonValueKind.Object)
                    WriteReasoning(writer, property.Value);
                else if (property.NameEquals("text"))
                    WriteTextOptions(writer, property.Value);
                else if (property.NameEquals("instructions"))
                {
                    if (property.Value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(property.Value.GetString()))
                    {
                        writer.WriteString("instructions", property.Value.GetString());
                        instructionsWritten = true;
                    }
                }
                else if (property.NameEquals("previous_response_id"))
                {
                    // ConversationId is PiSharp's stable session affinity key, not a Responses id.
                }
                else if (property.NameEquals("tools")) WriteTools(writer, property.Value, grammarTools);
                else if (property.NameEquals("input")) WriteInput(writer, property.Value, grammarTools);
                else property.WriteTo(writer);
            }

            if (!storeWritten) writer.WriteBoolean("store", false);
            if (!cacheKeyWritten && sessionId is { Length: > 0 }) writer.WriteString("prompt_cache_key", sessionId);
            if (!includeWritten) WriteInclude(writer, null);
            if (!document.RootElement.TryGetProperty("text", out _))
                WriteTextOptions(writer, null);
            if (!instructionsWritten) writer.WriteString("instructions", "You are a helpful assistant.");
            if (!document.RootElement.TryGetProperty("tool_choice", out _)) writer.WriteString("tool_choice", "auto");
            if (!document.RootElement.TryGetProperty("parallel_tool_calls", out _)) writer.WriteBoolean("parallel_tool_calls", true);
            writer.WriteEndObject();
        }

        var projected = new ByteArrayContent(buffer.ToArray());
        foreach (var header in content.Headers)
            if (!header.Key.Equals("Content-Length", StringComparison.OrdinalIgnoreCase))
                projected.Headers.TryAddWithoutValidation(header.Key, header.Value);
        request.Content = projected;
        content.Dispose();
    }

    private static void WriteTools(Utf8JsonWriter writer, JsonElement tools,
        IReadOnlyDictionary<string, OpenAiCodexGrammarTool> grammarTools)
    {
        writer.WritePropertyName("tools");
        if (tools.ValueKind != JsonValueKind.Array)
        {
            tools.WriteTo(writer);
            return;
        }

        writer.WriteStartArray();
        foreach (var tool in tools.EnumerateArray())
        {
            if (tool.ValueKind != JsonValueKind.Object || !tool.TryGetProperty("name", out var name) ||
                name.ValueKind != JsonValueKind.String || !grammarTools.TryGetValue(name.GetString()!, out var grammar))
            {
                tool.WriteTo(writer);
                continue;
            }

            writer.WriteStartObject();
            writer.WriteString("type", "custom");
            writer.WriteString("name", grammar.Name);
            if (grammar.Description is not null) writer.WriteString("description", grammar.Description);
            writer.WritePropertyName("format");
            writer.WriteStartObject();
            writer.WriteString("type", "grammar");
            writer.WriteString("syntax", grammar.Syntax);
            writer.WriteString("definition", grammar.Definition);
            writer.WriteEndObject();
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
    }

    private static void WriteInput(Utf8JsonWriter writer, JsonElement input,
        IReadOnlyDictionary<string, OpenAiCodexGrammarTool> grammarTools)
    {
        writer.WritePropertyName("input");
        if (input.ValueKind != JsonValueKind.Array || grammarTools.Count == 0)
        {
            input.WriteTo(writer);
            return;
        }

        var callTools = new Dictionary<string, OpenAiCodexGrammarTool>(StringComparer.Ordinal);
        writer.WriteStartArray();
        foreach (var item in input.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object || !item.TryGetProperty("type", out var type) ||
                type.ValueKind != JsonValueKind.String)
            {
                item.WriteTo(writer);
                continue;
            }

            if (type.GetString() == "function_call" && TryGetGrammarCall(item, grammarTools, out var grammar, out var callId, out var callInput))
            {
                if (callId is { Length: > 0 }) callTools[callId] = grammar;
                WriteCustomCall(writer, item, grammar.Name, callInput);
            }
            else if (type.GetString() == "function_call_output" &&
                item.TryGetProperty("call_id", out var outputCallId) && outputCallId.ValueKind == JsonValueKind.String &&
                callTools.TryGetValue(outputCallId.GetString()!, out _))
            {
                writer.WriteStartObject();
                writer.WriteString("type", "custom_tool_call_output");
                writer.WritePropertyName("call_id");
                outputCallId.WriteTo(writer);
                if (item.TryGetProperty("output", out var output))
                {
                    writer.WritePropertyName("output");
                    output.WriteTo(writer);
                }
                writer.WriteEndObject();
            }
            else item.WriteTo(writer);
        }
        writer.WriteEndArray();
    }

    private static bool TryGetGrammarCall(JsonElement item,
        IReadOnlyDictionary<string, OpenAiCodexGrammarTool> grammarTools, out OpenAiCodexGrammarTool grammar,
        out string? callId, out string callInput)
    {
        grammar = null!;
        callId = null;
        callInput = string.Empty;
        if (!item.TryGetProperty("name", out var name) || name.ValueKind != JsonValueKind.String ||
            !grammarTools.TryGetValue(name.GetString()!, out var resolvedGrammar) ||
            !item.TryGetProperty("arguments", out var arguments) || arguments.ValueKind != JsonValueKind.String)
            return false;

        grammar = resolvedGrammar;

        try
        {
            using var document = JsonDocument.Parse(arguments.GetString()!);
            if (document.RootElement.ValueKind != JsonValueKind.Object ||
                !document.RootElement.TryGetProperty(grammar.InputProperty, out var value) ||
                value.ValueKind != JsonValueKind.String)
                return false;
            callInput = value.GetString()!;
            if (item.TryGetProperty("call_id", out var callIdElement) && callIdElement.ValueKind == JsonValueKind.String)
                callId = callIdElement.GetString();
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static void WriteCustomCall(Utf8JsonWriter writer, JsonElement source, string name, string input)
    {
        writer.WriteStartObject();
        writer.WriteString("type", "custom_tool_call");
        if (source.TryGetProperty("call_id", out var callId))
        {
            writer.WritePropertyName("call_id");
            callId.WriteTo(writer);
        }
        writer.WriteString("name", name);
        writer.WriteString("input", input);
        if (source.TryGetProperty("namespace", out var itemNamespace))
        {
            writer.WritePropertyName("namespace");
            itemNamespace.WriteTo(writer);
        }
        writer.WriteEndObject();
    }

    private static void WriteInclude(Utf8JsonWriter writer, JsonElement? existing)
    {
        writer.WritePropertyName("include");
        writer.WriteStartArray();
        var hasEncryptedReasoning = false;
        if (existing is { ValueKind: JsonValueKind.Array } include)
            foreach (var item in include.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.String && item.GetString() == EncryptedReasoning)
                    hasEncryptedReasoning = true;
                item.WriteTo(writer);
            }
        if (!hasEncryptedReasoning) writer.WriteStringValue(EncryptedReasoning);
        writer.WriteEndArray();
    }

    private static void WriteReasoning(Utf8JsonWriter writer, JsonElement reasoning)
    {
        writer.WritePropertyName("reasoning");
        writer.WriteStartObject();
        foreach (var property in reasoning.EnumerateObject())
        {
            if (property.NameEquals("summary")) continue;
            property.WriteTo(writer);
        }
        writer.WriteString("summary", "auto");
        writer.WriteEndObject();
    }

    private static void WriteTextOptions(Utf8JsonWriter writer, JsonElement? existing)
    {
        writer.WritePropertyName("text");
        writer.WriteStartObject();
        var hasVerbosity = false;
        if (existing is { ValueKind: JsonValueKind.Object } text)
            foreach (var property in text.EnumerateObject())
            {
                property.WriteTo(writer);
                if (property.NameEquals("verbosity")) hasVerbosity = true;
            }
        if (!hasVerbosity) writer.WriteString("verbosity", "low");
        writer.WriteEndObject();
    }

    private static string ExtractAccountId(string token)
    {
        try
        {
            var parts = token.Split('.');
            if (parts.Length != 3) throw new FormatException();
            var payload = parts[1].Replace('-', '+').Replace('_', '/');
            payload = payload.PadRight((payload.Length + 3) / 4 * 4, '=');
            using var document = JsonDocument.Parse(Convert.FromBase64String(payload));
            var claim = document.RootElement.GetProperty("https://api.openai.com/auth");
            var accountId = claim.GetProperty("chatgpt_account_id").GetString();
            if (string.IsNullOrWhiteSpace(accountId) || accountId.Length > 256 ||
                accountId.Any(char.IsControl)) throw new FormatException();
            return accountId;
        }
        catch (Exception error) when (error is FormatException or JsonException or KeyNotFoundException or InvalidOperationException)
        {
            throw new InvalidOperationException("The OpenAI Codex access token does not contain a valid ChatGPT account ID.");
        }
    }
}
