using System.Text.Json;
using PiSharp.Core;

namespace PiSharp.Cli.Tui;

/// <summary>Projected session tree: filters, folding, search and selection. Mirrors pinned Pi's TreeList.</summary>
internal sealed class TerminalTreeState
{
    private readonly TerminalTreeEntryCodec _codec = new();
    private readonly string? _currentLeafId;
    private readonly List<TerminalTreeNode> _nodes;
    private readonly Dictionary<string, List<TerminalTreeNode>> _childrenById = new(StringComparer.Ordinal);
    private readonly Dictionary<string, TerminalTreeNode> _byId = new(StringComparer.Ordinal);
    private readonly Dictionary<string, TerminalTreeEntryInfo> _decoded = new(StringComparer.Ordinal);
    private readonly HashSet<string> _activePath = new(StringComparer.Ordinal);
    private List<TerminalTreeFlatNode> _flatNodes = [];
    private List<TerminalTreeFlatNode> _filtered = [];
    private Dictionary<string, TerminalTreeNode?> _visibleParent = new(StringComparer.Ordinal);
    private Dictionary<string, List<string>> _visibleChildren = new(StringComparer.Ordinal);
    private bool _multipleRoots;
    private readonly HashSet<string> _foldedNodes = new(StringComparer.Ordinal);
    private TerminalTreeFilterMode _filterMode;
    private string _searchQuery = "";
    private string? _lastSelectedId;
    private int _selectedIndex;

    public TerminalTreeState(IReadOnlyList<ConversationNode> entries, string? currentLeafId,
        TerminalTreeFilterMode initialFilterMode = TerminalTreeFilterMode.Default)
    {
        ArgumentNullException.ThrowIfNull(entries);
        _currentLeafId = currentLeafId;
        _filterMode = initialFilterMode;
        var materialized = entries.ToArray();
        _nodes = materialized.Select((entry, index) => new TerminalTreeNode { Entry = entry, AppendIndex = index }).ToList();
        foreach (var node in _nodes) _byId[node.Entry.Id] = node;
        IndexChildren();
        ProjectLabels();
        foreach (var node in _nodes) _codec.ObserveAssistantCalls(node.Entry);
        foreach (var node in _nodes) _decoded[node.Entry.Id] = _codec.Decode(node.Entry);
        BuildActivePath();
        Flatten();
        ApplyFilter();
        _selectedIndex = FindNearestVisibleIndex(_currentLeafId);
        if (_filtered.Count > 0) _lastSelectedId = _filtered[_selectedIndex].Node.Entry.Id;
    }

    public IReadOnlyList<TerminalTreeFlatNode> VisibleNodes => _filtered;
    public int SelectedIndex => _selectedIndex;
    public TerminalTreeFilterMode FilterMode => _filterMode;
    public string SearchQuery => _searchQuery;
    public bool ShowLabelTimestamps { get; private set; }
    public string FilterModeName => _filterMode switch
    {
        TerminalTreeFilterMode.NoTools => "no-tools",
        TerminalTreeFilterMode.UserOnly => "user-only",
        TerminalTreeFilterMode.LabeledOnly => "labeled-only",
        TerminalTreeFilterMode.All => "all",
        _ => "default"
    };
    public TerminalTreeFlatNode? SelectedNode =>
        _selectedIndex >= 0 && _selectedIndex < _filtered.Count ? _filtered[_selectedIndex] : null;
    public string? SelectedEntryId => SelectedNode?.Node.Entry.Id;
    public TerminalTreeEntryInfo? SelectedInfo =>
        SelectedNode is { } node ? _decoded[node.Node.Entry.Id] : null;
    public TerminalTreeEntryInfo Decode(string entryId) => _decoded[entryId];
    public bool HasMultipleRoots => _multipleRoots;
    public bool IsFolded(string entryId) => _foldedNodes.Contains(entryId);
    public bool IsOnActivePath(string entryId) => _activePath.Contains(entryId);

    public int MaxVisibleLines { get; set; } = 10;

