using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using PiSharp.Extensions.Abstractions;

namespace PiSharp.Runtime.Tools;

/// <summary>Direct executable invocation using the shared process ownership and drain helpers.</summary>
internal static class DirectProcessExecution
{
    internal const long MaximumOutputBytes = 64 * 1024 * 1024;

    internal static async Task<PiSharpExtensionExecResult> ExecuteAsync(string command, IReadOnlyList<string> args,
        string workingDirectory, TimeSpan? timeout, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(command);
        ArgumentNullException.ThrowIfNull(args);
        if (command.Contains('\0') || args.Any(argument => argument is null || argument.Contains('\0')))
            throw new ArgumentException("Executable and arguments must not contain null characters or null arguments.");
        if (timeout is { TotalMilliseconds: > uint.MaxValue - 1 })
            throw new ArgumentOutOfRangeException(nameof(timeout));
        cancellationToken.ThrowIfCancellationRequested();
        if (!Directory.Exists(workingDirectory))
            throw new DirectoryNotFoundException($"Working directory does not exist: {workingDirectory}");
        var executable = ResolveExecutable(command, workingDirectory);
        using var process = new Process
        {
            StartInfo = ProcessLifetime.CreateStartInfo(executable, args, workingDirectory, out var processGroup)
        };
        process.StartInfo.RedirectStandardInput = true;
        using var deadline = new CancellationTokenSource();
        using var captureFailure = new CancellationTokenSource();
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken, deadline.Token, captureFailure.Token);
        using var pumpStop = new CancellationTokenSource();
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        long capturedBytes = 0;
        long lastOutput = Stopwatch.GetTimestamp();
        Exception? outputError = null;
        Task? stdoutPump = null;
        Task? stderrPump = null;
        var started = false;
        var cleaned = false;
        var killed = false;
        int? exitCodeBeforeTermination = null;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            process.Start();
            started = true;
            process.StandardInput.Close();
            if (timeout is { } duration && duration > TimeSpan.Zero) deadline.CancelAfter(duration);
            stdoutPump = PumpAsync(process.StandardOutput.BaseStream, stdout);
            stderrPump = PumpAsync(process.StandardError.BaseStream, stderr);
            try
            {
                await process.WaitForExitAsync(operation.Token).ConfigureAwait(false);
                Interlocked.Exchange(ref lastOutput, Stopwatch.GetTimestamp());
                await ProcessLifetime.DrainOutputAfterExitAsync(process, stdoutPump, stderrPump, operation.Token,
                    pumpStop, () => Interlocked.Read(ref lastOutput)).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (operation.IsCancellationRequested)
            {
                killed = true;
                if (process.HasExited) exitCodeBeforeTermination = process.ExitCode;
                await ProcessLifetime.TerminateAsync(process, processGroup).ConfigureAwait(false);
                // Drain output already written by the terminated process before closing inherited handles.
                Interlocked.Exchange(ref lastOutput, Stopwatch.GetTimestamp());
                await ProcessLifetime.DrainOutputAfterExitAsync(process, stdoutPump, stderrPump,
                    CancellationToken.None, pumpStop, () => Interlocked.Read(ref lastOutput))
                    .WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            }
            if (outputError is not null) throw new IOException("Failed to capture process output.", outputError);
            cleaned = true;
            return new(stdout.ToString(), stderr.ToString(), killed ? exitCodeBeforeTermination ?? 0 : process.ExitCode, killed);
        }
        finally
        {
            if (started && !cleaned)
            {
                ProcessLifetime.KillProcessTree(process, processGroup);
                await ProcessLifetime.WaitForExitQuietlyAsync(process).ConfigureAwait(false);
                await ProcessLifetime.StopPumpsAsync(process, stdoutPump, stderrPump, pumpStop).ConfigureAwait(false);
            }
        }

        async Task PumpAsync(Stream stream, StringBuilder target)
        {
            var bytes = new byte[8192];
            var chars = new char[Encoding.UTF8.GetMaxCharCount(bytes.Length)];
            var decoder = new UTF8Encoding(false).GetDecoder();
            try
            {
                int count;
                while ((count = await stream.ReadAsync(bytes, pumpStop.Token).ConfigureAwait(false)) > 0)
                {
                    Interlocked.Exchange(ref lastOutput, Stopwatch.GetTimestamp());
                    if (Interlocked.Add(ref capturedBytes, count) > MaximumOutputBytes)
                        throw new IOException($"Process output exceeds the {MaximumOutputBytes}-byte capture limit.");
                    target.Append(chars, 0, decoder.GetChars(bytes, 0, count, chars, 0, flush: false));
                }
            }
            catch (Exception error) when (pumpStop.IsCancellationRequested &&
                error is OperationCanceledException or IOException or ObjectDisposedException)
            { }
            catch (Exception error)
            {
                Interlocked.CompareExchange(ref outputError, error, null);
                captureFailure.Cancel();
            }
            finally
            {
                target.Append(chars, 0, decoder.GetChars([], 0, 0, chars, 0, flush: true));
            }
        }
    }

    private static string ResolveExecutable(string command, string workingDirectory)
    {
        string[] candidates = OperatingSystem.IsWindows() && !Path.HasExtension(command)
            ? [command, command + ".exe", command + ".com"] : [command];
        var executable = candidates.Select(candidate =>
                candidate.IndexOfAny([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar]) >= 0
                    ? Path.GetFullPath(candidate, workingDirectory)
                    : ProcessLifetime.FindExecutable(candidate))
            .FirstOrDefault(candidate => candidate is not null && File.Exists(candidate));
        if (executable is null || !File.Exists(executable))
            throw new Win32Exception(2, $"Executable not found: {command}");
        if (!OperatingSystem.IsWindows() && (File.GetUnixFileMode(executable) &
            (UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute)) == 0)
            throw new Win32Exception(13, $"Executable permission denied: {command}");
        return Path.GetFullPath(executable, workingDirectory);
    }
}
