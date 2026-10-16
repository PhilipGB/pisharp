using System.Text;
using System.Text.Json;

namespace PiSharp.Cli;

/// <summary>Projects Codex grammar-tool SSE events into the Responses function-call event shape.</summary>
internal static class OpenAiCodexGrammarSseStream
{
    public static async Task ProjectAsync(HttpResponseMessage response,
        IReadOnlyDictionary<string, OpenAiCodexGrammarTool> grammarTools, CancellationToken cancellationToken)
    {
        var content = response.Content;
        var source = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        var projected = new StreamContent(new ProjectingStream(source, content, grammarTools));
        foreach (var header in content.Headers)
            if (!header.Key.Equals("Content-Length", StringComparison.OrdinalIgnoreCase))
                projected.Headers.TryAddWithoutValidation(header.Key, header.Value);
        response.Content = projected;
    }

    private sealed class ProjectingStream : Stream
    {
        private readonly Stream _source;
        private readonly HttpContent _owner;
        private readonly StreamReader _reader;
        private readonly IReadOnlyDictionary<string, OpenAiCodexGrammarTool> _grammarTools;
        private readonly Dictionary<string, OpenAiCodexGrammarCallState> _customCalls = new(StringComparer.Ordinal);
        private byte[] _pending = [];
        private int _pendingOffset;

        public ProjectingStream(Stream source, HttpContent owner,
            IReadOnlyDictionary<string, OpenAiCodexGrammarTool> grammarTools)
        {
            _source = source;
            _owner = owner;
            _reader = new StreamReader(source, Encoding.UTF8, detectEncodingFromByteOrderMarks: false,
                bufferSize: 1024, leaveOpen: true);
            _grammarTools = grammarTools;
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override int Read(byte[] buffer, int offset, int count) =>
            ReadAsync(buffer.AsMemory(offset, count), CancellationToken.None).AsTask().GetAwaiter().GetResult();

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            ReadCoreAsync(buffer, cancellationToken);

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadCoreAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        private async ValueTask<int> ReadCoreAsync(Memory<byte> destination, CancellationToken cancellationToken)
        {
            if (destination.IsEmpty) return 0;
            while (_pendingOffset >= _pending.Length)
            {
                var frame = await ReadFrameAsync(cancellationToken).ConfigureAwait(false);
                if (frame is null) return 0;
                _pending = Encoding.UTF8.GetBytes(frame);
                _pendingOffset = 0;
                if (_pending.Length == 0) continue;
            }

            var count = Math.Min(destination.Length, _pending.Length - _pendingOffset);
            _pending.AsMemory(_pendingOffset, count).CopyTo(destination);
            _pendingOffset += count;
            return count;
        }

        private async Task<string?> ReadFrameAsync(CancellationToken cancellationToken)
        {
            while (true)
            {
                var lines = new List<string>();
                var reachedEnd = false;
                while (true)
                {
                    var line = await _reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
                    if (line is null)
                    {
                        reachedEnd = true;
                        break;
                    }
                    if (line.Length == 0) break;
                    lines.Add(line);
                }

                if (lines.Count == 0)
                {
                    if (reachedEnd) return null;
                    continue;
                }
                var dataIndexes = lines.Select((line, index) => (line, index))
                    .Where(item => item.line.StartsWith("data:", StringComparison.Ordinal))
                    .Select(item => item.index).ToArray();
                if (dataIndexes.Length == 0) return string.Join('\n', lines) + "\n\n";

                var data = string.Join('\n', dataIndexes.Select(index =>
                {
                    var value = lines[index][5..];
                    return value.StartsWith(' ') ? value[1..] : value;
                }));
                var eventName = lines.FirstOrDefault(line => line.StartsWith("event:", StringComparison.Ordinal))?[6..].Trim();
                var projectedData = ProjectData(data, eventName);
                if (string.Equals(data, projectedData, StringComparison.Ordinal))
                    return string.Join('\n', lines) + "\n\n";

                var newLines = new List<string>(lines.Count);
                var firstData = true;
                foreach (var line in lines)
                {
                    if (line.StartsWith("data:", StringComparison.Ordinal))
                    {
                        if (!firstData) continue;
                        newLines.Add($"data: {projectedData}");
                        firstData = false;
                    }
                    else if (line.StartsWith("event:", StringComparison.Ordinal))
                        newLines.Add($"event: {GetEventType(projectedData) ?? eventName}");
                    else newLines.Add(line);
                }
                return string.Join('\n', newLines) + "\n\n";
            }
        }

        private string ProjectData(string data, string? eventName)
        {
            if (data == "[DONE]") return data;
            try
            {
                using var document = JsonDocument.Parse(data);
                var eventType = GetEventType(document.RootElement) ?? eventName;
                return OpenAiCodexWebSocketBridgeHandler.ProjectCustomToolEvent(
                    document.RootElement, eventType, _grammarTools, _customCalls);
            }
            catch (JsonException)
            {
                return data;
            }
        }

        private static string? GetEventType(string json)
        {
            try
            {
                using var document = JsonDocument.Parse(json);
                return GetEventType(document.RootElement);
            }
            catch (JsonException)
            {
                return null;
            }
        }

        private static string? GetEventType(JsonElement root) =>
            root.ValueKind == JsonValueKind.Object && root.TryGetProperty("type", out var type) &&
            type.ValueKind == JsonValueKind.String ? type.GetString() : null;

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _reader.Dispose();
                _source.Dispose();
                _owner.Dispose();
            }
            base.Dispose(disposing);
        }

        public override ValueTask DisposeAsync()
        {
            _reader.Dispose();
            _source.Dispose();
            _owner.Dispose();
            GC.SuppressFinalize(this);
            return ValueTask.CompletedTask;
        }
    }
}
