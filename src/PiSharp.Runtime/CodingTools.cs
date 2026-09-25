using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using PiSharp.Core;
using PiSharp.Runtime.Tools;

namespace PiSharp.Runtime;

/// <summary>Local coding tools. Paths resolve against the directory in which the agent was started.</summary>
public sealed class CodingTools
{
    internal const string BashOutputContextKey = "PiSharp.Runtime.BashOutputUpdate";
    private const double MaxTimeoutSeconds = int.MaxValue / 1000d;
    private static readonly TimeSpan s_stdioIdleGrace = TimeSpan.FromMilliseconds(100);
    private static readonly string[] s_piSessionEnvironmentNames =
        ["PI_SESSION_ID", "PI_SESSION_FILE", "PI_PROVIDER", "PI_MODEL", "PI_REASONING_LEVEL"];
    private readonly string _cwd;
    private readonly string? _shellPath;
    private readonly ConcurrentDictionary<Guid, CancellationTokenSource> _runningBash = new();
    private static readonly FileMutationQueue s_mutations = new();

    public CodingTools(string workingDirectory, string? shellPath = null)
    {
        _cwd = Path.GetFullPath(workingDirectory);
        _shellPath = shellPath;
    }

    public IList<AITool> Create(IReadOnlyList<string>? requested = null, IReadOnlyList<string>? excluded = null, bool noTools = false)
    {
        var available = new Dictionary<string, AITool>(StringComparer.Ordinal)
        {
            ["read"] = AIFunctionFactory.Create(ReadForTool, name: "read"),
            ["bash"] = AIFunctionFactory.Create(BashForTool, name: "bash"),
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

    private static string? FindBlockingFileAncestor(string path)
    {
        var current = Path.GetFullPath(path);
        while (true)
        {
            if (File.Exists(current)) return current;
            var parent = Path.GetDirectoryName(current);
            if (parent is null || string.Equals(parent, current,
                    OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                return null;
            current = parent;
        }
    }

    private static async Task<byte[]> ReadEditableFileAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite,
            FileShare.ReadWrite | FileShare.Delete, 81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var content = new MemoryStream();
        await stream.CopyToAsync(content, cancellationToken);
        return content.ToArray();
    }

    [Description("Read a text file. Output is limited to 2000 lines or 50KB. Use offset and limit to continue.")]
    public async Task<string> Read(
        [Description("Path relative to the working directory or absolute path.")] string path,
        [Description("First line to read, starting at 1.")] int offset = 1,
        [Description("Maximum number of lines to return.")] int? limit = null,
        CancellationToken cancellationToken = default) =>
        (await ReadCoreAsync(path, offset, limit, cancellationToken)).Text;

    [Description("Read text files and images (JPEG, PNG, GIF, WebP, BMP). Text output is limited to 2000 lines or 50KB. Use offset and limit to continue.")]
    private async Task<object> ReadForTool(
        [Description("Path relative to the working directory or absolute path.")] string path,
        [Description("First line to read, starting at 1.")] int offset = 1,
        [Description("Maximum number of lines to return.")] int? limit = null,
        CancellationToken cancellationToken = default)
    {
        var output = await ReadCoreAsync(path, offset, limit, cancellationToken);
        return output.ImageDataBase64 is null ? output.Text : output;
    }

    private async Task<ReadToolOutput> ReadCoreAsync(string path, int offset, int? limit, CancellationToken cancellationToken)
    {
        try
        {
            var resolved = Resolve(path);
            const int maxImageBytes = 20 * 1024 * 1024;
            await using var stream = new FileStream(resolved, FileMode.Open, FileAccess.Read, FileShare.Read,
                bufferSize: 4096, FileOptions.Asynchronous | FileOptions.SequentialScan);
            var length = stream.Length;
            var mimeType = await ReadImageDetector.DetectAsync(stream, cancellationToken);
            if (mimeType is not null)
            {
                if (length > maxImageBytes)
                    return new ReadToolOutput($"Read image file [{mimeType}]\n[Image omitted: could not be resized below the inline image size limit.]");
                var bytes = new byte[checked((int)length)];
                await stream.ReadExactlyAsync(bytes, cancellationToken);
                return await Task.Run(() => ReadImageProcessor.Process(bytes, mimeType, cancellationToken), cancellationToken);
            }
            if (offset < 1 || limit is <= 0) throw new ToolFailureException("offset and limit must be positive.");
            if (length > 2 * 1024 * 1024)
                return new ReadToolOutput(await StreamingTextReader.SelectAsync(resolved, path, offset, limit, cancellationToken));
            var content = new byte[checked((int)length)];
            stream.Position = 0;
            await stream.ReadExactlyAsync(content, cancellationToken);
            var text = Encoding.UTF8.GetString(content);
            return new ReadToolOutput(ReadTextPlanner.Select(text, path, offset, limit));
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
        var absolute = Resolve(path);
        var parent = Path.GetDirectoryName(absolute)!;
        try
        {
            return await s_mutations.RunAsync(absolute, async () =>
            {
                if (FindBlockingFileAncestor(parent) is { } blockingFile)
                {
                    var pathIsFile = string.Equals(blockingFile, parent,
                        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
                    var code = pathIsFile ? "EEXIST" : "ENOTDIR";
                    var description = pathIsFile ? "file already exists" : "not a directory";
                    throw new ToolFailureException($"{code}: {description}, mkdir '{parent}'");
                }
                try { Directory.CreateDirectory(parent); }
                catch (UnauthorizedAccessException error)
                {
                    throw new ToolFailureException($"EACCES: permission denied, mkdir '{parent}'", inner: error);
                }
                if (Directory.Exists(absolute))
                    throw new ToolFailureException($"EISDIR: illegal operation on a directory, open '{absolute}'");

                await AtomicFileWriter.ReplaceAsync(absolute, Encoding.UTF8.GetBytes(content), cancellationToken);
                return $"Successfully wrote to {path}";
            }, cancellationToken);
        }
        catch (UnauthorizedAccessException error)
        {
            throw new ToolFailureException($"EACCES: permission denied, open '{absolute}'", inner: error);
        }
        catch (IOException e)
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
                byte[] bytes;
                try { bytes = await ReadEditableFileAsync(absolute, cancellationToken); }
                catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException)
                {
                    throw new ToolFailureException($"Could not edit file: {path}. Error code: ENOENT.", inner: e);
                }
                catch (UnauthorizedAccessException e)
                {
                    throw new ToolFailureException($"Could not edit file: {path}. Error code: EACCES.", inner: e);
                }
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

    [Description("Execute a bash command in the current working directory. Returns stdout and stderr. Output is truncated to the last 2000 lines or 50KB, whichever is hit first. If truncated, full output is saved to a temp file. The shell receives PI_SESSION_ID, PI_SESSION_FILE when saved, PI_PROVIDER, PI_MODEL and PI_REASONING_LEVEL for the current run.")]
    private Task<string> BashForTool(
        [Description("Shell command to execute.")] string command,
        [Description("Optional timeout in seconds; no default timeout.")] double? timeout = null,
        AIFunctionArguments? arguments = null,
        CancellationToken cancellationToken = default)
    {
        Action<string>? onUpdate = null;
        if (arguments?.Context?.TryGetValue(BashOutputContextKey, out var value) == true)
            onUpdate = value as Action<string>;
        return BashToolAsync(command, timeout, onUpdate, cancellationToken, CurrentBashSessionEnvironment());
    }

    /// <summary>Execute bash in the configured working directory, returning the bounded combined output.</summary>
    public Task<string> Bash(string command, double? timeout = null, CancellationToken cancellationToken = default) =>
        BashToolAsync(command, timeout, onUpdate: null, cancellationToken, normalizeOutput: true);

    /// <summary>Execute bash and return the process result without converting a nonzero exit to a tool failure.</summary>
    public Task<BashExecutionResult> ExecuteBashAsync(string command, Action<string>? onUpdate = null,
        CancellationToken cancellationToken = default) =>
        BashCoreAsync(command, timeout: null, onUpdate, cancellationToken, returnCancellationResult: true,
            normalizeOutput: true, throttleUpdates: false);

    public void AbortBash()
    {
        foreach (var cancellation in _runningBash.Values)
        {
            try { cancellation.Cancel(); }
            catch (ObjectDisposedException) { }
        }
    }

    private async Task<string> BashToolAsync(string command, double? timeout, Action<string>? onUpdate,
        CancellationToken cancellationToken, IReadOnlyDictionary<string, string?>? sessionEnvironment = null,
        bool normalizeOutput = false, bool throttleUpdates = true)
    {
        var result = await BashCoreAsync(command, timeout, onUpdate, cancellationToken, returnCancellationResult: false,
            sessionEnvironment, normalizeOutput, throttleUpdates);
        if (result.ExitCode is { } exitCode && exitCode != 0)
            throw new ToolFailureException($"Command exited with code {exitCode}", result.DisplayOutput, exitCode);
        return result.DisplayOutput;
    }

    private async Task<BashExecutionResult> BashCoreAsync(string command, double? timeout, Action<string>? onUpdate,
        CancellationToken cancellationToken, bool returnCancellationResult,
        IReadOnlyDictionary<string, string?>? sessionEnvironment = null, bool normalizeOutput = false,
        bool throttleUpdates = true)
    {
        if (timeout.HasValue && (!double.IsFinite(timeout.Value) || timeout.Value <= 0))
            throw new ToolFailureException("Invalid timeout: must be a finite number of seconds");
        if (timeout > MaxTimeoutSeconds)
            throw new ToolFailureException($"Invalid timeout: maximum is {MaxTimeoutSeconds.ToString("0.###", CultureInfo.InvariantCulture)} seconds");
        if (!Directory.Exists(_cwd))
            throw new ToolFailureException($"Working directory does not exist: {_cwd}\nCannot execute bash commands.");

        var shell = ResolveShell(_shellPath, _cwd);
        var operationId = Guid.NewGuid();
        using var abortSource = new CancellationTokenSource();
        _runningBash[operationId] = abortSource;
        using var timeoutSource = new CancellationTokenSource();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutSource.Token, abortSource.Token);
        using var process = new Process
        {
            StartInfo = CreateStartInfo(shell, command, _cwd, out var processGroup, sessionEnvironment)
        };
        using var pumpStop = new CancellationTokenSource();
        await using var output = new ShellOutputBuffer(normalizeOutput);
        using var updates = new BashOutputUpdates(onUpdate, throttleUpdates);
        long lastOutputTicks = Stopwatch.GetTimestamp();
        var started = false;
        Task? stdout = null;
        Task? stderr = null;
        try
        {
            linked.Token.ThrowIfCancellationRequested();
            process.Start();
            started = true;
            if (timeout.HasValue) timeoutSource.CancelAfter(TimeSpan.FromSeconds(timeout.Value));
            stdout = PumpAsync(process.StandardOutput.BaseStream, output, updates, pumpStop.Token, ticks =>
                Interlocked.Exchange(ref lastOutputTicks, ticks));
            stderr = PumpAsync(process.StandardError.BaseStream, output, updates, pumpStop.Token, ticks =>
                Interlocked.Exchange(ref lastOutputTicks, ticks));
            await process.WaitForExitAsync(linked.Token);
            Interlocked.Exchange(ref lastOutputTicks, Stopwatch.GetTimestamp());
            await DrainOutputAfterExitAsync(process, stdout, stderr, linked.Token, pumpStop,
                () => Interlocked.Read(ref lastOutputTicks));
            var finalDecoded = await output.FlushDecoderAsync();
            updates.Append(finalDecoded);
            updates.Complete();
            var result = await output.FinishWithMetadataAsync();
            var cancelled = returnCancellationResult && (cancellationToken.IsCancellationRequested || abortSource.IsCancellationRequested);
            return new(result.Output, result.DisplayOutput, cancelled ? null : process.ExitCode, cancelled,
                result.Truncated, result.FullOutputPath);
        }
        catch (OperationCanceledException) when (started)
        {
            KillProcessTree(process, processGroup);
            await WaitForExitQuietlyAsync(process);
            await StopPumpsAsync(process, stdout, stderr, pumpStop);
            var finalDecoded = await output.FlushDecoderAsync();
            updates.Append(finalDecoded);
            var result = await output.FinishWithMetadataAsync();
            updates.Complete();
            if (cancellationToken.IsCancellationRequested || abortSource.IsCancellationRequested)
            {
                if (returnCancellationResult)
                    return new(result.Output, result.DisplayOutput, null, true, result.Truncated, result.FullOutputPath);
                var abortToken = cancellationToken.IsCancellationRequested ? cancellationToken : abortSource.Token;
                throw new OperationCanceledException(result.DisplayOutput == "(no output)" ? "Command aborted" :
                    $"{result.DisplayOutput}\n\nCommand aborted", abortToken);
            }
            var seconds = timeout!.Value.ToString("0.################", CultureInfo.InvariantCulture);
            throw new ToolFailureException($"Command timed out after {seconds} seconds", result.DisplayOutput);
        }
        catch (OperationCanceledException) when (!started && returnCancellationResult &&
            (cancellationToken.IsCancellationRequested || abortSource.IsCancellationRequested))
        {
            var result = await output.FinishWithMetadataAsync();
            updates.Complete();
            return new(result.Output, result.DisplayOutput, null, true, result.Truncated, result.FullOutputPath);
        }
        catch (Exception e) when (e is IOException or System.ComponentModel.Win32Exception)
        {
            if (started)
            {
                KillProcessTree(process, processGroup);
                await WaitForExitQuietlyAsync(process);
                await StopPumpsAsync(process, stdout, stderr, pumpStop);
            }
            throw new ToolFailureException($"Error executing command: {e.Message}", inner: e);
        }
        finally
        {
            _runningBash.TryRemove(operationId, out _);
            if (started && (!process.HasExited || stdout is { IsCompleted: false } || stderr is { IsCompleted: false }))
            {
                KillProcessTree(process, processGroup);
                await WaitForExitQuietlyAsync(process);
                await StopPumpsAsync(process, stdout, stderr, pumpStop);
            }
        }

        static async Task PumpAsync(Stream source, ShellOutputBuffer target, BashOutputUpdates updates,
            CancellationToken stop, Action<long> activity)
        {
            var buffer = new byte[8192];
            try
            {
                int count;
                while ((count = await source.ReadAsync(buffer, stop)) > 0)
                {
                    activity(Stopwatch.GetTimestamp());
                    await target.AppendAsync(buffer.AsMemory(0, count), stop, updates.Append);
                }
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
            catch (IOException) when (stop.IsCancellationRequested) { }
            catch (ObjectDisposedException) when (stop.IsCancellationRequested) { }
        }

        static async Task DrainOutputAfterExitAsync(Process process, Task stdoutTask, Task stderrTask,
            CancellationToken cancellationToken, CancellationTokenSource stop, Func<long> lastOutput)
        {
            var streams = Task.WhenAll(stdoutTask, stderrTask);
            while (!streams.IsCompleted)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var quietFor = Stopwatch.GetElapsedTime(lastOutput());
                var remaining = s_stdioIdleGrace - quietFor;
                if (remaining <= TimeSpan.Zero) break;
                await Task.WhenAny(streams, Task.Delay(remaining, cancellationToken));
            }
            if (!streams.IsCompleted)
            {
                stop.Cancel();
                process.StandardOutput.Close();
                process.StandardError.Close();
            }
            await streams;
        }

        static async Task StopPumpsAsync(Process process, Task? stdoutTask, Task? stderrTask,
            CancellationTokenSource stop)
        {
            stop.Cancel();
            try { process.StandardOutput.Close(); } catch (InvalidOperationException) { }
            try { process.StandardError.Close(); } catch (InvalidOperationException) { }
            var active = new[] { stdoutTask, stderrTask }.Where(task => task is not null).Cast<Task>().ToArray();
            if (active.Length == 0) return;
            try { await Task.WhenAll(active).WaitAsync(TimeSpan.FromSeconds(5)); }
            catch (TimeoutException) { }
        }

        static async Task WaitForExitQuietlyAsync(Process process)
        {
            try { await process.WaitForExitAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5)); }
            catch (TimeoutException) { }
            catch (InvalidOperationException) { }
        }
    }

    private static ProcessStartInfo CreateStartInfo(string shell, string command, string workingDirectory,
        out bool processGroup, IReadOnlyDictionary<string, string?>? sessionEnvironment)
    {
        processGroup = false;
        var start = new ProcessStartInfo
        {
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        if (!OperatingSystem.IsWindows() && FindExecutable("setsid") is { } setsid)
        {
            start.FileName = setsid;
            start.ArgumentList.Add(shell);
            start.ArgumentList.Add("-c");
            start.ArgumentList.Add(command);
            processGroup = true;
        }
        else
        {
            start.FileName = shell;
            start.ArgumentList.Add("-c");
            start.ArgumentList.Add(command);
        }
        var pathKey = start.Environment.Keys.FirstOrDefault(key => key.Equals("PATH", StringComparison.OrdinalIgnoreCase)) ?? "PATH";
        var path = start.Environment.TryGetValue(pathKey, out var inheritedPath) ? inheritedPath ?? "" : "";
        var appDirectory = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (!path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries).Contains(appDirectory,
                OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal))
            start.Environment[pathKey] = path.Length == 0 ? appDirectory : appDirectory + Path.PathSeparator + path;
        foreach (var name in s_piSessionEnvironmentNames) start.Environment.Remove(name);
        if (sessionEnvironment is not null)
            foreach (var name in s_piSessionEnvironmentNames)
                if (sessionEnvironment.TryGetValue(name, out var value) && value is not null)
                    start.Environment[name] = value;
        return start;
    }

    private static IReadOnlyDictionary<string, string?>? CurrentBashSessionEnvironment()
    {
        AgentRunContext? context;
        try { context = AIAgent.CurrentRunContext; }
        catch (InvalidOperationException) { return null; }
        if (context is null) return null;
        var properties = context.RunOptions?.AdditionalProperties;
        if (properties is null) return null;
        var environment = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var name in s_piSessionEnvironmentNames)
            if (properties.TryGetValue(name, out var value) && value is string text)
                environment[name] = text;
        return environment;
    }

    private static string ResolveShell(string? customPath, string workingDirectory)
    {
        if (!string.IsNullOrWhiteSpace(customPath))
        {
            var expanded = customPath == "~" ? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) :
                customPath.StartsWith("~/", StringComparison.Ordinal) || customPath.StartsWith("~\\", StringComparison.Ordinal)
                    ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), customPath[2..])
                    : customPath;
            expanded = NormalizeWindowsShellPath(expanded);
            var resolved = Path.GetFullPath(expanded, workingDirectory);
            if (File.Exists(resolved)) return resolved;
            throw new ToolFailureException($"Custom shell path not found: {customPath}");
        }
        if (OperatingSystem.IsWindows())
        {
            var candidates = new[]
            {
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Git", "bin", "bash.exe"),
                Environment.GetEnvironmentVariable("ProgramFiles(x86)") is { Length: > 0 } x86
                    ? Path.Combine(x86, "Git", "bin", "bash.exe") : null,
                FindExecutable("bash.exe")
            };
            var match = candidates.FirstOrDefault(candidate => candidate is not null && File.Exists(candidate));
            if (match is not null) return match;
            throw new ToolFailureException("No bash shell found. Install Git for Windows, add bash.exe to PATH, or set shellPath in settings.json.");
        }
        if (File.Exists("/bin/bash")) return "/bin/bash";
        if (FindExecutable("bash") is { } bash) return bash;
        return File.Exists("/bin/sh") ? "/bin/sh" : "sh";
    }

    private static string NormalizeWindowsShellPath(string path)
    {
        if (!OperatingSystem.IsWindows() || !path.StartsWith('/') || path.Contains('\\')) return path;
        var parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var driveIndex = parts.Length > 1 && parts[0] is "mnt" or "cygdrive" ? 1 : 0;
        if (parts.Length <= driveIndex || parts[driveIndex].Length != 1 || !char.IsAsciiLetter(parts[driveIndex][0])) return path;
        var drive = char.ToUpperInvariant(parts[driveIndex][0]) + ":\\";
        return parts.Length == driveIndex + 1 ? drive : Path.Combine(drive, Path.Combine(parts[(driveIndex + 1)..]));
    }

    private static string? FindExecutable(string name)
    {
        foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator,
                     StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = Path.Combine(directory, name);
            if (File.Exists(candidate)) return candidate;
        }
        return null;
    }

    private static void KillProcessTree(Process process, bool processGroup)
    {
        try
        {
            if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS() || OperatingSystem.IsFreeBSD())
            {
                if (processGroup && kill(-process.Id, 9) == 0) return;
            }
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
        catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch (Exception fallback) when (fallback is InvalidOperationException or System.ComponentModel.Win32Exception) { }
        }
    }

    [DllImport("libc", SetLastError = true)]
    private static extern int kill(int processId, int signal);
}
