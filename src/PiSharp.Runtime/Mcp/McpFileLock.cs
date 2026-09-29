using System.Diagnostics;
using System.Runtime.InteropServices;

namespace PiSharp.Runtime.Mcp;

/// <summary>Private cross-process file lock used by MCP credential and config edits.</summary>
internal sealed class McpFileLock(FileStream stream) : IAsyncDisposable, IDisposable
{
    private const int LockExclusive = 2;
    private const int LockNonBlocking = 4;
    private const int LockUnlock = 8;
    private const int Interrupted = 4;
    private int _disposed;

    public static async Task<McpFileLock> AcquireAsync(string path, TimeSpan timeout, TimeSpan retryInterval,
        CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(path))!;
        if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
            Directory.CreateDirectory(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite |
                UnixFileMode.UserExecute);
        else Directory.CreateDirectory(directory);

        var options = new FileStreamOptions
        {
            Mode = FileMode.OpenOrCreate,
            Access = FileAccess.ReadWrite,
            Share = FileShare.ReadWrite
        };
        if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        var file = new FileStream(path, options);
        var elapsed = Stopwatch.StartNew();
        try
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (TryLock(file)) return new McpFileLock(file);
                if (elapsed.Elapsed >= timeout)
                    throw new TimeoutException("Timed out waiting for another MCP file edit.");
                await Task.Delay(retryInterval, cancellationToken);
            }
        }
        catch
        {
            await file.DisposeAsync();
            throw;
        }
    }

    private static bool TryLock(FileStream file)
    {
        if (!OperatingSystem.IsMacOS())
        {
            try { file.Lock(0, 1); return true; }
            catch (IOException) { return false; }
        }

        if (Flock(file.SafeFileHandle.DangerousGetHandle().ToInt32(), LockExclusive | LockNonBlocking) == 0)
            return true;
        var error = Marshal.GetLastPInvokeError();
        if (error is 11 or 35 or Interrupted) return false;
        throw new IOException("MCP file lock failed with native error " + error + ".");
    }

    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        try
        {
            if (OperatingSystem.IsMacOS())
            {
                _ = Flock(stream.SafeFileHandle.DangerousGetHandle().ToInt32(), LockUnlock);
            }
            else stream.Unlock(0, 1);
        }
        finally { stream.Dispose(); }
    }

    [DllImport("libc", EntryPoint = "flock", SetLastError = true)]
    private static extern int Flock(int fileDescriptor, int operation);
}
