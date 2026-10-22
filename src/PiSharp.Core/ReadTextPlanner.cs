using System.Globalization;
using System.Text;

namespace PiSharp.Core;

public sealed record ReadTextSelection(string Text, TextTruncationResult? Truncation);

/// <summary>Pi's text read line/byte selection; image reads and streamed file IO are separate concerns.</summary>
public static class ReadTextPlanner
{
    public static string Select(string text, string path, int offset = 1, int? limit = null) =>
        SelectDetailed(text, path, offset, limit).Text;

    public static ReadTextSelection SelectDetailed(string text, string path, int offset = 1, int? limit = null)
    {
        if (offset < 1 || limit is <= 0) throw new ArgumentOutOfRangeException(nameof(offset), "Offset and limit must be positive.");
        var lines = text.Split('\n');
        if (offset > lines.Length) throw new ArgumentOutOfRangeException(nameof(offset), $"Offset {offset} is beyond end of file ({lines.Length} lines total)");
        var start = offset - 1;
        var count = Math.Min(limit ?? lines.Length, lines.Length - start);
        var selected = string.Join("\n", lines.Skip(start).Take(count));
        return FormatSelection(path, lines.Length, offset, limit.HasValue ? count : null,
            TextOutputTruncator.Head(selected), Encoding.UTF8.GetByteCount(lines[start]));
    }

    public static ReadTextSelection FormatSelection(string path, long totalFileLines, int offset,
        long? userLimitedLines, TextTruncationResult truncation, long firstLineBytes)
    {
        if (truncation.FirstLineExceedsLimit)
            return new($"[Line {offset} is {Size(firstLineBytes)}, exceeds 50.0KB limit. Use bash: sed -n '{offset}p' {path} | head -c {truncation.MaxBytes}]", truncation);
        if (truncation.Truncated)
        {
            var end = offset + truncation.OutputLines - 1;
            return new(truncation.Content + $"\n\n[Showing lines {offset}-{end} of {totalFileLines}{(truncation.TruncatedBy == "bytes" ? " (50.0KB limit)" : "")}. Use offset={end + 1} to continue.]", truncation);
        }
        if (userLimitedLines is { } count && offset - 1 + count < totalFileLines)
            return new($"{truncation.Content}\n\n[{totalFileLines - offset + 1 - count} more lines in file. Use offset={offset + count} to continue.]", null);
        return new(truncation.Content, null);
    }

    private static string Size(long bytes) => bytes < 1024 ? $"{bytes}B" : bytes < 1024 * 1024
        ? $"{(bytes / 1024d).ToString("F1", CultureInfo.InvariantCulture)}KB"
        : $"{(bytes / (1024d * 1024)).ToString("F1", CultureInfo.InvariantCulture)}MB";
}
