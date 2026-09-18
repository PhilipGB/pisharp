using System.Runtime.Versioning;
using PiSharp.Core.Models.Providers;

namespace PiSharp.Core.Models.Auth;

/// <summary>
/// Credential storage backed by an auth.json file (pinned Pi: AuthStorage +
/// FileAuthStorageBackend). File mode 0600 and directory mode 0700 are applied on
/// creation only, so administrator-managed modes remain intact. Mutations serialize
/// through a cross-process lock file: 30s acquisition deadline, exponential backoff
/// from 10ms to 1s with jitter (proper-lockfile retry policy).
/// </summary>
public sealed class FileCredentialStore : ICredentialStore
{
    private const int LockDeadlineMs = 30_000;
    private const int LockBaseDelayMs = 10;
    private const int LockMaxDelayMs = 2_000;

    private readonly string _authPath;
    private readonly object _readGate = new();
    private Dictionary<string, Credential> _readStateCredentials = new();
    private string? _readStateRevision;

    /// <summary>Creates a store for the given auth.json path.</summary>
    public FileCredentialStore(string authPath)
    {
        _authPath = Path.GetFullPath(authPath);
    }

    /// <summary>
    /// Creates a store at the default agent location: {agentDir}/auth.json. The agent
    /// directory comes from PISHARP_AGENT_DIR, falling back to ~/.pisharp.
    /// </summary>
    public static FileCredentialStore CreateDefault(string? agentDir = null)
    {
        var dir = agentDir
            ?? Environment.GetEnvironmentVariable("PISHARP_AGENT_DIR")
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".pisharp");
        return new FileCredentialStore(Path.Combine(dir, "auth.json"));
    }

    public Task<Credential?> ReadAsync(string providerId, CancellationToken cancellationToken = default)
    {
        var data = LoadDataForRead(cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(data.TryGetValue(providerId, out var credential) ? credential : null);
    }

    public Task<IReadOnlyList<CredentialInfo>> ListAsync(CancellationToken cancellationToken = default)
    {
        var data = LoadDataForRead(cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult<IReadOnlyList<CredentialInfo>>(
            data.Select(entry => new CredentialInfo(entry.Key, entry.Value.Type)).ToArray());
    }

    public async Task<Credential?> ModifyAsync(
        string providerId,
        Func<Credential?, Task<Credential?>> modify,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var release = await AcquireLockAsync(cancellationToken);

        var currentData = ReadRawDataLocked(cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        var next = await modify(currentData.TryGetValue(providerId, out var current) ? current : null);
        cancellationToken.ThrowIfCancellationRequested();

        if (next is null)
        {
            UpdateReadState(currentData);
            return currentData.TryGetValue(providerId, out var unchanged) ? unchanged : null;
        }

        currentData[providerId] = next;
        WriteDataLocked(currentData, cancellationToken);
        UpdateReadState(currentData);
        return next;
    }

    public async Task DeleteAsync(string providerId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var release = await AcquireLockAsync(cancellationToken);

        var currentData = ReadRawDataLocked(cancellationToken);
        currentData.Remove(providerId);
        WriteDataLocked(currentData, cancellationToken);
        UpdateReadState(currentData);
    }

    /// <summary>
    /// One-off read of a stored credential without instantiating a store (pinned Pi:
    /// readStoredCredential). Malformed files resolve to null.
    /// </summary>
    public static Credential? ReadStored(string providerId, string authPath)
    {
        try
        {
            var content = JsonText.ReadFileText(authPath);
            var data = CredentialJson.Deserialize(content);
            return data.TryGetValue(providerId, out var credential) ? credential : null;
        }
        catch
        {
            return null;
        }
    }

    private Dictionary<string, Credential> LoadDataForRead(CancellationToken cancellationToken)
    {
        lock (_readGate)
        {
            var revision = GetFileRevision(_authPath);
            if (_readStateRevision is not null && revision is not null && revision == _readStateRevision)
            {
                return _readStateCredentials;
            }

            try
            {
                var content = JsonText.ReadFileText(_authPath);
                var data = CredentialJson.Deserialize(content);
                UpdateReadStateLocked(data, revision);
                return _readStateCredentials;
            }
            catch (Exception fileError) when (fileError is FileNotFoundException or DirectoryNotFoundException)
            {
                UpdateReadStateLocked(new Dictionary<string, Credential>(), revision);
                return _readStateCredentials;
            }
        }
    }

    private Dictionary<string, Credential> ReadRawDataLocked(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            var content = JsonText.ReadFileText(_authPath);
            return CredentialJson.Deserialize(content);
        }
        catch (Exception fileError) when (fileError is FileNotFoundException or DirectoryNotFoundException)
        {
            return new Dictionary<string, Credential>();
        }
    }

    private void WriteDataLocked(Dictionary<string, Credential> data, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        EnsureParentDir();
        File.WriteAllText(_authPath, CredentialJson.Serialize(data) + "\n", System.Text.Encoding.UTF8);
        EnsureFilePermissions();
    }

    private void UpdateReadState(Dictionary<string, Credential> data)
    {
        lock (_readGate)
        {
            UpdateReadStateLocked(data, GetFileRevision(_authPath));
        }
    }

    private void UpdateReadStateLocked(Dictionary<string, Credential> data, string? revision)
    {
        _readStateCredentials = data;
        _readStateRevision = revision;
    }

    private void EnsureParentDir()
    {
        var dir = Path.GetDirectoryName(_authPath);
        if (string.IsNullOrEmpty(dir))
        {
            return;
        }

        var created = !Directory.Exists(dir);
        Directory.CreateDirectory(dir);
        if (created && (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS()))
        {
            // 0700 on creation only: administrator-managed modes remain intact.
            TrySetUnixMode(dir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    private void EnsureFilePermissions()
    {
        if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
        {
            TrySetUnixMode(_authPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
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

    private Task<IDisposable> AcquireLockAsync(CancellationToken cancellationToken)
    {
        EnsureParentDir();
        EnsureFileExists();
        return CrossProcessFileLock.AcquireAsync(_authPath, cancellationToken);
    }
    private void EnsureFileExists()
    {
        if (!File.Exists(_authPath))
        {
            File.WriteAllText(_authPath, "{}\n", System.Text.Encoding.UTF8);
            EnsureFilePermissions();
        }
    }
}
