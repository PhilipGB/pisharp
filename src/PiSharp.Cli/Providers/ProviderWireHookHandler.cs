using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using PiSharp.Runtime.Extensions;

namespace PiSharp.Cli;

/// <summary>Runs provider wire hooks at the final HTTP request boundary and observes raw SSE frames.</summary>
internal sealed class ProviderWireHookHandler : DelegatingHandler
{
    private readonly string _providerId;
    private readonly string _modelId;
    private readonly string _protocol;
    private readonly bool _observeResponse;
    private readonly bool _observeStreamEvents;
    private readonly bool _observeWireActivity;

    internal ProviderWireHookHandler(string providerId, string modelId, string protocol,
        HttpMessageHandler innerHandler, bool observeResponse = true, bool observeStreamEvents = true,
        bool observeWireActivity = false) : base(innerHandler)
    {
        _providerId = providerId;
        _modelId = modelId;
        _protocol = protocol;
        _observeResponse = observeResponse;
        _observeStreamEvents = observeStreamEvents;
        _observeWireActivity = observeWireActivity;
    }

    private ProviderWireHookHandler(string providerId, string modelId, string protocol,
        bool observeWireActivity) : base()
    {
        _providerId = providerId;
        _modelId = modelId;
        _protocol = protocol;
        _observeResponse = true;
        _observeStreamEvents = true;
        _observeWireActivity = observeWireActivity;
    }

    internal static DelegatingHandler CreateWithActivity(ModelSelection selection, string protocol,
        HttpMessageHandler innerHandler, bool observeResponse = true, bool observeStreamEvents = true) =>
        new ProviderWireHookHandler(selection.Provider.Id, selection.Model.Id, protocol, innerHandler,
            observeResponse, observeStreamEvents, observeWireActivity: true);

    internal static DelegatingHandler CreateAnthropic(ModelSelection selection) =>
        new ProviderWireHookHandler(selection.Provider.Id, selection.Model.Id, "anthropic-messages",
            observeWireActivity: true);

