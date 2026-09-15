namespace PiSharp.Core;

/// <summary>In-memory prompt history with draft-preserving previous/next navigation.</summary>
public sealed class PromptHistory
{
    private const int MaximumEntries = 100;
    private readonly List<string> _entries = [];
    private int _index = -1;
    private string? _draft;

    /// <summary>Gets the newest entry first.</summary>
    public IReadOnlyList<string> Entries => _entries;

    /// <summary>Adds a non-empty prompt and leaves history navigation idle.</summary>
    public void Add(string text)
    {
        var normalized = text.Trim();
        if (normalized.Length == 0)
        {
            return;
        }
        if (_entries.Count == 0 || !string.Equals(_entries[0], normalized, StringComparison.Ordinal))
        {
            _entries.Insert(0, normalized);
            if (_entries.Count > MaximumEntries)
            {
                _entries.RemoveAt(_entries.Count - 1);
            }
        }

        ResetNavigation();
    }

    /// <summary>Moves to an older prompt, capturing the current draft on first use.</summary>
    public string? Previous(string currentDraft)
    {
        if (_entries.Count == 0)
        {
            return null;
        }
        if (_index == -1)
        {
            _draft = currentDraft;
            _index = 0;
        }
        else
        {
            _index = Math.Min(_index + 1, _entries.Count - 1);
        }

        return _entries[_index];
    }

    /// <summary>Moves to a newer prompt or restores the captured draft.</summary>
    public string? Next()
    {
        if (_index == -1)
        {
            return null;
        }
        if (_index == 0)
        {
            var draft = _draft;
            ResetNavigation();
            return draft;
        }

        _index--;
        return _entries[_index];
    }

    /// <summary>Leaves browsing mode without changing stored entries.</summary>
    public void ResetNavigation()
    {
        _index = -1;
        _draft = null;
    }
}
