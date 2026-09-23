using System.Text.Json;

namespace PiSharp.Core;

/// <summary>Append-only conversation tree. Selecting a parent never deletes its descendants.</summary>
public sealed class ConversationTree
{
    private readonly Dictionary<string, ConversationNode> _byId = new(StringComparer.Ordinal);
    private readonly List<ConversationNode> _entries = [];
    public IReadOnlyList<ConversationNode> Entries => _entries;
    public string? HeadId { get; private set; }

    public ConversationTree(IEnumerable<ConversationNode>? entries = null, string? headId = null)
    {
        if (entries is not null)
            foreach (var entry in entries) Add(entry);
        Select(headId ?? _entries.LastOrDefault()?.Id);
    }

    public ConversationNode Append(string type, JsonElement payload, DateTimeOffset? at = null)
    {
        var entry = new ConversationNode(Guid.NewGuid().ToString("N"), HeadId, type, payload.Clone(), at ?? DateTimeOffset.UtcNow);
        Add(entry);
        HeadId = entry.Id;
        return entry;
    }

    public void Select(string? id)
    {
        if (id is not null && !_byId.ContainsKey(id)) throw new KeyNotFoundException($"No session entry with id {id}.");
        HeadId = id;
    }

    public IReadOnlyList<ConversationNode> ActivePath()
    {
        var path = new List<ConversationNode>();
        var id = HeadId;
        while (id is not null)
        {
            var entry = _byId[id];
            path.Add(entry);
            id = entry.ParentId;
        }
        path.Reverse();
        return path;
    }

    public ConversationTree CloneActivePath() => new(ActivePath(), HeadId);

    /// <summary>Copy one ancestor path through an entry (or an empty path for null).</summary>
    public ConversationTree ClonePath(string? headId)
    {
        if (headId is null) return new ConversationTree();
        if (!_byId.ContainsKey(headId)) throw new KeyNotFoundException($"No session entry with id {headId}.");
        var path = new List<ConversationNode>();
        for (var current = headId; current is not null; current = _byId[current].ParentId)
            path.Add(_byId[current]);
        path.Reverse();
        return new ConversationTree(path, headId);
    }

    private void Add(ConversationNode entry)
    {
        if (string.IsNullOrWhiteSpace(entry.Id) || string.IsNullOrWhiteSpace(entry.Type))
            throw new InvalidDataException("Session entry id and type are required.");
        if (_byId.ContainsKey(entry.Id)) throw new InvalidDataException($"Duplicate entry id: {entry.Id}");
        if (entry.ParentId is not null && !_byId.ContainsKey(entry.ParentId))
            throw new InvalidDataException($"Entry {entry.Id} refers to missing or forward parent {entry.ParentId}.");
        _byId.Add(entry.Id, entry);
        _entries.Add(entry);
    }
}

public sealed record ConversationNode(string Id, string? ParentId, string Type, JsonElement Payload, DateTimeOffset Timestamp);
