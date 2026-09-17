using System.Text.Json;

namespace PiSharp.Core;

public sealed class SessionDocument
{
    private readonly List<SessionTurn> _turns;
    private readonly List<SessionEntry> _entries;

    public SessionDocument(string filePath, SessionHeader header, IEnumerable<SessionTurn>? turns = null)
    {
        FilePath = Path.GetFullPath(filePath);
        Header = header;
        _turns = turns?.ToList() ?? [];
        _entries = [];
        ValidateTurns();
    }

    internal SessionDocument(string filePath, PiSessionHeader header, IEnumerable<SessionEntry> entries)
    {
        FilePath = Path.GetFullPath(filePath);
        PiHeader = header;
        Header = SessionHeader.FromPi(header);
        _entries = entries.ToList();
        _turns = [];
        ValidateEntries();
    }

    public string FilePath { get; }

    public SessionHeader Header { get; }

    public PiSessionHeader? PiHeader { get; }

    public bool IsPiV3 => PiHeader is not null;

    public IReadOnlyList<SessionTurn> Turns => IsPiV3 ? ProjectTurns() : _turns;

    public IReadOnlyList<SessionEntry> Entries => _entries;

    /// <summary>Gets the durable leaf represented by the final entry in the file.</summary>
    public string? LatestEntryId => _entries.Count == 0 ? null : _entries[^1].Id;

    public string? Name => IsPiV3
        ? _entries.OfType<SessionInfoEntry>().LastOrDefault()?.Name
        : null;

    public SessionTurn? LatestTurn => Turns.Count == 0 ? null : Turns[^1];

    /// <summary>
    /// Returns the effective label for an entry, applying label changes in file order so the
    /// most recent change wins. A blank label clears a previous one and yields null.
    /// </summary>
    public string? GetLabel(string entryId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(entryId);
        string? label = null;
        foreach (var entry in _entries)
        {
            if (entry is LabelEntry change &&
                string.Equals(change.TargetId, entryId, StringComparison.OrdinalIgnoreCase))
            {
                label = string.IsNullOrWhiteSpace(change.Label) ? null : change.Label;
            }
        }

        return label;
    }

