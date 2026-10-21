using System.Globalization;
using System.Text;

namespace PiSharp.Cli.Tui;

internal sealed class TerminalTranscriptSearchInput(EditorBuffer buffer, EditorKeymap keymap,
    Func<TerminalScreen?> getScreen)
{
    private bool _searchingTranscript;
    private string _savedDraft = "";
    private int _savedCursor;
    private EditorBufferSnapshot _savedEditorSnapshot;
    private string _lastSearchQuery = "";

    public bool IsActive => _searchingTranscript;
    public EditorBufferSnapshot CaptureEditorSnapshotForDisplay() =>
        _searchingTranscript ? _savedEditorSnapshot : buffer.CaptureSnapshot();

    public void Start()
    {
        if (getScreen() is not { IsActive: true } screen) return;
        lock (buffer.SyncRoot)
        {
            var snapshot = buffer.CaptureSnapshot();
            _savedEditorSnapshot = snapshot;
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
        var snapshot = buffer.CaptureSnapshot();
        var query = snapshot.Text;
        var state = screen.SearchTranscript(query, direction);
        var (panel, cursorOffset) = RenderPanel(query, snapshot.Cursor, state, screen.TerminalWidth);
        var panelWidth = PanelWidth(screen.TerminalWidth);
        var panelLeft = Math.Max(0, screen.TerminalWidth - panelWidth - 1);
        screen.SetTranscriptSearchPanel(panel, panelLeft + 1 + cursorOffset);
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
        getScreen()?.SetTranscriptSearchPanel(null);
    }

    private (IReadOnlyList<string> Panel, int CursorOffset) RenderPanel(string query, int cursor,
        TranscriptSearchState state, int terminalWidth)
    {
        var width = PanelWidth(terminalWidth);
        if (width < 3) return ([], 0);
        var innerWidth = width - 2;
        var result = query.Length == 0 ? "" : state.MatchCount == 0 ? "No matches" :
            $"{state.SelectedMatch}/{(state.HasMoreMatches ? $"{state.MatchCount}+" : state.MatchCount)}";
        var resultText = result.Length == 0 ? "" : "\u001b[2m " +
            TerminalTranscriptViewport.Clip(result, Math.Max(0, innerWidth - 3)) + " \u001b[22m";
        var resultWidth = TerminalTextLayout.Width(resultText);
        var inputWidth = Math.Max(0, innerWidth - resultWidth);
        var input = RenderInput(query, cursor, inputWidth);
        var content = input.Text + new string(' ', Math.Max(0, inputWidth - TerminalTextLayout.Width(input.Text))) + resultText;
        content = TerminalTranscriptViewport.Clip(content, innerWidth);
        content += new string(' ', Math.Max(0, innerWidth - TerminalTextLayout.Width(content)));

        var previous = DisplayKey("tui.altScreen.searchPrevious", "Shift+Enter");
        var next = DisplayKey("tui.altScreen.searchNext", "Enter");
        var previousButton = "↑ " + previous;
        var nextButton = "↓ " + next;
        var buttons = previousButton + " · " + nextButton;
        if (TerminalTextLayout.Width(buttons) > Math.Max(0, innerWidth - 3))
            buttons = "↑ ↓";
        var buttonWidth = TerminalTextLayout.Width(buttons);
        var leftRule = Math.Max(0, innerWidth - buttonWidth - 3);
        var bottom = "└" + new string('─', leftRule) + (buttonWidth > 0 ? " " + buttons + " " : "") +
            "─┘";
        bottom = TerminalTranscriptViewport.Clip(bottom, width);
        bottom += new string(' ', Math.Max(0, width - TerminalTextLayout.Width(bottom)));

        return ([
            "┌" + new string('─', innerWidth) + "┐",
            "│" + content + "│",
            bottom
        ], input.CursorOffset);
    }

    private static int PanelWidth(int terminalWidth) =>
        Math.Min(Math.Max(1, terminalWidth - 2), Math.Max(32, (int)(terminalWidth * 0.4)));

    private static (string Text, int CursorOffset) RenderInput(string query, int cursor, int width)
    {
        var availableWidth = Math.Max(0, width - 1);
        if (availableWidth == 0) return (" ", 1);
        if (query.Length == 0)
        {
            var placeholder = TerminalTranscriptViewport.Clip("Find in transcript", availableWidth);
            var firstElementLength = placeholder.Length == 0 ? 0 :
                StringInfo.GetNextTextElement(placeholder, 0).Length;
            var first = firstElementLength == 0 ? " " : placeholder[..firstElementLength];
            var rest = firstElementLength == 0 ? "" : placeholder[firstElementLength..];
            var body = "\u001b[2m\u001b[7m" + first + "\u001b[27m" + rest + "\u001b[22m";
            return (FitInput(" " + body, width), 1);
        }

        var elements = new List<(string Text, int Start, int Width, int CellStart)>();
        var textElementEnumerator = StringInfo.GetTextElementEnumerator(query);
        var totalWidth = 0;
        while (textElementEnumerator.MoveNext())
        {
            var text = textElementEnumerator.GetTextElement();
            var elementWidth = Math.Max(0, TerminalCells.Width(text));
            elements.Add((text, textElementEnumerator.ElementIndex, elementWidth, totalWidth));
            totalWidth += elementWidth;
        }

        cursor = Math.Clamp(cursor, 0, query.Length);
        var cursorColumn = elements.Where(element => element.Start + element.Text.Length <= cursor)
            .Sum(element => element.Width);
        var visibleWidth = Math.Max(1, availableWidth);
        var startColumn = 0;
        var cursorDisplay = cursorColumn;
        if (totalWidth >= availableWidth)
        {
            var scrollWidth = cursor == query.Length ? Math.Max(0, availableWidth - 1) : availableWidth;
            var halfWidth = scrollWidth / 2;
            if (cursorColumn > totalWidth - halfWidth)
                startColumn = Math.Max(0, totalWidth - scrollWidth);
            else if (cursorColumn >= halfWidth)
                startColumn = Math.Max(0, cursorColumn - halfWidth);
            cursorDisplay = cursorColumn - startColumn;
            visibleWidth = scrollWidth;
        }

        var visible = elements.Where(element => element.CellStart >= startColumn &&
            element.CellStart + element.Width <= startColumn + visibleWidth).ToArray();
        var bodyBuilder = new StringBuilder();
        var currentColumn = 0;
        var cursorRendered = false;
        foreach (var element in visible)
        {
            if (!cursorRendered && currentColumn == cursorDisplay)
            {
                bodyBuilder.Append("\u001b[7m").Append(element.Text).Append("\u001b[27m");
                cursorRendered = true;
            }
            else bodyBuilder.Append(element.Text);
            currentColumn += element.Width;
        }
        if (!cursorRendered)
            bodyBuilder.Append("\u001b[7m \u001b[27m");
        return (FitInput(" " + bodyBuilder, width), 1 + cursorDisplay);
    }

    private static string FitInput(string input, int width)
    {
        var clipped = TerminalTranscriptViewport.Clip(input, width);
        return clipped + new string(' ', Math.Max(0, width - TerminalTextLayout.Width(clipped)));
    }

    private string DisplayKey(string action, string fallback) =>
        keymap.GetDisplayKeys(action)?.Split('/', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? fallback;
}
