using PiSharp.Runtime.Sessions;

namespace PiSharp.Cli.Tui;

/// <summary>One flattened session-tree row with the prefix metadata pinned Pi renders.</summary>
internal sealed record SessionPickerTreeRow(SessionCatalogEntry Entry, int Depth, bool IsLast,
    IReadOnlyList<bool> AncestorContinues)
{
    public string Prefix => Depth == 0 ? "" :
        string.Concat(AncestorContinues.Select(continues => continues ? "│  " : "   ")) +
        (IsLast ? "└─ " : "├─ ");
}

/// <summary>
/// Pinned Pi's <c>buildSessionTree</c>/<c>flattenSessionTree</c>: canonical parent links, subtree
/// latest activity, stable descending sibling order, and pre-order flattening.
/// </summary>
internal static class SessionPickerTree
{
    private sealed class Node(SessionCatalogEntry entry)
    {
        public SessionCatalogEntry Entry { get; } = entry;
        public Node? Parent { get; set; }
        public List<Node> Children { get; } = [];
        public long LatestActivity { get; set; } = entry.Listing.ModifiedAt.UtcTicks;
    }

    public static IReadOnlyList<SessionPickerTreeRow> Build(IReadOnlyList<SessionCatalogEntry> entries)
    {
        var pathComparer = OperatingSystem.IsWindows()
            ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        var byPath = new Dictionary<string, Node>(pathComparer);
        var nodes = new List<Node>(entries.Count);
        foreach (var entry in entries)
        {
            var node = new Node(entry);
            nodes.Add(node);
            byPath.TryAdd(entry.Listing.CanonicalPath, node);
        }

        var roots = new List<Node>();
        foreach (var node in nodes)
        {
            var parentPath = node.Entry.Listing.ParentSessionPath;
            var parent = string.IsNullOrWhiteSpace(parentPath) ? null : byPath.GetValueOrDefault(
                SessionCatalog.CanonicalPath(parentPath));
            if (parent is not null && !ReferenceEquals(parent, node) && !WouldCreateCycle(node, parent))
            {
                node.Parent = parent;
                parent.Children.Add(node);
            }
            else roots.Add(node);
        }

        foreach (var root in roots) UpdateLatestActivity(root);
        SortNodes(roots);

        var rows = new List<SessionPickerTreeRow>(entries.Count);
        for (var index = 0; index < roots.Count; index++)
            Walk(roots[index], depth: 0, ancestorContinues: [], isLast: index == roots.Count - 1, rows);
        return rows;
    }

    /// <summary>Breaks parent/child back-edges from corrupt or hand-edited JSONL files.</summary>
    private static bool WouldCreateCycle(Node node, Node parent)
    {
        var visited = new HashSet<Node>(ReferenceEqualityComparer.Instance);
        for (var current = parent; current is not null; current = current.Parent)
        {
            if (ReferenceEquals(current, node)) return true;
            if (!visited.Add(current)) return true;
        }
        return false;
    }

    private static long UpdateLatestActivity(Node node)
    {
        foreach (var child in node.Children)
            node.LatestActivity = Math.Max(node.LatestActivity, UpdateLatestActivity(child));
        return node.LatestActivity;
    }

    private static void SortNodes(List<Node> nodes)
    {
        // Enumerable.OrderByDescending is stable, matching JavaScript's stable Array.sort.
        var sorted = nodes.OrderByDescending(node => node.LatestActivity).ToArray();
        nodes.Clear();
        nodes.AddRange(sorted);
        foreach (var node in nodes) SortNodes(node.Children);
    }

    private static void Walk(Node node, int depth, IReadOnlyList<bool> ancestorContinues, bool isLast,
        List<SessionPickerTreeRow> rows)
    {
        rows.Add(new(node.Entry, depth, isLast, ancestorContinues));
        for (var index = 0; index < node.Children.Count; index++)
        {
            var childIsLast = index == node.Children.Count - 1;
            var continues = depth > 0 && !isLast;
            Walk(node.Children[index], depth + 1, [.. ancestorContinues, continues], childIsLast, rows);
        }
    }
}
