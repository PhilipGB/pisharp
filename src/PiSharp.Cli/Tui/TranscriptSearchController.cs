using System.Globalization;
using System.Text;

namespace PiSharp.Cli.Tui;

/// <summary>Owns transcript search state, match cycling, ANSI projection, and highlighting.</summary>
internal sealed class TranscriptSearchController
{
    private const int MaxMatches = 10_000;
    private string _query = "";
    private int _selection;
    private int _matchCount;
    private bool _hasMoreMatches;

    public string Query => _query;
    public TranscriptSearchState State => new(_matchCount, _matchCount == 0 ? 0 : _selection + 1, _hasMoreMatches);

    public void SetQuery(string query, int direction)
    {
        if (!query.Equals(_query, StringComparison.Ordinal)) _selection = 0;
        else if (direction != 0 && _matchCount > 0)
            _selection = ((_selection + direction) % _matchCount + _matchCount) % _matchCount;
        _query = query;
    }

    public void Clear()
    {
        _query = "";
        _selection = 0;
        _matchCount = 0;
        _hasMoreMatches = false;
    }

    public TranscriptSearchResult Highlight(string text)
    {
        if (_query.Length == 0)
        {
            _matchCount = 0;
            _hasMoreMatches = false;
            return new(text, 0, -1);
        }

        var projection = CreateProjection(text);
        var matches = FindMatches(projection, _query, out _hasMoreMatches);
        _matchCount = matches.Count;
        if (_matchCount == 0)
        {
            _selection = 0;
            _hasMoreMatches = false;
            return new(text, 0, -1);
        }

        _selection = Math.Clamp(_selection, 0, _matchCount - 1);
        var selected = matches[_selection];
        return new(HighlightMatches(text, matches, _selection), _matchCount, selected.TextStart);
    }

    private static SearchProjection CreateProjection(string text)
    {
        var visible = new StringBuilder(text.Length);
        var rawOffsets = new List<int>(text.Length + 1);
        for (var offset = 0; offset < text.Length;)
        {
            if (TryReadSgr(text, offset, out var sequenceLength))
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
            matches.Add(new(start, projection.RawOffsets[start], projection.RawOffsets[end]));
            cursor = Math.Max(start + 1, end);
        }
        return matches;
    }

    private static string HighlightMatches(string text, IReadOnlyList<SearchMatch> matches, int selected)
    {
        var output = new StringBuilder(text.Length + matches.Count * 12);
        var cursor = 0;
        for (var index = 0; index < matches.Count; index++)
        {
            var match = matches[index];
            output.Append(text, cursor, match.RawStart - cursor);
            output.Append(index == selected ? "\u001b[7;1m" : "\u001b[7m")
                .Append(text, match.RawStart, match.RawEnd - match.RawStart)
                .Append("\u001b[27m");
            cursor = match.RawEnd;
        }
        output.Append(text, cursor, text.Length - cursor);
        return output.ToString();
    }

    private static bool TryReadSgr(string text, int offset, out int length)
    {
        length = 0;
        if (offset + 2 >= text.Length || text[offset] != '\u001b' || text[offset + 1] != '[') return false;
        var end = text.IndexOf('m', offset + 2);
        if (end < 0) return false;
        for (var index = offset + 2; index < end; index++)
            if (text[index] is not (>= '0' and <= '9') and not ';') return false;
        length = end - offset + 1;
        return true;
    }

    private sealed record SearchProjection(string Text, int[] RawOffsets);
    private readonly record struct SearchMatch(int TextStart, int RawStart, int RawEnd);
}

internal readonly record struct TranscriptSearchState(int MatchCount, int SelectedMatch, bool HasMoreMatches);
internal readonly record struct TranscriptSearchResult(string HighlightedText, int MatchCount, int SelectedTextStart);
