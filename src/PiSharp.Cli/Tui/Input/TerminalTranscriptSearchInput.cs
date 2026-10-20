namespace PiSharp.Cli.Tui;

internal sealed class TerminalTranscriptSearchInput(EditorBuffer buffer, EditorKeymap keymap,
    Func<TerminalScreen?> getScreen)
{
    private bool _searchingTranscript;
    private string _savedDraft = "";
    private string _savedFooter = "";
    private int _savedCursor;
    private string _lastSearchQuery = "";

    public bool IsActive => _searchingTranscript;

    public void Start()
    {
        if (getScreen() is not { IsActive: true } screen) return;
        _savedFooter = screen.FooterText;
        lock (buffer.SyncRoot)
        {
            var snapshot = buffer.CaptureSnapshot();
            _savedDraft = snapshot.Text;
            _savedCursor = snapshot.Cursor;
            buffer.SetText(_lastSearchQuery);
        }
        _searchingTranscript = true;
        Update();
    }

    public bool HandleInput(TerminalInputEvent next)
    {
        if (getScreen() is not { IsActive: true })
        {
            Close();
            return true;
        }
        if (next.Key is { } key)
        {
            if (keymap.Matches("tui.altScreen.searchClose", key) || keymap.Matches("app.interrupt", key))
            {
                Close();
                return true;
            }
            if (keymap.Matches("tui.altScreen.searchNext", key))
            {
                Update(direction: 1);
                return true;
            }
            if (keymap.Matches("tui.altScreen.searchPrevious", key))
            {
                Update(direction: -1);
                return true;
            }
        }

        if (next.Text is not null) buffer.InsertText(next.Text);
        else if (next.Key is { } editKey) _ = buffer.Handle(editKey);
        Update();
        return true;
    }

    public void Update(int direction = 0)
    {
        if (getScreen() is not { IsActive: true } screen) return;
        var query = buffer.CaptureSnapshot().Text;
        var state = screen.SearchTranscript(query, direction);
        var count = state.HasMoreMatches ? $"{state.MatchCount}+" : state.MatchCount.ToString();
        var status = query.Length == 0 ? "type to search" : state.MatchCount == 0 ? "no matches" : $"{state.SelectedMatch}/{count}";
        screen.SetFooter($"Search: {query} · {status} · Enter next · Shift+Enter previous · Escape close");
    }

    public void Close()
    {
        lock (buffer.SyncRoot)
        {
            _lastSearchQuery = buffer.Text;
            _searchingTranscript = false;
            buffer.SetText(_savedDraft, _savedCursor);
        }
        getScreen()?.ClearTranscriptSearch();
        if (_savedFooter.Length > 0) getScreen()?.SetFooter(_savedFooter);
    }
}
