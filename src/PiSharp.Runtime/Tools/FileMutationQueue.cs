using PiSharp.Runtime.Tools;

namespace PiSharp.Runtime;

/// <summary>Serializes edits and writes to the same resolved file, without blocking unrelated paths.</summary>
public sealed class FileMutationQueue
{
    private readonly object _gate = new();
    private readonly Dictionary<string, Lease> _leases = new(OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
    private sealed class Lease { public readonly SemaphoreSlim Semaphore = new(1); public int Users; }

    public async Task<T> RunAsync<T>(string path, Func<Task<T>> operation, CancellationToken cancellationToken)
    {
        // Resolve an existing symlink before taking the lock so aliases share one queue.
        var absolute = Path.GetFullPath(path);
        var key = LinuxFileAccess.ResolveExistingPath(absolute) ?? ResolveExistingPath(absolute);
        Lease lease;
        lock (_gate)
        {
            if (!_leases.TryGetValue(key, out lease!)) _leases[key] = lease = new Lease();
            lease.Users++;
        }
        try
        {
            await lease.Semaphore.WaitAsync(cancellationToken);
            try { return await operation(); }
            finally { lease.Semaphore.Release(); }
        }
        finally
        {
            lock (_gate)
                if (--lease.Users == 0) { _leases.Remove(key); lease.Semaphore.Dispose(); }
        }
    }

    private static string ResolveExistingPath(string absolute)
    {
        var root = Path.GetPathRoot(absolute)!;
        var current = root;
        var segments = absolute[root.Length..].Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries);
        for (var index = 0; index < segments.Length; index++)
        {
            current = Path.Combine(current, segments[index]);
            FileAttributes attributes;
            try { attributes = File.GetAttributes(current); }
            catch (IOException error) when (error is FileNotFoundException or DirectoryNotFoundException)
            {
                // Keep resolved parent aliases when the final file or its descendants do not exist yet.
                return segments.Skip(index + 1).Aggregate(current, Path.Combine);
            }

            if ((attributes & FileAttributes.ReparsePoint) == 0) continue;
            FileSystemInfo info = (attributes & FileAttributes.Directory) != 0
                ? new DirectoryInfo(current) : new FileInfo(current);
            try
            {
                if (info.ResolveLinkTarget(returnFinalTarget: true) is { } target)
                    current = Path.GetFullPath(target.FullName);
            }
            catch (FileNotFoundException) { return absolute; }
            catch (DirectoryNotFoundException) { return absolute; }
        }
        return Path.GetFullPath(current);
    }
}
