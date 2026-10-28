using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using PiSharp.Core;
using PiSharp.Runtime.Sessions;

namespace PiSharp.Cli.Protocols;

internal static class RpcSessionEntryProjector
{
    public static IReadOnlyList<JsonElement> ProjectEntries(ConversationSession session, string? api = null)
    {
        var nodes = session.Tree.Entries;
        var nodesById = nodes.ToDictionary(node => node.Id, StringComparer.Ordinal);
        var hiddenIds = nodes.Where(IsInternalToolLoadout).Select(node => node.Id).ToHashSet(StringComparer.Ordinal);
        var projectedById = PiJsonlSessionInterchange.ProjectEntries(session, api)
            .ToDictionary(entry => entry.GetProperty("id").GetString()!, StringComparer.Ordinal);
        var entries = new List<JsonElement>(nodes.Count - hiddenIds.Count);
        foreach (var node in nodes)
        {
            if (hiddenIds.Contains(node.Id)) continue;
            var entry = JsonNode.Parse(projectedById[node.Id].GetRawText())!.AsObject();
            if (node.Type == "model_change") entry.Remove("endpoint");
            var parentId = node.ParentId;
            while (parentId is not null && hiddenIds.Contains(parentId) && nodesById.TryGetValue(parentId, out var parent))
                parentId = parent.ParentId;
            entry["parentId"] = parentId;
            entries.Add(JsonSerializer.SerializeToElement(entry));
        }
        return entries;
    }

    public static string? ProjectLeafId(ConversationSession session)
    {
        var hiddenIds = session.Tree.Entries.Where(IsInternalToolLoadout).Select(node => node.Id)
            .ToHashSet(StringComparer.Ordinal);
        return session.Tree.ActivePath().LastOrDefault(node => !hiddenIds.Contains(node.Id))?.Id;
    }

    public static JsonElement ProjectAppendedEntry(ConversationSession session, string entryId, string? api = null) =>
        ProjectEntries(session, api).Single(entry => entry.GetProperty("id").GetString() == entryId);

    private static bool IsInternalToolLoadout(ConversationNode node) => node.Type == "tool_loadout";

    public static IReadOnlyList<RpcSessionTreeNode> ProjectTree(ConversationSession session, string? api = null)
    {
        var entries = ProjectEntries(session, api);
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
