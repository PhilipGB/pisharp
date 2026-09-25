namespace PiSharp.Cli.Tui;

/// <summary>Stores kill entries and rotates them for the editor's yank-pop action.</summary>
internal sealed class EditorKillRing
{
    private readonly List<string> _entries = [];

    public int Count => _entries.Count;

    public string? Peek() => _entries.Count == 0 ? null : _entries[^1];

    public void Push(string text, bool prepend, bool accumulate)
    {
        if (text.Length == 0) return;
        if (accumulate && _entries.Count > 0)
        {
            var previous = _entries[^1];
            _entries[^1] = prepend ? text + previous : previous + text;
        }
        else
        {
            _entries.Add(text);
        }
    }

    public void Rotate()
    {
        if (_entries.Count < 2) return;
        var latest = _entries[^1];
        _entries.RemoveAt(_entries.Count - 1);
        _entries.Insert(0, latest);
    }
}
