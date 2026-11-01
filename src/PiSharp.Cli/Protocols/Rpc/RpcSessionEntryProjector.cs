using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using PiSharp.Core;
using PiSharp.Runtime.Sessions;

namespace PiSharp.Cli.Protocols;

internal static class RpcSessionEntryProjector
{
    public static IReadOnlyList<JsonElement> ProjectEntries(ConversationSession session, string? api = null,
        JsonObject? systemMessage = null)
    {
        var nodes = session.Tree.Entries;
        var nodesById = nodes.ToDictionary(node => node.Id, StringComparer.Ordinal);
        var hiddenIds = HiddenIds(nodes, nodesById);
        var usageByAssistantId = RpcSessionMessageProjector.ResolveModelUsageByAssistantId(nodes);
        var projectedById = PiJsonlSessionInterchange.ProjectEntries(session, api)
            .ToDictionary(entry => entry.GetProperty("id").GetString()!, StringComparer.Ordinal);
        var syntheticSystemIds = SyntheticSystemIds(session, nodes, nodesById, systemMessage);
        var entries = new List<JsonElement>(nodes.Count - hiddenIds.Count + syntheticSystemIds.Count);
        foreach (var node in nodes)
        {
            if (node.Type == "run_started" && syntheticSystemIds.TryGetValue(node.Id, out var systemId))
            {
                var projectedSystemMessage = systemMessage!.DeepClone().AsObject();
                projectedSystemMessage["timestamp"] = node.Timestamp.ToUnixTimeMilliseconds();
                entries.Add(JsonSerializer.SerializeToElement(new
                {
                    type = "message",
                    id = systemId,
                    parentId = VisibleParent(node.ParentId, nodesById, hiddenIds, syntheticSystemIds),
                    timestamp = node.Timestamp.ToUniversalTime().ToString("O"),
                    message = projectedSystemMessage
                }));
                continue;
            }
            if (hiddenIds.Contains(node.Id)) continue;
            var entry = JsonNode.Parse(projectedById[node.Id].GetRawText())!.AsObject();
            if (node.Type == "model_change") entry.Remove("endpoint");
            if (node.Type == "chat" && PiJsonlEntryCodec.OriginalEntry(node) is null &&
                entry["message"] is JsonObject entryMessage &&
                entryMessage["role"]?.GetValue<string>() == "assistant" &&
                usageByAssistantId.TryGetValue(node.Id, out var usage))
                entryMessage["usage"] = RpcAssistantMessageProjector.ProjectUsage(usage);
            if (node.Type == "chat" && PiJsonlEntryCodec.OriginalEntry(node) is null &&
                entry["message"] is JsonObject userEntryMessage && userEntryMessage["role"]?.GetValue<string>() == "user" &&
                userEntryMessage["content"] is JsonValue content && content.TryGetValue<string>(out var userText))
            {
                userEntryMessage["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = userText });
            }
            var parentId = node.ParentId;
            while (parentId is not null && hiddenIds.Contains(parentId))
            {
                if (syntheticSystemIds.TryGetValue(parentId, out var systemParent))
                {
                    parentId = systemParent;
                    break;
                }
                if (!nodesById.TryGetValue(parentId, out var parent)) break;
                parentId = parent.ParentId;
            }
            entry["parentId"] = parentId;
            entries.Add(JsonSerializer.SerializeToElement(entry));
        }
        return entries;
    }

    public static string? ProjectLeafId(ConversationSession session, JsonObject? systemMessage = null)
    {
        var nodes = session.Tree.Entries;
        var nodesById = nodes.ToDictionary(node => node.Id, StringComparer.Ordinal);
        var hiddenIds = HiddenIds(nodes, nodesById);
        var syntheticSystemIds = SyntheticSystemIds(session, nodes, nodesById, systemMessage);
        foreach (var node in session.Tree.ActivePath().Reverse())
        {
            if (!hiddenIds.Contains(node.Id)) return node.Id;
            if (syntheticSystemIds.TryGetValue(node.Id, out var systemId)) return systemId;
        }
        return null;
    }

    public static JsonElement ProjectAppendedEntry(ConversationSession session, string entryId, string? api = null) =>
        ProjectEntries(session, api).Single(entry => entry.GetProperty("id").GetString() == entryId);

    /// <summary>
    /// Entries that are not visible as Pi session entries. An interrupted node that duplicates the
    /// persisted failed-assistant chat entry is hidden: the chat entry is the exported record
    /// (PiJsonlEntryCodec.IsSuppressedInterrupted), so entry and leaf ids resolve to it.
    /// </summary>
    private static HashSet<string> HiddenIds(IReadOnlyList<ConversationNode> nodes,
        IReadOnlyDictionary<string, ConversationNode> nodesById)
    {
        var hidden = nodes.Where(IsInternalEntry).Select(node => node.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var node in nodes)
            if (PiJsonlEntryCodec.IsSuppressedInterrupted(node, nodesById)) hidden.Add(node.Id);
        return hidden;
    }

    private static bool IsInternalEntry(ConversationNode node) => node.Type is
        "tool_loadout" or "run_started" or "run_finished" or "run_recovered" or "tool_intent" or
        "tool_outcome" or "tool_skipped" or "assistant_progress" or "usage" or "context_projection" or
        "tool_transcript";

    private static Dictionary<string, string> SyntheticSystemIds(ConversationSession session,
        IReadOnlyList<ConversationNode> nodes, IReadOnlyDictionary<string, ConversationNode> nodesById,
        JsonObject? systemMessage)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        if (systemMessage is null) return result;

        foreach (var node in nodes)
        {
            if (node.Type != "run_started" || !node.Payload.TryGetProperty("runId", out var runId) ||
                runId.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(runId.GetString()) ||
                HasSystemMessageAncestor(session, node, nodesById, result)) continue;
            result[node.Id] = $"rpc-system-{runId.GetString()}";
        }
        return result;
    }

