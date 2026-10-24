using System.Text;

namespace PiSharp.Core;

public sealed record TextTruncationResult(string Content, bool Truncated, string? TruncatedBy,
    long TotalLines, long TotalBytes, int OutputLines, int OutputBytes, bool LastLinePartial,
    bool FirstLineExceedsLimit, long MaxLines, int MaxBytes);

/// <summary>Complete-line head selection and UTF-8-safe tail selection with pinned output accounting.</summary>
public static class TextOutputTruncator
{
    public static TextTruncationResult Head(string content, long maxLines = 2000, int maxBytes = 50 * 1024) =>
        Truncate(content, fromTail: false, maxLines, maxBytes);

    public static TextTruncationResult Tail(string content, long maxLines = 2000, int maxBytes = 50 * 1024) =>
        Truncate(content, fromTail: true, maxLines, maxBytes);

    private static TextTruncationResult Truncate(string content, bool fromTail, long maxLines, int maxBytes)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentOutOfRangeException.ThrowIfNegative(maxLines);
        ArgumentOutOfRangeException.ThrowIfNegative(maxBytes);
        var totalBytes = Encoding.UTF8.GetByteCount(content);
        var lines = content.Length == 0 ? [] : content.Split('\n');
        if (content.EndsWith('\n')) Array.Resize(ref lines, lines.Length - 1);
        var totalLines = lines.Length;
        if (totalLines <= maxLines && totalBytes <= maxBytes)
            return new(content, false, null, totalLines, totalBytes, totalLines, totalBytes,
                false, false, maxLines, maxBytes);
        if (!fromTail && Encoding.UTF8.GetByteCount(lines[0]) > maxBytes)
            return new("", true, "bytes", totalLines, totalBytes, 0, 0, false, true, maxLines, maxBytes);

        var output = new List<string>();
        var used = 0;
        var truncatedBy = "lines";
        var partial = false;
        for (var index = fromTail ? lines.Length - 1 : 0;
             index >= 0 && index < lines.Length && output.Count < maxLines;
             index += fromTail ? -1 : 1)
        {
            var line = lines[index];
            var bytes = (long)Encoding.UTF8.GetByteCount(line) + (output.Count > 0 ? 1 : 0);
            if (used + bytes > maxBytes)
            {
                truncatedBy = "bytes";
                if (fromTail && output.Count == 0)
                {
                    var suffix = Utf8Suffix(line, maxBytes);
                    output.Add(suffix);
                    used = Encoding.UTF8.GetByteCount(suffix);
                    partial = true;
                }
                break;
            }
            output.Add(line);
            used += (int)bytes;
        }
        if (output.Count >= maxLines && used <= maxBytes) truncatedBy = "lines";
        if (fromTail) output.Reverse();
        var text = string.Join('\n', output);
        return new(text, true, truncatedBy, totalLines, totalBytes, output.Count,
            Encoding.UTF8.GetByteCount(text), partial, false, maxLines, maxBytes);
    }

    private static string Utf8Suffix(string value, int maxBytes)
    {
        var start = value.Length;
        var used = 0;
        while (start > 0)
        {
            var last = value[start - 1];
            var pair = char.IsLowSurrogate(last) && start > 1 && char.IsHighSurrogate(value[start - 2]);
            var bytes = pair ? 4 : last <= 0x7f ? 1 : last <= 0x7ff ? 2 : 3;
            if (used + bytes > maxBytes) break;
            used += bytes;
            start -= pair ? 2 : 1;
        }
        // Match the buffer-based oracle's replacement of invalid UTF-16, using only the bounded suffix.
        return Encoding.UTF8.GetString(Encoding.UTF8.GetBytes(value[start..]));
    }
}
