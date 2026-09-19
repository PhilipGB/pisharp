using System.ComponentModel;
using System.Diagnostics;

namespace PiSharp.Cli;

/// <summary>
/// /share support: publish a session export to a non-public GitHub gist via the gh CLI.
/// This is PiSharp's documented alternative to the pinned Radius gateway share (see
/// docs/PARITY.md); the pinned share posts a presentation JSONL through the gateway and
/// falls back to a private gist, while PiSharp gists the self-contained HTML export.
/// </summary>
internal static class SessionShare
{
    /// <summary>
    /// Test seam replacing the gh CLI invocation (returns exit code, stdout, stderr).
    /// Receives the export file path and the exact argument list that would be passed to gh.
    /// </summary>
    internal static Func<string, IReadOnlyList<string>, CancellationToken, Task<(int ExitCode, string StdOut, string? Error)>>? Launcher;

    /// <summary>
    /// The pinned shareViaGist argument list: a non-public gist (<c>--public=false</c>,
    /// never <c>--public</c>) plus the file path, each element passed verbatim through
    /// ArgumentList so quote characters in the path cannot alter argument boundaries.
    /// </summary>
    internal static IReadOnlyList<string> BuildGhGistArguments(string filePath) =>
    [
        "gist",
        "create",
        filePath,
        "--public=false",
        "-d",
        "PiSharp session",
    ];

    /// <summary>Creates a non-public gist for the file and returns the gist URL from gh's output.</summary>
    public static async Task<string> ShareFileAsync(string filePath, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(filePath);
        if (!File.Exists(filePath))
        {
            throw new FileNotFoundException("Nothing to share — the export file does not exist.", filePath);
        }

        var arguments = BuildGhGistArguments(filePath);
        var (exitCode, stdOut, error) = await (Launcher ?? RunGhGistAsync)(filePath, arguments, cancellationToken);
        if (exitCode != 0)
        {
            var detail = error?.Trim();
            throw new InvalidOperationException(
                string.IsNullOrWhiteSpace(detail)
                    ? $"gh gist create failed: exit code {exitCode}"
                    : $"gh gist create failed: {detail}");
        }

        var url = stdOut
            .Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .LastOrDefault();
        if (string.IsNullOrWhiteSpace(url))
        {
            throw new InvalidOperationException("gh gist create returned no gist url");
        }

        return url;
    }

    private static async Task<(int ExitCode, string StdOut, string? Error)> RunGhGistAsync(
        string filePath, IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        try
        {
            return await RunProcessAsync("gh", arguments, cancellationToken);
        }
        catch (Win32Exception)
        {
            throw new InvalidOperationException(
                "gh CLI not found. Install it from https://cli.github.com to share sessions.");
        }
    }

    /// <summary>
    /// The real process launcher: configure the process, start it, and only then read
    /// stdout/stderr (the streams do not exist before <see cref="Process.Start()"/>),
    /// draining both while waiting for exit so neither deadlocks on a full buffer.
    /// No shell is involved; arguments go through <see cref="ProcessStartInfo.ArgumentList"/>.
    /// </summary>
    internal static async Task<(int ExitCode, string StdOut, string? Error)> RunProcessAsync(
        string fileName, IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = fileName,
                // ArgumentList passes each element verbatim (no quoting layer), so paths
                // containing quote characters cannot alter the argument boundaries.
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            },
        };

        foreach (var argument in arguments)
        {
            process.StartInfo.ArgumentList.Add(argument);
        }

        if (!process.Start())
        {
            throw new InvalidOperationException($"failed to start {fileName}");
        }

        // Reading the streams is only valid once the process has started; begin both
        // drains before waiting so the wait cannot deadlock on a full pipe buffer.
        var stdOutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stdErrTask = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        return (process.ExitCode, await stdOutTask, await stdErrTask);
    }
}
