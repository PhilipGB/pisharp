using System.Text.Json;
using System.Text.Json.Nodes;
using PiSharp.Core;

namespace PiSharp.Runtime.Sessions;

/// <summary>Rebuilds a selected session branch using Pi's fork-entry ordering.</summary>
internal static class PiSessionForkProjection
{
    public static ConversationTree CreateTree(IEnumerable<ConversationNode> sourcePath)
    {
        var entries = new List<ConversationNode>();
        var labels = new List<ConversationNode>();
        var pendingLabels = new List<ConversationNode>();
        var replacementIds = new Dictionary<string, string>(StringComparer.Ordinal);
        string? parentId = null;

        foreach (var source in sourcePath)
        {
            if (source.Type == "label")
            {
                labels.Add(source);
                pendingLabels.Add(source);
                continue;
            }

            foreach (var label in pendingLabels)
                replacementIds[label.Id] = source.Id;
            pendingLabels.Clear();

            var payload = ReprojectPayload(source, parentId, replacementIds);
            entries.Add(source with { ParentId = parentId, Payload = payload });
            parentId = source.Id;
        }

        foreach (var label in labels)
        {
            entries.Add(label with { ParentId = parentId, Payload = ReprojectPayload(label, parentId, replacementIds) });
            parentId = label.Id;
        }

        return new ConversationTree(entries, parentId);
    }

    private static JsonElement ReprojectPayload(ConversationNode entry, string? parentId,
        IReadOnlyDictionary<string, string> replacementIds)
    {
        if (entry.Payload.ValueKind != JsonValueKind.Object) return entry.Payload.Clone();
        var payload = JsonNode.Parse(entry.Payload.GetRawText())!.AsObject();
        string? replacement = null;
        if (entry.Type == "compaction" && payload["firstKeptEntryId"] is JsonValue keptValue &&
            keptValue.TryGetValue<string>(out var keptId) && replacementIds.TryGetValue(keptId, out var replacedId))
        {
            replacement = replacedId;
            payload["firstKeptEntryId"] = replacement;
        }

        var originalKey = payload.ContainsKey("PiEntry") ? "PiEntry" :
            payload.ContainsKey("piOriginalEntry") ? "piOriginalEntry" : null;
        if (originalKey is not null && payload[originalKey] is JsonObject original)
        {
            original["parentId"] = parentId;
            if (replacement is not null && entry.Type == "compaction")
                original["firstKeptEntryId"] = replacement;
        }

        return JsonSerializer.SerializeToElement(payload);
    }
}
