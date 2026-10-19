namespace PiSharp.Extensions.Abstractions;

/// <summary>Information about a user-originated shell command.</summary>
public sealed record PiSharpUserBashEvent(string Command, bool ExcludeFromContext, string WorkingDirectory);

/// <summary>Identifies the kind of user-bash replacement returned by a handler.</summary>
public enum PiSharpUserBashResultKind
{
    /// <summary>The handler supplied a complete execution result.</summary>
    FullResult,
    /// <summary>The handler supplied a custom operation for the host to execute.</summary>
    CustomOperation
}

/// <summary>Executes a replacement user-bash command through the host's output and cancellation pipeline.</summary>
/// <param name="command">The command after the host's configured shell prefix has been applied.</param>
/// <param name="workingDirectory">The working directory selected by the host.</param>
/// <param name="outputSink">Receives raw output bytes as the operation produces them.</param>
/// <param name="cancellationToken">Cancellation owned by the host command operation.</param>
public delegate ValueTask<int?> PiSharpUserBashOperation(
    string command,
    string workingDirectory,
    Func<ReadOnlyMemory<byte>, ValueTask> outputSink,
    CancellationToken cancellationToken);

/// <summary>A full replacement result or a custom operation for user-originated shell execution.</summary>
public sealed record PiSharpUserBashResult
{
    private PiSharpUserBashResult(PiSharpUserBashResultKind kind, string? output = null, int? exitCode = null,
        bool cancelled = false, bool truncated = false, string? fullOutputPath = null,
        PiSharpUserBashOperation? customOperation = null)
    {
        Kind = kind;
        Output = output;
        ExitCode = exitCode;
        Cancelled = cancelled;
        Truncated = truncated;
        FullOutputPath = fullOutputPath;
        CustomOperation = customOperation;
    }

    /// <summary>The kind of replacement result.</summary>
    public PiSharpUserBashResultKind Kind { get; }

    /// <summary>The captured output for a full result.</summary>
    public string? Output { get; }

    /// <summary>The process status for a full result, or null when unavailable.</summary>
    public int? ExitCode { get; }

    /// <summary>Whether the operation was cancelled.</summary>
    public bool Cancelled { get; }

    /// <summary>Whether captured output was truncated.</summary>
    public bool Truncated { get; }

    /// <summary>Path to the complete output when the host spilled it to a file.</summary>
    public string? FullOutputPath { get; }

    /// <summary>The operation used for a custom result.</summary>
    public PiSharpUserBashOperation? CustomOperation { get; }

    /// <summary>Creates a full result that replaces local shell execution.</summary>
    public static PiSharpUserBashResult Full(string output, int? exitCode = null, bool cancelled = false,
        bool truncated = false, string? fullOutputPath = null) =>
        new(PiSharpUserBashResultKind.FullResult,
            output ?? throw new ArgumentNullException(nameof(output)), exitCode, cancelled, truncated, fullOutputPath);

    /// <summary>Creates a custom operation that uses the host's command and output pipeline.</summary>
    public static PiSharpUserBashResult Custom(PiSharpUserBashOperation operation) =>
        new(PiSharpUserBashResultKind.CustomOperation, customOperation:
            operation ?? throw new ArgumentNullException(nameof(operation)));
}

/// <summary>Handles a user-originated shell command. Return null to let the next handler run.</summary>
public delegate ValueTask<PiSharpUserBashResult?> PiSharpExtensionUserBashHandler(
    PiSharpUserBashEvent eventData,
    PiSharpExtensionContext context,
    CancellationToken cancellationToken);
