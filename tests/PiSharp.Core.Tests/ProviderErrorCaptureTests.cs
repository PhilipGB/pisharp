using System.Net;
using System.Text;
using PiSharp.Cli;

namespace PiSharp.Core.Tests;

/// <summary>
/// Item 6: the provider error-capture handler must bound the error body read to the
/// capture size (a misbehaving or malicious server streaming a multi-gigabyte error body
/// must not be materialized in memory), and must preserve the status/headers the retry
/// layer needs.
/// </summary>
public sealed class ProviderErrorCaptureTests
{
    private const int Cap = 8_192;

    [Fact]
    public async Task ErrorBodyReadStopsAtTheCaptureCap()
    {
        // The stream REFUSES any read that would cross the cap: the old implementation
        // (ReadAsByteArrayAsync materializes the entire body before truncating) would hit
        // the refusal, fall into the blanket catch, and produce an EMPTY body. The
        // bounded read stops exactly at the cap and keeps the full capture.
        using var client = new HttpClient(new ProviderErrorCaptureHandler(new ErrorResponseHandler()));
        using var request = new HttpRequestMessage(HttpMethod.Post, "http://localhost:9/v1/chat");
        request.Content = new StringContent("{}", Encoding.UTF8, "application/json");

        var error = await Assert.ThrowsAsync<ProviderHttpException>(
            () => client.SendAsync(request));

        Assert.Equal(400, error.Status);
        Assert.Equal(Cap, error.Body.Length);
        Assert.Equal(new string('x', Cap), error.Body);
        Assert.Equal("7", error.Headers["Retry-After"]);
        Assert.StartsWith("HTTP 400:", error.Message);
    }

    [Fact]
    public async Task SmallErrorBodyPassesThroughWhole()
    {
        using var client = new HttpClient(new ProviderErrorCaptureHandler(new ErrorResponseHandler(
            statusCode: 429, body: "rate limit reached")));
        using var request = new HttpRequestMessage(HttpMethod.Post, "http://localhost:9/v1/chat");
        request.Content = new StringContent("{}", Encoding.UTF8, "application/json");

        var error = await Assert.ThrowsAsync<ProviderHttpException>(
            () => client.SendAsync(request));

        Assert.Equal(429, error.Status);
        Assert.Equal("rate limit reached", error.Body);
    }

    [Fact]
    public async Task SuccessfulResponsePassesThroughUntouched()
    {
        using var handler = new ErrorResponseHandler(statusCode: 200, body: "ok");
        using var client = new HttpClient(new ProviderErrorCaptureHandler(handler));
        using var request = new HttpRequestMessage(HttpMethod.Post, "http://localhost:9/v1/chat");
        request.Content = new StringContent("{}", Encoding.UTF8, "application/json");

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("ok", await response.Content.ReadAsStringAsync());
    }

    /// <summary>Serves an error (or, when told, a success) with a huge capped stream body.</summary>
    private sealed class ErrorResponseHandler(int statusCode = 400, string? body = null) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var response = new HttpResponseMessage((HttpStatusCode)statusCode);
            if (statusCode == 400)
            {
                response.Headers.Add("Retry-After", "7");
            }

            response.Content = body is null ? new CappedContent() : new StringContent(body, Encoding.UTF8, "application/json");
            return Task.FromResult(response);
        }
    }

    /// <summary>
    /// Reports a 1 MiB body length but serves at most <see cref="Cap"/> bytes and throws
    /// if a read would cross the cap.
    /// </summary>
    private sealed class CappedContent : HttpContent
    {
        protected override bool TryComputeLength(out long length)
        {
            length = 1024L * 1024;
            return true;
        }

        protected override Task<Stream> CreateContentReadStreamAsync(CancellationToken cancellationToken = default)
        {
            var bytes = new byte[Cap];
            for (var i = 0; i < Cap; i++)
            {
                bytes[i] = (byte)'x';
            }

            return Task.FromResult<Stream>(new RefusingReadStream(bytes));
        }

        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context)
        {
            // The bounded implementation never serializes the content; it reads the stream.
            throw new NotSupportedException("content must be read, not serialized");
        }
    }

    /// <summary>Serves its payload once; any read crossing the cap is a protocol violation.</summary>
    private sealed class RefusingReadStream(byte[] payload) : Stream
    {
        private int _position;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => _position;
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            var read = CopyInto(buffer.AsSpan(offset, count));
            return read;
        }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
            => ValueTask.FromResult(CopyInto(buffer.Span));

        private int CopyInto(Span<byte> destination)
        {
            if (_position + destination.Length > Cap)
            {
                throw new InvalidOperationException("read would cross the capture cap");
            }

            var count = Math.Min(destination.Length, payload.Length - _position);
            payload.AsSpan(_position, count).CopyTo(destination);
            _position += count;
            return count;
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
