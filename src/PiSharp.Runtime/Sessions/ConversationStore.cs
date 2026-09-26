using System.Security.Cryptography;
using System.Text;

namespace PiSharp.Runtime.Sessions;

/// <summary>Atomic, user-private storage for the authoritative conversation tree.</summary>
public sealed class ConversationStore(string workingDirectory, string? directory = null)
{
    public string WorkingDirectory { get; } = Path.GetFullPath(workingDirectory);
    private readonly Dictionary<string, string> _knownHashes = new(StringComparer.Ordinal);
    public string DefaultDirectoryPath { get; } = CreateDefaultDirectory(workingDirectory);
    public string DirectoryPath { get; } = Path.GetFullPath(directory ?? CreateDefaultDirectory(workingDirectory));
    public bool FiltersForeignWorkingDirectories => !PathsEqual(DirectoryPath, DefaultDirectoryPath);

    public string NewPath(ConversationSession conversation) => Path.Combine(DirectoryPath,
        $"{DateTimeOffset.UtcNow:yyyyMMddTHHmmss}_{conversation.Id}.session.json");

    public string? MostRecentPath()
    {
        if (!Directory.Exists(DirectoryPath)) return null;
        foreach (var path in Directory.EnumerateFiles(DirectoryPath, "*.session.json")
            .OrderByDescending(File.GetLastWriteTimeUtc))
        {
            if (!FiltersForeignWorkingDirectories) return path;
            try
            {
                var session = ConversationSession.Parse(File.ReadAllText(path));
                if (WorkingDirectoriesMatch(session.WorkingDirectory, WorkingDirectory)) return path;
            }
            catch (Exception error) when (error is IOException or InvalidDataException or System.Text.Json.JsonException)
            {
                // Pi's recent-session discovery skips unreadable candidates and checks the next file.
            }
        }
        return null;
    }

    public static bool WorkingDirectoriesMatch(string left, string right) =>
        PathsEqual(Path.GetFullPath(left), Path.GetFullPath(right));

    private static bool PathsEqual(string left, string right) =>
        string.Equals(left, right, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    private static string CreateDefaultDirectory(string workingDirectory) => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".pisharp", "sessions",
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(workingDirectory)))).ToLowerInvariant()[..16]);

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

    /// <summary>Delete a listed inactive project session only if its bytes still match the indexed snapshot.</summary>
    public async Task DeleteAsync(SessionListing listing, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(listing);
        var target = Path.GetFullPath(listing.Path);
        if (Path.GetDirectoryName(target) != Path.GetFullPath(DirectoryPath) ||
            !Path.GetFileName(target).EndsWith(".session.json", StringComparison.Ordinal) ||
            listing.Fingerprint.Length != 64)
            throw new InvalidDataException("Session deletion requires a catalog entry from this project directory.");
        var lockOptions = new FileStreamOptions { Mode = FileMode.OpenOrCreate, Access = FileAccess.ReadWrite, Share = FileShare.ReadWrite };
        if (OperatingSystem.IsLinux()) lockOptions.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        await using var lease = new FileStream(target + ".lock", lockOptions);
        if (OperatingSystem.IsMacOS()) throw new PlatformNotSupportedException("Session file locking is not supported on macOS.");
        lease.Lock(0, 1);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if ((File.GetAttributes(target) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("Refusing to delete a symbolic-link session.");
            var bytes = await File.ReadAllBytesAsync(target, cancellationToken);
            var hash = Convert.ToHexString(SHA256.HashData(bytes));
            if (hash != listing.Fingerprint || _knownHashes.TryGetValue(target, out var known) && hash != known)
                throw new InvalidDataException("Session changed on disk; list sessions again before deletion.");
            var session = ConversationSession.Parse(Encoding.UTF8.GetString(bytes));
            if (session.WorkingDirectory != WorkingDirectory || session.Id != listing.Id)
                throw new InvalidDataException("Session identity or working directory changed.");
            cancellationToken.ThrowIfCancellationRequested();
            File.Delete(target);
            _knownHashes.Remove(target);
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
