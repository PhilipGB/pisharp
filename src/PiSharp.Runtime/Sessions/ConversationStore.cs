using System.Security.Cryptography;
using System.Text;

namespace PiSharp.Runtime.Sessions;

/// <summary>Atomic, user-private storage for the authoritative conversation tree.</summary>
public sealed class ConversationStore
{
    private const long MaximumPiJsonlFileBytes = 128L * 1024 * 1024;
    private readonly Dictionary<string, string> _knownHashes = new(StringComparer.Ordinal);

    public ConversationStore(string workingDirectory, string? directory = null)
        : this(workingDirectory, directory, defaultDirectoryPath: null)
    {
    }

    internal ConversationStore(string workingDirectory, string? directory, string? defaultDirectoryPath)
    {
        WorkingDirectory = Path.GetFullPath(workingDirectory);
        DefaultDirectoryPath = Path.GetFullPath(defaultDirectoryPath ?? CreateDefaultDirectory(workingDirectory));
        DirectoryPath = Path.GetFullPath(directory ?? DefaultDirectoryPath);
    }

    public string WorkingDirectory { get; }
    public string DefaultDirectoryPath { get; }
    public string DirectoryPath { get; }
    public bool FiltersForeignWorkingDirectories => !PathsEqual(DirectoryPath, DefaultDirectoryPath);

    public string NewPath(ConversationSession conversation) => Path.Combine(DirectoryPath,
        $"{DateTimeOffset.UtcNow:yyyyMMddTHHmmss}_{conversation.Id}.session.json");

