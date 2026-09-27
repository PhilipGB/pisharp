using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;

namespace PiSharp.Runtime.Sessions;

/// <summary>Bounds a summarizer's view of a message without first allocating its entire JSON representation.</summary>
internal static class SummaryTranscriptExcerpt
{
    public static string SerializeForSummary(ChatMessage message)
    {
        if (message.Role == ChatRole.User)
        {
            var content = string.Join("\n", message.Contents.OfType<TextContent>().Select(item => item.Text));
            if (content.Length > 0) return $"[User]: {Excerpt(content, 3600)}";
            return message.Contents.OfType<DataContent>().Any() ? "[User]: [image]" : "";
        }

        if (message.Role == ChatRole.Assistant)
        {
            var parts = new List<string>();
            var reasoning = message.Contents.OfType<TextReasoningContent>().Select(item => item.Text).ToArray();
            if (reasoning.Length > 0) parts.Add($"[Assistant thinking]: {Excerpt(string.Join("\n", reasoning), 3600)}");
            var text = string.Join("", message.Contents.OfType<TextContent>().Select(item => item.Text));
            if (text.Length > 0) parts.Add($"[Assistant]: {Excerpt(text, 3600)}");
            var calls = message.Contents.OfType<FunctionCallContent>().Select(call =>
            {
                var arguments = call.Arguments is { } values
                    ? string.Join(", ", values.Select(argument =>
                        $"{argument.Key}={SerializeJson(argument.Value)}"))
                    : "";
                return $"{call.Name}({arguments})";
            }).ToArray();
            if (calls.Length > 0) parts.Add($"[Assistant tool calls]: {string.Join("; ", calls)}");
            return string.Join("\n\n", parts);
        }

        if (message.Role == ChatRole.Tool)
        {
            var results = message.Contents.OfType<FunctionResultContent>()
                .Select(result => ResultText(result.Result)).Where(result => result.Length > 0).ToArray();
            return results.Length == 0 ? "" : $"[Tool result]: {Excerpt(string.Join("\n", results), 2000)}";
        }

        return "";
    }

    private static string SerializeJson(object? value)
    {
        using var sink = new HeadTailStream();
        JsonSerializer.Serialize(sink, value, AIJsonUtilities.DefaultOptions);
        return sink.Excerpt();
    }

    private static string ResultText(object? result) => result switch
    {
        null => "",
        string text => text,
        _ => SerializeJson(result)
    };

    private static string Excerpt(string text, int limit)
    {
        if (text.Length <= limit) return text;
        var head = limit / 2;
        var tail = limit - head;
        return text[..head] + $"\n\n[... {text.Length - limit} characters omitted ...]\n\n" + text[^tail..];
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
