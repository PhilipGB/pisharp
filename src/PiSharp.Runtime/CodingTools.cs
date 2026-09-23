using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using Microsoft.Extensions.AI;
using PiSharp.Core;

namespace PiSharp.Runtime;

/// <summary>Local coding tools. Paths resolve against the directory in which the agent was started.</summary>
public sealed class CodingTools(string workingDirectory)
{
    private readonly string _cwd = Path.GetFullPath(workingDirectory);
    private static readonly FileMutationQueue s_mutations = new();
    private const int MaxBytes = 50 * 1024;
    private const int MaxLines = 2000;

    public IList<AITool> Create() =>
    [
        AIFunctionFactory.Create(Read, name: "read"), AIFunctionFactory.Create(Write, name: "write"),
        AIFunctionFactory.Create(EditBatch, name: "edit"), AIFunctionFactory.Create(Bash, name: "bash")
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
            return await s_mutations.RunAsync(absolute, async () =>
            {
                Directory.CreateDirectory(Path.GetDirectoryName(absolute)!);
                cancellationToken.ThrowIfCancellationRequested();
                await File.WriteAllTextAsync(absolute, content, cancellationToken);
                return $"Successfully wrote to {path}";
            }, cancellationToken);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return $"Error writing {path}: {e.Message}";
        }
    }

    [Description("Replace one uniquely matching block in an existing file. Does not create files.")]
    public Task<string> Edit(string path, string oldText, string newText, CancellationToken cancellationToken = default) =>
        EditBatch(path, [new TextEdit(oldText, newText)], cancellationToken);

    [Description("Edit a file with multiple exact, unique, nonoverlapping replacements matched against its original contents.")]
    public async Task<string> EditBatch(
        [Description("Path relative to the working directory or absolute path.")] string path,
        [Description("One or more oldText/newText blocks; each oldText must match uniquely in the original file.")] List<TextEdit> edits,
        CancellationToken cancellationToken = default)
    {
        if (edits is null || edits.Count == 0) return "Error: Edit tool input is invalid. edits must contain at least one replacement.";
        try
        {
            var absolute = Resolve(path);
            return await s_mutations.RunAsync(absolute, async () =>
            {
                var bytes = await File.ReadAllBytesAsync(absolute, cancellationToken);
                var bom = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF;
                var source = new UTF8Encoding(false, true).GetString(bytes, bom ? 3 : 0, bytes.Length - (bom ? 3 : 0));
                var lf = source.IndexOf('\n');
                var ending = lf > 0 && source[lf - 1] == '\r' ? "\r\n" : "\n";
                var modified = FileEdits.Apply(source, edits, path);
                var restored = ending == "\r\n" ? modified.Replace("\n", "\r\n", StringComparison.Ordinal) : modified;
                var payload = Encoding.UTF8.GetBytes(restored);
                if (bom) payload = [0xEF, 0xBB, 0xBF, .. payload];
                cancellationToken.ThrowIfCancellationRequested();
                await File.WriteAllBytesAsync(absolute, payload, cancellationToken);
                return $"Successfully replaced {edits.Count} block(s) in {path}.";
            }, cancellationToken);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or DecoderFallbackException)
        {
            return $"Error: {e.Message}";
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
        await using var output = new ShellOutputBuffer();
        try
        {
            linked.Token.ThrowIfCancellationRequested();
            process.Start();
            static async Task Pump(Stream source, ShellOutputBuffer target)
            {
                var buffer = new byte[8192];
                int count;
                while ((count = await source.ReadAsync(buffer)) > 0)
                    await target.AppendAsync(buffer.AsMemory(0, count));
            }
            var stdout = Pump(process.StandardOutput.BaseStream, output);
            var stderr = Pump(process.StandardError.BaseStream, output);
            try
            {
                await process.WaitForExitAsync(linked.Token);
                // A background descendant may inherit the pipes after the shell exits.
                // Keep the timeout active until both streams reach EOF.
                await Task.WhenAll(stdout, stderr).WaitAsync(linked.Token);
                var result = await output.FinishAsync();
                return process.ExitCode == 0 ? result : $"{result}\n\nError: Command exited with code {process.ExitCode}";
            }
            catch (OperationCanceledException)
            {
                // setsid puts the shell and descendants into their own process group.
                using var kill = Process.Start(new ProcessStartInfo("/bin/kill") { ArgumentList = { "-KILL", "--", $"-{process.Id}" } });
                if (kill is not null) await kill.WaitForExitAsync(CancellationToken.None);
                if (!process.HasExited) process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync(CancellationToken.None);
                try { await Task.WhenAll(stdout, stderr).WaitAsync(TimeSpan.FromSeconds(5)); }
                catch (TimeoutException) { process.StandardOutput.Close(); process.StandardError.Close(); }
                var result = await output.FinishAsync();
                return $"{result}\n\nError: " + (cancellationToken.IsCancellationRequested ? "Command aborted" : $"Command timed out after {timeout} seconds");
            }
        }
        catch (Exception e) when (e is IOException or System.ComponentModel.Win32Exception)
        {
            return $"Error executing command: {e.Message}";
        }
    }
}
