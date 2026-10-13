using System.Net;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using PiSharp.Runtime.Sessions;

namespace PiSharp.Cli;

// The SDK owns response parsing; raw fragments support argument repair and RPC progress.
internal sealed class ProviderToolCallDeltaCapture : IProviderToolCallDeltaSource
{
    private Capture? _active;

    public DelegatingHandler CreateHandler(HttpMessageHandler? innerHandler = null) => new CaptureHandler(this, innerHandler);

    public IDisposable? EnsureCapture() => Volatile.Read(ref _active) is null ? BeginToolCallDeltaCapture() : null;

    public string? Arguments(string callId) => Volatile.Read(ref _active)?.Arguments(callId);

    public IProviderToolCallDeltaCapture BeginToolCallDeltaCapture()
    {
        var capture = new Capture(this);
        if (Interlocked.CompareExchange(ref _active, capture, null) is not null)
            throw new InvalidOperationException("A provider tool-call stream is already active.");
        return capture;
    }

    private void Release(Capture capture)
    {
        Interlocked.CompareExchange(ref _active, null, capture);
        capture.Complete();
    }

    private sealed class CaptureHandler : DelegatingHandler
    {
        private readonly ProviderToolCallDeltaCapture _owner;

        public CaptureHandler(ProviderToolCallDeltaCapture owner, HttpMessageHandler? innerHandler)
        {
            _owner = owner;
            if (innerHandler is not null) InnerHandler = innerHandler;
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            HttpResponseMessage response;
            try { response = await base.SendAsync(request, cancellationToken); }
            catch
            {
                Volatile.Read(ref _owner._active)?.Complete();
                throw;
            }
            var capture = Volatile.Read(ref _owner._active);
            if (capture is not null && request.RequestUri?.AbsolutePath is { } path &&
                (path.EndsWith("/chat/completions", StringComparison.Ordinal) || path.EndsWith("/messages", StringComparison.Ordinal)))
            {
                if (response.IsSuccessStatusCode && response.Content.Headers.ContentType?.MediaType == "text/event-stream")
                {
                    var original = response.Content;
                    response.Content = new CapturingContent(original, capture);
                    foreach (var header in original.Headers)
                        response.Content.Headers.TryAddWithoutValidation(header.Key, header.Value);
                }
                else
                {
                    if (!response.IsSuccessStatusCode)
                    {
                        // Preserve the HTTP error body when SDK stream cleanup masks its status.
                        var original = response.Content;
                        var body = await original.ReadAsByteArrayAsync(cancellationToken);
                        var replacement = new ByteArrayContent(body);
                        foreach (var header in original.Headers)
                            replacement.Headers.TryAddWithoutValidation(header.Key, header.Value);
                        response.Content = replacement;
                        original.Dispose();
                        var shouldRetry = response.Headers.TryGetValues("x-should-retry", out var values)
                            ? values.FirstOrDefault() : null;
                        if (!ProviderRequestRetryPolicy.IsRetryableStatus((int)response.StatusCode, shouldRetry))
                        {
                            capture.SetResponseFailure(response.StatusCode, Encoding.UTF8.GetString(body));
                            capture.Complete();
                        }
                    }
                    else capture.Complete();
                }
            }
            return response;
        }
    }

    private sealed class CapturingContent(HttpContent inner, Capture capture) : HttpContent
    {
        protected override async Task<Stream> CreateContentReadStreamAsync() =>
            new CapturingStream(await inner.ReadAsStreamAsync(), capture);