    private static bool HasSystemMessageAncestor(ConversationSession session, ConversationNode node,
        IReadOnlyDictionary<string, ConversationNode> nodesById,
        IReadOnlyDictionary<string, string> syntheticSystemIds)
    {
        var parentId = node.ParentId;
        while (parentId is not null && nodesById.TryGetValue(parentId, out var parent))
        {
            if (syntheticSystemIds.ContainsKey(parentId) ||
                parent.Type == "compaction" && PiJsonlEntryCodec.CompactionSystemMessage(parent) is not null)
                return true;
            if (parent.Type == "chat" &&
                PiJsonlEntryCodec.ProjectEntry(session, parent)["message"] is JsonObject message &&
                message["role"]?.GetValue<string>() == "system")
                return true;
            parentId = parent.ParentId;
        }
        return false;
    }

    public static IReadOnlyList<RpcSessionTreeNode> ProjectTree(ConversationSession session, string? api = null,
        JsonObject? systemMessage = null)
    {
        var entries = ProjectEntries(session, api, systemMessage);
        var labels = ProjectLabels(entries);
        var nodes = new Dictionary<string, RpcSessionTreeNode>(StringComparer.Ordinal);

        for (var index = 0; index < entries.Count; index++)
        {
            var entry = entries[index];
            var id = entry.GetProperty("id").GetString()!;
            var node = new RpcSessionTreeNode(entry, index, ReadTimestamp(entry));
            if (labels.TryGetValue(id, out var label))
            {
                node.Label = label.Name;
                node.LabelTimestamp = label.Timestamp;
            }
            nodes.Add(id, node);
        }

        var roots = new List<RpcSessionTreeNode>();
        foreach (var entry in entries)
        {
            var node = nodes[entry.GetProperty("id").GetString()!];
            var parentId = entry.TryGetProperty("parentId", out var parent) && parent.ValueKind == JsonValueKind.String
                ? parent.GetString() : null;
            if (parentId is null || parentId == node.Entry.GetProperty("id").GetString() ||
                !nodes.TryGetValue(parentId, out var parentNode))
                roots.Add(node);
            else
                parentNode.Children.Add(node);
        }

        SortChildrenByTimestamp(roots);
        return roots;
    }

    private static string? VisibleParent(string? parentId,
        IReadOnlyDictionary<string, ConversationNode> nodesById,
        IReadOnlySet<string> hiddenIds,
        IReadOnlyDictionary<string, string> syntheticSystemIds)
    {
        while (parentId is not null && hiddenIds.Contains(parentId))
        {
            if (syntheticSystemIds.TryGetValue(parentId, out var systemParent)) return systemParent;
            if (!nodesById.TryGetValue(parentId, out var parent)) return null;
            parentId = parent.ParentId;
        }
        return parentId;
    }

    private static Dictionary<string, SessionLabel> ProjectLabels(IReadOnlyList<JsonElement> entries)
    {
        var labels = new Dictionary<string, SessionLabel>(StringComparer.Ordinal);
        foreach (var entry in entries)
        {
            if (!entry.TryGetProperty("type", out var type) || type.GetString() != "label" ||
                !entry.TryGetProperty("targetId", out var target) || target.ValueKind != JsonValueKind.String)
                continue;

            var targetId = target.GetString()!;
            var name = entry.TryGetProperty("label", out var value) && value.ValueKind == JsonValueKind.String
                ? value.GetString() : null;
            if (string.IsNullOrEmpty(name))
                labels.Remove(targetId);
            else
                labels[targetId] = new(name, entry.TryGetProperty("timestamp", out var timestamp) &&
                    timestamp.ValueKind == JsonValueKind.String ? timestamp.GetString() : null);
        }
        return labels;
    }

    private static void SortChildrenByTimestamp(List<RpcSessionTreeNode> roots)
    {
        var pending = new Stack<RpcSessionTreeNode>(roots);
        while (pending.TryPop(out var node))
        {
            node.Children.Sort(static (left, right) =>
            {
                var byTime = left.Timestamp is { } leftTime && right.Timestamp is { } rightTime
                    ? leftTime.CompareTo(rightTime) : 0;
                return byTime != 0 ? byTime : left.AppendOrder.CompareTo(right.AppendOrder);
            });
            foreach (var child in node.Children) pending.Push(child);
        }
    }

    private static DateTimeOffset? ReadTimestamp(JsonElement entry) =>
        entry.TryGetProperty("timestamp", out var value) && value.ValueKind == JsonValueKind.String &&
        DateTimeOffset.TryParse(value.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.None,
            out var timestamp) ? timestamp : null;

    private sealed record SessionLabel(string Name, string? Timestamp);
}

internal sealed class RpcSessionTreeNode(JsonElement entry, int appendOrder, DateTimeOffset? timestamp)
{
    [JsonPropertyName("entry")]
    public JsonElement Entry { get; } = entry;

    [JsonPropertyName("children")]
    public List<RpcSessionTreeNode> Children { get; } = [];

    [JsonPropertyName("label")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Label { get; set; }

    [JsonPropertyName("labelTimestamp")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? LabelTimestamp { get; set; }

    [JsonIgnore]
    public int AppendOrder { get; } = appendOrder;

    [JsonIgnore]
    public DateTimeOffset? Timestamp { get; } = timestamp;
}
