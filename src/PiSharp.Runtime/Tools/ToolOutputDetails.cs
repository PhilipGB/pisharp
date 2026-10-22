using System.Text.Json.Serialization;
using PiSharp.Core;

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
    int MaxBytes)
{
    public string Content { get; init; } = "";
}

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

// Text/Details identifies the persisted result; MAF must retain Details even when it is null.
internal sealed record SearchToolOutput(string Text,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] object? Details = null) : IStructuredToolOutput
{
    public object? EventDetails => Details;
    public override string ToString() => Text;
}

internal static class ToolOutputTruncator
{
    private const long UnlimitedLineLimit = 9_007_199_254_740_991L;

    public static (string Content, ToolTruncationDetails? Details) TruncateHead(string content, int maxBytes)
    {
        var result = TextOutputTruncator.Head(content, UnlimitedLineLimit, maxBytes);
        return (result.Content, result.Truncated ? new ToolTruncationDetails(true, result.TruncatedBy,
            checked((int)result.TotalLines), checked((int)result.TotalBytes), result.OutputLines, result.OutputBytes,
            result.LastLinePartial, result.FirstLineExceedsLimit, result.MaxLines, result.MaxBytes)
        { Content = result.Content } : null);
    }
}
