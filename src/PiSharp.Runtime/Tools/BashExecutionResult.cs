namespace PiSharp.Runtime.Tools;

public sealed record BashExecutionResult(string Output, string DisplayOutput, int? ExitCode, bool Cancelled,
    bool Truncated, string? FullOutputPath);