    public string? SelectedCopyText()
    {
        var info = SelectedInfo;
        if (info is null) return null;
        return string.IsNullOrWhiteSpace(info.CopyText) ? null : info.CopyText;
    }

    /// <summary>Rebuild the projection after a label change, keeping filter/search/selection/fold state.</summary>
    public void Refresh(IReadOnlyList<ConversationNode> entries)
    {
        var selectedId = SelectedEntryId ?? _lastSelectedId;
        var materialized = entries.ToArray();
        _nodes.Clear();
        _byId.Clear();
        _childrenById.Clear();
        _decoded.Clear();
        _nodes.AddRange(materialized.Select((entry, index) => new TerminalTreeNode { Entry = entry, AppendIndex = index }));
        foreach (var node in _nodes) _byId[node.Entry.Id] = node;
        IndexChildren();
        ProjectLabels();
        foreach (var node in _nodes) _codec.ObserveAssistantCalls(node.Entry);
        foreach (var node in _nodes) _decoded[node.Entry.Id] = _codec.Decode(node.Entry);
        BuildActivePath();
        Flatten();
        _lastSelectedId = selectedId;
        ApplyFilter();
    }

    public void MoveSelection(int delta)
    {
        if (_filtered.Count == 0) return;
        _selectedIndex = ((_selectedIndex + delta) % _filtered.Count + _filtered.Count) % _filtered.Count;
        _lastSelectedId = _filtered[_selectedIndex].Node.Entry.Id;
    }

    public void MoveSelectionClamped(int delta)
    {
        if (_filtered.Count == 0) return;
        _selectedIndex = Math.Clamp(_selectedIndex + delta, 0, _filtered.Count - 1);
        _lastSelectedId = _filtered[_selectedIndex].Node.Entry.Id;
    }

    public void PageUp(int lines) => MoveSelectionClamped(-Math.Max(1, lines));
    public void PageDown(int lines) => MoveSelectionClamped(Math.Max(1, lines));

    /// <summary>Fold the selected segment, or jump to the previous segment start.</summary>
    public void FoldOrUp()
    {
        var currentId = SelectedEntryId;
        if (currentId is not null && IsFoldable(currentId) && !_foldedNodes.Contains(currentId))
        {
            _foldedNodes.Add(currentId);
            ApplyFilter();
            return;
        }
        _selectedIndex = FindBranchSegmentStart("up");
        _lastSelectedId = SelectedEntryId;
    }

    /// <summary>Unfold the selected segment, or jump to the next segment start or branch end.</summary>
    public void UnfoldOrDown()
    {
        var currentId = SelectedEntryId;
        if (currentId is not null && _foldedNodes.Contains(currentId))
        {
            _foldedNodes.Remove(currentId);
            ApplyFilter();
            return;
        }
        _selectedIndex = FindBranchSegmentStart("down");
        _lastSelectedId = SelectedEntryId;
    }

    public void SetFilterMode(TerminalTreeFilterMode mode)
    {
        _filterMode = mode;
        _foldedNodes.Clear();
        ApplyFilter();
    }

    public void ToggleFilterMode(TerminalTreeFilterMode mode) =>
        SetFilterMode(_filterMode == mode ? TerminalTreeFilterMode.Default : mode);

    public void CycleFilter(int direction)
    {
        TerminalTreeFilterMode[] modes = [TerminalTreeFilterMode.Default, TerminalTreeFilterMode.NoTools,
            TerminalTreeFilterMode.UserOnly, TerminalTreeFilterMode.LabeledOnly, TerminalTreeFilterMode.All];
        var current = Array.IndexOf(modes, _filterMode);
        var next = direction >= 0 ? (current + 1) % modes.Length : (current - 1 + modes.Length) % modes.Length;
        SetFilterMode(modes[next]);
    }

    public void AppendSearchText(string text)
    {
        if (string.IsNullOrEmpty(text)) return;
        _searchQuery += text;
        _foldedNodes.Clear();
        ApplyFilter();
    }

    public void SearchBackspace()
    {
        if (_searchQuery.Length == 0) return;
        _searchQuery = _searchQuery[..^1];
        _foldedNodes.Clear();
        ApplyFilter();
    }

