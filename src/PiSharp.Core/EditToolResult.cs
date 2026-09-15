namespace PiSharp.Core;

/// <summary>Compact edit-tool output containing the user-facing result and diff metadata.</summary>
public sealed record EditToolResult(
    string Message,
    string Diff,
    string Patch,
    int? FirstChangedLine,
    bool UsedFuzzyMatch);
