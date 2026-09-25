using System.Text.Json;

namespace PiSharp.Runtime.Sessions;

/// <summary>A direct Bash execution recorded separately from provider chat messages.</summary>
public sealed record BashExecutionRecord(
    string Command,
    string Output,
    int? ExitCode,
    bool Cancelled,
    bool Truncated,
    string? FullOutputPath,
    bool ExcludeFromContext)
{
    /// <summary>Original Pi JSONL record retained for loss-aware export.</summary>
    public JsonElement? PiOriginalEntry { get; init; }
}
