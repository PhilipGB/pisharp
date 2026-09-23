using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using Microsoft.Extensions.AI;
using PiSharp.Core;
using PiSharp.Runtime.Tools;

namespace PiSharp.Runtime;

/// <summary>Local coding tools. Paths resolve against the directory in which the agent was started.</summary>
public sealed class CodingTools(string workingDirectory)
{
    private readonly string _cwd = Path.GetFullPath(workingDirectory);
    private static readonly FileMutationQueue s_mutations = new();

    public IList<AITool> Create(IReadOnlyList<string>? requested = null, IReadOnlyList<string>? excluded = null, bool noTools = false)
    {
        var available = new Dictionary<string, AITool>(StringComparer.Ordinal)
        {
            ["read"] = AIFunctionFactory.Create(Read, name: "read"),
            ["bash"] = AIFunctionFactory.Create(Bash, name: "bash"),
            ["edit"] = AIFunctionFactory.Create(EditBatch, name: "edit"),
            ["write"] = AIFunctionFactory.Create(Write, name: "write"),
            ["grep"] = AIFunctionFactory.Create(new SearchTools(_cwd).Grep, name: "grep"),
            ["find"] = AIFunctionFactory.Create(new SearchTools(_cwd).Find, name: "find"),
            ["ls"] = AIFunctionFactory.Create(new DirectoryListingTool(_cwd).List, name: "ls")
        };
        var names = requested ?? (noTools ? [] : ["read", "bash", "edit", "write"]);
        var disabled = excluded is null ? null : new HashSet<string>(excluded, StringComparer.Ordinal);
        var selected = new List<AITool>();
        foreach (var name in names.Distinct(StringComparer.Ordinal))
        {
            if (!available.TryGetValue(name, out var tool)) throw new ArgumentException($"Unknown tool name: {name}");
            if (disabled?.Contains(name) != true) selected.Add(tool);
        }
        return selected;
    }

    private string Resolve(string path) => Path.GetFullPath(path, _cwd);

    [Description("Read a text file. Output is limited to 2000 lines or 50KB. Use offset and limit to continue.")]
    public async Task<string> Read(
        [Description("Path relative to the working directory or absolute path.")] string path,
        [Description("First line to read, starting at 1.")] int offset = 1,
        [Description("Maximum number of lines to return.")] int? limit = null,
        CancellationToken cancellationToken = default)
    {
        if (offset < 1 || limit is <= 0) throw new ToolFailureException("offset and limit must be positive.");
        try
        {
            var text = await File.ReadAllTextAsync(Resolve(path), cancellationToken);
            return ReadTextPlanner.Select(text, path, offset, limit);
        }
        catch (ArgumentOutOfRangeException e) { throw new ToolFailureException(e.Message.Split('\n')[0], inner: e); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            throw new ToolFailureException($"Error reading {path}: {e.Message}", inner: e);
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
                await AtomicFileWriter.ReplaceAsync(absolute, Encoding.UTF8.GetBytes(content), cancellationToken);
                return $"Successfully wrote to {path}";
            }, cancellationToken);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            throw new ToolFailureException($"Error writing {path}: {e.Message}", inner: e);
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
        if (edits is null || edits.Count == 0) throw new ToolFailureException("Edit tool input is invalid. edits must contain at least one replacement.");
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
                await AtomicFileWriter.ReplaceAsync(absolute, payload, cancellationToken);
                return $"Successfully replaced {edits.Count} block(s) in {path}.";
            }, cancellationToken);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or DecoderFallbackException)
        {
            throw new ToolFailureException(e.Message, inner: e);
        }
    }

    [Description("Execute a bash command in the working directory. Returns combined stdout/stderr and exit code.")]
    public async Task<string> Bash(
        [Description("Shell command to execute.")] string command,
        [Description("Optional timeout in seconds; no default timeout.")] int? timeout = null,
        CancellationToken cancellationToken = default)
    {
        if (timeout is <= 0) throw new ToolFailureException("timeout must be positive.");
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
                if (process.ExitCode != 0) throw new ToolFailureException($"Command exited with code {process.ExitCode}", result, process.ExitCode);
                return result;
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
                if (cancellationToken.IsCancellationRequested) throw new OperationCanceledException($"Command aborted. {result}", cancellationToken);
                throw new ToolFailureException($"Command timed out after {timeout} seconds", result);
            }
        }
        catch (Exception e) when (e is IOException or System.ComponentModel.Win32Exception)
        {
            throw new ToolFailureException($"Error executing command: {e.Message}", inner: e);
        }
    }
}
