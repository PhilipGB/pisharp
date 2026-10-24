using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;

namespace PiSharp.Runtime.Tools;

/// <summary>Shared process-tree termination and pipe cleanup for shell and direct execution.</summary>
internal static class ProcessLifetime
{
    private static readonly TimeSpan s_stdioIdleGrace = TimeSpan.FromMilliseconds(100);
    private static readonly ConcurrentDictionary<Process, bool> s_active = new();

    static ProcessLifetime() => AppDomain.CurrentDomain.ProcessExit += (_, _) => KillTrackedProcesses();

    internal static void Track(Process process, bool processGroup) => s_active[process] = processGroup;

    internal static void Untrack(Process process) => s_active.TryRemove(process, out _);

    internal static void KillTrackedProcesses()
    {
        foreach (var (process, processGroup) in s_active) KillProcessTree(process, processGroup);
    }

    internal static ProcessStartInfo CreateStartInfo(string executable, IEnumerable<string> arguments,
        string workingDirectory, out bool processGroup)
    {
        var start = new ProcessStartInfo
        {
            FileName = executable,
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        processGroup = false;
        if (!OperatingSystem.IsWindows() && FindExecutable("setsid") is { } setsid)
        {
            start.FileName = setsid;
            start.ArgumentList.Add(executable);
            processGroup = true;
        }
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        return start;
    }

    internal static string? FindExecutable(string name) =>
        (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator,
            StringSplitOptions.RemoveEmptyEntries).Select(directory => Path.Combine(directory, name))
            .FirstOrDefault(File.Exists);

    internal static async Task TerminateAsync(Process process, bool processGroup)
    {
        if (processGroup && !OperatingSystem.IsWindows())
        {
            try { kill(-process.Id, 15); }
            catch (InvalidOperationException) { }
            try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false); }
            catch (TimeoutException) { }
        }
        KillProcessTree(process, processGroup);
        await WaitForExitQuietlyAsync(process).ConfigureAwait(false);
        if (!process.HasExited) throw new IOException("The child process did not exit after termination.");
    }

    internal static async Task DrainOutputAfterExitAsync(Process process, Task stdoutTask, Task stderrTask,
        CancellationToken cancellationToken, CancellationTokenSource stop, Func<long> lastOutput)
    {
        var streams = Task.WhenAll(stdoutTask, stderrTask);
        while (!streams.IsCompleted)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var quietFor = Stopwatch.GetElapsedTime(lastOutput());
            var remaining = s_stdioIdleGrace - quietFor;
            if (remaining <= TimeSpan.Zero)
            {
                // A delayed read continuation does not make bytes already in a pipe idle output.
                if (HasBufferedOutput(process.StandardOutput.BaseStream) || HasBufferedOutput(process.StandardError.BaseStream))
                {
                    await Task.WhenAny(streams, Task.Delay(s_stdioIdleGrace, cancellationToken)).ConfigureAwait(false);
                    continue;
                }
                break;
            }
            await Task.WhenAny(streams, Task.Delay(remaining, cancellationToken)).ConfigureAwait(false);
        }
        if (!streams.IsCompleted)
        {
            stop.Cancel();
            process.StandardOutput.Close();
            process.StandardError.Close();
        }
        await streams.ConfigureAwait(false);
    }

    internal static async Task StopPumpsAsync(Process process, Task? stdoutTask, Task? stderrTask,
        CancellationTokenSource stop)
    {
        stop.Cancel();
        try { process.StandardOutput.Close(); } catch (InvalidOperationException) { }
        try { process.StandardError.Close(); } catch (InvalidOperationException) { }
        var active = new[] { stdoutTask, stderrTask }.Where(task => task is not null).Cast<Task>().ToArray();
        if (active.Length == 0) return;
        try { await Task.WhenAll(active).WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false); }
        catch (TimeoutException) { }
    }

    internal static async Task WaitForExitQuietlyAsync(Process process)
    {
        try { await process.WaitForExitAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false); }
        catch (TimeoutException) { }
        catch (InvalidOperationException) { }
    }

    internal static void KillProcessTree(Process process, bool processGroup)
    {
        // Walk descendants before killing their parent, while detached children are still discoverable.
        try
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
        catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception) { }
        if (processGroup && (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS() || OperatingSystem.IsFreeBSD()))
        {
            try { kill(-process.Id, 9); }
            catch (InvalidOperationException) { }
        }
    }

    [DllImport("libc", SetLastError = true)]
    private static extern int kill(int processId, int signal);

    private static bool HasBufferedOutput(Stream stream)
    {
        if (OperatingSystem.IsWindows() || stream is not PipeStream pipe || pipe.SafePipeHandle.IsClosed) return false;
        var descriptor = new PollDescriptor { FileDescriptor = pipe.SafePipeHandle.DangerousGetHandle().ToInt32(), Events = 1 };
        return poll(ref descriptor, 1, 0) > 0 && (descriptor.ReturnedEvents & 1) != 0;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PollDescriptor
    {
        public int FileDescriptor;
        public short Events;
        public short ReturnedEvents;
    }

    [DllImport("libc", SetLastError = true)]
    private static extern int poll(ref PollDescriptor descriptor, nuint count, int timeout);

}
