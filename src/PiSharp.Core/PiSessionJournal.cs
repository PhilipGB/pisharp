using System.Text.Json;
using System.Text.Json.Nodes;

namespace PiSharp.Core;

/// <summary>
/// Pi v3 JSONL session representation. This is the canonical, append-only entry format,
/// distinct from the current MAF provider-specific snapshot. v1/v2 are migrated in memory on load.
/// </summary>
public sealed class PiSessionJournal
{
    public const int Version = 3;
    public string Id { get; }
    public string Cwd { get; }
    public DateTimeOffset Created { get; }
    public string? ParentSession { get; }
    public ConversationTree Tree { get; }

    public PiSessionJournal(string cwd, string? parentSession = null)
        : this(Guid.NewGuid().ToString(), cwd, DateTimeOffset.UtcNow, parentSession, new ConversationTree()) { }

    private PiSessionJournal(string id, string cwd, DateTimeOffset created, string? parentSession, ConversationTree tree)
    {
        Id = id;
        Cwd = cwd;
        Created = created;
        ParentSession = parentSession;
        Tree = tree;
    }

    /// <summary>Append a Pi entry. Payload holds type-specific fields, not id/parentId/timestamp.</summary>
    public ConversationNode Append(string type, JsonElement payload)
    {
        if (payload.ValueKind != JsonValueKind.Object) throw new ArgumentException("Entry payload must be an object.", nameof(payload));
        foreach (var reserved in new[] { "type", "id", "parentId", "timestamp" })
            if (payload.TryGetProperty(reserved, out _)) throw new ArgumentException($"Entry payload contains reserved field {reserved}.", nameof(payload));
        return Tree.Append(type, payload);
    }

    public string ToJsonLines()
    {
        var header = new JsonObject
        {
            ["type"] = "session",
            ["version"] = Version,
            ["id"] = Id,
            ["timestamp"] = Created.ToUniversalTime().ToString("O"),
            ["cwd"] = Cwd
        };
        if (ParentSession is not null) header["parentSession"] = ParentSession;
        var lines = new List<string> { header.ToJsonString() };
        foreach (var entry in Tree.Entries)
        {
            var value = JsonNode.Parse(entry.Payload.GetRawText()) as JsonObject
                ?? throw new InvalidDataException($"Entry {entry.Id} payload must be an object.");
            foreach (var reserved in new[] { "type", "id", "parentId", "timestamp" })
                if (value.ContainsKey(reserved)) throw new InvalidDataException($"Entry {entry.Id} payload contains reserved field {reserved}.");
            var node = new JsonObject
            {
                ["type"] = entry.Type,
                ["id"] = entry.Id,
                ["parentId"] = entry.ParentId,
                ["timestamp"] = entry.Timestamp.ToUniversalTime().ToString("O")
            };
            foreach (var property in value) node[property.Key] = property.Value?.DeepClone();
            lines.Add(node.ToJsonString());
        }
        return string.Join('\n', lines) + "\n";
    }

    private static void Migrate(List<JsonObject> records, int version)
    {
        if (version < 2)
        {
            string? previous = null;
            var ids = new HashSet<string>(StringComparer.Ordinal);
            foreach (var item in records.Skip(1))
            {
                string id;
                do { id = Guid.NewGuid().ToString("N")[..8]; } while (!ids.Add(id));
                item["id"] = id;
                item["parentId"] = previous;
                previous = id;
                if (item["type"]?.GetValue<string>() == "compaction" && item["firstKeptEntryIndex"] is JsonValue indexValue)
                {
                    var index = indexValue.GetValue<int>();
                    if (index < 1 || index >= records.Count)
                        throw new InvalidDataException($"Compaction firstKeptEntryIndex {index} is invalid.");
                    item["firstKeptEntryId"] = records[index]["id"]?.GetValue<string>();
                    item.Remove("firstKeptEntryIndex");
                }
            }
        }
        if (version < 3)
            foreach (var item in records.Skip(1))
                if (item["type"]?.GetValue<string>() == "message" && item["message"] is JsonObject message &&
                    message["role"]?.GetValue<string>() == "hookMessage") message["role"] = "custom";
        records[0]["version"] = Version;
    }

    public static PiSessionJournal Parse(string jsonl)
    {
        var lines = jsonl.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        if (lines.Length == 0) throw new InvalidDataException("Session header is missing.");
        try
        {
            var records = lines.Select((line, index) => JsonNode.Parse(line) as JsonObject
                ?? throw new InvalidDataException($"Session line {index + 1} is not an object.")).ToList();
            var header = records[0];
            if (header["type"]?.GetValue<string>() != "session") throw new InvalidDataException("First line is not a session header.");
            var version = header["version"]?.GetValue<int>() ?? 1;
            if (version < 1 || version > Version) throw new InvalidDataException($"Unsupported Pi session version: {version}.");
            Migrate(records, version);
            using var headerDocument = JsonDocument.Parse(header.ToJsonString());
            var root = headerDocument.RootElement;
            var entries = new List<ConversationNode>();
            for (var i = 1; i < records.Count; i++)
            {
                using var document = JsonDocument.Parse(records[i].ToJsonString());
                var item = document.RootElement;
                var extra = new JsonObject();
                foreach (var field in item.EnumerateObject())
                    if (field.Name is not ("type" or "id" or "parentId" or "timestamp"))
                        extra[field.Name] = JsonNode.Parse(field.Value.GetRawText());
                entries.Add(new ConversationNode(
                    item.GetProperty("id").GetString()!,
                    item.GetProperty("parentId").GetString(),
                    item.GetProperty("type").GetString()!,
                    JsonSerializer.SerializeToElement(extra),
                    item.GetProperty("timestamp").GetDateTimeOffset()));
            }
            var parentSession = root.TryGetProperty("parentSession", out var parent) ? parent.GetString() : null;
            return new PiSessionJournal(root.GetProperty("id").GetString()!, root.GetProperty("cwd").GetString()!,
                root.GetProperty("timestamp").GetDateTimeOffset(), parentSession, new ConversationTree(entries));
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
        {
            throw new InvalidDataException("Malformed Pi session JSONL.", ex);
        }
    }
}
