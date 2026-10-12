using System.Text.Json;
using PiSharp.Core;

namespace PiSharp.Runtime.Sessions;

internal static class PiJsonlSessionTreeBuilder
{
    public static ConversationTree Build(IReadOnlyList<JsonElement> entries)
    {
        var allIds = entries.Select(entry => PiJsonlEntryCodec.StringProperty(entry, "id"))
            .Where(id => id is not null).ToHashSet(StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var nodes = new List<ConversationNode>(entries.Count);
        foreach (var entry in entries)
        {
            var id = PiJsonlEntryCodec.StringProperty(entry, "id");
            var type = PiJsonlEntryCodec.StringProperty(entry, "type");
            if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(type) || !seen.Add(id))
                throw new InvalidDataException("Pi session entries must have unique ids and a type.");

            var parentId = PiJsonlEntryCodec.StringProperty(entry, "parentId");
            if (parentId is not null && !allIds.Contains(parentId))
            {
                // Pi resolves a missing parent to the end of the available path. Preserve the original
                // link for Pi projections, but root the entry in PiSharp's stricter conversation graph.
                parentId = null;
            }
            var timestamp = PiJsonlEntryCodec.ParseTimestamp(
                PiJsonlEntryCodec.StringProperty(entry, "timestamp"));
            nodes.Add(PiJsonlEntryCodec.ToNode(entry, id, parentId, type, timestamp));
        }

        return ConversationTree.FromEntries(nodes);
    }
}