        protected override async Task<Stream> CreateContentReadStreamAsync(CancellationToken cancellationToken) =>
            new CapturingStream(await inner.ReadAsStreamAsync(cancellationToken), capture);

        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context)
        {
            await using var captured = await CreateContentReadStreamAsync();
            await captured.CopyToAsync(stream);
        }

        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context,
            CancellationToken cancellationToken)
        {
            await using var captured = await CreateContentReadStreamAsync(cancellationToken);
            await captured.CopyToAsync(stream, cancellationToken);
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

    private sealed class CapturingStream(Stream inner, Capture capture) : Stream
    {
        private readonly SseToolCallParser _parser = new(capture.Publish);
        private int _completed;

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
            try
            {
                var read = inner.Read(buffer, offset, count);
                Observe(buffer.AsSpan(offset, read));
                return read;
            }
            catch { Complete(); throw; }
        }

        public override int Read(Span<byte> buffer)
        {
            try
            {
                var read = inner.Read(buffer);
                Observe(buffer[..read]);
                return read;
            }
            catch { Complete(); throw; }
        }

        public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            try
            {
                var read = await inner.ReadAsync(buffer.AsMemory(offset, count), cancellationToken);
                Observe(buffer.AsSpan(offset, read));
                return read;
            }
            catch { Complete(); throw; }
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            try
            {
                var read = await inner.ReadAsync(buffer, cancellationToken);
                Observe(buffer.Span[..read]);
                return read;
            }
            catch { Complete(); throw; }
        }

        private void Observe(ReadOnlySpan<byte> bytes)
        {
            if (bytes.Length == 0) Complete();
            else _parser.Append(bytes);
        }

        private void Complete()
        {
            if (Interlocked.Exchange(ref _completed, 1) != 0) return;
            _parser.Complete();
            capture.Complete();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                Complete();
                inner.Dispose();
            }
            base.Dispose(disposing);
        }
    }

    private sealed class Capture(ProviderToolCallDeltaCapture owner) : IProviderToolCallDeltaCapture
    {
        private readonly Channel<ProviderToolCallDelta> _events = Channel.CreateUnbounded<ProviderToolCallDelta>(
            new UnboundedChannelOptions { SingleReader = true });
        private int _completed;
        private readonly object _argumentsGate = new();
        private readonly Dictionary<int, (string? Id, StringBuilder Text)> _arguments = [];

        public Exception? ResponseFailure { get; private set; }

        public void SetResponseFailure(HttpStatusCode statusCode, string body) =>
            ResponseFailure = new HttpRequestException(
                $"Response status code does not indicate success: {(int)statusCode} ({statusCode}). {body}",
                null, statusCode);

        public void Publish(ProviderToolCallDelta delta)
        {
            lock (_argumentsGate)
            {
                var call = _arguments.GetValueOrDefault(delta.Index, (null, new StringBuilder()));
                _arguments[delta.Index] = (delta.CallId ?? call.Id, call.Text);
                if (delta.Arguments is not null) call.Text.Append(delta.Arguments);
            }
            if (Volatile.Read(ref _completed) == 0) _events.Writer.TryWrite(delta);
        }

        public string? Arguments(string callId)
        {
            lock (_argumentsGate)
                return _arguments.Values.FirstOrDefault(call => call.Id == callId).Text?.ToString();
        }

        public async IAsyncEnumerable<ProviderToolCallDelta> ReadAllAsync(
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await foreach (var item in _events.Reader.ReadAllAsync(cancellationToken)) yield return item;
        }

        public void Complete()
        {
            if (Interlocked.Exchange(ref _completed, 1) == 0) _events.Writer.TryComplete();
        }

        public void Dispose() => owner.Release(this);
    }

    private sealed class SseToolCallParser(Action<ProviderToolCallDelta> publish)
    {
        private readonly Decoder _decoder = Encoding.UTF8.GetDecoder();
        private readonly StringBuilder _line = new();
        private readonly StringBuilder _data = new();

        public void Append(ReadOnlySpan<byte> bytes)
        {
            var chars = new char[Encoding.UTF8.GetMaxCharCount(bytes.Length)];
            var count = _decoder.GetChars(bytes, chars, flush: false);
            for (var i = 0; i < count; i++) Append(chars[i]);
        }

        public void Complete()
        {
            Span<char> chars = stackalloc char[4];
            var count = _decoder.GetChars(ReadOnlySpan<byte>.Empty, chars, flush: true);
            for (var i = 0; i < count; i++) Append(chars[i]);
            if (_line.Length > 0) ProcessLine();
            Dispatch();
        }

        private void Append(char value)
        {
            if (value == '\n') ProcessLine();
            else _line.Append(value);
        }

        private void ProcessLine()
        {
            if (_line.Length > 0 && _line[^1] == '\r') _line.Length--;
            if (_line.Length == 0) Dispatch();
            else if (_line.ToString().StartsWith("data:", StringComparison.Ordinal))
            {
                if (_data.Length > 0) _data.Append('\n');
                var value = _line.ToString(5, _line.Length - 5);
                if (value.StartsWith(' ')) value = value[1..];
                _data.Append(value);
            }
            _line.Clear();
        }

        private void Dispatch()
        {
            if (_data.Length == 0) return;
            var data = _data.ToString();
            _data.Clear();
            if (data == "[DONE]") return;
            try
            {
                using var document = JsonDocument.Parse(data);
                if (document.RootElement.TryGetProperty("type", out var type))
                {
                    var root = document.RootElement;
                    if (type.GetString() == "content_block_start" && root.GetProperty("content_block") is { } block &&
                        block.GetProperty("type").GetString() == "tool_use")
                        publish(new(root.GetProperty("index").GetInt32(), block.GetProperty("id").GetString(),
                            block.GetProperty("name").GetString(), null));
                    else if (type.GetString() == "content_block_delta" && root.GetProperty("delta") is { } blockDelta &&
                        blockDelta.TryGetProperty("partial_json", out var partial))
                        publish(new(root.GetProperty("index").GetInt32(), null, null, partial.GetString()));
                }
                if (!document.RootElement.TryGetProperty("choices", out var choices)) return;
                foreach (var choice in choices.EnumerateArray())
                {
                    if (!choice.TryGetProperty("delta", out var delta) ||
                        !delta.TryGetProperty("tool_calls", out var calls)) continue;
                    foreach (var call in calls.EnumerateArray())
                    {
                        if (!call.TryGetProperty("index", out var index)) continue;
                        var id = call.TryGetProperty("id", out var idValue) ? idValue.GetString() : null;
                        string? name = null;
                        string? arguments = null;
                        if (call.TryGetProperty("function", out var function))
                        {
                            if (function.TryGetProperty("name", out var nameValue)) name = nameValue.GetString();
                            if (function.TryGetProperty("arguments", out var argumentsValue)) arguments = argumentsValue.GetString();
                        }
                        if (id is not null || name is not null || arguments is not null)
                            publish(new ProviderToolCallDelta(index.GetInt32(), id, name, arguments));
                    }
                }
            }
            catch (Exception error) when (error is JsonException or InvalidOperationException or FormatException or KeyNotFoundException) { }
        }
    }
}