    public string? MostRecentPath()
    {
        if (!Directory.Exists(DirectoryPath)) return null;
        foreach (var path in Directory.EnumerateFiles(DirectoryPath)
            .Where(IsSessionFilePath)
            .OrderByDescending(File.GetLastWriteTimeUtc))
        {
            try
            {
                if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) continue;
                var bytes = ReadSessionFile(path);
                var session = ParseSessionFile(path, bytes, WorkingDirectory);
                if (IsPiJsonlSessionPath(path) && FiltersForeignWorkingDirectories &&
                    !HasPersistedWorkingDirectory(session)) continue;
                if (!FiltersForeignWorkingDirectories || WorkingDirectoriesMatch(session.WorkingDirectory, WorkingDirectory))
                    return path;
            }
            catch (Exception error) when (error is IOException or InvalidDataException or System.Text.Json.JsonException or
                KeyNotFoundException or UnauthorizedAccessException or ArgumentException or FormatException)
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

    internal static bool IsPiJsonlSessionPath(string path) => path.EndsWith(".jsonl", StringComparison.OrdinalIgnoreCase);

    internal static bool IsSessionFilePath(string path) =>
        path.EndsWith(".session.json", StringComparison.OrdinalIgnoreCase) || IsPiJsonlSessionPath(path);

    internal static byte[] ReadSessionFile(string path)
    {
        if (!IsPiJsonlSessionPath(path)) return File.ReadAllBytes(path);
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return ReadBoundedPiJsonl(stream);
    }

    internal static async Task<byte[]> ReadSessionFileAsync(string path, CancellationToken cancellationToken)
    {
        if (!IsPiJsonlSessionPath(path)) return await File.ReadAllBytesAsync(path, cancellationToken);
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (stream.Length > MaximumPiJsonlFileBytes)
            throw new InvalidDataException("Pi session exceeds the 128 MiB import limit.");
        var bytes = GC.AllocateUninitializedArray<byte>(checked((int)stream.Length));
        await stream.ReadExactlyAsync(bytes, cancellationToken);
        if (await stream.ReadAsync(new byte[1], cancellationToken) != 0)
            throw new InvalidDataException("Pi session changed while it was being read.");
        return bytes;
    }

    private static byte[] ReadBoundedPiJsonl(FileStream stream)
    {
        if (stream.Length > MaximumPiJsonlFileBytes)
            throw new InvalidDataException("Pi session exceeds the 128 MiB import limit.");
        var bytes = GC.AllocateUninitializedArray<byte>(checked((int)stream.Length));
        stream.ReadExactly(bytes);
        if (stream.ReadByte() != -1)
            throw new InvalidDataException("Pi session changed while it was being read.");
        return bytes;
    }

    internal static ConversationSession ParseSessionFile(string path, byte[] bytes, string fallbackWorkingDirectory)
    {
        if (IsPiJsonlSessionPath(path))
        {
            if (bytes.LongLength > MaximumPiJsonlFileBytes)
                throw new InvalidDataException("Pi session exceeds the 128 MiB import limit.");
            return PiJsonlSessionInterchange.Import(Encoding.UTF8.GetString(bytes),
                workingDirectoryFallback: fallbackWorkingDirectory);
        }
        return ConversationSession.Parse(Encoding.UTF8.GetString(bytes));
    }

    internal static bool HasPersistedWorkingDirectory(ConversationSession session) =>
        session.PiJsonlHeader is { ValueKind: System.Text.Json.JsonValueKind.Object } header &&
        header.TryGetProperty("cwd", out var cwd) && cwd.ValueKind == System.Text.Json.JsonValueKind.String &&
        !string.IsNullOrWhiteSpace(cwd.GetString());

    private static string CreateDefaultDirectory(string workingDirectory) => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".pisharp", "sessions",
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(workingDirectory)))).ToLowerInvariant()[..16]);

    public async Task<ConversationSession> LoadAsync(string path, CancellationToken cancellationToken = default)
    {
        var target = Path.GetFullPath(path);
        var bytes = await ReadSessionFileAsync(target, cancellationToken);
        var session = ParseSessionFile(target, bytes, WorkingDirectory);
        if (!WorkingDirectoriesMatch(session.WorkingDirectory, WorkingDirectory))
            throw new InvalidDataException("Session belongs to another working directory.");
        _knownHashes[target] = Convert.ToHexString(SHA256.HashData(bytes));
        return session;
    }

    public async Task<ConversationSession> LoadAsync(SessionListing listing,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(listing);
        var target = Path.GetFullPath(listing.Path);
        EnsureCatalogPath(target);
        var bytes = await ReadSessionFileAsync(target, cancellationToken);
        var hash = Convert.ToHexString(SHA256.HashData(bytes));
        if (!string.Equals(hash, listing.Fingerprint, StringComparison.Ordinal))
            throw new InvalidDataException("Session changed on disk; list sessions again before opening.");
        var session = ParseSessionFile(target, bytes, WorkingDirectory);
        ValidateListingIdentity(listing, session);
        _knownHashes[target] = hash;
        return session;
    }

    public async Task ValidateListingUnchangedAsync(SessionListing listing,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(listing);
        var target = Path.GetFullPath(listing.Path);
        EnsureCatalogPath(target);
        var bytes = await ReadSessionFileAsync(target, cancellationToken);
        var hash = Convert.ToHexString(SHA256.HashData(bytes));
        if (!string.Equals(hash, listing.Fingerprint, StringComparison.Ordinal))
            throw new InvalidDataException("Session changed on disk; list sessions again before opening.");
        var session = ParseSessionFile(target, bytes, WorkingDirectory);
        ValidateListingIdentity(listing, session);
    }

    private void ValidateListingIdentity(SessionListing listing, ConversationSession session)
    {
        if (session.Id != listing.Id || !WorkingDirectoriesMatch(session.WorkingDirectory, WorkingDirectory))
            throw new InvalidDataException("Session identity or working directory changed after listing.");
    }

    private void EnsureCatalogPath(string target)
    {
        if (!IsSessionFilePath(target) || !string.Equals(Path.GetDirectoryName(target), Path.GetFullPath(DirectoryPath),
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            throw new InvalidDataException("Session selection must come from this project catalog.");
        if ((File.GetAttributes(target) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("Session path is a symbolic link.");
    }

    public async Task SaveAsync(ConversationSession session, string path, CancellationToken cancellationToken = default)
    {
        if (session.WorkingDirectory != WorkingDirectory) throw new InvalidDataException("Session belongs to another working directory.");
        var target = Path.GetFullPath(path);
        if (IsPiJsonlSessionPath(target))
        {
            var expectedHash = _knownHashes.GetValueOrDefault(target);
            var fileStore = new PiJsonlSessionFileStore(target, expectedHash);
            await fileStore.SaveAsync(session, cancellationToken);
            _knownHashes[target] = fileStore.KnownHash!;
            return;
        }
        var folder = Path.GetDirectoryName(target)!;
        if (OperatingSystem.IsLinux())
            Directory.CreateDirectory(folder, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        else Directory.CreateDirectory(folder);
        // Two CLI processes must not silently overwrite each other. Lock the sibling lock file
        // across the compare and atomic rename; reject stale copies rather than merging turns.
        await using var lease = await SessionFileLease.AcquireAsync(target, cancellationToken);
        if (_knownHashes.TryGetValue(target, out var expected))
        {
            if (!File.Exists(target) || Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(target, cancellationToken))) != expected)
                throw new InvalidDataException("Session changed on disk; reopen before writing.");
        }
        else if (File.Exists(target)) throw new InvalidDataException("Session already exists; refusing to replace an unloaded file.");
        var snapshot = Encoding.UTF8.GetBytes(session.ToJson());
        await AtomicSessionFileWriter.WriteAsync(snapshot, target, folder, cancellationToken);
        _knownHashes[target] = Convert.ToHexString(SHA256.HashData(snapshot));
    }

    public async Task ValidateUnchangedAsync(ConversationSession session, string path,
        CancellationToken cancellationToken = default)
    {
        if (session.WorkingDirectory != WorkingDirectory)
            throw new InvalidDataException("Session belongs to another working directory.");
        var target = Path.GetFullPath(path);
        await using var lease = await SessionFileLease.AcquireAsync(target, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        if (!_knownHashes.TryGetValue(target, out var expected) || !File.Exists(target))
            throw new InvalidDataException("Session changed on disk; reopen before writing.");
        if ((File.GetAttributes(target) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("Refusing to replace a symbolic-link session.");
        var actual = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(target, cancellationToken)));
        if (actual != expected) throw new InvalidDataException("Session changed on disk; reopen before writing.");
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
        await using var lease = await SessionFileLease.AcquireAsync(target, cancellationToken);
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

}
