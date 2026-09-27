using System.Diagnostics;
using System.Runtime.InteropServices;

namespace PiSharp.Runtime.Sessions;

internal sealed class SessionFileLease : IAsyncDisposable
{
    private static readonly TimeSpan LockWaitLimit = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan RetryDelay = TimeSpan.FromMilliseconds(20);
    private static readonly object ProcessLocksSync = new();
    private static readonly Dictionary<string, ProcessLockEntry> ProcessLocks = new(
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
    private const int MacOsLockExclusive = 2;
    private const int MacOsLockNonBlocking = 4;
    private const int MacOsWouldBlockError = 35;

    private readonly FileStream _stream;
    private readonly ProcessLockEntry _processLock;

    private SessionFileLease(FileStream stream, ProcessLockEntry processLock)
    {
        _stream = stream;
        _processLock = processLock;
    }

    public static async Task<SessionFileLease> AcquireAsync(string sessionPath, CancellationToken cancellationToken)
    {
        var lockPath = Path.GetFullPath(sessionPath + ".lock");
        var processLock = RentProcessLock(lockPath);
        var processLockHeld = false;
        FileStream? lease = null;
        var started = Stopwatch.GetTimestamp();
        try
        {
            processLockHeld = await processLock.Semaphore.WaitAsync(LockWaitLimit, cancellationToken);
            if (!processLockHeld)
                throw new IOException($"Timed out waiting for the session file lock: {sessionPath}");

            if (OperatingSystem.IsMacOS())
                return await AcquireMacOsAsync(processLock, sessionPath, started, cancellationToken);

            var options = new FileStreamOptions
            {
                Mode = FileMode.OpenOrCreate,
                Access = FileAccess.ReadWrite,
                Share = FileShare.ReadWrite
            };
            if (OperatingSystem.IsLinux()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            lease = new FileStream(lockPath, options);
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    lease.Lock(0, 1);
                    return new SessionFileLease(lease, processLock);
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
            if (lease is not null) await lease.DisposeAsync();
            if (processLockHeld) processLock.Semaphore.Release();
            ReturnProcessLock(processLock);
            throw;
        }
    }

    private static async Task<SessionFileLease> AcquireMacOsAsync(ProcessLockEntry processLock, string sessionPath,
        long started, CancellationToken cancellationToken)
    {
        // FileShare.None uses an exclusive flock on macOS. Reacquiring it explicitly verifies
        // the filesystem honored the lock; .NET otherwise ignores ENOTSUP.
        var options = new FileStreamOptions
        {
            Mode = FileMode.OpenOrCreate,
            Access = FileAccess.ReadWrite,
            Share = FileShare.None
        };
        if (OperatingSystem.IsMacOS()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        var lockPath = processLock.Path;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            FileStream? lease = null;
            try
            {
                lease = new FileStream(lockPath, options);
                if (flock(lease.SafeFileHandle.DangerousGetHandle().ToInt32(), MacOsLockExclusive | MacOsLockNonBlocking) != 0)
                {
                    var errorCode = Marshal.GetLastPInvokeError();
                    if (errorCode != MacOsWouldBlockError)
                        throw new IOException($"Could not acquire the session file lock on macOS (errno {errorCode}): {sessionPath}");
                    throw new IOException($"The macOS session file lock is held by another process: {sessionPath}", errorCode);
                }
                return new SessionFileLease(lease, processLock);
            }
            catch (IOException error) when (error.HResult == MacOsWouldBlockError)
            {
                if (lease is not null) await lease.DisposeAsync();
                if (Stopwatch.GetElapsedTime(started) >= LockWaitLimit)
                    throw new IOException($"Timed out waiting for the session file lock: {sessionPath}", error);
                await Task.Delay(RetryDelay, cancellationToken);
            }
            catch
            {
                if (lease is not null) await lease.DisposeAsync();
                throw;
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        try { if (!OperatingSystem.IsMacOS()) _stream.Unlock(0, 1); }
        finally
        {
            try { await _stream.DisposeAsync(); }
            finally
            {
                _processLock.Semaphore.Release();
                ReturnProcessLock(_processLock);
            }
        }
    }

    private static ProcessLockEntry RentProcessLock(string path)
    {
        lock (ProcessLocksSync)
        {
            if (!ProcessLocks.TryGetValue(path, out var processLock))
                ProcessLocks.Add(path, processLock = new ProcessLockEntry(path));
            processLock.ReferenceCount++;
            return processLock;
        }
    }

    private static void ReturnProcessLock(ProcessLockEntry processLock)
    {
        lock (ProcessLocksSync)
        {
            if (--processLock.ReferenceCount == 0)
            {
                ProcessLocks.Remove(processLock.Path);
                processLock.Semaphore.Dispose();
            }
        }
    }

    [DllImport("libc", SetLastError = true)]
    private static extern int flock(int fileDescriptor, int operation);

    private sealed class ProcessLockEntry(string path)
    {
        public string Path { get; } = path;
        public SemaphoreSlim Semaphore { get; } = new(1, 1);
        public int ReferenceCount { get; set; }
    }
}