    /// <summary>Resolves any Pi v3 entry by an exact id or an unambiguous prefix.</summary>
    public SessionEntry ResolveEntry(string idOrPrefix)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(idOrPrefix);
        var matches = _entries
            .Where(entry => entry.Id.StartsWith(idOrPrefix, StringComparison.OrdinalIgnoreCase))
            .ToList();
        return matches.Count switch
        {
            0 => throw new KeyNotFoundException($"No session entry matches '{idOrPrefix}'."),
            1 => matches[0],
            _ => throw new InvalidOperationException($"Entry id prefix '{idOrPrefix}' is ambiguous."),
        };
    }

    /// <summary>Returns the last projected turn on the selected Pi v3 path.</summary>
    public SessionTurn? GetLatestTurnOnPath(string? entryId)
    {
        if (!IsPiV3)
        {
            var legacyPath = GetActivePath(entryId);
            return legacyPath.Count == 0 ? null : legacyPath[^1];
        }

        var path = GetActiveEntryPath(entryId);
        var pathIds = path.Select(entry => entry.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var projected = ProjectTurns().Where(turn => pathIds.Contains(turn.Id)).ToArray();
        return projected.Length == 0 ? null : projected[^1];
    }

    /// <summary>Returns context-visible entries with the latest compaction boundary applied.</summary>
    public IReadOnlyList<SessionEntry> GetActiveContextEntries(string? entryId) =>
        IsPiV3
            ? PiCompactionPlanner.BuildContextEntries(GetActiveEntryPath(entryId))
            : [];

    public SessionStatistics GetStatistics()
    {
        if (!IsPiV3)
        {
            var messageCount = _turns.Count * 2;
            return new SessionStatistics(
                Header.SessionId,
                null,
                FilePath,
                _turns.Count,
                _turns.Count,
                0,
                0,
                messageCount);
        }

        var messages = _entries.OfType<MessageEntry>().ToArray();
        var users = messages.Count(entry => RoleIs(entry, "user"));
        var assistants = messages.Count(entry => RoleIs(entry, "assistant"));
        var toolResults = messages.Count(entry => RoleIs(entry, "toolResult"));
        var toolCalls = messages
            .Where(entry => RoleIs(entry, "assistant"))
            .Sum(entry => CountContentType(entry.Message, "toolCall"));
        return new SessionStatistics(
            Header.SessionId,
            Name,
            FilePath,
            users,
            assistants,
            toolCalls,
            toolResults,
            messages.Length);
    }

    public void Add(SessionTurn turn)
    {
        EnsureLegacy();
        if (_turns.Any(existing => existing.Id == turn.Id))
        {
            throw new InvalidDataException($"Duplicate session turn id: {turn.Id}");
        }

        if (turn.ParentId is not null && _turns.All(existing => existing.Id != turn.ParentId))
        {
            throw new InvalidDataException($"Parent turn does not exist: {turn.ParentId}");
        }

        _turns.Add(turn);
    }

    internal void AddEntries(IEnumerable<SessionEntry> entries)
    {
        if (!IsPiV3)
        {
            throw new InvalidOperationException("Typed entries can only be added to a Pi v3 session.");
        }

        foreach (var entry in entries)
        {
            if (_entries.Any(existing => existing.Id == entry.Id))
            {
                throw new InvalidDataException($"Duplicate session entry id: {entry.Id}");
            }

            if (entry.ParentId is not null && _entries.All(existing => existing.Id != entry.ParentId))
            {
                throw new InvalidDataException($"Parent session entry does not exist: {entry.ParentId}");
            }

            _entries.Add(entry);
        }
    }

    public SessionTurn ResolveTurn(string idOrPrefix)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(idOrPrefix);
        var matches = Turns
            .Where(turn => turn.Id.StartsWith(idOrPrefix, StringComparison.OrdinalIgnoreCase))
            .ToList();

        return matches.Count switch
        {
            0 => throw new KeyNotFoundException($"No session turn matches '{idOrPrefix}'."),
            1 => matches[0],
            _ => throw new InvalidOperationException($"Turn id prefix '{idOrPrefix}' is ambiguous."),
        };
    }

    public IReadOnlyList<SessionTurn> GetActivePath(string? turnId)
    {
        if (turnId is null)
        {
            return [];
        }

        var byId = Turns.ToDictionary(turn => turn.Id, StringComparer.OrdinalIgnoreCase);
        if (!byId.TryGetValue(turnId, out var current))
        {
            throw new KeyNotFoundException($"Unknown session turn: {turnId}");
        }

        var path = new List<SessionTurn>();
        while (true)
        {
            path.Add(current);
            var parentId = current.ParentId;
            if (parentId is null)
            {
                break;
            }

            if (!byId.TryGetValue(parentId, out var parent))
            {
                throw new InvalidDataException($"Session turn '{current.Id}' references missing parent '{parentId}'.");
            }

            current = parent;
        }

        path.Reverse();
        return path;
    }

    /// <summary>Gets the chronological leaf belonging to a projected assistant turn.</summary>
    public string? GetTurnLeafEntryId(string turnId)
    {
        var byParent = _entries
            .Where(entry => entry.ParentId is not null)
            .GroupBy(entry => entry.ParentId!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.OrdinalIgnoreCase);
        var currentId = turnId;
        while (byParent.TryGetValue(currentId, out var children))
        {
            var child = children.FirstOrDefault(entry =>
                entry is not MessageEntry message || !RoleIs(message, "user"));
            if (child is null)
            {
                break;
            }
            currentId = child.Id;
        }
        return currentId;
    }

    /// <summary>Returns the root-to-leaf path for a Pi v3 entry.</summary>
    public IReadOnlyList<SessionEntry> GetActiveEntryPath(string? entryId)
    {
        if (entryId is null)
        {
            return [];
        }

        var byId = _entries.ToDictionary(entry => entry.Id, StringComparer.OrdinalIgnoreCase);
        if (!byId.TryGetValue(entryId, out var current))
        {
            throw new KeyNotFoundException($"Unknown session entry: {entryId}");
        }

        var path = new List<SessionEntry>();
        while (true)
        {
            path.Add(current);
            if (current.ParentId is null)
            {
                break;
            }

            if (!byId.TryGetValue(current.ParentId, out current!))
            {
                throw new InvalidDataException($"Session entry references missing parent: {path[^1].ParentId}");
            }
        }

        path.Reverse();
        return path;
    }

    private IReadOnlyList<SessionTurn> ProjectTurns()
    {
        var byId = _entries.ToDictionary(entry => entry.Id, StringComparer.OrdinalIgnoreCase);
        var states = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
        foreach (var cache in _entries.OfType<CustomEntry>()
                     .Where(entry => entry.CustomType == SessionEntryTypes.AgentStateCache))
        {
            var owner = FindAncestor(cache, byId, entry => entry is MessageEntry message && RoleIs(message, "assistant"));
            if (owner is not null)
            {
                states[owner.Id] = cache.Data ?? EmptyObject();
            }
        }
        var turns = new List<SessionTurn>();
        foreach (var assistant in _entries.OfType<MessageEntry>().Where(entry => RoleIs(entry, "assistant")))
        {
            var user = FindAncestor(assistant, byId, entry => entry is MessageEntry message && RoleIs(message, "user"));
            if (user is not MessageEntry userMessage)
            {
                continue;
            }

            var parentAssistant = FindAncestor(userMessage, byId, entry => entry is MessageEntry message && RoleIs(message, "assistant"));
            var state = states.GetValueOrDefault(assistant.Id, EmptyObject());
            turns.Add(new SessionTurn(
                "turn",
                assistant.Id,
                parentAssistant?.Id,
                assistant.Timestamp,
                ExtractText(userMessage.Message),
                ExtractText(assistant.Message),
                state));
        }

        return turns;
    }

    private static SessionEntry? FindAncestor(
        SessionEntry entry,
        IReadOnlyDictionary<string, SessionEntry> byId,
        Func<SessionEntry, bool> predicate)
    {
        var parentId = entry.ParentId;
        while (parentId is not null && byId.TryGetValue(parentId, out var parent))
        {
            if (predicate(parent))
            {
                return parent;
            }
            parentId = parent.ParentId;
        }

        return null;
    }

    private static bool RoleIs(MessageEntry entry, string role) =>
        entry.Message.ValueKind == JsonValueKind.Object &&
        entry.Message.TryGetProperty("role", out var roleValue) &&
        string.Equals(roleValue.GetString(), role, StringComparison.Ordinal);

    private static int CountContentType(JsonElement message, string type)
    {
        if (!message.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array)
        {
            return 0;
        }

        return content.EnumerateArray().Count(item =>
            item.ValueKind == JsonValueKind.Object &&
            item.TryGetProperty("type", out var typeValue) &&
            string.Equals(typeValue.GetString(), type, StringComparison.Ordinal));
    }

    private static string ExtractText(JsonElement message)
    {
        if (message.ValueKind != JsonValueKind.Object || !message.TryGetProperty("content", out var content))
        {
            return string.Empty;
        }

        if (content.ValueKind == JsonValueKind.String)
        {
            return content.GetString() ?? string.Empty;
        }

        if (content.ValueKind != JsonValueKind.Array)
        {
            return content.ToString();
        }

        return string.Join(
            string.Empty,
            content.EnumerateArray()
                .Where(item => item.ValueKind == JsonValueKind.Object && item.TryGetProperty("text", out _))
                .Select(item => item.GetProperty("text").GetString() ?? string.Empty));
    }

    private static JsonElement EmptyObject()
    {
        using var document = JsonDocument.Parse("{}");
        return document.RootElement.Clone();
    }

    private void ValidateTurns()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var turn in _turns)
        {
            if (!seen.Add(turn.Id))
            {
                throw new InvalidDataException($"Duplicate session turn id: {turn.Id}");
            }

            if (turn.ParentId is not null && !seen.Contains(turn.ParentId))
            {
                throw new InvalidDataException($"Turn '{turn.Id}' references parent '{turn.ParentId}' before that parent exists.");
            }
        }
    }

    private void ValidateEntries()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in _entries)
        {
            if (!seen.Add(entry.Id))
            {
                throw new InvalidDataException($"Duplicate session entry id: {entry.Id}");
            }

            if (entry.ParentId is not null && !seen.Contains(entry.ParentId))
            {
                throw new InvalidDataException($"Entry '{entry.Id}' references parent '{entry.ParentId}' before that parent exists.");
            }
        }
    }

    private void EnsureLegacy()
    {
        if (IsPiV3)
        {
            throw new InvalidOperationException("Legacy turns cannot be added to a Pi v3 session.");
        }
    }
}
