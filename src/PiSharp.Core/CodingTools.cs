using System.ComponentModel;
using System.Diagnostics;
using System.Text;

namespace PiSharp.Core;

public sealed class CodingTools
{
    private const int MaxReadCharacters = 200_000;
    private readonly WorkspacePathPolicy _paths;
    private readonly EditEngine _editEngine = new();

    public CodingTools(string workspaceRoot)
    {
        _paths = new WorkspacePathPolicy(workspaceRoot);
    }

    public string WorkspaceRoot => _paths.Root;

    [Description("Read a UTF-8 text file from the workspace. Returns line-numbered text. Use offset and limit for large files.")]
    public async Task<string> ReadAsync(
        [Description("Workspace-relative file path.")] string path,
        [Description("1-based first line to return.")] int offset = 1,
        [Description("Maximum number of lines to return.")] int limit = 500,
        CancellationToken cancellationToken = default)
    {
        if (offset < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(offset), "offset must be at least 1.");
        }
        if (limit is < 1 or > 5000)
        {
            throw new ArgumentOutOfRangeException(nameof(limit), "limit must be between 1 and 5000.");
        }

        var absolutePath = _paths.Resolve(path);
        if (!File.Exists(absolutePath))
        {
            throw new FileNotFoundException($"File not found: {path}", absolutePath);
        }

        var lines = await File.ReadAllLinesAsync(absolutePath, cancellationToken);
        var start = Math.Min(offset - 1, lines.Length);
        var end = Math.Min(start + limit, lines.Length);
        var builder = new StringBuilder();

        for (var index = start; index < end; index++)
        {
            builder.Append(index + 1).Append("\t").AppendLine(lines[index]);
            if (builder.Length >= MaxReadCharacters)
            {
                builder.AppendLine("[output truncated]");
                break;
            }
        }

        if (end < lines.Length)
        {
            builder.Append($"[showing lines {start + 1}-{end} of {lines.Length}]");
        }

        return builder.ToString();
    }

    [Description("Write an entire UTF-8 text file in the workspace. Creates parent directories when needed and replaces existing content.")]
    public async Task<string> WriteAsync(
        [Description("Workspace-relative file path.")] string path,
        [Description("Complete new file contents.")] string content,
        CancellationToken cancellationToken = default)
    {
        var absolutePath = _paths.Resolve(path);
        var parent = Path.GetDirectoryName(absolutePath);
        if (!string.IsNullOrEmpty(parent))
        {
            Directory.CreateDirectory(parent);
        }

        await File.WriteAllTextAsync(absolutePath, content, new UTF8Encoding(false), cancellationToken);
        return $"Wrote {content.Length} characters to {path}.";
    }

    [Description("Edit one file using exact text replacements. Each oldText must be non-empty, unique in the original file, and edits must not overlap.")]
    public async Task<string> EditAsync(
        [Description("Workspace-relative file path.")] string path,
        [Description("Exact replacements to apply against the original file contents.")] IReadOnlyList<EditOperation> edits,
        CancellationToken cancellationToken = default)
    {
        await EditWithDetailsAsync(path, edits, cancellationToken);
        return $"Successfully replaced {edits.Count} block(s) in {path}.";
    }

    /// <summary>Applies edits and returns the diff and unified patch used by terminal renderers.</summary>
    public async Task<EditResult> EditWithDetailsAsync(
        string path,
        IReadOnlyList<EditOperation> edits,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(edits);
        if (edits.Count == 0)
        {
            throw new ArgumentException("At least one edit is required.", nameof(edits));
        }

        var absolutePath = _paths.Resolve(path);
        if (!File.Exists(absolutePath))
        {
            throw new FileNotFoundException($"File not found: {path}", absolutePath);
        }

        var content = await File.ReadAllTextAsync(absolutePath, cancellationToken);
        var result = _editEngine.Apply(path, content, edits);
        await File.WriteAllTextAsync(absolutePath, result.UpdatedContent, new UTF8Encoding(false), cancellationToken);
        return result;
    }

    /// <summary>Applies an edit and returns only the metadata needed by an AI tool renderer.</summary>
    public async Task<EditToolResult> EditForAgentAsync(
        string path,
        IReadOnlyList<EditOperation> edits,
        CancellationToken cancellationToken = default)
    {
        var result = await EditWithDetailsAsync(path, edits, cancellationToken);
        return new EditToolResult(
            $"Successfully replaced {edits.Count} block(s) in {path}.",
            result.Diff,
            result.Patch,
            result.FirstChangedLine,
            result.UsedFuzzyMatch);
    }

    [Description("Run a shell command in the workspace and return stdout, stderr, and the exit code. Use for builds, tests, git, search, and other repository operations.")]
    public async Task<string> BashAsync(
        [Description("Shell command to execute.")] string command,
        [Description("Command timeout in seconds, from 1 to 600.")] int timeoutSeconds = 120,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(command);
        if (timeoutSeconds is < 1 or > 600)
        {
            throw new ArgumentOutOfRangeException(nameof(timeoutSeconds), "timeoutSeconds must be between 1 and 600.");
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = OperatingSystem.IsWindows() ? "powershell.exe" : "/bin/bash",
            WorkingDirectory = WorkspaceRoot,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        if (OperatingSystem.IsWindows())
        {
            startInfo.ArgumentList.Add("-NoProfile");
            startInfo.ArgumentList.Add("-Command");
            startInfo.ArgumentList.Add(command);
        }
        else
        {
            startInfo.ArgumentList.Add("-lc");
            startInfo.ArgumentList.Add(command);
        }

        using var process = new Process { StartInfo = startInfo };
        process.Start();

        var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));

        try
        {
            await process.WaitForExitAsync(timeoutCts.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            TryKill(process);
            throw new TimeoutException($"Command exceeded {timeoutSeconds} seconds.");
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            throw;
        }

        var stdout = await stdoutTask;
        var stderr = await stderrTask;
        return FormatCommandResult(process.ExitCode, stdout, stderr);
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch
        {
            // Best effort during cancellation/timeout.
        }
    }

    private static string FormatCommandResult(int exitCode, string stdout, string stderr)
    {
        var builder = new StringBuilder();
        builder.AppendLine($"exit_code: {exitCode}");
        builder.AppendLine("stdout:");
        AppendTruncated(builder, stdout);
        builder.AppendLine();
        builder.AppendLine("stderr:");
        AppendTruncated(builder, stderr);
        return builder.ToString();
    }

    private static void AppendTruncated(StringBuilder builder, string value)
    {
        var result = OutputTruncator.Tail(value);
        builder.Append(result.Content);
        if (!result.Truncated)
        {
            return;
        }

        builder.AppendLine();
        builder.Append($"[output truncated: showing {result.OutputLines} of {result.TotalLines} lines]");
    }
}
