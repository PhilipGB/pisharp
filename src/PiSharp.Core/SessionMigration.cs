using System.Text.Json;
using System.Text.Json.Nodes;

namespace PiSharp.Core;

/// <summary>
/// Port of the pinned session-manager.ts <c>migrateToCurrentVersion</c>/<c>migrateV1ToV2</c>/
/// <c>migrateV2ToV3</c>: in-place <see cref="JsonNode"/> migration of Pi v1/v2 session files
/// to v3. The pinned runtime migrates on load (<c>_loadEntries</c>) and rewrites the file
/// immediately, so old Pi sessions become current-version files on first open.
/// </summary>
public static class SessionMigration
{
    /// <summary>Current Pi session format version (pinned <c>CURRENT_SESSION_VERSION</c>).</summary>
    public const int CurrentVersion = 3;

    /// <summary>
    /// Migrates the entries in place when the session header version is below
    /// <see cref="CurrentVersion"/>. Returns true when a migration was applied.
    /// Legacy PiSharp v1 documents (headers with <c>sessionId</c> instead of <c>id</c>)
    /// predate Pi's format and are never migrated.
    /// </summary>
    public static bool MigrateEntries(IReadOnlyList<JsonNode?> entries)
    {
        var header = entries
            .OfType<JsonObject>()
            .FirstOrDefault(entry => entry["type"]?.GetValue<string>() == "session");
        if (header is null)
        {
            return false;
        }

        // Legacy PiSharp v1 documents are turn-based (sessionId/workingDirectory property
        // names); migrating them would re-id and re-chain the turns and destroy the format.
        if (header["sessionId"] is not null)
        {
            return false;
        }

        var version = ReadInt(header["version"]) ?? 1;
        if (version >= CurrentVersion)
        {
            return false;
        }

        if (version < 2)
        {
            MigrateV1ToV2(entries);
        }

        MigrateV2ToV3(entries);
        return true;
    }

    /// <summary>
    /// Parses the session file's lines, migrates them in place when needed, and returns
    /// the migrated lines (or null when no migration was applied). Malformed lines are
    /// skipped, matching the pinned <c>parseSessionEntries</c>.
    /// </summary>
    public static string[]? MigrateSessionLines(IReadOnlyList<string> lines)
    {
        var nodes = new List<JsonNode?>();
        foreach (var line in lines)
        {
            try
            {
                nodes.Add(JsonNode.Parse(line));
            }
            catch (JsonException)
            {
                // Pinned parseSessionEntries: malformed lines are skipped.
            }
        }

        if (!MigrateEntries(nodes))
        {
            return null;
        }

        return nodes
            .Select(node => node?.ToJsonString() ?? string.Empty)
            .Where(line => line.Length > 0)
            .ToArray();
    }

    /// <summary>Reads an integer-valued JSON number, or null when the node is absent or not an integer number.</summary>
    private static int? ReadInt(JsonNode? node)
    {
        if (node is JsonValue value && value.GetValueKind() == JsonValueKind.Number)
        {
            try
            {
                return value.GetValue<int>();
            }
            catch (Exception ex) when (ex is FormatException or OverflowException)
            {
                return null;
            }
        }

        return null;
    }

    /// <summary>
    /// Pinned generateId: the first 8 hex chars of a UUID, collision-checked against the
    /// already taken ids, 100 attempts, with a full UUID as the fallback.
    /// </summary>
    internal static string GenerateId(HashSet<string> taken)
    {
        for (var attempt = 0; attempt < 100; attempt++)
        {
            var id = Guid.NewGuid().ToString("N")[..8];
            if (taken.Add(id))
            {
                return id;
            }
        }

        return Guid.NewGuid().ToString("N");
    }

    /// <summary>
    /// Pinned migrateV1ToV2: every non-header entry gets a fresh id and a linear parentId
    /// chain following file order; compaction firstKeptEntryIndex becomes firstKeptEntryId.
    /// </summary>
    private static void MigrateV1ToV2(IReadOnlyList<JsonNode?> entries)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        string? previousId = null;

        for (var index = 0; index < entries.Count; index++)
        {
            if (entries[index] is not JsonObject entry)
            {
                continue;
            }

            if (entry["type"]?.GetValue<string>() == "session")
            {
                entry["version"] = 2;
                continue;
            }

            entry["id"] = GenerateId(ids);
            entry["parentId"] = (JsonNode?)previousId;
            previousId = entry["id"]!.GetValue<string>();

            // Pinned: remap the compaction's positional reference onto entry ids.
            if (entry["type"]?.GetValue<string>() == "compaction" &&
                ReadInt(entry["firstKeptEntryIndex"]) is { } firstKeptIndex)
            {
                if (firstKeptIndex >= 0 &&
                    firstKeptIndex < entries.Count &&
                    entries[firstKeptIndex] is JsonObject target &&
                    target["type"]?.GetValue<string>() != "session" &&
                    target["id"]?.GetValue<string>() is { } targetId)
                {
                    entry["firstKeptEntryId"] = targetId;
                }

                entry.Remove("firstKeptEntryIndex");
            }
        }
    }

    /// <summary>Pinned migrateV2ToV3: bump the header to v3 and rename hookMessage role to custom.</summary>
    private static void MigrateV2ToV3(IReadOnlyList<JsonNode?> entries)
    {
        foreach (var entry in entries.OfType<JsonObject>())
        {
            var type = entry["type"]?.GetValue<string>();
            if (type == "session")
            {
                entry["version"] = CurrentVersion;
            }
            else if (type == "message" &&
                     entry["message"] is JsonObject message &&
                     message["role"]?.GetValue<string>() == "hookMessage")
            {
                message["role"] = "custom";
            }
        }
    }
}
