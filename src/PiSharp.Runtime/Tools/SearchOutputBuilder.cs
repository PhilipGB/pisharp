using System.Text;

namespace PiSharp.Runtime.Tools;

/// <summary>Counts complete search output while retaining only the accepted head lines.</summary>
internal sealed class SearchOutputBuilder
{
    private const int MaximumBytes = 50 * 1024;
    private readonly StringBuilder _head = new();
    private int _totalBytes;
    private int _totalLines;
    private int _headBytes;
    private int _headLines;
    private bool _stopped;
    private bool _firstLineExceedsLimit;

    internal readonly record struct Position(int Length, int TotalBytes, int TotalLines, int HeadBytes,
        int HeadLines, bool Stopped, bool FirstLineExceedsLimit);

    internal Position Capture() => new(_head.Length, _totalBytes, _totalLines, _headBytes, _headLines, _stopped, _firstLineExceedsLimit);

    internal void Restore(Position position)
    {
        _head.Length = position.Length;
        (_totalBytes, _totalLines, _headBytes, _headLines, _stopped, _firstLineExceedsLimit) =
            (position.TotalBytes, position.TotalLines, position.HeadBytes, position.HeadLines, position.Stopped, position.FirstLineExceedsLimit);
    }

    public void Append(string entry)
    {
        foreach (var line in entry.Split('\n'))
        {
            var bytes = Encoding.UTF8.GetByteCount(line);
            _totalBytes = checked(_totalBytes + bytes + (_totalLines > 0 ? 1 : 0));
            _totalLines++;
            if (_totalLines == 1) _firstLineExceedsLimit = bytes > MaximumBytes;
            if (_stopped) continue;
            var retainedBytes = _headBytes + bytes + (_headLines > 0 ? 1 : 0);
            if (retainedBytes > MaximumBytes) { _stopped = true; continue; }
            if (_headLines > 0) _head.Append('\n');
            _head.Append(line);
            _headBytes = retainedBytes;
            _headLines++;
        }
    }

    public (string Content, ToolTruncationDetails? Details) Finish()
    {
        var content = _head.ToString();
        return (content, !_stopped ? null : new ToolTruncationDetails(true, "bytes", _totalLines,
            _totalBytes, _headLines, _headBytes, false, _firstLineExceedsLimit, 9_007_199_254_740_991, MaximumBytes)
        { Content = content });
    }
}
