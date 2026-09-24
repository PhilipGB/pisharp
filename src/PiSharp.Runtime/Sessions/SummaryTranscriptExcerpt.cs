using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;

namespace PiSharp.Runtime.Sessions;

/// <summary>Bounds a summarizer's view of a message without first allocating its entire JSON representation.</summary>
internal static class SummaryTranscriptExcerpt
{
    public static string Serialize(ChatMessage message)
    {
        using var sink = new HeadTailStream();
        JsonSerializer.Serialize(sink, message, AIJsonUtilities.DefaultOptions);
        return sink.Excerpt();
    }

    private sealed class HeadTailStream : Stream
    {
        private readonly byte[] _head = new byte[2000];
        private readonly byte[] _tail = new byte[900];
        private int _headCount;
        private int _tailCount;
        private int _tailStart;
        private long _written;

        public string Excerpt()
        {
            if (_written <= _head.Length) return Encoding.UTF8.GetString(_head.AsSpan(0, _headCount));
            Span<byte> tail = stackalloc byte[900];
            _tail.AsSpan(_tailStart, _tail.Length - _tailStart).CopyTo(tail);
            _tail.AsSpan(0, _tailStart).CopyTo(tail[(_tail.Length - _tailStart)..]);
            return Encoding.UTF8.GetString(_head.AsSpan(0, 900)) +
                $" [omitted {_written - 1800} serialized bytes] " + Encoding.UTF8.GetString(tail);
        }

        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => _written;
        public override long Position { get => _written; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));
        public override void Write(ReadOnlySpan<byte> buffer)
        {
            var headBytes = Math.Min(buffer.Length, _head.Length - _headCount);
            buffer[..headBytes].CopyTo(_head.AsSpan(_headCount));
            _headCount += headBytes;
            _written += buffer.Length;
            if (buffer.Length >= _tail.Length)
            {
                buffer[^_tail.Length..].CopyTo(_tail);
                _tailStart = 0;
                _tailCount = _tail.Length;
            }
            else
            {
                foreach (var value in buffer)
                {
                    if (_tailCount < _tail.Length)
                    {
                        _tail[(_tailStart + _tailCount) % _tail.Length] = value;
                        _tailCount++;
                    }
                    else
                    {
                        _tail[_tailStart] = value;
                        _tailStart = (_tailStart + 1) % _tail.Length;
                    }
                }
            }
        }
    }
}