    /// <summary>Escape with an active search clears search and folding; otherwise the navigator closes.</summary>
    public bool TryEscape()
    {
        if (_searchQuery.Length == 0) return false;
        _searchQuery = "";
        _foldedNodes.Clear();
        ApplyFilter();
        return true;
    }

    public void ToggleLabelTimestamps() => ShowLabelTimestamps = !ShowLabelTimestamps;

    /// <summary>Select an entry by id, used when re-opening the navigator after a branch choice.</summary>
    public void SelectById(string entryId)
    {
        _selectedIndex = FindNearestVisibleIndex(entryId);
        _lastSelectedId = _filtered.Count > 0 ? _filtered[_selectedIndex].Node.Entry.Id : _lastSelectedId;
    }

    private void ProjectLabels()
    {
        var labels = new Dictionary<string, (string Label, string? Timestamp)>(StringComparer.Ordinal);
        foreach (var node in _nodes)
        {
            if (node.Entry.Type != "label") continue;
            var payload = node.Entry.Payload;
            if (payload.ValueKind != JsonValueKind.Object) continue;
            if (!payload.TryGetProperty("targetId", out var target) || target.ValueKind != JsonValueKind.String) continue;
            var targetId = target.GetString()!;
            var label = payload.TryGetProperty("label", out var value) && value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;
            if (string.IsNullOrEmpty(label)) labels.Remove(targetId);
            else labels[targetId] = (label, node.Entry.Timestamp.ToString("O"));
        }
        foreach (var node in _nodes)
        {
            if (labels.TryGetValue(node.Entry.Id, out var label))
            {
                node.Label = label.Label;
                node.LabelTimestamp = label.Timestamp;
            }
        }
    }

    private void BuildActivePath()
    {
        _activePath.Clear();
        if (_currentLeafId is null) return;
        string? currentId = _currentLeafId;
        while (currentId is not null)
        {
            _activePath.Add(currentId);
            if (!_byId.TryGetValue(currentId, out var node)) break;
            currentId = node.Entry.ParentId;
        }
    }

    private void Flatten()
    {
        _flatNodes = [];
        var roots = BuildOrderedRoots();
        var containsActive = ContainsActiveMap(roots);
        _multipleRoots = roots.Count > 1;

        var multipleRoots = _multipleRoots;
        var orderedRoots = PrioritizeActive(roots, containsActive);
        var stack = new Stack<(TerminalTreeNode Node, int Indent, bool JustBranched, bool ShowConnector, bool IsLast,
            IReadOnlyList<TreeGutter> Gutters, bool IsVirtualRootChild)>();
        for (var index = orderedRoots.Length - 1; index >= 0; index--)
        {
            var isLast = index == orderedRoots.Length - 1;
            stack.Push((orderedRoots[index], multipleRoots ? 1 : 0, multipleRoots, multipleRoots, isLast, [], multipleRoots));
        }

        while (stack.Count > 0)
        {
            var (node, indent, justBranched, showConnector, isLast, gutters, isVirtualRootChild) = stack.Pop();
            _flatNodes.Add(new TerminalTreeFlatNode
            {
                Node = node,
                Indent = indent,
                ShowConnector = showConnector,
                IsLast = isLast,
                Gutters = gutters,
                IsVirtualRootChild = isVirtualRootChild
            });

            var children = ChildrenOf(node);
            var multipleChildren = children.Length > 1;
            var orderedChildren = PrioritizeActive(children, containsActive);

            int childIndent;
            if (multipleChildren) childIndent = indent + 1;
            else if (justBranched && indent > 0) childIndent = indent + 1;
            else childIndent = indent;

            var connectorDisplayed = showConnector && !isVirtualRootChild;
            var currentDisplayIndent = _multipleRoots ? Math.Max(0, indent - 1) : indent;
            var connectorPosition = Math.Max(0, currentDisplayIndent - 1);
            var childGutters = connectorDisplayed
                ? gutters.Append(new TreeGutter(connectorPosition, !isLast)).ToArray()
                : gutters;

            for (var index = orderedChildren.Length - 1; index >= 0; index--)
            {
                var childIsLast = index == orderedChildren.Length - 1;
                stack.Push((orderedChildren[index], childIndent, multipleChildren, multipleChildren,
                    childIsLast, childGutters, false));
            }
        }
    }

