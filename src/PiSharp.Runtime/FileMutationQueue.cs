namespace PiSharp.Runtime;

/// <summary>Serializes edits and writes to the same resolved file, without blocking unrelated paths.</summary>
public sealed class FileMutationQueue
{
    private readonly object _gate = new();
    private readonly Dictionary<string, Lease> _leases = new(StringComparer.Ordinal);
    private sealed class Lease { public readonly SemaphoreSlim Semaphore = new(1); public int Users; }

    public async Task<T> RunAsync<T>(string path, Func<Task<T>> operation, CancellationToken cancellationToken)
    {
        // Resolve an existing symlink before taking the lock so aliases share one queue.
        var absolute = Path.GetFullPath(path);
        string key;
        try { key = new FileInfo(absolute).ResolveLinkTarget(true)?.FullName ?? absolute; }
        catch (FileNotFoundException) { key = absolute; }
        catch (DirectoryNotFoundException) { key = absolute; }
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
}
