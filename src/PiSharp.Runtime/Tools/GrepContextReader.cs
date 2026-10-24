using System.Text;

namespace PiSharp.Runtime.Tools;

internal sealed class GrepContextReader(Func<string, Stream>? openFile = null) : IAsyncDisposable
{
    internal const int MaximumFiles = 8;
    internal const int MaximumLinesPerFile = 1024;
    internal const int MaximumCharactersPerFile = 32 * 1024;
    private readonly List<CachedFile> _files = [];

    internal int RetainedCharacters => _files.Sum(file => file.Window?.RetainedCharacters ?? 0);
    internal int RetainedLines => _files.Sum(file => file.Window?.RetainedLines ?? 0);

    public async Task<bool> AppendAsync(string path, double matchLine, double context,
        Action<double, string, bool> append, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var index = _files.FindIndex(file => string.Equals(file.Path, path,
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal));
        CachedFile cached;
        if (index >= 0)
        {
            cached = _files[index];
            _files.RemoveAt(index);
        }
        else
        {
            if (_files.Count == MaximumFiles)
            {
                if (_files[0].Window is { } evicted) await evicted.DisposeAsync();
                _files.RemoveAt(0);
            }
            cached = new(path);
            try
            {
                cached.Window = new(openFile?.Invoke(path) ?? new FileStream(path, FileMode.Open, FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete, 4096, FileOptions.Asynchronous | FileOptions.SequentialScan));
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        }
        _files.Add(cached);
        if (cached.Window is null) return false;
        try
        {
            await cached.Window.AppendAsync(matchLine, context, append, cancellationToken);
            return true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            await cached.Window.DisposeAsync();
            cached.Window = null;
            return false;
        }
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var file in _files)
            if (file.Window is { } window) await window.DisposeAsync();
        _files.Clear();
    }

    private sealed class CachedFile(string path)
    {
        public string Path { get; } = path;
        public Window? Window { get; set; }
    }

    private sealed class Window(Stream file) : IAsyncDisposable
    {
        private readonly ContextFile _file = new(file);
        private readonly Queue<ContextLine> _lines = new();
        private long _windowStartLine = 1;
        private long _windowStartOffset;
        public int RetainedCharacters { get; private set; }
        public int RetainedLines => _lines.Count;

        public async Task AppendAsync(double matchLine, double context, Action<double, string, bool> append,
            CancellationToken cancellationToken)
        {
            var start = Math.Max(1, matchLine - context);
            var end = matchLine + context;
            var readThrough = Math.Ceiling(end);
            var fractional = start != Math.Truncate(start);
            if (!fractional && start < _file.NextLine && (_lines.Count == 0 || start < _lines.Peek().Number))
            {
                // An oversized or backward window may miss the cache. Forward windows restart at
                // their preceding boundary, never at the beginning of the already skipped prefix.
                _file.Seek(start >= _windowStartLine ? _windowStartOffset : 0,
                    start >= _windowStartLine ? _windowStartLine : 1);
                Clear();
            }
            while (_lines.TryPeek(out var first) && first.Number < start)
                RetainedCharacters -= _lines.Dequeue().Text.Length;
            foreach (var line in _lines)
            {
                if (line.Number == Math.Ceiling(start)) RememberStart(line);
                if (!fractional && line.Number <= end) append(line.Number, line.Text, line.Truncated);
            }
            while (_file.NextLine <= readThrough)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var capture = !fractional && _file.NextLine >= start;
                if (_file.NextLine == Math.Ceiling(start))
                {
                    _windowStartLine = _file.NextLine;
                    _windowStartOffset = _file.Offset;
                }
                var line = await _file.ReadLineAsync(capture, cancellationToken);
                if (line is null) break;
                if (!capture) { Clear(); continue; }
                _lines.Enqueue(line.Value);
                RetainedCharacters += line.Value.Text.Length;
                while (_lines.Count > MaximumLinesPerFile || RetainedCharacters > MaximumCharactersPerFile)
                    RetainedCharacters -= _lines.Dequeue().Text.Length;
                if (line.Value.Number <= end) append(line.Value.Number, line.Value.Text, line.Value.Truncated);
            }
            if (fractional)
                for (var current = start; current <= Math.Min(_file.NextLine - 1, end); current++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    append(current, "", false);
                }
        }

        private void RememberStart(ContextLine line) => (_windowStartLine, _windowStartOffset) = (line.Number, line.Offset);
        private void Clear() { _lines.Clear(); RetainedCharacters = 0; }
        public ValueTask DisposeAsync() => _file.DisposeAsync();
    }

    private readonly record struct ContextLine(long Number, long Offset, string Text, bool Truncated);

    private sealed class ContextFile(Stream stream) : IAsyncDisposable
    {
        private readonly byte[] _buffer = new byte[8192];
        private readonly byte[] _prefix = new byte[2048];
        private int _position;
        private int _count;
        private bool _finished;
        public long NextLine { get; private set; } = 1;
        public long Offset { get; private set; }

        public async ValueTask<ContextLine?> ReadLineAsync(bool capture, CancellationToken cancellationToken)
        {
            if (_finished) return null;
            var offset = Offset;
            var prefixLength = 0;
            while (await FillAsync(cancellationToken))
            {
                var remaining = _buffer.AsSpan(_position, _count - _position);
                var separator = remaining.IndexOfAny((byte)'\r', (byte)'\n');
                var length = separator < 0 ? remaining.Length : separator;
                if (capture)
                {
                    var retained = Math.Min(length, _prefix.Length - prefixLength);
                    remaining[..retained].CopyTo(_prefix.AsSpan(prefixLength));
                    prefixLength += retained;
                }
                Consume(length);
                if (separator < 0) continue;
                var carriageReturn = _buffer[_position] == '\r';
                Consume(1);
                if (carriageReturn && await FillAsync(cancellationToken) && _buffer[_position] == '\n') Consume(1);
                return Complete();
            }
            _finished = true;
            return Complete();

            ContextLine Complete()
            {
                // Even a prefix ending inside a UTF-8 sequence contains more than 500 UTF-16
                // units when full. Its replacement suffix cannot affect the retained text.
                var text = capture ? Encoding.UTF8.GetString(_prefix, 0, prefixLength) : "";
                var truncated = text.Length > 500;
                if (truncated) text = text[..500] + "... [truncated]";
                return new(NextLine++, offset, text, truncated);
            }
        }

        private async ValueTask<bool> FillAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_position < _count) return true;
            _count = await stream.ReadAsync(_buffer, cancellationToken);
            _position = 0;
            return _count > 0;
        }

        private void Consume(int count) { _position += count; Offset += count; }

        public void Seek(long offset, long line)
        {
            stream.Seek(offset, SeekOrigin.Begin);
            (_position, _count, _finished, Offset, NextLine) = (0, 0, false, offset, line);
        }

        public ValueTask DisposeAsync() => stream.DisposeAsync();
    }
}
