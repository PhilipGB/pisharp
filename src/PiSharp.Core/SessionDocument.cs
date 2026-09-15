namespace PiSharp.Core;

public sealed class SessionDocument
{
    private readonly List<SessionTurn> _turns;

    public SessionDocument(string filePath, SessionHeader header, IEnumerable<SessionTurn>? turns = null)
    {
        FilePath = Path.GetFullPath(filePath);
        Header = header;
        _turns = turns?.ToList() ?? [];
        Validate();
    }

    public string FilePath { get; }

    public SessionHeader Header { get; }

    public IReadOnlyList<SessionTurn> Turns => _turns;

    public SessionTurn? LatestTurn => _turns.Count == 0 ? null : _turns[^1];

    public void Add(SessionTurn turn)
    {
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

    public SessionTurn ResolveTurn(string idOrPrefix)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(idOrPrefix);
        var matches = _turns
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

        var byId = _turns.ToDictionary(turn => turn.Id, StringComparer.OrdinalIgnoreCase);
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

    private void Validate()
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
}
