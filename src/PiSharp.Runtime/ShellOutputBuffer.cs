using System.Text;

namespace PiSharp.Runtime;

/// <summary>Captures command output with a bounded in-memory tail; spills full output to a private temp file.</summary>
public sealed class ShellOutputBuffer : IAsyncDisposable
{
    private const int MaxBytes = 50 * 1024;
    private const int MaxLines = 2000;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly MemoryStream _prefix = new();
    private readonly StringBuilder _tail = new();
    private readonly Decoder _decoder = new UTF8Encoding(false).GetDecoder();
    private FileStream? _full;
    private string? _fullPath;
    private long _bytes;
    private int _newlines;
    private bool _endsWithNewline;

    public async Task AppendAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_full is null && _bytes + data.Length > MaxBytes)
            {
                _fullPath = Path.Combine(Path.GetTempPath(), "pisharp-bash-" + Guid.NewGuid().ToString("N") + ".log");
                _full = NewPrivateFile(_fullPath);
                _prefix.Position = 0;
                await _prefix.CopyToAsync(_full, cancellationToken);
            }
            if (_full is null) await _prefix.WriteAsync(data, cancellationToken);
            else await _full.WriteAsync(data, cancellationToken);
            _bytes += data.Length;
            var chars = new char[Encoding.UTF8.GetMaxCharCount(data.Length)];
            var count = _decoder.GetChars(data.Span, chars, flush: false);
            _tail.Append(chars, 0, count);
            for (var i = 0; i < count; i++) if (chars[i] == '\n') _newlines++;
            if (count > 0) _endsWithNewline = chars[count - 1] == '\n';
            // Keep a generous rolling suffix so we can select the last 2000 lines or 50KB.
            if (_tail.Length > 4 * MaxBytes) _tail.Remove(0, _tail.Length - 2 * MaxBytes);
        }
        finally { _gate.Release(); }
    }

    public async Task<string> FinishAsync()
    {
        await _gate.WaitAsync();
        try
        {
            if (_full is not null) await _full.FlushAsync();
            var suffix = _tail.ToString();
            var lines = suffix.Split('\n');
            var keep = MaxLines + (suffix.EndsWith('\n') ? 1 : 0);
            if (lines.Length > keep) suffix = string.Join("\n", lines.TakeLast(keep));
            var tailBytes = Encoding.UTF8.GetBytes(suffix);
            if (tailBytes.Length > MaxBytes)
            {
                var start = tailBytes.Length - MaxBytes;
                while (start < tailBytes.Length && (tailBytes[start] & 0xC0) == 0x80) start++;
                suffix = Encoding.UTF8.GetString(tailBytes, start, tailBytes.Length - start);
                var newline = suffix.IndexOf('\n');
                if (newline >= 0) suffix = suffix[(newline + 1)..];
            }
            var totalLines = _newlines + (_endsWithNewline ? 0 : _bytes > 0 ? 1 : 0);
            if (_full is not null || totalLines > MaxLines)
            {
                // Large output is always spooled, even when the limit is line count only.
                if (_full is null)
                {
                    _fullPath = Path.Combine(Path.GetTempPath(), "pisharp-bash-" + Guid.NewGuid().ToString("N") + ".log");
                    await using var spill = NewPrivateFile(_fullPath);
                    await spill.WriteAsync(_prefix.ToArray());
                }
                suffix += $"\n\n[Output truncated; {totalLines} lines, {_bytes} bytes. Full output: {_fullPath}]";
            }
            return suffix.Length == 0 ? "(no output)" : suffix;
        }
        finally { _gate.Release(); }
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
        if (OperatingSystem.IsLinux()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        return new FileStream(path, options);
    }
    public async ValueTask DisposeAsync()
    {
        if (_full is not null) await _full.DisposeAsync();
        _prefix.Dispose();
        _gate.Dispose();
    }
}