    private void IndexChildren()
    {
        _childrenById.Clear();
        foreach (var node in _nodes)
        {
            if (node.Entry.ParentId is { } parent && _byId.ContainsKey(parent))
            {
                if (!_childrenById.TryGetValue(parent, out var children))
                    _childrenById[parent] = children = [];
                children.Add(node);
            }
        }
        foreach (var children in _childrenById.Values)
            SortChildren(children);
    }

    private List<TerminalTreeNode> BuildOrderedRoots()
    {
        // Pi keeps roots in file order (append order), prioritizing the one with the active branch.
        var roots = new List<TerminalTreeNode>();
        foreach (var node in _nodes)
            if (node.Entry.ParentId is not { } parent || !_byId.ContainsKey(parent))
                roots.Add(node);
        return roots;
    }

    private TerminalTreeNode[] ChildrenOf(TerminalTreeNode node) =>
        _childrenById.GetValueOrDefault(node.Entry.Id)?.ToArray() ?? [];

    private static void SortChildren(List<TerminalTreeNode> children)
    {
        children.Sort((left, right) =>
        {
            var byTime = left.Entry.Timestamp.CompareTo(right.Entry.Timestamp);
            return byTime != 0 ? byTime : left.AppendIndex.CompareTo(right.AppendIndex);
        });
    }

    private Dictionary<TerminalTreeNode, bool> ContainsActiveMap(IReadOnlyList<TerminalTreeNode> roots)
    {
        var containsActive = new Dictionary<TerminalTreeNode, bool>(ReferenceEqualityComparer.Instance);
        foreach (var node in _nodes) containsActive[node] = false;
        if (_currentLeafId is not null)
        {
            var preOrder = new List<TerminalTreeNode>();
            var pending = new Stack<TerminalTreeNode>(roots.Reverse());
            while (pending.Count > 0)
            {
                var node = pending.Pop();
                preOrder.Add(node);
                foreach (var child in ChildrenOf(node).Reverse()) pending.Push(child);
            }
            for (var index = preOrder.Count - 1; index >= 0; index--)
            {
                var node = preOrder[index];
                var has = _currentLeafId is { } leaf && node.Entry.Id == leaf;
                foreach (var child in ChildrenOf(node))
                    if (containsActive.GetValueOrDefault(child))
                        has = true;
                containsActive[node] = has;
            }
        }
        return containsActive;
    }

    private static TerminalTreeNode[] PrioritizeActive(IReadOnlyList<TerminalTreeNode> nodes,
        IReadOnlyDictionary<TerminalTreeNode, bool> containsActive)
    {
        var prioritized = new List<TerminalTreeNode>(nodes.Count);
        var rest = new List<TerminalTreeNode>(nodes.Count);
        foreach (var node in nodes)
            (containsActive.GetValueOrDefault(node) ? prioritized : rest).Add(node);
        return prioritized.Concat(rest).ToArray();
    }

