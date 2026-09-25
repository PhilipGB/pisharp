using System.Globalization;
using System.Text;

namespace PiSharp.Runtime;

public sealed record ShellOutputResult(string Output, string DisplayOutput, bool Truncated, string? FullOutputPath);

/// <summary>Captures command output with a bounded in-memory tail; spills full output to a private temp file.</summary>
public sealed class ShellOutputBuffer : IAsyncDisposable
{
    private const int MaxBytes = 50 * 1024;
    private const int MaxLines = 2000;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly MemoryStream _prefix = new();
    private readonly StringBuilder _tail = new();
    private readonly Decoder _decoder = new UTF8Encoding(false).GetDecoder();
    private readonly ShellOutputNormalizer? _normalizer;
    private FileStream? _full;
    private string? _fullPath;
    private long _rawBytes;
    private long _decodedBytes;
    private long _currentLineBytes;
    private int _newlines;
    private bool _endsWithNewline;
    private bool _decoderFlushed;

    /// <param name="normalizeOutput">Strip terminal escapes, binary controls and carriage returns for direct Bash execution.</param>
    public ShellOutputBuffer(bool normalizeOutput = false)
    {
        if (normalizeOutput) _normalizer = new ShellOutputNormalizer();
    }

    /// <summary>Appends raw bytes and returns only newly decoded UTF-8 text.</summary>
    public async Task<string> AppendAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default,
        Action<string>? onDecoded = null)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_decoderFlushed) throw new InvalidOperationException("Cannot append after the output decoder has been flushed.");
            var chars = new char[Encoding.UTF8.GetMaxCharCount(data.Length)];
            var count = _decoder.GetChars(data.Span, chars, flush: false);
            var decoded = _normalizer is null ? new string(chars, 0, count) : _normalizer.Append(chars.AsSpan(0, count));
            ReadOnlyMemory<byte> stored = _normalizer is null ? data : Encoding.UTF8.GetBytes(decoded);

            if (_full is null && _rawBytes + data.Length > MaxBytes)
            {
                _fullPath = Path.Combine(Path.GetTempPath(), "pi-bash-" + Guid.NewGuid().ToString("N") + ".log");
                _full = NewPrivateFile(_fullPath);
                _prefix.Position = 0;
                await _prefix.CopyToAsync(_full, cancellationToken);
            }
            if (_full is null) await _prefix.WriteAsync(stored, cancellationToken);
            else await _full.WriteAsync(stored, cancellationToken);
            _rawBytes += data.Length;

            decoded = AppendDecoded(decoded.AsSpan(), decoded.Length);
            onDecoded?.Invoke(decoded);
            return decoded;
        }
        finally { _gate.Release(); }
    }

    /// <summary>Flushes any incomplete UTF-8 sequence using the decoder's replacement fallback.</summary>
    public async Task<string> FlushDecoderAsync()
    {
        await _gate.WaitAsync();
        try
        {
            if (_decoderFlushed) return "";
            _decoderFlushed = true;
            var chars = new char[4];
            var count = _decoder.GetChars(ReadOnlySpan<byte>.Empty, chars, flush: true);
            var decoded = _normalizer is null ? new string(chars, 0, count) :
                _normalizer.Append(chars.AsSpan(0, count)) + _normalizer.Finish();
            if (_normalizer is not null && decoded.Length != 0)
            {
                var stored = Encoding.UTF8.GetBytes(decoded);
                if (_full is null) await _prefix.WriteAsync(stored);
                else await _full.WriteAsync(stored);
            }
            return AppendDecoded(decoded.AsSpan(), decoded.Length);
        }
        finally { _gate.Release(); }
    }

    public async Task<string> FinishAsync() => (await FinishWithMetadataAsync()).DisplayOutput;

    public async Task<ShellOutputResult> FinishWithMetadataAsync()
    {
        await FlushDecoderAsync();
        await _gate.WaitAsync();
        try
        {
            if (_full is not null) await _full.FlushAsync();
            var totalLines = TotalLines;
            var truncated = totalLines > MaxLines || _decodedBytes > MaxBytes;
            string content;
            string displayOutput;
            int shownLines, shownBytes;
            bool partialLastLine;
            if (truncated)
                content = SelectTail(_tail.ToString(), out shownLines, out shownBytes, out partialLastLine);
            else
            {
                content = _tail.ToString();
                shownLines = totalLines;
                shownBytes = (int)_decodedBytes;
                partialLastLine = false;
            }
            displayOutput = content;
            if (truncated)
            {
                if (_full is null)
                {
                    _fullPath = Path.Combine(Path.GetTempPath(), "pi-bash-" + Guid.NewGuid().ToString("N") + ".log");
                    await using var spill = NewPrivateFile(_fullPath);
                    _prefix.Position = 0;
                    await _prefix.CopyToAsync(spill);
                }
                var startLine = totalLines - shownLines + 1;
                if (partialLastLine)
                {
                    var lineBytes = LastLineBytes;
                    var shown = FormatSize(shownBytes);
                    displayOutput += $"\n\n[Showing last {shown} of line {totalLines} (line is {FormatSize(lineBytes)}). Full output: {_fullPath}]";
                }
                else if (shownLines >= MaxLines && shownBytes <= MaxBytes)
                    displayOutput += $"\n\n[Showing lines {startLine}-{totalLines} of {totalLines}. Full output: {_fullPath}]";
                else
                    displayOutput += $"\n\n[Showing lines {startLine}-{totalLines} of {totalLines} ({FormatSize(MaxBytes)} limit). Full output: {_fullPath}]";
            }
            if (displayOutput.Length == 0) displayOutput = "(no output)";
            return new(content, displayOutput, truncated, _fullPath);
        }
        finally { _gate.Release(); }
    }

    private int TotalLines => _decodedBytes == 0 ? 0 : _newlines + (_endsWithNewline ? 0 : 1);
    private long LastLineBytes
        => _currentLineBytes;

    private string AppendDecoded(ReadOnlySpan<char> chars, int count)
    {
        if (count == 0) return "";
        _decodedBytes += Encoding.UTF8.GetByteCount(chars[..count]);
        var lastNewline = -1;
        for (var i = 0; i < count; i++)
            if (chars[i] == '\n') { _newlines++; lastNewline = i; }
        _currentLineBytes = lastNewline < 0
            ? _currentLineBytes + Encoding.UTF8.GetByteCount(chars[..count])
            : Encoding.UTF8.GetByteCount(chars[(lastNewline + 1)..count]);
        _endsWithNewline = chars[count - 1] == '\n';
        _tail.Append(chars[..count]);
        // Keep enough suffix for both the byte and line limits, including multibyte text.
        if (_tail.Length > 4 * MaxBytes)
        {
            var remove = _tail.Length - 2 * MaxBytes;
            if (remove < _tail.Length && char.IsLowSurrogate(_tail[remove])) remove++;
            _tail.Remove(0, remove);
        }
        return new string(chars[..count]);
    }

    private static string SelectTail(string content, out int outputLines, out int outputBytes, out bool partialLastLine)
    {
        if (content.EndsWith('\n')) content = content[..^1];
        if (content.Length == 0)
        {
            outputLines = outputBytes = 0;
            partialLastLine = false;
            return "";
        }

        var lines = content.Split('\n');
        var selected = new List<string>(Math.Min(lines.Length, MaxLines));
        var bytes = 0;
        partialLastLine = false;
        for (var i = lines.Length - 1; i >= 0 && selected.Count < MaxLines; i--)
        {
            var lineBytes = Encoding.UTF8.GetByteCount(lines[i]) + (selected.Count > 0 ? 1 : 0);
            if (bytes + lineBytes > MaxBytes)
            {
                if (selected.Count == 0)
                {
                    selected.Add(TruncateUtf8FromEnd(lines[i], MaxBytes));
                    partialLastLine = true;
                }
                break;
            }
            selected.Insert(0, lines[i]);
            bytes += lineBytes;
        }
        var suffix = string.Join('\n', selected);
        outputLines = selected.Count;
        outputBytes = Encoding.UTF8.GetByteCount(suffix);
        return suffix;
    }

    private static string TruncateUtf8FromEnd(string value, int maxBytes)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        var start = Math.Max(0, bytes.Length - maxBytes);
        while (start < bytes.Length && (bytes[start] & 0xC0) == 0x80) start++;
        return Encoding.UTF8.GetString(bytes, start, bytes.Length - start);
    }

    private static string FormatSize(long bytes)
    {
        if (bytes < 1024) return $"{bytes}B";
        var divisor = bytes < 1024 * 1024 ? 1024d : 1024d * 1024;
        var unit = bytes < 1024 * 1024 ? "KB" : "MB";
        return (bytes / divisor).ToString("0.0", CultureInfo.InvariantCulture) + unit;
    }

    private static FileStream NewPrivateFile(string path)
    {
        var options = new FileStreamOptions
        {
            Mode = FileMode.CreateNew,
            Access = FileAccess.Write,
            Share = FileShare.Read,
            BufferSize = 8192,
            Options = FileOptions.Asynchronous
        };
        if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        return new FileStream(path, options);
    }

    public async ValueTask DisposeAsync()
    {
        if (_full is not null) await _full.DisposeAsync();
        _prefix.Dispose();
        _gate.Dispose();
    }
}
