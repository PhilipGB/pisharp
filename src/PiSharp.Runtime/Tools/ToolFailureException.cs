namespace PiSharp.Runtime.Tools;

/// <summary>A tool failed; output remains available to both the model and the terminal.</summary>
public sealed class ToolFailureException(string message, string? output = null, int? exitCode = null, Exception? inner = null)
    : Exception(output is null or "" ? message : $"{output}\n\n{message}", inner)
{
    public string? Output { get; } = output;
    public int? ExitCode { get; } = exitCode;
}
