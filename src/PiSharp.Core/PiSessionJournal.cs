using System.Text.Json;
using System.Text.Json.Nodes;

namespace PiSharp.Core;

/// <summary>
/// Pi v3 JSONL session representation. This is the canonical, append-only entry format,
/// distinct from the current MAF provider-specific snapshot. v1/v2 migration is not supported yet.
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

    public static PiSessionJournal Parse(string jsonl)
    {
        var lines = jsonl.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        if (lines.Length == 0) throw new InvalidDataException("Session header is missing.");
        try
        {
            using var header = JsonDocument.Parse(lines[0]);
            var root = header.RootElement;
            if (root.GetProperty("type").GetString() != "session") throw new InvalidDataException("First line is not a session header.");
            if (root.GetProperty("version").GetInt32() != Version)
                throw new InvalidDataException("Only Pi v3 sessions are supported; migration is not implemented.");
            var entries = new List<ConversationNode>();
            for (var i = 1; i < lines.Length; i++)
            {
                using var document = JsonDocument.Parse(lines[i]);
                var item = document.RootElement;
                if (item.ValueKind != JsonValueKind.Object) throw new InvalidDataException($"Session line {i + 1} is not an object.");
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
