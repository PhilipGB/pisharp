using System.Globalization;
using System.Text;

namespace PiSharp.Cli.Tui;

/// <summary>Owns transcript search state, match cycling, ANSI projection, and highlighting.</summary>
internal sealed class TranscriptSearchController
{
    private const int MaxMatches = 10_000;
    private string _query = "";
    private int _selection;
    private int _selectionAnchorOffset;
    private bool _selectAtAnchor;
    private int _selectedTextStart = -1;
    private int _selectedTextEnd = -1;
    private int _matchCount;
    private bool _hasMoreMatches;

    public string Query => _query;
    public int SelectedTextStart => _selectedTextStart;
    public int SelectedTextEnd => _selectedTextEnd;
    public TranscriptSearchState State => new(_matchCount, _matchCount == 0 ? 0 : _selection + 1, _hasMoreMatches);

    public void SetQuery(string query, int direction, int anchorOffset = 0)
    {
        if (!query.Equals(_query, StringComparison.Ordinal))
        {
            _selectionAnchorOffset = Math.Max(0, anchorOffset);
            _selectAtAnchor = true;
            _selection = 0;
        }
        else if (direction != 0 && _matchCount > 0)
            _selection = ((_selection + direction) % _matchCount + _matchCount) % _matchCount;
        _query = query;
    }

    public void Clear()
    {
        _query = "";
        _selection = 0;
        _selectionAnchorOffset = 0;
        _selectAtAnchor = false;
        _selectedTextStart = -1;
        _selectedTextEnd = -1;
        _matchCount = 0;
        _hasMoreMatches = false;
    }

    public TranscriptSearchResult Highlight(string text, TerminalTheme? theme = null)
    {
        if (_query.Length == 0)
        {
            _matchCount = 0;
            _hasMoreMatches = false;
            _selectedTextStart = -1;
            _selectedTextEnd = -1;
            return new(text, 0, -1, -1);
        }

        var projection = CreateProjection(text);
        var matches = FindMatches(projection, _query, out _hasMoreMatches);
        _matchCount = matches.Count;
        if (_matchCount == 0)
        {
            _selection = 0;
            _hasMoreMatches = false;
            _selectedTextStart = -1;
            _selectedTextEnd = -1;
            _selectAtAnchor = false;
            return new(text, 0, -1, -1);
        }

        if (_selectAtAnchor)
        {
            var anchoredMatch = matches.FindIndex(match => match.RawStart >= _selectionAnchorOffset);
            _selection = anchoredMatch >= 0 ? anchoredMatch : 0;
            _selectAtAnchor = false;
        }

        _selection = Math.Clamp(_selection, 0, _matchCount - 1);
        var selected = matches[_selection];
        _selectedTextStart = selected.RawStart;
        _selectedTextEnd = selected.RawEnd;
        return new(HighlightMatches(text, matches, _selection, theme ?? TerminalTheme.Default),
            _matchCount, selected.RawStart, selected.RawEnd);
    }

    private static SearchProjection CreateProjection(string text)
    {
        var visible = new StringBuilder(text.Length);
        var rawOffsets = new List<int>(text.Length + 1);
        for (var offset = 0; offset < text.Length;)
        {
            if (TerminalImageRenderer.TryReadMarker(text, offset, out var markerLength, out _, out _))
            {
                offset += markerLength;
                continue;
            }
            if (TerminalTextLayout.TryReadEscape(text, offset, out var sequenceLength))
            {
                offset += sequenceLength;
                continue;
            }
            var element = StringInfo.GetNextTextElement(text, offset);
            for (var index = 0; index < element.Length; index++) rawOffsets.Add(offset + index);
            visible.Append(element);
            offset += element.Length;
        }
        rawOffsets.Add(text.Length);
        return new(visible.ToString(), rawOffsets.ToArray());
    }

    private static List<SearchMatch> FindMatches(SearchProjection projection, string query, out bool hasMore)
    {
        var matches = new List<SearchMatch>();
        hasMore = false;
        var cursor = 0;
        while (cursor <= projection.Text.Length - query.Length)
        {
            var start = projection.Text.IndexOf(query, cursor, StringComparison.OrdinalIgnoreCase);
            if (start < 0) break;
            if (matches.Count == MaxMatches)
            {
                hasMore = true;
                break;
            }
            var end = start + query.Length;
            matches.Add(new(projection.RawOffsets[start], projection.RawOffsets[end]));
            cursor = Math.Max(start + 1, end);
        }
        return matches;
    }

    private static string HighlightMatches(string text, IReadOnlyList<SearchMatch> matches, int selected,
        TerminalTheme theme)
    {
        var output = new StringBuilder(text.Length + matches.Count * 48);
        var activeSgr = new List<string>();
        var cursor = 0;
        for (var index = 0; index < matches.Count; index++)
        {
            var match = matches[index];
            AppendSource(text, cursor, match.RawStart, output, activeSgr, null);
            var current = index == selected;
            var matchStyle = theme.Fg("searchMatchText") + theme.Bg("searchMatchBg") +
                (current ? "\u001b[1m\u001b[7m" : "\u001b[4m");
            output.Append(matchStyle);
            AppendSource(text, match.RawStart, match.RawEnd, output, activeSgr, matchStyle);
            output.Append("\u001b[0m");
            foreach (var sgr in activeSgr) output.Append(sgr);
            cursor = match.RawEnd;
        }
        AppendSource(text, cursor, text.Length, output, activeSgr, null);
        return output.ToString();
    }

    private static void AppendSource(string text, int start, int end, StringBuilder output,
        List<string> activeSgr, string? styleAfterSgr)
    {
        for (var offset = start; offset < end;)
        {
            if (TerminalTextLayout.TryReadEscape(text, offset, out var length, out var kind, out var payload))
            {
                output.Append(text, offset, length);
                if (kind == TerminalTextLayout.EscapeKind.Sgr)
                {
                    if (payload.Length == 0 || payload.Split(';').Any(parameter => parameter.Length == 0 || parameter == "0"))
                        activeSgr.Clear();
                    activeSgr.Add(text.Substring(offset, length));
                    if (styleAfterSgr is not null) output.Append(styleAfterSgr);
                }
                offset += length;
                continue;
            }

            output.Append(text[offset]);
            offset++;
        }
    }

    private sealed record SearchProjection(string Text, int[] RawOffsets);
    private readonly record struct SearchMatch(int RawStart, int RawEnd);
}

internal readonly record struct TranscriptSearchState(int MatchCount, int SelectedMatch, bool HasMoreMatches);
internal readonly record struct TranscriptSearchResult(
    string HighlightedText, int MatchCount, int SelectedTextStart, int SelectedTextEnd);