    private static readonly HashSet<string> s_transportManagedHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "content-length", "transfer-encoding", "connection", "host"
    };

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        var invocation = ProviderWireInvocation.Current;
        var activity = _observeWireActivity ? ProviderWireActivity.CurrentObserver : null;
        if (invocation is not null)
        {
            await TransformPayloadAsync(request, invocation, cancellationToken).ConfigureAwait(false);
            await TransformHeadersAsync(request, invocation, cancellationToken).ConfigureAwait(false);
        }

        var response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
        ProviderRetryResponseCapture.Observe(response);
        if (invocation is null && activity is null) return response;

        var webSocketTransport = request.Options.TryGetValue(OpenAiCodexWebSocketBridgeHandler.UsedTransportKey,
            out var transport) && transport == OpenAiCodexWebSocketBridgeHandler.WebSocketTransportValue;
        if (invocation is not null && _observeResponse && !webSocketTransport)
            await invocation.ObserveResponseAsync(_providerId, _modelId, _protocol, (int)response.StatusCode,
                ReadHeaders(response), cancellationToken).ConfigureAwait(false);

        if ((_observeStreamEvents || activity is not null) && response.Content is { } content)
        {
            var observed = new ProviderWireEventContent(content, invocation, _providerId, _modelId, _protocol,
                cancellationToken, _observeStreamEvents, activity);
            foreach (var header in content.Headers)
                observed.Headers.TryAddWithoutValidation(header.Key, header.Value);
            response.Content = observed;
        }
        return response;
    }

    private async Task TransformPayloadAsync(HttpRequestMessage request, ProviderWireInvocation invocation,
        CancellationToken cancellationToken)
    {
        if (request.Content is not { } content) return;
        var bytes = await content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
        JsonDocument document;
        try { document = JsonDocument.Parse(bytes); }
        catch (JsonException) { return; }

        using (document)
        {
            var replacement = await invocation.TransformPayloadAsync(_providerId, _modelId, _protocol,
                document.RootElement, cancellationToken).ConfigureAwait(false);
            if (replacement.ValueKind == JsonValueKind.Undefined) return;
            var replacementContent = new ByteArrayContent(Encoding.UTF8.GetBytes(replacement.GetRawText()));
            foreach (var header in content.Headers)
                if (!header.Key.Equals("Content-Length", StringComparison.OrdinalIgnoreCase))
                    replacementContent.Headers.TryAddWithoutValidation(header.Key, header.Value);
            request.Content = replacementContent;
            content.Dispose();
        }
    }

    private async Task TransformHeadersAsync(HttpRequestMessage request, ProviderWireInvocation invocation,
        CancellationToken cancellationToken)
    {
        var headers = ReadHeaders(request);
        foreach (var name in s_transportManagedHeaders) headers.Remove(name);
        var transformed = await invocation.TransformHeadersAsync(_providerId, _modelId, _protocol, headers,
            cancellationToken).ConfigureAwait(false);

        request.Headers.Clear();
        if (request.Content is { } content) content.Headers.Clear();
        foreach (var (name, value) in transformed)
        {
            if (value is null || s_transportManagedHeaders.Contains(name)) continue;
            if (!request.Headers.TryAddWithoutValidation(name, value) && request.Content is { } targetContent)
                targetContent.Headers.TryAddWithoutValidation(name, value);
        }
    }

    private static Dictionary<string, string?> ReadHeaders(HttpRequestMessage request)
    {
        var headers = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        Add(request.Headers, headers);
        if (request.Content is { } content) Add(content.Headers, headers);
        return headers;
    }

    private static Dictionary<string, string> ReadHeaders(HttpResponseMessage response)
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var header in response.Headers)
            headers[header.Key] = string.Join(", ", header.Value);
        if (response.Content is { } content)
            foreach (var header in content.Headers)
                headers[header.Key] = string.Join(", ", header.Value);
        return headers;
    }

    private static void Add(HttpHeaders source, IDictionary<string, string?> destination)
    {
        foreach (var header in source)
            if (!s_transportManagedHeaders.Contains(header.Key))
                destination[header.Key] = string.Join(", ", header.Value);
    }

    private sealed class ProviderWireEventContent(HttpContent inner, ProviderWireInvocation? invocation,
        string provider, string model, string api, CancellationToken requestCancellation,
        bool observeStreamEvents, Action? observeActivity) : HttpContent
    {
        protected override async Task<Stream> CreateContentReadStreamAsync() =>
            new ProviderWireEventStream(await inner.ReadAsStreamAsync().ConfigureAwait(false), invocation,
                provider, model, api, requestCancellation, observeStreamEvents, observeActivity);

        protected override async Task<Stream> CreateContentReadStreamAsync(CancellationToken cancellationToken) =>
            new ProviderWireEventStream(await inner.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false),
                invocation, provider, model, api, requestCancellation, observeStreamEvents, observeActivity);

        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context)
        {
            await using var content = await CreateContentReadStreamAsync().ConfigureAwait(false);
            await content.CopyToAsync(stream).ConfigureAwait(false);
        }

        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context,
            CancellationToken cancellationToken)
        {
            await using var content = await CreateContentReadStreamAsync(cancellationToken).ConfigureAwait(false);
            await content.CopyToAsync(stream, cancellationToken).ConfigureAwait(false);
        }

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) inner.Dispose();
            base.Dispose(disposing);
        }
    }

    private sealed class ProviderWireEventStream(Stream inner, ProviderWireInvocation? invocation,
        string provider, string model, string api, CancellationToken requestCancellation,
        bool observeStreamEvents, Action? observeActivity) : Stream
    {
        private readonly Decoder _decoder = Encoding.UTF8.GetDecoder();
        private readonly StringBuilder _line = new();
        private readonly StringBuilder _data = new();
        private string? _eventType;
        private bool _finished;

        public override bool CanRead => inner.CanRead;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override int Read(byte[] buffer, int offset, int count)
        {
            var read = inner.Read(buffer, offset, count);
            ProcessAsync(buffer.AsMemory(offset, read), read == 0, CancellationToken.None)
                .AsTask().GetAwaiter().GetResult();
            return read;
        }

        public override int Read(Span<byte> buffer)
        {
            var read = inner.Read(buffer);
            var copy = new byte[read];
            buffer[..read].CopyTo(copy);
            ProcessAsync(copy, read == 0, CancellationToken.None).AsTask().GetAwaiter().GetResult();
            return read;
        }

        public override async Task<int> ReadAsync(byte[] buffer, int offset, int count,
            CancellationToken cancellationToken)
        {
            var read = await inner.ReadAsync(buffer.AsMemory(offset, count), cancellationToken).ConfigureAwait(false);
            await ProcessAsync(buffer.AsMemory(offset, read), read == 0, cancellationToken).ConfigureAwait(false);
            return read;
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            var read = await inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            await ProcessAsync(buffer[..read], read == 0, cancellationToken).ConfigureAwait(false);
            return read;
        }

        private async ValueTask ProcessAsync(ReadOnlyMemory<byte> bytes, bool endOfStream,
            CancellationToken cancellationToken)
        {
            if (_finished) return;
            if (!bytes.IsEmpty) observeActivity?.Invoke();
            if (!observeStreamEvents || invocation is null) return;
            var charCount = _decoder.GetCharCount(bytes.Span, flush: endOfStream);
            var chars = new char[charCount];
            _decoder.GetChars(bytes.Span, chars, flush: endOfStream);
            foreach (var character in chars)
            {
                if (character == '\n')
                {
                    var line = _line.ToString().TrimEnd('\r');
                    _line.Clear();
                    await ProcessLineAsync(line, cancellationToken).ConfigureAwait(false);
                }
                else _line.Append(character);
            }

            if (!endOfStream) return;
            if (_line.Length > 0)
            {
                await ProcessLineAsync(_line.ToString().TrimEnd('\r'), cancellationToken).ConfigureAwait(false);
                _line.Clear();
            }
            await DispatchEventAsync(cancellationToken).ConfigureAwait(false);
            _finished = true;
        }

        private async Task ProcessLineAsync(string line, CancellationToken cancellationToken)
        {
            if (line.Length == 0)
            {
                await DispatchEventAsync(cancellationToken).ConfigureAwait(false);
                return;
            }
            if (line[0] == ':') return;
            var separator = line.IndexOf(':');
            var field = separator < 0 ? line : line[..separator];
            var value = separator < 0 ? string.Empty : line[(separator + 1)..];
            if (value.StartsWith(' ')) value = value[1..];
            if (field == "event") _eventType = value;
            else if (field == "data")
            {
                if (_data.Length > 0) _data.Append('\n');
                _data.Append(value);
            }
        }

        private async Task DispatchEventAsync(CancellationToken cancellationToken)
        {
            if (_data.Length == 0)
            {
                _eventType = null;
                return;
            }
            var data = _data.ToString();
            _data.Clear();
            var eventType = _eventType;
            _eventType = null;
            if (data == "[DONE]") return;
            try
            {
                using var document = JsonDocument.Parse(data);
                await invocation!.ObserveStreamEventAsync(provider, model, api, eventType,
                    document.RootElement, requestCancellation.CanBeCanceled ? requestCancellation : cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (JsonException)
            {
                // Only parsed provider events cross Pi's raw-stream callback boundary.
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) inner.Dispose();
            base.Dispose(disposing);
        }
    }
}
