using System.Text.Json;

namespace PiSharp.Core;

/// <summary>Append-only conversation tree. Selecting a parent never deletes its descendants.</summary>
public sealed class ConversationTree
{
    private readonly object _gate = new();
    private readonly Dictionary<string, ConversationNode> _byId = new(StringComparer.Ordinal);
    private readonly List<ConversationNode> _entries = [];
    private string? _headId;
    public IReadOnlyList<ConversationNode> Entries { get { lock (_gate) return _entries.ToArray(); } }
    public string? HeadId { get { lock (_gate) return _headId; } }

    public ConversationTree(IEnumerable<ConversationNode>? entries = null, string? headId = null)
    {
        if (entries is not null)
            foreach (var entry in entries) Add(entry);
        Select(headId ?? _entries.LastOrDefault()?.Id);
    }

    public static ConversationTree FromEntries(IEnumerable<ConversationNode> entries, string? headId = null)
    {
        ArgumentNullException.ThrowIfNull(entries);
        var materialized = entries.ToArray();
        var tree = new ConversationTree();
        foreach (var entry in materialized) tree.Add(entry, requireParentAlreadyAdded: false);
        tree.ValidateLinksAndAcyclicity();
        tree.Select(headId ?? materialized.LastOrDefault()?.Id);
        return tree;
    }

    public ConversationNode Append(string type, JsonElement payload, DateTimeOffset? at = null)
    {
        lock (_gate)
        {
            var entry = new ConversationNode(Guid.NewGuid().ToString("N"), _headId, type, payload.Clone(), at ?? DateTimeOffset.UtcNow);
            Add(entry);
            _headId = entry.Id;
            return entry;
        }
    }

    public void RollbackAppend(ConversationNode entry, string? previousHeadId)
    {
        lock (_gate)
        {
            if (_entries.Count == 0 || !ReferenceEquals(_entries[^1], entry) || _headId != entry.Id ||
                entry.ParentId != previousHeadId)
                throw new InvalidOperationException("Only the latest append can be rolled back from its selected branch.");
            _entries.RemoveAt(_entries.Count - 1);
            _byId.Remove(entry.Id);
            _headId = previousHeadId;
        }
    }

    public void Select(string? id)
    {
        lock (_gate)
        {
            if (id is not null && !_byId.ContainsKey(id)) throw new KeyNotFoundException($"No session entry with id {id}.");
            _headId = id;
        }
    }

    public IReadOnlyList<ConversationNode> ActivePath()
    {
        lock (_gate)
        {
            var path = new List<ConversationNode>();
            var id = _headId;
            while (id is not null)
            {
                var entry = _byId[id];
                path.Add(entry);
                id = entry.ParentId;
            }
            path.Reverse();
            return path;
        }
    }

    public ConversationTree CloneActivePath()
    {
        lock (_gate) return new ConversationTree(ActivePath(), _headId);
    }

    public ConversationTree Clone()
    {
        lock (_gate) return FromEntries(_entries, _headId);
    }

    /// <summary>Copy one ancestor path through an entry (or an empty path for null).</summary>
    public ConversationTree ClonePath(string? headId)
    {
        lock (_gate)
        {
            if (headId is null) return new ConversationTree();
            if (!_byId.ContainsKey(headId)) throw new KeyNotFoundException($"No session entry with id {headId}.");
            var path = new List<ConversationNode>();
            for (var current = headId; current is not null; current = _byId[current].ParentId)
                path.Add(_byId[current]);
            path.Reverse();
            return new ConversationTree(path, headId);
        }
    }

    private void Add(ConversationNode entry, bool requireParentAlreadyAdded = true)
    {
        if (string.IsNullOrWhiteSpace(entry.Id) || string.IsNullOrWhiteSpace(entry.Type))
            throw new InvalidDataException("Session entry id and type are required.");
        if (_byId.ContainsKey(entry.Id)) throw new InvalidDataException($"Duplicate entry id: {entry.Id}");
        if (requireParentAlreadyAdded && entry.ParentId is not null && !_byId.ContainsKey(entry.ParentId))
            throw new InvalidDataException($"Entry {entry.Id} refers to missing or forward parent {entry.ParentId}.");
        _byId.Add(entry.Id, entry);
        _entries.Add(entry);
    }

    private void ValidateLinksAndAcyclicity()
    {
        foreach (var entry in _entries)
            if (entry.ParentId is not null && !_byId.ContainsKey(entry.ParentId))
                throw new InvalidDataException($"Entry {entry.Id} refers to missing parent {entry.ParentId}.");

        var states = new Dictionary<string, byte>(StringComparer.Ordinal);
        foreach (var entry in _entries)
        {
            if (states.GetValueOrDefault(entry.Id) == 2) continue;
            var path = new List<ConversationNode>();
            var current = entry;
            while (true)
            {
                var state = states.GetValueOrDefault(current.Id);
                if (state == 1)
                    throw new InvalidDataException($"Session entries contain a parent cycle at {current.Id}.");
                if (state == 2) break;
                states[current.Id] = 1;
                path.Add(current);
                if (current.ParentId is null) break;
                current = _byId[current.ParentId];
            }
            foreach (var node in path) states[node.Id] = 2;
        }
    }
}

public sealed record ConversationNode(string Id, string? ParentId, string Type, JsonElement Payload, DateTimeOffset Timestamp);
