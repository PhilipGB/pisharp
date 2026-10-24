using PiSharp.Core;

namespace PiSharp.Runtime.Tools;

public sealed record BashExecutionResult(string Output, string DisplayOutput, int? ExitCode, bool Cancelled,
    bool Truncated, string? FullOutputPath, string? StructuredOutput = null,
    bool StructuredTruncated = false, double WallTimeSeconds = 0)
{
    public TextTruncationResult? Truncation { get; init; }
}
