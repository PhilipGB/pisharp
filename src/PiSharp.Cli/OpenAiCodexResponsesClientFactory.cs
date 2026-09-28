using System.ClientModel;
using System.ClientModel.Primitives;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using OpenAI;

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
                    new ProviderWireActivityHandler(new HttpClientHandler { AllowAutoRedirect = false })),
                disposeHandler: true))
        };
        var client = new OpenAIClient(new ApiKeyCredential(selection.ApiKey), options);
#pragma warning disable OPENAI001
        var chat = new StatelessResponsesChatClient(client.GetResponsesClient().AsIChatClient(selection.Model.Id));
#pragma warning restore OPENAI001
        return new OpenAiCodexChatOptionsClient(chat, requestContext);
    }
}

internal sealed record OpenAiCodexRequestMetadata(string? SessionId);

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
internal sealed class OpenAiCodexChatOptionsClient(IChatClient inner, OpenAiCodexRequestContext requestContext)
    : DelegatingChatClient(inner)
{
    public override async Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages,
        ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        using var scope = requestContext.Push(new(ClampSessionId(options?.ConversationId ?? OpenAiCodexRequestContext.GetCurrentSessionId())));
        return await base.GetResponseAsync(messages, options, cancellationToken).ConfigureAwait(false);
    }

    public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        using var scope = requestContext.Push(new(ClampSessionId(options?.ConversationId ?? OpenAiCodexRequestContext.GetCurrentSessionId())));
        await foreach (var update in base.GetStreamingResponseAsync(messages, options, cancellationToken)
            .WithCancellation(cancellationToken).ConfigureAwait(false))
            yield return update;
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

    public OpenAiCodexRequestHandler(string accessToken, OpenAiCodexRequestContext requestContext,
        HttpMessageHandler innerHandler) : base(innerHandler)
    {
        _accessToken = accessToken;
        _accountId = ExtractAccountId(accessToken);
        _requestContext = requestContext;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        if (request.RequestUri is { } requestUri) request.RequestUri = ResolveCodexUri(requestUri);

        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _accessToken);
        request.Headers.Remove("api-key");
        request.Headers.Remove("chatgpt-account-id");
        request.Headers.TryAddWithoutValidation("chatgpt-account-id", _accountId);
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
            await ProjectBodyAsync(request, metadata.SessionId, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            await ProjectBodyAsync(request, null, cancellationToken).ConfigureAwait(false);
        }

        return await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
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

    private static async Task ProjectBodyAsync(HttpRequestMessage request, string? sessionId,
        CancellationToken cancellationToken)
    {
        if (request.Content is not { } content) return;
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
