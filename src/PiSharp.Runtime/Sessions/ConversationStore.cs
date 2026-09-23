using System.Security.Cryptography;
using System.Text;

namespace PiSharp.Runtime.Sessions;

/// <summary>Atomic, user-private storage for the authoritative conversation tree.</summary>
public sealed class ConversationStore(string workingDirectory, string? directory = null)
{
    public string WorkingDirectory { get; } = Path.GetFullPath(workingDirectory);
    private readonly Dictionary<string, string> _knownHashes = new(StringComparer.Ordinal);
    public string DirectoryPath { get; } = directory ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".pisharp", "sessions",
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(workingDirectory)))).ToLowerInvariant()[..16]);

    public string NewPath(ConversationSession conversation) => Path.Combine(DirectoryPath,
        $"{DateTimeOffset.UtcNow:yyyyMMddTHHmmss}_{conversation.Id}.session.json");

    public string? MostRecentPath() => Directory.Exists(DirectoryPath)
        ? Directory.EnumerateFiles(DirectoryPath, "*.session.json").OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault()
        : null;

    public async Task<ConversationSession> LoadAsync(string path, CancellationToken cancellationToken = default)
    {
        var target = Path.GetFullPath(path);
        var bytes = await File.ReadAllBytesAsync(target, cancellationToken);
        var session = ConversationSession.Parse(Encoding.UTF8.GetString(bytes));
        if (session.WorkingDirectory != WorkingDirectory)
            throw new InvalidDataException("Session belongs to another working directory.");
        _knownHashes[target] = Convert.ToHexString(SHA256.HashData(bytes));
        return session;
    }

    public async Task SaveAsync(ConversationSession session, string path, CancellationToken cancellationToken = default)
    {
        if (session.WorkingDirectory != WorkingDirectory) throw new InvalidDataException("Session belongs to another working directory.");
        var target = Path.GetFullPath(path);
        var folder = Path.GetDirectoryName(target)!;
        if (OperatingSystem.IsLinux())
            Directory.CreateDirectory(folder, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        else Directory.CreateDirectory(folder);
        // Two CLI processes must not silently overwrite each other. Lock the sibling lock file
        // across the compare and atomic rename; reject stale copies rather than merging turns.
        var lockOptions = new FileStreamOptions
        {
            Mode = FileMode.OpenOrCreate,
            Access = FileAccess.ReadWrite,
            Share = FileShare.ReadWrite
        };
        if (OperatingSystem.IsLinux()) lockOptions.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        await using var lease = new FileStream(target + ".lock", lockOptions);
        if (OperatingSystem.IsMacOS()) throw new PlatformNotSupportedException("Session file locking is not supported on macOS.");
        lease.Lock(0, 1);
        try
        {
            if (_knownHashes.TryGetValue(target, out var expected))
            {
                if (!File.Exists(target) || Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(target, cancellationToken))) != expected)
                    throw new InvalidDataException("Session changed on disk; reopen before writing.");
            }
            else if (File.Exists(target)) throw new InvalidDataException("Session already exists; refusing to replace an unloaded file.");
            var snapshot = Encoding.UTF8.GetBytes(session.ToJson());
            await WriteLockedAsync(snapshot, target, folder, cancellationToken);
            _knownHashes[target] = Convert.ToHexString(SHA256.HashData(snapshot));
        }
        finally { lease.Unlock(0, 1); }
    }

    private static async Task WriteLockedAsync(byte[] snapshot, string target, string folder, CancellationToken cancellationToken)
    {
        var temp = Path.Combine(folder, ".pisharp-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            var options = new FileStreamOptions
            {
                Mode = FileMode.CreateNew,
                Access = FileAccess.Write,
                Share = FileShare.None,
                Options = FileOptions.Asynchronous
            };
            if (OperatingSystem.IsLinux()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            await using (var file = new FileStream(temp, options))
            {
                await file.WriteAsync(snapshot, cancellationToken);
                file.Flush(flushToDisk: true);
            }
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temp, target, overwrite: true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
}
