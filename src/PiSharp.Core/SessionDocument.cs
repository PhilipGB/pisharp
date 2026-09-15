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

    public SessionTurn? LatestTurn => Turns.Count == 0 ? null : Turns[^1];

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

    internal IReadOnlyList<SessionEntry> GetActiveEntryPath(string? entryId)
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
        var states = _entries
            .OfType<CustomEntry>()
            .Where(entry => entry.CustomType == SessionEntryTypes.AgentStateCache && entry.ParentId is not null)
            .ToDictionary(entry => entry.ParentId!, entry => entry.Data ?? EmptyObject(), StringComparer.OrdinalIgnoreCase);
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
