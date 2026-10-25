using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using static PiSharp.Runtime.Sessions.PiJsonlEntryCodec;

namespace PiSharp.Runtime.Sessions;

/// <summary>Parses bounded Pi logs, migrates older records and restores their selected session branch.</summary>
internal static class PiJsonlSessionImporter
{
    private const int MaximumFileBytes = 128 * 1024 * 1024;
    private const int MaximumEntryBytes = 16 * 1024 * 1024;

    /// <summary>Imports Pi v1-v3 JSONL, migrating legacy links and retaining original Pi records for export.</summary>
    public static ConversationSession Import(string jsonl, string? workingDirectoryOverride = null,
        string? workingDirectoryFallback = null, Func<string>? generateEntryId = null)
    {
        ArgumentNullException.ThrowIfNull(jsonl);
        if (Encoding.UTF8.GetByteCount(jsonl) > MaximumFileBytes)
            throw new InvalidDataException("Pi session exceeds the 128 MiB import limit.");

        var records = ParseRecords(jsonl);
        if (records.Count == 0 || StringProperty(records[0], "type") != "session")
            throw new InvalidDataException("The file does not begin with a Pi session header.");
        var header = records[0];
        var sessionId = StringProperty(header, "id");
        var cwd = workingDirectoryOverride ?? StringProperty(header, "cwd") ?? workingDirectoryFallback;
        if (string.IsNullOrWhiteSpace(sessionId))
            throw new InvalidDataException("The Pi session header must contain a session id.");
        if (string.IsNullOrWhiteSpace(cwd)) cwd = Environment.CurrentDirectory;
        cwd = Path.GetFullPath(cwd);

        var version = 1;
        if (TryProperty(header, "version", out var versionValue))
        {
            if (versionValue.ValueKind != JsonValueKind.Number || !versionValue.TryGetInt32(out version))
                throw new InvalidDataException("Pi session version must be an integer.");
        }
        if (version is < 1 or > 3)
            throw new InvalidDataException($"Pi session version {version} is not supported.");

        // Pi's file loader requires the first successfully parsed JSON value to be the session header.
        // Keep that ordering check even though non-entry values in the body are discarded safely here.
        var entries = records.Skip(1)
            .Where(record => record.ValueKind == JsonValueKind.Object && TryProperty(record, "type", out _))
            .Select(record => JsonNode.Parse(record.GetRawText())!.AsObject())
            .ToArray();
        if (version == 1) MigrateV1(entries, generateEntryId);
        if (version < 3)
            foreach (var entry in entries) MigrateHookMessage(entry);

        var tree = PiJsonlSessionTreeBuilder.Build(entries.Select(entry => JsonSerializer.SerializeToElement(entry)).ToArray());
        var activePath = tree.ActivePath();
        var model = "unknown";
        string? provider = null;
        string? name = null;
        foreach (var node in activePath)
        {
            if (node.Type == "model_change")
            {
                var source = OriginalEntry(node);
                if (source is { } modelEntry)
                {
                    model = StringProperty(modelEntry, "modelId") ?? model;
                    provider = StringProperty(modelEntry, "provider") ?? provider;
                }
            }
            else if (node.Type == "chat" && OriginalEntry(node) is { } messageEntry &&
                TryProperty(messageEntry, "message", out var message) && StringProperty(message, "role") == "assistant")
            {
                model = StringProperty(message, "model") ?? model;
                provider = StringProperty(message, "provider") ?? provider;
            }
            else if (node.Type == "session_info" && OriginalEntry(node) is { } info)
            {
                name = TryProperty(info, "name", out var nameValue) && nameValue.ValueKind == JsonValueKind.String &&
                    !string.IsNullOrWhiteSpace(nameValue.GetString()) ? nameValue.GetString()!.Trim() : null;
            }
        }

        var session = ConversationSession.FromPiJsonl(sessionId, cwd, model, provider, name, tree, header);
        _ = session.ActiveMessages();
        _ = session.ContextMessages();
        return session;
    }

    private static List<JsonElement> ParseRecords(string text)
    {
        var records = new List<JsonElement>();
        using var reader = new StringReader(text);
        while (reader.ReadLine() is { } line)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            if (Encoding.UTF8.GetByteCount(line) > MaximumEntryBytes) continue;
            try
            {
                using var document = JsonDocument.Parse(line);
                records.Add(document.RootElement.Clone());
            }
            catch (JsonException) { }
        }
        return records;
    }

    private static void MigrateV1(JsonObject[] entries, Func<string>? generateEntryId)
    {
        // Keep this set populated so a short-ID collision cannot create a self-parenting canonical graph.
        var generatedIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in entries)
            entry["id"] = PiJsonlEntryIdGenerator.GenerateUnique(generatedIds, generateEntryId);

        string? previous = null;
        foreach (var entry in entries)
        {
            entry["parentId"] = previous;
            previous = NodeString(entry, "id");
        }

        foreach (var entry in entries)
        {
            if (NodeString(entry, "type") != "compaction" || entry["firstKeptEntryIndex"] is not JsonNode indexNode ||
                indexNode.GetValueKind() != JsonValueKind.Number) continue;

            if (indexNode is JsonValue scalar && scalar.TryGetValue<double>(out var numericIndex) &&
                double.IsFinite(numericIndex) && numericIndex == Math.Truncate(numericIndex) &&
                numericIndex > 0 && numericIndex <= entries.Length)
                entry["firstKeptEntryId"] = NodeString(entries[(int)numericIndex - 1], "id");
            entry.Remove("firstKeptEntryIndex");
        }
    }

    private static void MigrateHookMessage(JsonObject entry)
    {
        if (NodeString(entry, "type") == "message" && entry["message"] is JsonObject message &&
            NodeString(message, "role") == "hookMessage") message["role"] = "custom";
    }

    private static string? NodeString(JsonObject value, string key) => value[key] is JsonValue scalar &&
        scalar.TryGetValue<string>(out var text) ? text : null;

}