    private void ApplyFilter()
    {
        // Preserve the current selection across filter rebuilds.
        if (_filtered.Count > 0)
            _lastSelectedId = _filtered[Math.Clamp(_selectedIndex, 0, _filtered.Count - 1)].Node.Entry.Id;

        var searchTokens = _searchQuery
            .ToLowerInvariant()
            .Split([' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries);

        _filtered = _flatNodes.Where(flatNode =>
        {
            var entry = flatNode.Node.Entry;
            if (!_decoded.TryGetValue(entry.Id, out var info)) return false;
            if (info.IsBookkeeping) return false;
            var isCurrentLeaf = entry.Id == _currentLeafId;

            // Tool-call-only assistant messages without text and without an error/abort stop are
            // hidden; the current leaf always stays visible.
            if (info.DisplayKind == "assistant" && !isCurrentLeaf && !info.HasText && !info.HasErrorStop)
                return false;

            var passes = _filterMode switch
            {
                TerminalTreeFilterMode.UserOnly => info.DisplayKind == "user",
                TerminalTreeFilterMode.NoTools => !info.IsSettingsEntry && !info.IsToolResult,
                TerminalTreeFilterMode.LabeledOnly => flatNode.Node.Label is not null,
                TerminalTreeFilterMode.All => true,
                _ => !info.IsSettingsEntry
            };
            if (!passes) return false;

            if (searchTokens.Length == 0) return true;
            var searchable = flatNode.Node.Label is { } nodeLabel
                ? nodeLabel.ToLowerInvariant() + " " + info.SearchableText
                : info.SearchableText;
            return searchTokens.All(token => searchable.Contains(token, StringComparison.Ordinal));
        }).ToList();

        // Hide descendants of folded nodes.
        if (_foldedNodes.Count > 0)
        {
            var skip = new HashSet<string>(StringComparer.Ordinal);
            foreach (var flatNode in _flatNodes)
            {
                var (id, parentId) = (flatNode.Node.Entry.Id, flatNode.Node.Entry.ParentId);
                if (parentId is not null && (_foldedNodes.Contains(parentId) || skip.Contains(parentId)))
                    skip.Add(id);
            }
            _filtered = _filtered.Where(flatNode => !skip.Contains(flatNode.Node.Entry.Id)).ToList();
        }

        RecalculateVisualStructure();

        // Restore selection on the nearest visible ancestor, or clamp it.
        if (_lastSelectedId is not null)
            _selectedIndex = FindNearestVisibleIndex(_lastSelectedId);
        else if (_selectedIndex >= _filtered.Count)
            _selectedIndex = Math.Max(0, _filtered.Count - 1);

        if (_filtered.Count > 0)
            _lastSelectedId = _filtered[_selectedIndex].Node.Entry.Id;
    }

    private void RecalculateVisualStructure()
    {
        if (_filtered.Count == 0)
        {
            _visibleParent = new Dictionary<string, TerminalTreeNode?>(StringComparer.Ordinal);
            _visibleChildren = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            return;
        }

        var visibleIds = new HashSet<string>(_filtered.Select(node => node.Node.Entry.Id), StringComparer.Ordinal);
        var visibleParent = new Dictionary<string, TerminalTreeNode?>(StringComparer.Ordinal);
        var visibleChildren = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        var visibleRoots = new List<string>();

        string? FindVisibleAncestor(string nodeId)
        {
            var currentId = _byId.TryGetValue(nodeId, out var node) ? node.Entry.ParentId : null;
            while (currentId is not null)
            {
                if (visibleIds.Contains(currentId)) return currentId;
                currentId = _byId.TryGetValue(currentId, out var parent) ? parent.Entry.ParentId : null;
            }
            return null;
        }

        foreach (var flatNode in _filtered)
        {
            var nodeId = flatNode.Node.Entry.Id;
            var ancestor = FindVisibleAncestor(nodeId);
            visibleParent[nodeId] = ancestor is not null && _byId.TryGetValue(ancestor, out var ancestorNode)
                ? ancestorNode
                : null;
            if (ancestor is not null)
            {
                if (!visibleChildren.TryGetValue(ancestor, out var siblings))
                    visibleChildren[ancestor] = siblings = [];
                siblings.Add(nodeId);
            }
            else
            {
                visibleRoots.Add(nodeId);
            }
        }

        _visibleParent = visibleParent;
        _visibleChildren = visibleChildren;

        var filteredById = _filtered.ToDictionary(node => node.Node.Entry.Id, StringComparer.Ordinal);
        _multipleRoots = visibleRoots.Count > 1;

        var stack = new Stack<(string NodeId, int Indent, bool JustBranched, bool ShowConnector, bool IsLast,
            IReadOnlyList<TreeGutter> Gutters, bool IsVirtualRootChild)>();
        for (var index = visibleRoots.Count - 1; index >= 0; index--)
        {
            var isLast = index == visibleRoots.Count - 1;
            stack.Push((visibleRoots[index], _multipleRoots ? 1 : 0, _multipleRoots, _multipleRoots, isLast, [],
                _multipleRoots));
        }

        // visibleRoots preserves the filtered DFS order, which matches Pi's stable ordering.

        while (stack.Count > 0)
        {
            var (nodeId, indent, justBranched, showConnector, isLast, gutters, isVirtualRootChild) = stack.Pop();
            if (!filteredById.TryGetValue(nodeId, out var flatNode)) continue;

            flatNode.Indent = indent;
            flatNode.ShowConnector = showConnector;
            flatNode.IsLast = isLast;
            flatNode.Gutters = gutters;
            flatNode.IsVirtualRootChild = isVirtualRootChild;

            var children = visibleChildren.GetValueOrDefault(nodeId) ?? [];
            var multipleChildren = children.Count > 1;

            int childIndent;
            if (multipleChildren) childIndent = indent + 1;
            else if (justBranched && indent > 0) childIndent = indent + 1;
            else childIndent = indent;

            var connectorDisplayed = showConnector && !isVirtualRootChild;
            var currentDisplayIndent = _multipleRoots ? Math.Max(0, indent - 1) : indent;
            var connectorPosition = Math.Max(0, currentDisplayIndent - 1);
            var childGutters = connectorDisplayed
                ? gutters.Append(new TreeGutter(connectorPosition, !isLast)).ToArray()
                : gutters;

            for (var index = children.Count - 1; index >= 0; index--)
            {
                var childIsLast = index == children.Count - 1;
                stack.Push((children[index], childIndent, multipleChildren, multipleChildren, childIsLast,
                    childGutters, false));
            }
        }
    }

    private int FindNearestVisibleIndex(string? entryId)
    {
        if (_filtered.Count == 0) return 0;
        var visibleIdToIndex = new Dictionary<string, int>(_filtered.Count, StringComparer.Ordinal);
        for (var index = 0; index < _filtered.Count; index++)
            visibleIdToIndex[_filtered[index].Node.Entry.Id] = index;

        string? currentId = entryId;
        while (currentId is not null)
        {
            if (visibleIdToIndex.TryGetValue(currentId, out var index)) return index;
            if (!_byId.TryGetValue(currentId, out var node)) break;
            currentId = node.Entry.ParentId;
        }

        return _filtered.Count - 1;
    }

    internal bool IsFoldable(string entryId)
    {
        if (!_visibleChildren.TryGetValue(entryId, out var children) || children.Count == 0) return false;
        if (!_visibleParent.TryGetValue(entryId, out var parent) || parent is null) return true;
        return _visibleChildren.TryGetValue(parent.Entry.Id, out var siblings) && siblings.Count > 1;
    }

    internal bool IsFoldableForSelection()
    {
        var currentId = SelectedEntryId;
        return currentId is not null && IsFoldable(currentId);
    }

    private int FindBranchSegmentStart(string direction)
    {
        var selectedId = SelectedEntryId;
        if (selectedId is null) return _selectedIndex;

        var indexByEntryId = new Dictionary<string, int>(_filtered.Count, StringComparer.Ordinal);
        for (var index = 0; index < _filtered.Count; index++)
            indexByEntryId[_filtered[index].Node.Entry.Id] = index;

        var currentId = selectedId;
        if (direction == "down")
        {
            while (true)
            {
                var children = _visibleChildren.GetValueOrDefault(currentId) ?? [];
                if (children.Count == 0) return indexByEntryId[currentId];
                if (children.Count > 1) return indexByEntryId[children[0]];
                currentId = children[0];
            }
        }

        while (true)
        {
            string? parentId = _visibleParent.TryGetValue(currentId, out var parent) ? parent?.Entry.Id : null;
            if (parentId is null) return indexByEntryId[currentId];
            var children = _visibleChildren.GetValueOrDefault(parentId) ?? [];
            if (children.Count > 1)
            {
                var segmentStart = indexByEntryId[currentId];
                if (segmentStart < _selectedIndex) return segmentStart;
            }
            currentId = parentId;
        }
    }
}
