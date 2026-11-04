namespace PiSharp.Runtime.Sessions;

public sealed record SessionListing(string Path, string Id, string? Name, DateTimeOffset ModifiedAt,
    int MessageCount, string Model, string Fingerprint, string? WorkingDirectory = null);

/// <summary>A catalog row together with the store that owns its directory.</summary>
public sealed record SessionCatalogEntry(SessionListing Listing, ConversationStore Store);

/// <summary>Project-scoped session discovery; corrupt documents fail visibly rather than vanishing from the picker.</summary>
public static class SessionCatalog
{
    public static async Task<IReadOnlyList<SessionCatalogEntry>> ListAllAsync(ConversationStore current,
        CancellationToken cancellationToken = default)
    {
        // Pinned Pi scans the default sessions root (one directory per project) or the configured
        // sessions directory. All-scope discovery is best-effort: a corrupt or unreadable foreign
        // session is skipped instead of hiding every other project's sessions.
        var directories = new SortedSet<string>(StringComparer.Ordinal);
        var root = current.FiltersForeignWorkingDirectories
            ? current.DirectoryPath
            : Path.GetDirectoryName(current.DirectoryPath) ?? current.DirectoryPath;
        if (Directory.Exists(root))
        {
            directories.Add(root);
            foreach (var directory in Directory.EnumerateDirectories(root))
            {
                try
                {
                    if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0) continue;
                    directories.Add(directory);
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                {
                    // A project directory can disappear between enumeration and inspection.
                }
            }
        }
        directories.Add(current.DirectoryPath);

        var entries = new List<SessionCatalogEntry>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var directory in directories)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!Directory.Exists(directory)) continue;
            foreach (var path in Directory.EnumerateFiles(directory).Where(ConversationStore.IsSessionFilePath))
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) continue;
                    if (!seen.Add(Path.GetFullPath(path))) continue;
                    var bytes = await ConversationStore.ReadSessionFileAsync(path, cancellationToken);
                    var session = ConversationStore.ParseSessionFile(path, bytes, directory);
                    var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes));
                    var store = new ConversationStore(session.WorkingDirectory, directory);
                    entries.Add(new(new(path, session.Id, session.Name, ActivityTime(session, path),
                        session.ActiveMessages().Count, session.Model, hash, session.WorkingDirectory), store));
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException or
                    InvalidDataException or System.Text.Json.JsonException or ArgumentException or FormatException)
                {
                    // Foreign sessions are best-effort, matching pinned Pi's all-project discovery.
                }
            }
        }
        return entries.OrderByDescending(entry => entry.Listing.ModifiedAt)
            .ThenBy(entry => entry.Listing.Path, StringComparer.Ordinal).ToArray();
    }

    public static async Task<IReadOnlyList<SessionListing>> ListAsync(ConversationStore store,
        CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(store.DirectoryPath)) return [];
        var listings = new List<SessionListing>();
        foreach (var path in Directory.EnumerateFiles(store.DirectoryPath).Where(ConversationStore.IsSessionFilePath))
        {
            cancellationToken.ThrowIfCancellationRequested();
            ConversationSession session;
            string hash;
            try
            {
                if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidDataException("Session path is a symbolic link.");
                var bytes = await ConversationStore.ReadSessionFileAsync(path, cancellationToken);
                session = ConversationStore.ParseSessionFile(path, bytes, store.WorkingDirectory);
                hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes));
                if (ConversationStore.IsPiJsonlSessionPath(path) && store.FiltersForeignWorkingDirectories &&
                    !ConversationStore.HasPersistedWorkingDirectory(session)) continue;
                if (!ConversationStore.WorkingDirectoriesMatch(session.WorkingDirectory, store.WorkingDirectory))
                {
                    if (store.FiltersForeignWorkingDirectories) continue;
                    throw new InvalidDataException("Session belongs to another working directory.");
                }
            }
            catch (Exception error) when (error is IOException or InvalidDataException or System.Text.Json.JsonException)
            { throw new InvalidDataException($"Could not index session '{path}': {error.Message}", error); }
            listings.Add(new(path, session.Id, session.Name, ActivityTime(session, path),
                session.ActiveMessages().Count, session.Model, hash, session.WorkingDirectory));
        }
        return listings.OrderByDescending(item => item.ModifiedAt).ThenBy(item => item.Path, StringComparer.Ordinal).ToArray();
    }

    private static DateTimeOffset ActivityTime(ConversationSession session, string path)
    {
        var lastMessage = session.Tree.Entries.Where(entry => entry.Type == "chat")
            .Select(entry => entry.Timestamp).DefaultIfEmpty(DateTimeOffset.MinValue).Max();
        if (lastMessage > DateTimeOffset.MinValue) return lastMessage;
        if (session.PiJsonlHeader is { ValueKind: System.Text.Json.JsonValueKind.Object } header &&
            header.TryGetProperty("timestamp", out var timestamp) &&
            timestamp.ValueKind == System.Text.Json.JsonValueKind.String &&
            DateTimeOffset.TryParse(timestamp.GetString(), out var created)) return created;
        return File.GetLastWriteTimeUtc(path);
    }

    public static IReadOnlyList<SessionListing> Search(IReadOnlyList<SessionListing> listings, string query) =>
        listings.Where(item => item.Id.Contains(query, StringComparison.OrdinalIgnoreCase) ||
            item.Name?.Contains(query, StringComparison.OrdinalIgnoreCase) == true ||
            item.Model.Contains(query, StringComparison.OrdinalIgnoreCase)).ToArray();

    public static SessionListing Resolve(IReadOnlyList<SessionListing> listings, string prefix)
    {
        if (string.IsNullOrWhiteSpace(prefix)) throw new ArgumentException("Specify a session ID prefix from /sessions.");
        var matches = listings.Where(item => item.Id.StartsWith(prefix, StringComparison.Ordinal) ||
            item.Name?.Equals(prefix, StringComparison.OrdinalIgnoreCase) == true).ToArray();
        return matches.Length == 1 ? matches[0] : throw new ArgumentException(matches.Length == 0 ?
            "No matching session in this project." : "Session prefix is ambiguous; use a longer ID.");
    }
}
