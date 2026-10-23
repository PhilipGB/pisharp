using System.Globalization;
using System.Text;
using PiSharp.Core;

namespace PiSharp.Runtime;

public sealed record ShellOutputResult(string Output, string DisplayOutput, bool Truncated, string? FullOutputPath)
{
    public TextTruncationResult? Truncation { get; init; }
}

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
    private long _newlines;
    private bool _endsWithNewline;
    private bool _decoderFlushed;
    private bool _decoderStarted;

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
            var decoded = DecodeStart(new string(chars, 0, count));
            if (_normalizer is not null) decoded = _normalizer.Append(decoded.AsSpan());
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
            var decoded = DecodeStart(new string(chars, 0, count));
            if (_normalizer is not null) decoded = _normalizer.Append(decoded.AsSpan()) + _normalizer.Finish();
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
        return await SnapshotAsync();
    }

    internal async Task<ShellOutputResult> SnapshotAsync()
    {
        await _gate.WaitAsync();
        try
        {
            if (_full is not null) await _full.FlushAsync();
            var totalLines = TotalLines;
            var truncated = totalLines > MaxLines || _decodedBytes > MaxBytes;
            var truncation = TextOutputTruncator.Tail(_tail.ToString()) with
            {
                TotalLines = totalLines,
                TotalBytes = _decodedBytes,
                Truncated = truncated
            };
            if (truncated && truncation.TruncatedBy is null)
                truncation = truncation with { TruncatedBy = _decodedBytes > MaxBytes ? "bytes" : "lines" };
            var content = truncation.Content;
            var displayOutput = content;
            if (truncated)
            {
                if (_full is null)
                {
                    _fullPath = Path.Combine(Path.GetTempPath(), "pi-bash-" + Guid.NewGuid().ToString("N") + ".log");
                    _full = NewPrivateFile(_fullPath);
                    _prefix.Position = 0;
                    await _prefix.CopyToAsync(_full);
                    await _full.FlushAsync();
                }
                var startLine = totalLines - truncation.OutputLines + 1;
                if (truncation.LastLinePartial)
                {
                    var lineBytes = LastLineBytes;
                    var shown = FormatSize(truncation.OutputBytes);
                    displayOutput += $"\n\n[Showing last {shown} of line {totalLines} (line is {FormatSize(lineBytes)}). Full output: {_fullPath}]";
                }
                else if (truncation.TruncatedBy == "lines")
                    displayOutput += $"\n\n[Showing lines {startLine}-{totalLines} of {totalLines}. Full output: {_fullPath}]";
                else
                    displayOutput += $"\n\n[Showing lines {startLine}-{totalLines} of {totalLines} ({FormatSize(MaxBytes)} limit). Full output: {_fullPath}]";
            }
            if (displayOutput.Length == 0) displayOutput = "(no output)";
            return new(content, displayOutput, truncated, _fullPath) { Truncation = truncation };
        }
        finally { _gate.Release(); }
    }

    internal async Task<(string Content, bool Truncated)> ReadFullOutputAsync(int maxBytes)
    {
        await _gate.WaitAsync();
        try
        {
            if (_full is not null) await _full.FlushAsync();
            if (_fullPath is null) return (WithoutBom(Encoding.UTF8.GetString(_prefix.ToArray())), false);
            await using var file = new FileStream(_fullPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite,
                8192, FileOptions.Asynchronous);
            if (file.Length <= maxBytes)
            {
                var bytes = new byte[(int)file.Length];
                await file.ReadExactlyAsync(bytes);
                return (WithoutBom(Encoding.UTF8.GetString(bytes)), false);
            }
            var head = new byte[maxBytes / 2];
            var tail = new byte[maxBytes - head.Length];
            await file.ReadExactlyAsync(head);
            file.Position = file.Length - tail.Length;
            await file.ReadExactlyAsync(tail);
            var chars = new char[Encoding.UTF8.GetMaxCharCount(head.Length)];
            var count = Encoding.UTF8.GetDecoder().GetChars(head, chars, flush: false);
            var tailStart = 0;
            while (tailStart < tail.Length && (tail[tailStart] & 0xc0) == 0x80) tailStart++;
            return (WithoutBom(new string(chars, 0, count)) + $"\n\n[... {file.Length - maxBytes} bytes omitted ...]\n\n" +
                WithoutBom(Encoding.UTF8.GetString(tail.AsSpan(tailStart))), true);
        }
        finally { _gate.Release(); }
    }

    private long TotalLines => _decodedBytes == 0 ? 0 : _newlines + (_endsWithNewline ? 0 : 1);

    private string DecodeStart(string decoded)
    {
        if (_decoderStarted || decoded.Length == 0) return decoded;
        _decoderStarted = true;
        return WithoutBom(decoded);
    }

    private static string WithoutBom(string text) => text.StartsWith('\uFEFF') ? text[1..] : text;
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
