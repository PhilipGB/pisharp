using System.Diagnostics;

namespace PiSharp.Cli;

/// <summary>Acquires a native private credential-file lease with bounded Windows permission retries.</summary>
internal static class AuthFileLock
{
    internal static async Task<FileStream> AcquireAsync(string path, CancellationToken cancellationToken,
        TimeSpan? timeout = null, bool? windows = null, Func<string, FileStream>? open = null,
        Func<int, CancellationToken, Task>? delay = null, Func<double>? random = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var directory = Path.GetDirectoryName(Path.GetFullPath(path))!;
        if (OperatingSystem.IsWindows()) Directory.CreateDirectory(directory);
        else Directory.CreateDirectory(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var isWindows = windows ?? OperatingSystem.IsWindows();
        var elapsed = Stopwatch.StartNew();
        var budget = timeout ?? TimeSpan.FromSeconds(30);
        var permissionRetries = 0;
        var retry = 0;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var stream = (open ?? Open)(path);
                if (cancellationToken.IsCancellationRequested)
                {
                    await stream.DisposeAsync();
                    cancellationToken.ThrowIfCancellationRequested();
                }
                return stream;
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var nativeCode = error.HResult & 0xffff;
                var permission = isWindows && (error is UnauthorizedAccessException || nativeCode == 5);
                var contention = error is IOException && (isWindows ? nativeCode is 32 or 33 :
                    nativeCode is 11 or 32 || OperatingSystem.IsMacOS() && nativeCode == 35);
                var remaining = budget - elapsed.Elapsed;
                if ((!permission && !contention) || remaining <= TimeSpan.Zero || permission && ++permissionRetries > 5) throw;
                var baseDelay = Math.Min(10 * Math.Pow(2, Math.Min(retry++, 10)), 1000);
                var milliseconds = (int)Math.Min(Math.Floor(baseDelay * (1 + (random?.Invoke() ?? Random.Shared.NextDouble())) + 0.5), remaining.TotalMilliseconds);
                if (delay is null) await Task.Delay(milliseconds, cancellationToken);
                else await delay(milliseconds, cancellationToken);
            }
        }
    }

    private static FileStream Open(string path)
    {
        if (new FileInfo(path).LinkTarget is not null)
            throw new InvalidDataException("Refusing a symbolic-link credential lock.");
        var options = new FileStreamOptions
        {
            Mode = FileMode.OpenOrCreate,
            Access = FileAccess.ReadWrite,
            Share = FileShare.None,
            BufferSize = 1,
            Options = FileOptions.Asynchronous
        };
        if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        return new FileStream(path, options);
    }
}
