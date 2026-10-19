namespace PiSharp.Extensions.Abstractions;

/// <summary>Options for direct executable invocation.</summary>
public sealed record PiSharpExtensionExecOptions
{
    /// <summary>Overrides the package's captured host directory. Relative paths use that directory.</summary>
    public string? WorkingDirectory { get; init; }

    /// <summary>A positive execution timeout; null or nonpositive values disable the timeout.</summary>
    public TimeSpan? Timeout { get; init; }
}

/// <summary>Separate captured streams and the completed program's status.</summary>
/// <param name="Stdout">Unformatted UTF-8 standard output.</param>
/// <param name="Stderr">Unformatted UTF-8 standard error.</param>
/// <param name="ExitCode">The exit code, or zero when the host terminated the program.</param>
/// <param name="Killed">Whether cancellation, timeout, or package unloading requested termination.</param>
public sealed record PiSharpExtensionExecResult(string Stdout, string Stderr, int ExitCode, bool Killed);

/// <summary>A host-backed executor valid for the lifetime of its owning extension package.</summary>
public abstract class PiSharpExtensionProcessExecutor
{
    /// <summary>Starts an executable without shell interpretation and captures both output streams.</summary>
    /// <remarks>Pass the current command, tool, or event cancellation token. Cancellation before start
    /// throws; after start it returns a killed result. Start and capture failures throw.</remarks>
    public abstract Task<PiSharpExtensionExecResult> ExecuteAsync(string command, IReadOnlyList<string> args,
        PiSharpExtensionExecOptions? options = null, CancellationToken cancellationToken = default);
}

public partial interface IPiSharpExtensionBuilder
{
    /// <summary>Gets the package-owned executor during Configure; retain it for later handlers.</summary>
    PiSharpExtensionProcessExecutor ProcessExecutor =>
        throw new NotSupportedException("This host does not provide native extension process execution.");
}
