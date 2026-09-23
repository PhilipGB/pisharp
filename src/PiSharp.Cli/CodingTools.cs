using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using Microsoft.Extensions.AI;

namespace PiSharp.Cli;

/// <summary>Local coding tools. Paths resolve against the directory in which the agent was started.</summary>
public sealed class CodingTools(string workingDirectory)
{
    private readonly string _cwd = Path.GetFullPath(workingDirectory);
    private const int MaxBytes = 50 * 1024;
    private const int MaxLines = 2000;

    public IList<AITool> Create() =>
    [
        AIFunctionFactory.Create(Read, name: "read"), AIFunctionFactory.Create(Write, name: "write"),
        AIFunctionFactory.Create(Edit, name: "edit"), AIFunctionFactory.Create(Bash, name: "bash")
    ];

    private string Resolve(string path) => Path.GetFullPath(path, _cwd);

    [Description("Read a text file. Output is limited to 2000 lines or 50KB. Use offset and limit to continue.")]
    public async Task<string> Read(
        [Description("Path relative to the working directory or absolute path.")] string path,
        [Description("First line to read, starting at 1.")] int offset = 1,
        [Description("Maximum number of lines to return.")] int? limit = null,
        CancellationToken cancellationToken = default)
    {
        if (offset < 1 || limit is <= 0) return "Error: offset and limit must be positive.";
        try
        {
            var lines = (await File.ReadAllTextAsync(Resolve(path), cancellationToken)).Split('\n');
            if (offset > lines.Length) return $"Error: Offset {offset} is beyond end of file ({lines.Length} lines total)";
            int count = Math.Min(lines.Length - offset + 1, Math.Min(limit ?? MaxLines, MaxLines));
            var result = string.Join("\n", lines.Skip(offset - 1).Take(count));
            while (Encoding.UTF8.GetByteCount(result) > MaxBytes && count > 1)
                result = string.Join("\n", lines.Skip(offset - 1).Take(--count));
            if (Encoding.UTF8.GetByteCount(result) > MaxBytes)
                return $"[Line {offset} exceeds 50KB limit. Use bash to inspect it.]";
            if (offset - 1 + count < lines.Length)
                result += $"\n\n[Showing lines {offset}-{offset + count - 1} of {lines.Length}. Use offset={offset + count} to continue.]";
            return result;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return $"Error reading {path}: {e.Message}";
        }
    }

    [Description("Write content to a file, creating parent directories if necessary. Overwrites existing files.")]
    public async Task<string> Write(
        [Description("Path relative to the working directory or absolute path.")] string path,
        [Description("Complete file contents.")] string content,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var absolute = Resolve(path);
            Directory.CreateDirectory(Path.GetDirectoryName(absolute)!);
            await File.WriteAllTextAsync(absolute, content, cancellationToken);
            return $"Successfully wrote to {path}";
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return $"Error writing {path}: {e.Message}";
        }
    }

    [Description("Replace one uniquely matching block in an existing file. Does not create files.")]
    public async Task<string> Edit(
        [Description("Path to the file to edit.")] string path,
        [Description("Exact text to replace; must occur exactly once.")] string oldText,
        [Description("Replacement text.")] string newText,
        CancellationToken cancellationToken = default)
    {
        if (oldText.Length == 0) return "Error: oldText cannot be empty.";
        try
        {
            var absolute = Resolve(path);
            var original = await File.ReadAllTextAsync(absolute, cancellationToken);
            var bom = original.StartsWith('\uFEFF') ? "\uFEFF" : "";
            var content = original[bom.Length..];
            bool crlf = content.Contains("\r\n", StringComparison.Ordinal);
            var normalized = content.Replace("\r\n", "\n", StringComparison.Ordinal);
            var needle = oldText.Replace("\r\n", "\n", StringComparison.Ordinal);
            int index = normalized.IndexOf(needle, StringComparison.Ordinal);
            if (index < 0) return $"Error: Could not find exact text in {path}.";
            if (normalized.IndexOf(needle, index + 1, StringComparison.Ordinal) >= 0)
                return $"Error: oldText is ambiguous in {path}; provide more context.";
            var replaced = normalized[..index] + newText.Replace("\r\n", "\n", StringComparison.Ordinal) + normalized[(index + needle.Length)..];
            cancellationToken.ThrowIfCancellationRequested();
            await File.WriteAllTextAsync(absolute, bom + (crlf ? replaced.Replace("\n", "\r\n", StringComparison.Ordinal) : replaced), cancellationToken);
            return $"Successfully replaced 1 block(s) in {path}.";
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return $"Error editing {path}: {e.Message}";
        }
    }

    [Description("Execute a bash command in the working directory. Returns combined stdout/stderr and exit code.")]
    public async Task<string> Bash(
        [Description("Shell command to execute.")] string command,
        [Description("Optional timeout in seconds; no default timeout.")] int? timeout = null,
        CancellationToken cancellationToken = default)
    {
        if (timeout is <= 0) return "Error: timeout must be positive.";
        using var timeoutSource = timeout.HasValue ? new CancellationTokenSource(TimeSpan.FromSeconds(timeout.Value)) : new CancellationTokenSource();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutSource.Token);
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo("/usr/bin/setsid")
            {
                WorkingDirectory = _cwd,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                ArgumentList = { "/bin/bash", "-c", command }
            }
        };
        try
        {
            process.Start();
            // Drain both pipes concurrently, even when output exceeds the display limit.
            var stdout = process.StandardOutput.ReadToEndAsync(linked.Token);
            var stderr = process.StandardError.ReadToEndAsync(linked.Token);
            try
            {
                await process.WaitForExitAsync(linked.Token);
                var combined = await stdout + await stderr;
                var lines = combined.Split('\n');
                var tail = string.Join("\n", lines.TakeLast(MaxLines));
                if (tail.Length > MaxBytes) tail = tail[^MaxBytes..];
                return (combined.Length != tail.Length ? "[Output truncated]\n" : "") + tail + $"\n\nProcess exited with code {process.ExitCode}";
            }
            catch (OperationCanceledException)
            {
                // setsid puts the shell and descendants into their own process group.
                using var kill = Process.Start(new ProcessStartInfo("/bin/kill") { ArgumentList = { "-KILL", "--", $"-{process.Id}" } });
                if (kill is not null) await kill.WaitForExitAsync(CancellationToken.None);
                if (!process.HasExited) process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync(CancellationToken.None);
                return cancellationToken.IsCancellationRequested ? "Error: aborted" : $"Error: timeout:{timeout}";
            }
        }
        catch (Exception e) when (e is IOException or System.ComponentModel.Win32Exception)
        {
            return $"Error executing command: {e.Message}";
        }
    }
}
