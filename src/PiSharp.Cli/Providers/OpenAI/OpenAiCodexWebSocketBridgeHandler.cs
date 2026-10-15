using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace PiSharp.Cli;

/// <summary>Uses Codex's Responses WebSocket transport while preserving the SDK's SSE event reader.</summary>
internal sealed class OpenAiCodexWebSocketBridgeHandler(HttpMessageHandler innerHandler, TimeSpan connectTimeout)
    : DelegatingHandler(innerHandler)
{
    private const string WebSocketBeta = "responses_websockets=2026-02-06";
    private static readonly TimeSpan SessionWebSocketIdleTtl = TimeSpan.FromMinutes(5);
    private readonly ConcurrentDictionary<(string SessionId, string AccountId), CachedConnection> _connections = new();

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        if (request.Method != HttpMethod.Post || request.RequestUri is not { } requestUri ||
            !requestUri.AbsolutePath.EndsWith("/codex/responses", StringComparison.OrdinalIgnoreCase))
            return await base.SendAsync(request, cancellationToken).ConfigureAwait(false);

        var originalBody = request.Content is { } content
            ? await content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false)
            : "{}"u8.ToArray();
        ReplaceContent(request, originalBody);

        var sessionId = request.Headers.TryGetValues("session-id", out var sessionValues)
            ? sessionValues.FirstOrDefault()
            : null;
        var accountId = request.Headers.TryGetValues("chatgpt-account-id", out var accountValues)
            ? accountValues.FirstOrDefault()
            : null;
        var cacheable = !string.IsNullOrWhiteSpace(sessionId) && !string.IsNullOrWhiteSpace(accountId);
        var connection = cacheable
            ? _connections.GetOrAdd((sessionId!, accountId!), static _ => new CachedConnection())
            : new CachedConnection();
        await connection.Busy.WaitAsync(cancellationToken).ConfigureAwait(false);

        ClientWebSocket? socket = null;
        var gateReleased = false;
        try
        {
            connection.CancelIdleExpiry();
            var retriedPreviousResponse = false;
            var retriedConnectionLimit = false;
            while (true)
            {
                var reused = false;
                if (connection.Socket is { State: WebSocketState.Open } openSocket)
                {
                    socket = openSocket;
                    reused = true;
                }
                else
                {
                    connection.Socket?.Dispose();
                    connection.Socket = null;
                    connection.Continuation = null;
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    if (connectTimeout > TimeSpan.Zero) timeout.CancelAfter(connectTimeout);
                    socket = CreateSocket(request);
                    await socket.ConnectAsync(ToWebSocketUri(requestUri), timeout.Token).ConfigureAwait(false);
                    connection.Socket = socket;
                }

                var continuation = reused ? connection.Continuation : null;
                connection.Continuation = null;
                var requestBody = continuation is not null &&
                    TryCreateCachedRequestBody(originalBody, continuation, out var cachedRequestBody)
                    ? cachedRequestBody
                    : originalBody;
                var createMessage = CreateResponseCreateMessage(requestBody);
                await socket.SendAsync(createMessage, WebSocketMessageType.Text, endOfMessage: true, cancellationToken)
                    .ConfigureAwait(false);
                var firstEvent = await ReceiveWebSocketMessageAsync(socket, cancellationToken).ConfigureAwait(false);
                var firstErrorCode = GetCodexErrorCode(firstEvent);
                if (firstErrorCode == "previous_response_not_found" && !retriedPreviousResponse)
                {
                    retriedPreviousResponse = true;
                    ResetConnection(connection);
                    socket = null;
                    continue;
                }
                if (firstErrorCode == "websocket_connection_limit_reached")
                {
                    if (!retriedConnectionLimit)
                    {
                        retriedConnectionLimit = true;
                        ResetConnection(connection);
                        socket = null;
                        continue;
                    }

                    ResetConnection(connection);
                    connection.Busy.Release();
                    gateReleased = true;
                    ReplaceContent(request, originalBody);
                    return await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
                }

                var response = new HttpResponseMessage(HttpStatusCode.OK)
                {
                    RequestMessage = request,
                    Content = new WebSocketEventContent(socket, firstEvent,
                        responseEvent => connection.Continuation = CreateContinuation(originalBody, responseEvent),
                        keepConnection => Release(connection, cacheable, keepConnection))
                };
                socket = null;
                return response;
            }
        }
        catch (Exception exception) when (!gateReleased && !cancellationToken.IsCancellationRequested &&
            IsTransportFailure(exception))
        {
            socket?.Dispose();
            ResetConnection(connection);
            connection.Busy.Release();
            gateReleased = true;
            ReplaceContent(request, originalBody);
            return await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            socket?.Dispose();
            ResetConnection(connection);
            if (!gateReleased) connection.Busy.Release();
            throw;
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            foreach (var connection in _connections.Values)
            {
                connection.CancelIdleExpiry();
                connection.Socket?.Dispose();
                connection.Socket = null;
            }
            _connections.Clear();
        }
        base.Dispose(disposing);
    }

    private static ClientWebSocket CreateSocket(HttpRequestMessage request)
    {
        var socket = new ClientWebSocket();
        foreach (var header in request.Headers)
        {
            if (header.Key.Equals("OpenAI-Beta", StringComparison.OrdinalIgnoreCase))
            {
                socket.Options.SetRequestHeader("OpenAI-Beta", WebSocketBeta);
                continue;
            }

            if (IsWebSocketManagedHeader(header.Key)) continue;
            socket.Options.SetRequestHeader(header.Key, string.Join(", ", header.Value));
        }
        if (!request.Headers.Contains("OpenAI-Beta"))
            socket.Options.SetRequestHeader("OpenAI-Beta", WebSocketBeta);
        return socket;
    }

    private static bool IsTransportFailure(Exception exception) => exception is
        WebSocketException or HttpRequestException or IOException or SocketException or OperationCanceledException;

    private static bool IsWebSocketManagedHeader(string name) =>
        name.Equals("Accept", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("Connection", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("Content-Length", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("Content-Type", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("Host", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("Upgrade", StringComparison.OrdinalIgnoreCase) ||
        name.StartsWith("Sec-WebSocket-", StringComparison.OrdinalIgnoreCase);

    private static Uri ToWebSocketUri(Uri uri) => new UriBuilder(uri)
    {
        Scheme = uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ? "wss" : "ws",
        Port = uri.IsDefaultPort ? -1 : uri.Port
    }.Uri;

    private static async Task<string> ReceiveWebSocketMessageAsync(ClientWebSocket socket,
        CancellationToken cancellationToken)
    {
        using var message = new MemoryStream();
        var segment = new byte[16 * 1024];
        ValueWebSocketReceiveResult result;
        do
        {
            result = await socket.ReceiveAsync(segment.AsMemory(), cancellationToken).ConfigureAwait(false);
            if (result.MessageType == WebSocketMessageType.Close)
                throw new WebSocketException("Codex WebSocket closed before the response started.");
            message.Write(segment, 0, result.Count);
        }
        while (!result.EndOfMessage);
        return Encoding.UTF8.GetString(message.GetBuffer(), 0, checked((int)message.Length));
    }

    private static string? GetCodexErrorCode(string eventJson)
    {
        return GetCodexFailure(eventJson)?.Code;
    }

    private static CodexFailure? GetCodexFailure(string eventJson)
    {
        using var document = JsonDocument.Parse(eventJson);
        var root = document.RootElement;
        if (!root.TryGetProperty("type", out var type) || type.GetString() is not ("response.failed" or "error"))
            return null;

        var error = type.GetString() == "response.failed" && root.TryGetProperty("response", out var response) &&
            response.TryGetProperty("error", out var responseError)
            ? responseError
            : root.TryGetProperty("error", out var rootError) ? rootError : root;
        var code = error.TryGetProperty("code", out var codeElement) ? codeElement.GetString() : null;
        var message = error.TryGetProperty("message", out var messageElement) ? messageElement.GetString() : null;
        return new CodexFailure(code, message ?? "Codex Responses request failed.");
    }

    private sealed record CodexFailure(string? Code, string Message);

    private static byte[] CreateResponseCreateMessage(byte[] requestBody)
    {
        using var document = JsonDocument.Parse(requestBody);
        if (document.RootElement.ValueKind != JsonValueKind.Object)
            throw new JsonException("Codex Responses request body must be a JSON object.");

        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("type", "response.create");
            foreach (var property in document.RootElement.EnumerateObject())
                property.WriteTo(writer);
            writer.WriteEndObject();
        }
        return buffer.ToArray();
    }

    private static bool TryCreateCachedRequestBody(byte[] requestBody, CachedContinuation continuation,
        out byte[] cachedRequestBody)
    {
        cachedRequestBody = [];
        using var currentDocument = JsonDocument.Parse(requestBody);
        using var previousDocument = JsonDocument.Parse(continuation.RequestBody);
        var current = currentDocument.RootElement;
        var previous = previousDocument.RootElement;
        if (current.ValueKind != JsonValueKind.Object || previous.ValueKind != JsonValueKind.Object ||
            !string.Equals(CanonicalObjectWithoutInput(current), CanonicalObjectWithoutInput(previous), StringComparison.Ordinal) ||
            !current.TryGetProperty("input", out var currentInput) || currentInput.ValueKind != JsonValueKind.Array ||
            !previous.TryGetProperty("input", out var previousInput) || previousInput.ValueKind != JsonValueKind.Array)
            return false;

        var currentItems = currentInput.EnumerateArray().ToArray();
        var previousItems = previousInput.EnumerateArray().ToArray();
        var baselineCount = previousItems.Length + continuation.ResponseItems.Length;
        if (currentItems.Length < baselineCount) return false;

        for (var index = 0; index < previousItems.Length; index++)
            if (!JsonElementsEqual(currentItems[index], previousItems[index])) return false;
        for (var index = 0; index < continuation.ResponseItems.Length; index++)
        {
            using var responseItem = JsonDocument.Parse(continuation.ResponseItems[index]);
            if (!ContinuationItemsEqual(currentItems[previousItems.Length + index], responseItem.RootElement)) return false;
        }

        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            foreach (var property in current.EnumerateObject())
                if (!property.NameEquals("input") && !property.NameEquals("previous_response_id"))
                    property.WriteTo(writer);
            writer.WriteString("previous_response_id", continuation.ResponseId);
            writer.WritePropertyName("input");
            writer.WriteStartArray();
            for (var index = baselineCount; index < currentItems.Length; index++)
                currentItems[index].WriteTo(writer);
            writer.WriteEndArray();
            writer.WriteEndObject();
        }
        cachedRequestBody = buffer.ToArray();
        return true;
    }

    private static bool ContinuationItemsEqual(JsonElement current, JsonElement response)
    {
        if (JsonElementsEqual(current, response)) return true;
        if (current.ValueKind != JsonValueKind.Object || response.ValueKind != JsonValueKind.Object ||
            !current.TryGetProperty("type", out var currentType) || currentType.GetString() != "function_call" ||
            !response.TryGetProperty("type", out var responseType) || responseType.GetString() != "function_call" ||
            !current.TryGetProperty("arguments", out var currentArguments) || currentArguments.ValueKind != JsonValueKind.String ||
            !response.TryGetProperty("arguments", out var responseArguments) || responseArguments.ValueKind != JsonValueKind.String ||
            !string.Equals(CanonicalObjectWithoutProperty(current, "arguments"),
                CanonicalObjectWithoutProperty(response, "arguments"), StringComparison.Ordinal))
            return false;

        try
        {
            using var currentDocument = JsonDocument.Parse(currentArguments.GetString()!);
            using var responseDocument = JsonDocument.Parse(responseArguments.GetString()!);
            return JsonElementsEqual(currentDocument.RootElement, responseDocument.RootElement);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static CachedContinuation? CreateContinuation(byte[] requestBody, JsonElement responseEvent)
    {
        using var requestDocument = JsonDocument.Parse(requestBody);
        if (requestDocument.RootElement.ValueKind != JsonValueKind.Object ||
            !responseEvent.TryGetProperty("response", out var response) || response.ValueKind != JsonValueKind.Object ||
            !response.TryGetProperty("id", out var idElement) || idElement.ValueKind != JsonValueKind.String ||
            string.IsNullOrWhiteSpace(idElement.GetString()) ||
            !requestDocument.RootElement.TryGetProperty("input", out var input) ||
            input.ValueKind != JsonValueKind.Array)
            return null;

        var responseItems = new List<string>();
        if (response.TryGetProperty("output", out var output) && output.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in output.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object || !item.TryGetProperty("type", out var typeElement))
                    return null;
                var type = typeElement.GetString();
                if (type is "function_call_output" or "custom_tool_call_output") continue;
                if (TryNormalizeResponseItem(item, type, out var normalized)) responseItems.Add(normalized);
                else return null;
            }
        }

        return new CachedContinuation(Encoding.UTF8.GetString(requestBody), idElement.GetString()!, responseItems.ToArray());
    }

    private static bool TryNormalizeResponseItem(JsonElement item, string? type, out string normalized)
    {
        normalized = string.Empty;
        if (type is not ("message" or "reasoning" or "function_call" or "custom_tool_call")) return false;
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("type", type);
            if (type == "message")
            {
                if (!item.TryGetProperty("role", out var role) || !item.TryGetProperty("content", out var content)) return false;
                writer.WritePropertyName("role");
                role.WriteTo(writer);
                writer.WritePropertyName("content");
                content.WriteTo(writer);
            }
            else if (type == "reasoning")
            {
                if (item.TryGetProperty("summary", out var summary))
                {
                    writer.WritePropertyName("summary");
                    summary.WriteTo(writer);
                }
                if (item.TryGetProperty("encrypted_content", out var encryptedContent))
                {
                    writer.WritePropertyName("encrypted_content");
                    encryptedContent.WriteTo(writer);
                }
            }
            else if (type == "function_call")
            {
                if (!CopyProperty(writer, item, "call_id") || !CopyProperty(writer, item, "name") ||
                    !CopyProperty(writer, item, "arguments")) return false;
                CopyOptionalProperty(writer, item, "namespace");
            }
            else
            {
                if (!CopyProperty(writer, item, "call_id") || !CopyProperty(writer, item, "name") ||
                    !CopyProperty(writer, item, "input")) return false;
                CopyOptionalProperty(writer, item, "namespace");
            }
            writer.WriteEndObject();
        }
        normalized = Encoding.UTF8.GetString(buffer.ToArray());
        return true;
    }

    private static bool CopyProperty(Utf8JsonWriter writer, JsonElement source, string name)
    {
        if (!source.TryGetProperty(name, out var value)) return false;
        writer.WritePropertyName(name);
        value.WriteTo(writer);
        return true;
    }

    private static void CopyOptionalProperty(Utf8JsonWriter writer, JsonElement source, string name)
    {
        if (source.TryGetProperty(name, out var value))
        {
            writer.WritePropertyName(name);
            value.WriteTo(writer);
        }
    }

    private static string CanonicalObjectWithoutInput(JsonElement element)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            foreach (var property in element.EnumerateObject()
                .Where(property => !property.NameEquals("input") && !property.NameEquals("previous_response_id"))
                .OrderBy(property => property.Name, StringComparer.Ordinal))
            {
                writer.WritePropertyName(property.Name);
                WriteCanonicalValue(writer, property.Value);
            }
            writer.WriteEndObject();
        }
        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    private static string CanonicalObjectWithoutProperty(JsonElement element, string excludedProperty)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            foreach (var property in element.EnumerateObject()
                .Where(property => !property.NameEquals(excludedProperty))
                .OrderBy(property => property.Name, StringComparer.Ordinal))
            {
                writer.WritePropertyName(property.Name);
                WriteCanonicalValue(writer, property.Value);
            }
            writer.WriteEndObject();
        }
        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    private static bool JsonElementsEqual(JsonElement left, JsonElement right)
    {
        using var leftBuffer = new MemoryStream();
        using var rightBuffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(leftBuffer)) WriteCanonicalValue(writer, left);
        using (var writer = new Utf8JsonWriter(rightBuffer)) WriteCanonicalValue(writer, right);
        return leftBuffer.ToArray().AsSpan().SequenceEqual(rightBuffer.ToArray());
    }

    private static void WriteCanonicalValue(Utf8JsonWriter writer, JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            writer.WriteStartObject();
            foreach (var property in value.EnumerateObject().OrderBy(property => property.Name, StringComparer.Ordinal))
            {
                writer.WritePropertyName(property.Name);
                WriteCanonicalValue(writer, property.Value);
            }
            writer.WriteEndObject();
        }
        else if (value.ValueKind == JsonValueKind.Array)
        {
            writer.WriteStartArray();
            foreach (var item in value.EnumerateArray()) WriteCanonicalValue(writer, item);
            writer.WriteEndArray();
        }
        else value.WriteTo(writer);
    }

    private static void ReplaceContent(HttpRequestMessage request, byte[] body)
    {
        var previous = request.Content;
        var replacement = new ByteArrayContent(body);
        if (previous is not null)
        {
            foreach (var header in previous.Headers)
                if (!header.Key.Equals("Content-Length", StringComparison.OrdinalIgnoreCase))
                    replacement.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }
        request.Content = replacement;
        previous?.Dispose();
    }

    private static void Release(CachedConnection connection, bool cacheable, bool keep)
    {
        if (!keep || !cacheable)
        {
            connection.Socket?.Dispose();
            connection.Socket = null;
            connection.Continuation = null;
        }
        else
        {
            connection.ScheduleIdleExpiry(ExpireIdleConnection);
        }
        connection.Busy.Release();
    }

    private static void ExpireIdleConnection(object? state)
    {
        if (state is not IdleExpiryState expiry || !expiry.Connection.Busy.Wait(0)) return;
        try
        {
            if (!expiry.Connection.TryConsumeIdleExpiry(expiry.Version)) return;
            ResetConnection(expiry.Connection);
        }
        finally
        {
            expiry.Connection.Busy.Release();
        }
    }

    private static void ResetConnection(CachedConnection connection)
    {
        connection.Socket?.Dispose();
        connection.Socket = null;
        connection.Continuation = null;
    }

    private sealed class CachedConnection
    {
        private readonly object _idleExpiryLock = new();
        private Timer? _idleTimer;
        private long _idleExpiryVersion;
        public SemaphoreSlim Busy { get; } = new(1, 1);
        public ClientWebSocket? Socket { get; set; }
        public CachedContinuation? Continuation { get; set; }

        public void CancelIdleExpiry()
        {
            lock (_idleExpiryLock)
            {
                _idleExpiryVersion++;
                _idleTimer?.Dispose();
                _idleTimer = null;
            }
        }

        public void ScheduleIdleExpiry(TimerCallback callback)
        {
            lock (_idleExpiryLock)
            {
                _idleExpiryVersion++;
                _idleTimer?.Dispose();
                _idleTimer = new Timer(callback, new IdleExpiryState(this, _idleExpiryVersion),
                    SessionWebSocketIdleTtl, Timeout.InfiniteTimeSpan);
            }
        }

        public bool TryConsumeIdleExpiry(long version)
        {
            lock (_idleExpiryLock)
            {
                if (_idleExpiryVersion != version) return false;
                _idleExpiryVersion++;
                _idleTimer?.Dispose();
                _idleTimer = null;
                return true;
            }
        }
    }

    private sealed record IdleExpiryState(CachedConnection Connection, long Version);
    private sealed record CachedContinuation(string RequestBody, string ResponseId, string[] ResponseItems);

    private sealed class WebSocketEventContent : HttpContent
    {
        private readonly WebSocketEventStream _stream;

        public WebSocketEventContent(ClientWebSocket socket, string firstEvent, Action<JsonElement> onCompleted,
            Action<bool> release)
        {
            _stream = new WebSocketEventStream(socket, firstEvent, onCompleted, release);
            Headers.ContentType = new("text/event-stream");
        }

        protected override Task<Stream> CreateContentReadStreamAsync() => Task.FromResult<Stream>(_stream);

        protected override Task<Stream> CreateContentReadStreamAsync(CancellationToken cancellationToken) =>
            Task.FromResult<Stream>(_stream);

        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
            _stream.CopyToAsync(stream);

        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context,
            CancellationToken cancellationToken) => _stream.CopyToAsync(stream, cancellationToken);

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) _stream.Dispose();
            base.Dispose(disposing);
        }
    }

    private sealed class WebSocketEventStream(ClientWebSocket socket, string firstEvent,
        Action<JsonElement> onCompleted, Action<bool> release) : Stream
    {
        private string? _firstEvent = firstEvent;
        private byte[] _pending = [];
        private int _pendingOffset;
        private bool _sawSuccessfulCompletion;
        private bool _emitDone;
        private bool _doneEmitted;
        private bool _released;

        public override bool CanRead => !_released;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override int Read(byte[] buffer, int offset, int count) =>
            ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            ReadCoreAsync(buffer, cancellationToken);

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        private async ValueTask<int> ReadCoreAsync(Memory<byte> destination, CancellationToken cancellationToken)
        {
            if (destination.IsEmpty) return 0;
            if (_released) return 0;
            if (_pendingOffset < _pending.Length) return CopyPending(destination);
            if (_emitDone && !_doneEmitted)
            {
                _pending = "data: [DONE]\n\n"u8.ToArray();
                _pendingOffset = 0;
                _doneEmitted = true;
                return CopyPending(destination);
            }
            if (_doneEmitted)
            {
                Release(_sawSuccessfulCompletion);
                return 0;
            }

            try
            {
                var eventJson = _firstEvent ?? await ReceiveWebSocketMessageAsync(socket, cancellationToken).ConfigureAwait(false);
                _firstEvent = null;
                using var document = JsonDocument.Parse(eventJson);
                var eventType = document.RootElement.TryGetProperty("type", out var typeElement)
                    ? typeElement.GetString()
                    : null;
                if (GetCodexFailure(eventJson) is { } failure)
                {
                    var detail = failure.Code is { Length: > 0 }
                        ? $"Codex Responses error '{failure.Code}': {failure.Message}"
                        : failure.Message;
                    throw new HttpRequestException(detail);
                }
                var successfulCompletion = eventType is "response.completed" or "response.done" or "response.incomplete";
                var terminal = successfulCompletion || eventType is "error" or "response.failed" or "response.cancelled";
                _sawSuccessfulCompletion = successfulCompletion;
                if (successfulCompletion) onCompleted(document.RootElement);
                _emitDone = terminal;
                _pending = Encoding.UTF8.GetBytes($"data: {eventJson}\n\n");
                _pendingOffset = 0;
                return CopyPending(destination);
            }
            catch
            {
                Release(keep: false);
                throw;
            }
        }

        private int CopyPending(Memory<byte> destination)
        {
            var count = Math.Min(destination.Length, _pending.Length - _pendingOffset);
            _pending.AsMemory(_pendingOffset, count).CopyTo(destination);
            _pendingOffset += count;
            return count;
        }

        private void Release(bool keep)
        {
            if (_released) return;
            _released = true;
            release(keep);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) Release(_sawSuccessfulCompletion && _doneEmitted);
            base.Dispose(disposing);
        }
    }
}
