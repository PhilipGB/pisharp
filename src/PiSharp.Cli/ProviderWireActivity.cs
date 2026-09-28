using System.Net;

namespace PiSharp.Cli;

/// <summary>Resets the active provider stream deadline when an HTTP response yields bytes.</summary>
internal static class ProviderWireActivity
{
    private static readonly AsyncLocal<Action?> s_observer = new();

    public static IDisposable Observe(Action onResponseBytes)
    {
        ArgumentNullException.ThrowIfNull(onResponseBytes);
        var previous = s_observer.Value;
        s_observer.Value = onResponseBytes;
        return new ObserverScope(previous);
    }

    internal static Action? CurrentObserver => s_observer.Value;

    private sealed class ObserverScope(Action? previous) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0) s_observer.Value = previous;
        }
    }
}

/// <summary>Wraps provider response bodies so stream reads can report wire activity to the active request.</summary>
internal sealed class ProviderWireActivityHandler : DelegatingHandler
{
    public ProviderWireActivityHandler() { }

    public ProviderWireActivityHandler(HttpMessageHandler innerHandler) : base(innerHandler) { }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        var observe = ProviderWireActivity.CurrentObserver;
        var response = await base.SendAsync(request, cancellationToken);
        ProviderRetryResponseCapture.Observe(response);
        if (observe is null || response.Content is not { } content) return response;

        var observedContent = new ObservedContent(content, observe);
        foreach (var header in content.Headers)
            observedContent.Headers.TryAddWithoutValidation(header.Key, header.Value);
        response.Content = observedContent;
        return response;
    }

    private sealed class ObservedContent(HttpContent inner, Action observe) : HttpContent
    {
        protected override async Task<Stream> CreateContentReadStreamAsync() =>
            new ObservedStream(await inner.ReadAsStreamAsync(), observe);

        protected override async Task<Stream> CreateContentReadStreamAsync(CancellationToken cancellationToken) =>
            new ObservedStream(await inner.ReadAsStreamAsync(cancellationToken), observe);

        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context)
        {
            await using var content = await CreateContentReadStreamAsync();
            await content.CopyToAsync(stream);
        }

        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context,
            CancellationToken cancellationToken)
        {
            await using var content = await CreateContentReadStreamAsync(cancellationToken);
            await content.CopyToAsync(stream, cancellationToken);
        }

        protected override bool TryComputeLength(out long length)
        {
            if (inner.Headers.ContentLength is { } contentLength)
            {
                length = contentLength;
                return true;
            }
            length = 0;
            return false;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) inner.Dispose();
            base.Dispose(disposing);
        }
    }

    private sealed class ObservedStream(Stream inner, Action observe) : Stream
    {
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
            if (read > 0) observe();
            return read;
        }

        public override int Read(Span<byte> buffer)
        {
            var read = inner.Read(buffer);
            if (read > 0) observe();
            return read;
        }

        public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            var read = await inner.ReadAsync(buffer.AsMemory(offset, count), cancellationToken);
            if (read > 0) observe();
            return read;
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var read = await inner.ReadAsync(buffer, cancellationToken);
            if (read > 0) observe();
            return read;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) inner.Dispose();
            base.Dispose(disposing);
        }
    }
}
