namespace PiSharp.Runtime.Sessions;

public sealed record SessionListing(string Path, string Id, string? Name, DateTimeOffset ModifiedAt,
    int MessageCount, string Model, string Fingerprint);

/// <summary>Project-scoped session discovery; corrupt documents fail visibly rather than vanishing from the picker.</summary>
public static class SessionCatalog
{
    public static async Task<IReadOnlyList<SessionListing>> ListAsync(ConversationStore store,
        CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(store.DirectoryPath)) return [];
        var listings = new List<SessionListing>();
        foreach (var path in Directory.EnumerateFiles(store.DirectoryPath, "*.session.json"))
        {
            cancellationToken.ThrowIfCancellationRequested();
            ConversationSession session;
            string hash;
            try
            {
                if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidDataException("Session path is a symbolic link.");
                var bytes = await File.ReadAllBytesAsync(path, cancellationToken);
                session = ConversationSession.Parse(System.Text.Encoding.UTF8.GetString(bytes));
                hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes));
                if (session.WorkingDirectory != store.WorkingDirectory)
                    throw new InvalidDataException("Session belongs to another working directory.");
            }
            catch (Exception error) when (error is IOException or InvalidDataException or System.Text.Json.JsonException)
            { throw new InvalidDataException($"Could not index session '{path}': {error.Message}", error); }
            listings.Add(new(path, session.Id, session.Name, File.GetLastWriteTimeUtc(path),
                session.ActiveMessages().Count, session.Model, hash));
        }
        return listings.OrderByDescending(item => item.ModifiedAt).ThenBy(item => item.Path, StringComparer.Ordinal).ToArray();
    }

    public static IReadOnlyList<SessionListing> Search(IReadOnlyList<SessionListing> listings, string query) =>
        listings.Where(item => item.Id.Contains(query, StringComparison.OrdinalIgnoreCase) ||
            item.Name?.Contains(query, StringComparison.OrdinalIgnoreCase) == true ||
            item.Model.Contains(query, StringComparison.OrdinalIgnoreCase)).ToArray();

    public static SessionListing Resolve(IReadOnlyList<SessionListing> listings, string prefix)
    {
        if (string.IsNullOrWhiteSpace(prefix)) throw new ArgumentException("Specify a session ID prefix from /sessions.");
        var matches = listings.Where(item => item.Id.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ||
            item.Name?.Equals(prefix, StringComparison.OrdinalIgnoreCase) == true).ToArray();
        return matches.Length == 1 ? matches[0] : throw new ArgumentException(matches.Length == 0 ?
            "No matching session in this project." : "Session prefix is ambiguous; use a longer ID.");
    }
}
