using System.Diagnostics;

namespace PiSharp.Runtime.Sessions;

internal sealed class SessionFileLease(FileStream stream) : IAsyncDisposable
{
    private static readonly TimeSpan LockWaitLimit = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan RetryDelay = TimeSpan.FromMilliseconds(20);

    public static async Task<SessionFileLease> AcquireAsync(string sessionPath, CancellationToken cancellationToken)
    {
        if (OperatingSystem.IsMacOS())
            throw new PlatformNotSupportedException("Session file locking is not supported on macOS.");
        var options = new FileStreamOptions
        {
            Mode = FileMode.OpenOrCreate,
            Access = FileAccess.ReadWrite,
            Share = FileShare.ReadWrite
        };
        if (OperatingSystem.IsLinux()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        var lease = new FileStream(sessionPath + ".lock", options);
        var started = Stopwatch.GetTimestamp();
        try
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    lease.Lock(0, 1);
                    return new SessionFileLease(lease);
                }
                catch (IOException error)
                {
                    if (Stopwatch.GetElapsedTime(started) >= LockWaitLimit)
                        throw new IOException($"Timed out waiting for the session file lock: {sessionPath}", error);
                    await Task.Delay(RetryDelay, cancellationToken);
                }
            }
        }
        catch
        {
            await lease.DisposeAsync();
            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        try { if (!OperatingSystem.IsMacOS()) stream.Unlock(0, 1); }
        finally { await stream.DisposeAsync(); }
    }
}
