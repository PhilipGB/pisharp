using System.Runtime.Versioning;

namespace PiSharp.Core.Models.Providers;

/// <summary>
/// In-memory catalog store (pinned pi-ai: InMemoryModelsStore).
/// </summary>
public sealed class InMemoryModelsStore : IModelsStore
{
    private readonly Dictionary<string, ModelsStoreEntry> _entries = new(StringComparer.Ordinal);
    private readonly object _gate = new();

    public Task<ModelsStoreEntry?> ReadAsync(string providerId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            return Task.FromResult(_entries.TryGetValue(providerId, out var entry) ? entry : null);
        }
    }

    public Task WriteAsync(string providerId, ModelsStoreEntry entry, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            _entries[providerId] = entry;
        }

        return Task.CompletedTask;
    }

    public Task DeleteAsync(string providerId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            _entries.Remove(providerId);
        }

        return Task.CompletedTask;
    }
}

/// <summary>
/// Locked JSON-backed storage for dynamically refreshed provider catalogs (pinned
/// Pi: FileModelsStore). Reuses the credential store's cross-process lock semantics.
/// </summary>
public sealed class FileModelsStore : IModelsStore
{
    private readonly string _path;
    private readonly object _readGate = new();
    private Dictionary<string, ModelsStoreEntry> _readStateData = new(StringComparer.Ordinal);
    private string? _readStateRevision;

    /// <summary>Creates a store for the given path (default {agentDir}/models-store.json).</summary>
    public FileModelsStore(string? path = null)
    {
        var dir = Environment.GetEnvironmentVariable("PISHARP_AGENT_DIR")
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".pisharp");
        _path = Path.GetFullPath(path ?? Path.Combine(dir, "models-store.json"));
    }

    public Task<ModelsStoreEntry?> ReadAsync(string providerId, CancellationToken cancellationToken = default)
    {
        var data = LoadForRead();
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(data.TryGetValue(providerId, out var entry) ? entry : null);
    }

    public async Task WriteAsync(string providerId, ModelsStoreEntry entry, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var release = await CrossProcessFileLock.AcquireAsync(_path, cancellationToken);
        var current = ReadRawLocked();
        current[providerId] = entry;
        WriteLocked(current, cancellationToken);
        UpdateReadState(current);
    }

    public async Task DeleteAsync(string providerId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var release = await CrossProcessFileLock.AcquireAsync(_path, cancellationToken);
        var current = ReadRawLocked();
        current.Remove(providerId);
        WriteLocked(current, cancellationToken);
        UpdateReadState(current);
    }

    private Dictionary<string, ModelsStoreEntry> LoadForRead()
    {
        lock (_readGate)
        {
            var revision = GetFileRevision(_path);
            if (_readStateRevision is not null && revision is not null && revision == _readStateRevision)
            {
                return _readStateData;
            }

            try
            {
                var data = ReadRawLocked();
                UpdateReadStateLocked(data, revision);
                return _readStateData;
            }
            catch (FileNotFoundException)
            {
                UpdateReadStateLocked(new Dictionary<string, ModelsStoreEntry>(StringComparer.Ordinal), revision);
                return _readStateData;
            }
        }
    }

    private Dictionary<string, ModelsStoreEntry> ReadRawLocked()
    {
        try
        {
            return ParseStore(JsonText.ReadFileText(_path));
        }
        catch (FileNotFoundException)
        {
            return new Dictionary<string, ModelsStoreEntry>(StringComparer.Ordinal);
        }
    }

    private void WriteLocked(Dictionary<string, ModelsStoreEntry> data, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var dir = Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        File.WriteAllText(_path, ModelsStoreJson.Serialize(data) + "\n", System.Text.Encoding.UTF8);
        if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
        {
            TrySetUnixMode(_path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }

    private void UpdateReadState(Dictionary<string, ModelsStoreEntry> data)
    {
        lock (_readGate)
        {
            UpdateReadStateLocked(data, GetFileRevision(_path));
        }
    }

    private void UpdateReadStateLocked(Dictionary<string, ModelsStoreEntry> data, string? revision)
    {
        _readStateData = data;
        _readStateRevision = revision;
    }

    [SupportedOSPlatform("linux")]
    [SupportedOSPlatform("macos")]
    private static void TrySetUnixMode(string path, UnixFileMode mode)
    {
        try
        {
            File.SetUnixFileMode(path, mode);
        }
        catch (PlatformNotSupportedException)
        {
            // Non-POSIX filesystem: permissions are best-effort.
        }
    }

    private static string? GetFileRevision(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists)
            {
                return null;
            }

            return $"{info.Length}:{info.LastWriteTimeUtc.Ticks}";
        }
        catch
        {
            return null;
        }
    }

    private static Dictionary<string, ModelsStoreEntry> ParseStore(string content)
    {
        var root = System.Text.Json.JsonDocument.Parse(JsonText.StripBom(content)).RootElement;
        var result = new Dictionary<string, ModelsStoreEntry>(StringComparer.Ordinal);
        if (root.ValueKind != System.Text.Json.JsonValueKind.Object)
        {
            return result;
        }

        foreach (var entry in root.EnumerateObject())
        {
            var parsed = ModelsStoreJson.ParseEntry(entry.Value);
            if (parsed is not null)
            {
                result[entry.Name] = parsed;
            }
        }

        return result;
    }
}

/// <summary>
/// Shared cross-process lock with the pinned Pi retry policy (30s deadline, backoff
/// 10ms to 1s with jitter). The lock is a FileStream held exclusively (flock on
/// POSIX); dead holders release automatically.
/// </summary>
internal static class CrossProcessFileLock
{
    private const int DeadlineMs = 30_000;
    private const int BaseDelayMs = 10;
    private const int MaxDelayMs = 2_000;

    public static async Task<IDisposable> AcquireAsync(string path, CancellationToken cancellationToken)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        EnsureFileExists(path);
        var lockPath = path + ".lock";
        var deadline = DateTime.UtcNow.AddMilliseconds(DeadlineMs);
        var retry = 0;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var stream = new FileStream(lockPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
                return new LockRelease(lockPath, stream);
            }
            catch (IOException)
            {
                var remaining = deadline - DateTime.UtcNow;
                if (remaining <= TimeSpan.Zero)
                {
                    throw new InvalidOperationException("Failed to acquire storage lock");
                }

                var baseDelay = Math.Min(BaseDelayMs * Math.Pow(2, retry), MaxDelayMs / 2.0);
                retry++;
                var jitter = 1 + Random.Shared.Next((int)(baseDelay / 2.0) + 1);
                await Task.Delay(TimeSpan.FromMilliseconds(baseDelay + jitter), cancellationToken);
            }
        }
    }

    private static void EnsureFileExists(string path)
    {
        if (!File.Exists(path))
        {
            File.WriteAllText(path, "{}\n", System.Text.Encoding.UTF8);
        }
    }

    private sealed class LockRelease(string lockPath, FileStream stream) : IDisposable
    {
        public void Dispose()
        {
            try
            {
                stream.Dispose();
            }
            catch
            {
                // Ignore unlock errors (pinned Pi: "Ignore unlock errors when lock is compromised").
            }

            try
            {
                if (File.Exists(lockPath))
                {
                    File.Delete(lockPath);
                }
            }
            catch
            {
                // Best effort.
            }
        }
    }
}
