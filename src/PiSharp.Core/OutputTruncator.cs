using System.Text;

namespace PiSharp.Core;

/// <summary>Describes how a tool result was limited for model context.</summary>
public sealed record TruncationResult(
    string Content,
    bool Truncated,
    string? TruncatedBy,
    int TotalLines,
    int TotalBytes,
    int OutputLines,
    int OutputBytes,
    bool LastLinePartial,
    int MaxLines,
    int MaxBytes);

/// <summary>Applies deterministic line and UTF-8 byte limits to tool output.</summary>
public static class OutputTruncator
{
    /// <summary>Default maximum number of complete lines returned by a tool.</summary>
    public const int DefaultMaxLines = 2_000;

    /// <summary>Default maximum UTF-8 bytes returned by a tool.</summary>
    public const int DefaultMaxBytes = 50 * 1024;

    /// <summary>Truncates from the beginning, suitable for file reads.</summary>
    public static TruncationResult Head(
        string content,
        int maxLines = DefaultMaxLines,
        int maxBytes = DefaultMaxBytes)
    {
        ValidateLimits(maxLines, maxBytes);
        var lines = SplitLines(content);
        var selected = SelectHead(lines, maxLines, maxBytes);
        return CreateResult(content, selected.Lines, selected.Partial, maxLines, maxBytes, selected.Truncated);
    }

    /// <summary>Truncates from the end, suitable for command output and diagnostics.</summary>
    public static TruncationResult Tail(
        string content,
        int maxLines = DefaultMaxLines,
        int maxBytes = DefaultMaxBytes)
    {
        ValidateLimits(maxLines, maxBytes);
        var lines = SplitLines(content);
        var selected = SelectTail(lines, maxLines, maxBytes);
        return CreateResult(content, selected.Lines, selected.Partial, maxLines, maxBytes, selected.Truncated);
    }

    private static (IReadOnlyList<string> Lines, bool Partial, bool Truncated) SelectHead(
        IReadOnlyList<string> lines,
        int maxLines,
        int maxBytes)
    {
        var selected = new List<string>();
        var bytes = 0;
        foreach (var line in lines.Take(maxLines))
        {
            var lineBytes = Encoding.UTF8.GetByteCount(line) + (selected.Count == 0 ? 0 : 1);
            if (bytes + lineBytes > maxBytes)
            {
                break;
            }

            selected.Add(line);
            bytes += lineBytes;
        }

        return (selected, false, selected.Count < lines.Count);
    }

    private static (IReadOnlyList<string> Lines, bool Partial, bool Truncated) SelectTail(
        IReadOnlyList<string> lines,
        int maxLines,
        int maxBytes)
    {
        var selected = new LinkedList<string>();
        var bytes = 0;
        var partial = false;
        for (var index = lines.Count - 1; index >= 0 && selected.Count < maxLines; index--)
        {
            var line = lines[index];
            var separatorBytes = selected.Count == 0 ? 0 : 1;
            var lineBytes = Encoding.UTF8.GetByteCount(line) + separatorBytes;
            if (bytes + lineBytes > maxBytes)
            {
                if (selected.Count == 0)
                {
                    line = TakeUtf8Suffix(line, maxBytes);
                    partial = true;
                    selected.AddFirst(line);
                }
                break;
            }

            selected.AddFirst(line);
            bytes += lineBytes;
        }

        return (selected.ToArray(), partial, selected.Count < lines.Count || partial);
    }

    private static TruncationResult CreateResult(
        string original,
        IReadOnlyList<string> lines,
        bool partial,
        int maxLines,
        int maxBytes,
        bool truncated)
    {
        var result = string.Join('\n', lines);
        var originalBytes = Encoding.UTF8.GetByteCount(original);
        var resultBytes = Encoding.UTF8.GetByteCount(result);
        var truncatedBy = truncated
            ? lines.Count >= maxLines ? "lines" : "bytes"
            : null;
        return new TruncationResult(
            result,
            truncated,
            truncated ? truncatedBy : null,
            SplitLines(original).Count,
            originalBytes,
            lines.Count,
            resultBytes,
            partial,
            maxLines,
            maxBytes);
    }

    private static IReadOnlyList<string> SplitLines(string content)
    {
        if (content.Length == 0)
        {
            return [];
        }

        var lines = content.Split('\n').ToList();
        if (lines[^1].Length == 0)
        {
            lines.RemoveAt(lines.Count - 1);
        }

        return lines;
    }

    private static string TakeUtf8Suffix(string value, int maxBytes)
    {
        if (Encoding.UTF8.GetByteCount(value) <= maxBytes)
        {
            return value;
        }

        var start = Math.Max(0, value.Length - maxBytes);
        while (start < value.Length && Encoding.UTF8.GetByteCount(value[start..]) > maxBytes)
        {
            start++;
        }

        return value[start..];
    }

    private static void ValidateLimits(int maxLines, int maxBytes)
    {
        if (maxLines < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(maxLines));
        }
        if (maxBytes < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(maxBytes));
        }
    }
}
