using System.Globalization;
using System.Text;

namespace PiSharp.Core;

public sealed record ReadTextSelection(string Text, TextTruncationResult? Truncation);

public sealed record ReadTextWindow(long Start, long End, double RawStart, double? UserLimitedLines);

/// <summary>Pi's text read line/byte selection; image reads and streamed file IO are separate concerns.</summary>
public static class ReadTextPlanner
{
    public static string Select(string text, string path, int offset = 1, int? limit = null) =>
        SelectDetailed(text, path, offset, limit).Text;

    public static ReadTextSelection SelectDetailed(string text, string path, int offset = 1, int? limit = null)
    {
        if (offset < 1 || limit is <= 0) throw new ArgumentOutOfRangeException(nameof(offset), "Offset and limit must be positive.");
        return SelectDetailed(text, path, (double)offset, limit);
    }

    public static ReadTextSelection SelectDetailed(string text, string path, double offset, double? limit)
    {
        var lines = text.Split('\n');
        var window = PlanWindow(lines.LongLength, offset, limit);
        var selected = string.Join("\n", lines.Skip((int)window.Start).Take((int)(window.End - window.Start)));
        var truncation = TextOutputTruncator.Head(selected);
        return FormatSelection(path, lines.LongLength, window, truncation,
            truncation.FirstLineExceedsLimit ? Encoding.UTF8.GetByteCount(lines[window.Start]) : 0);
    }

    public static ReadTextWindow PlanWindow(long totalFileLines, double offset, double? limit)
    {
        if (!double.IsFinite(offset) || limit is { } maximum && !double.IsFinite(maximum))
            throw new ArgumentOutOfRangeException(nameof(offset), "Offset and limit must be finite.");
        var start = offset == 0 ? 0 : Math.Max(0, offset - 1);
        if (start >= totalFileLines)
            throw new ArgumentOutOfRangeException(null, $"Offset {JavaScriptNumberText.Format(offset)} is beyond end of file ({totalFileLines} lines total)");
        var end = limit.HasValue ? Math.Min(start + limit.Value, totalFileLines) : totalFileLines;
        var first = SliceIndex(start, totalFileLines);
        return new(first, Math.Max(first, SliceIndex(end, totalFileLines)), start, limit.HasValue ? end - start : null);
    }

    private static long SliceIndex(double value, long length)
    {
        var index = Math.Truncate(value);
        if (index < 0) index += length;
        return index <= 0 ? 0 : index >= length ? length : (long)index;
    }

    public static ReadTextSelection FormatSelection(string path, long totalFileLines, int offset,
        long? userLimitedLines, TextTruncationResult truncation, long firstLineBytes) =>
        FormatSelection(path, totalFileLines, new(offset - 1L, offset - 1L, offset - 1d, userLimitedLines), truncation, firstLineBytes);

    public static ReadTextSelection FormatSelection(string path, long totalFileLines, ReadTextWindow window,
        TextTruncationResult truncation, long firstLineBytes)
    {
        var start = window.RawStart + 1;
        var displayStart = JavaScriptNumberText.Format(start);
        if (truncation.FirstLineExceedsLimit)
        {
            if (window.RawStart != Math.Truncate(window.RawStart))
                throw new ArgumentException("The \"string\" argument must be of type string or an instance of Buffer or ArrayBuffer. Received undefined");
            return new($"[Line {displayStart} is {Size(firstLineBytes)}, exceeds 50.0KB limit. Use bash: sed -n '{displayStart}p' {path} | head -c {truncation.MaxBytes}]", truncation);
        }
        if (truncation.Truncated)
        {
            var end = start + truncation.OutputLines - 1;
            return new(truncation.Content + $"\n\n[Showing lines {displayStart}-{JavaScriptNumberText.Format(end)} of {totalFileLines}{(truncation.TruncatedBy == "bytes" ? " (50.0KB limit)" : "")}. Use offset={JavaScriptNumberText.Format(end + 1)} to continue.]", truncation);
        }
        if (window.UserLimitedLines is { } count && window.RawStart + count < totalFileLines)
            return new($"{truncation.Content}\n\n[{JavaScriptNumberText.Format(totalFileLines - (window.RawStart + count))} more lines in file. Use offset={JavaScriptNumberText.Format(window.RawStart + count + 1)} to continue.]", null);
        return new(truncation.Content, null);
    }

    private static string Size(long bytes) => bytes < 1024 ? $"{bytes}B" : bytes < 1024 * 1024
        ? $"{(bytes / 1024d).ToString("F1", CultureInfo.InvariantCulture)}KB"
        : $"{(bytes / (1024d * 1024)).ToString("F1", CultureInfo.InvariantCulture)}MB";
}
