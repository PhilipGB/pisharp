using System.Diagnostics;

namespace PiSharp.Cli;

/// <summary>
/// /share support: publish a session export to a public GitHub gist via the gh CLI.
/// This is PiSharp's documented alternative to the pinned Radius gateway share (see
/// docs/PARITY.md); the pinned share posts a presentation JSONL through the gateway and
/// falls back to a private gist, while PiSharp gists the self-contained HTML export.
/// </summary>
internal static class SessionShare
{
    /// <summary>
    /// Test seam replacing the gh CLI invocation (returns exit code, stdout, stderr).
    /// </summary>
    internal static Func<string, CancellationToken, Task<(int ExitCode, string StdOut, string? Error)>>? Launcher;

    /// <summary>Creates a public gist for the file and returns the gist URL from gh's output.</summary>
    public static async Task<string> ShareFileAsync(string filePath, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(filePath);
        if (!File.Exists(filePath))
        {
            throw new FileNotFoundException("Nothing to share — the export file does not exist.", filePath);
        }

        var (exitCode, stdOut, error) = await (Launcher ?? RunGhGistAsync)(filePath, cancellationToken);
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
        string filePath, CancellationToken cancellationToken)
    {
        try
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "gh",
                    // ArgumentList passes each element verbatim (no quoting layer), so paths
                    // containing quote characters cannot alter the argument boundaries.
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                },
            };

            process.StartInfo.ArgumentList.Add("gist");
            process.StartInfo.ArgumentList.Add("create");
            process.StartInfo.ArgumentList.Add(filePath);
            process.StartInfo.ArgumentList.Add("--public");
            process.StartInfo.ArgumentList.Add("-d");
            process.StartInfo.ArgumentList.Add("PiSharp session");
            var stdOutTask = process.StandardOutput.ReadToEndAsync();
            var stdErrTask = process.StandardError.ReadToEndAsync();
            if (!process.Start())
            {
                throw new InvalidOperationException("failed to start gh");
            }

            await process.WaitForExitAsync(cancellationToken);
            return (process.ExitCode, await stdOutTask, await stdErrTask);
        }
        catch (System.ComponentModel.Win32Exception)
        {
            throw new InvalidOperationException(
                "gh CLI not found. Install it from https://cli.github.com to share sessions.");
        }
    }
}
