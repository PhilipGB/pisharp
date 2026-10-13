using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;

namespace PiSharp.Runtime.Tools;

/// <summary>Owns shell processes, output draining, cancellation and process-group cleanup.</summary>
internal sealed class ShellExecution(string workingDirectory, string? shellPath, string? shellCommandPrefix)
{
    private const double MaxTimeoutSeconds = int.MaxValue / 1000d;
    private static readonly TimeSpan s_stdioIdleGrace = TimeSpan.FromMilliseconds(100);
    private readonly ConcurrentDictionary<Guid, CancellationTokenSource> _runningBash = new();
    public static IReadOnlyList<string> SessionEnvironmentNames { get; } = Array.AsReadOnly<string>(
        ["PI_SESSION_ID", "PI_SESSION_FILE", "PI_PROVIDER", "PI_MODEL", "PI_REASONING_LEVEL"]);

    public void AbortBash()
    {
        foreach (var cancellation in _runningBash.Values)
        {
            try { cancellation.Cancel(); }
            catch (ObjectDisposedException) { }
        }
    }

    public async Task<string> ExecuteForToolAsync(string command, double? timeout, Action<string>? onUpdate,
        CancellationToken cancellationToken, IReadOnlyDictionary<string, string?>? sessionEnvironment = null,
        bool normalizeOutput = false, bool throttleUpdates = true)
    {
        var result = await ExecuteAsync(command, timeout, onUpdate, cancellationToken, returnCancellationResult: false,
            sessionEnvironment, normalizeOutput, throttleUpdates);
        if (result.ExitCode is { } exitCode && exitCode != 0)
            throw new ToolFailureException($"Command exited with code {exitCode}", result.DisplayOutput, exitCode);
        return result.DisplayOutput;
    }

    public async Task<BashExecutionResult> ExecuteAsync(string command, double? timeout, Action<string>? onUpdate,
        CancellationToken cancellationToken, bool returnCancellationResult,
        IReadOnlyDictionary<string, string?>? sessionEnvironment = null, bool normalizeOutput = false,
        bool throttleUpdates = true, bool captureStructuredOutput = false)
    {
        if (timeout.HasValue && (!double.IsFinite(timeout.Value) || timeout.Value <= 0))
            throw new ToolFailureException("Invalid timeout: must be a finite number of seconds");
        if (timeout > MaxTimeoutSeconds)
            throw new ToolFailureException($"Invalid timeout: maximum is {MaxTimeoutSeconds.ToString("0.###", CultureInfo.InvariantCulture)} seconds");
        if (!Directory.Exists(workingDirectory))
            throw new ToolFailureException($"Working directory does not exist: {workingDirectory}\nCannot execute bash commands.");

        var shell = ResolveShell(shellPath, workingDirectory);
        var operationId = Guid.NewGuid();
        using var abortSource = new CancellationTokenSource();
        _runningBash[operationId] = abortSource;
        using var timeoutSource = new CancellationTokenSource();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutSource.Token, abortSource.Token);
        using var process = new Process
        {
            StartInfo = CreateStartInfo(shell, shellCommandPrefix is { Length: > 0 } prefix
                ? $"{prefix}\n{command}" : command, workingDirectory, out var processGroup, sessionEnvironment)
        };
        using var pumpStop = new CancellationTokenSource();
        await using var output = new ShellOutputBuffer(normalizeOutput);
        using var updates = new BashOutputUpdates(onUpdate, throttleUpdates);
        long lastOutputTicks = Stopwatch.GetTimestamp();
        var started = false;
        var startTime = Stopwatch.GetTimestamp();
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
            var structured = captureStructuredOutput ? await output.ReadFullOutputAsync(BashToolOutput.MaximumBytes) : default;
            return new(result.Output, result.DisplayOutput, cancelled ? null : process.ExitCode, cancelled,
                result.Truncated, result.FullOutputPath, structured.Content, structured.Truncated,
                Math.Round(Stopwatch.GetElapsedTime(startTime).TotalSeconds, 1, MidpointRounding.AwayFromZero));
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
        foreach (var name in SessionEnvironmentNames) start.Environment.Remove(name);
        if (sessionEnvironment is not null)
            foreach (var name in SessionEnvironmentNames)
                if (sessionEnvironment.TryGetValue(name, out var value) && value is not null)
                    start.Environment[name] = value;
        return start;
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
