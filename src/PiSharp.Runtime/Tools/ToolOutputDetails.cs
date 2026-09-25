using System.Text;
using System.Text.Json.Serialization;

namespace PiSharp.Runtime.Tools;

internal interface IStructuredToolOutput
{
    string Text { get; }
    object? EventDetails { get; }
}

public sealed record ToolTruncationDetails(
    bool Truncated,
    string? TruncatedBy,
    int TotalLines,
    int TotalBytes,
    int OutputLines,
    int OutputBytes,
    bool LastLinePartial,
    bool FirstLineExceedsLimit,
    long MaxLines,
    int MaxBytes);

public sealed record GrepToolDetails(
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] ToolTruncationDetails? Truncation = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? MatchLimitReached = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] bool? LinesTruncated = null);

public sealed record FindToolDetails(
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] ToolTruncationDetails? Truncation = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? ResultLimitReached = null);

public sealed record LsToolDetails(
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] ToolTruncationDetails? Truncation = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? EntryLimitReached = null);

internal sealed record SearchToolOutput(string Text, object? Details = null) : IStructuredToolOutput
{
    public object? EventDetails => Details;
    public override string ToString() => Text;
}

internal static class ToolOutputTruncator
{
    private const long UnlimitedLineLimit = 9_007_199_254_740_991L;

    public static (string Content, ToolTruncationDetails? Details) TruncateHead(string content, int maxBytes)
    {
        var totalBytes = Encoding.UTF8.GetByteCount(content);
        var lines = content.Length == 0 ? [] : content.Split('\n');
        var totalLines = lines.Length;
        if (totalBytes <= maxBytes) return (content, null);
        var firstLineBytes = lines.Length == 0 ? 0 : Encoding.UTF8.GetByteCount(lines[0]);
        if (firstLineBytes > maxBytes)
            return ("", new ToolTruncationDetails(true, "bytes", totalLines, totalBytes, 0, 0, false, true,
                UnlimitedLineLimit, maxBytes));

        var output = new List<string>();
        var bytes = 0;
        for (var index = 0; index < lines.Length; index++)
        {
            var lineBytes = Encoding.UTF8.GetByteCount(lines[index]) + (index > 0 ? 1 : 0);
            if (bytes + lineBytes > maxBytes) break;
            output.Add(lines[index]);
            bytes += lineBytes;
        }
        var result = string.Join('\n', output);
        return (result, new ToolTruncationDetails(true, "bytes", totalLines, totalBytes, output.Count,
            Encoding.UTF8.GetByteCount(result), false, false, UnlimitedLineLimit, maxBytes));
    }
}
