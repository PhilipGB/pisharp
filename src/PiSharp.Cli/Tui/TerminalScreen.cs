using System.Globalization;
using System.Text;

namespace PiSharp.Cli.Tui;

/// <summary>Owns the active-run screen and composes transcript, status, and editor rows.</summary>
public sealed class TerminalScreen : IDisposable
{
    private const int MaxScreenTranscriptCharacters = 1_000_000;
    private const int MaxScreenTranscriptSegments = 10_000;
    private const int MaxRestoredTranscriptCharacters = 4_000_000;
    private const int MaxTranscriptScrollOffset = 60_000;
    private const int MaxTranscriptSearchMatches = 10_000;
    private const int MaxToolResultPreviewLines = 10;
    private readonly object _gate = new();
    private readonly TextWriter _originalOut;
    private readonly TextWriter _originalError;
    private readonly Func<int> _getColumns;
    private readonly Func<int> _getRows;
    private readonly List<CapturedChunk> _captured = [];
    private readonly List<TranscriptSegment> _transcript = [];
    private readonly ScreenWriter _out;
    private readonly ScreenWriter _error;
    private TextWriter? _installedOut;
    private TextWriter? _installedError;
    private string _editorText = "";
    private string _liveAssistant = "";
    private string _searchQuery = "";
    private int _editorCursor;
    private int _scrollOffset;
    private int _searchSelection;
    private int _searchMatchCount;
    private bool _searchHasMoreMatches;
    private bool _toolResultsExpanded;
    private string _footer = "Enter steers · follow-up queues · Escape aborts";
    private int _capturedCharacters;
    private int _transcriptCharacters;
    private bool _captureTruncated;
    private bool _activated;
    private volatile bool _active = true;

    public TerminalScreen(TextWriter originalOut, TextWriter originalError,
        Func<int>? getColumns = null, Func<int>? getRows = null)
    {
        ArgumentNullException.ThrowIfNull(originalOut);
        ArgumentNullException.ThrowIfNull(originalError);
        _originalOut = originalOut;
        _originalError = originalError;
        _getColumns = getColumns ?? ReadColumns;
        _getRows = getRows ?? ReadRows;
        _out = new(this, isError: false);
        _error = new(this, isError: true);
        try
        {
            _originalOut.Write("\u001b[?1049h\u001b[?25l");
            lock (_gate) RenderLocked();
        }
        catch
        {
            _originalOut.Write("\u001b[?25h\u001b[?1049l");
            _active = false;
            throw;
        }
    }

    public TextWriter Output => _out;
    public TextWriter Error => _error;
    public bool IsActive => _active;

    public void Activate()
    {
        lock (_gate)
        {
            if (!_active || _activated) return;
            Console.SetOut(_out);
            Console.SetError(_error);
            _installedOut = Console.Out;
            _installedError = Console.Error;
            _activated = true;
        }
    }

    public void SetEditor(string text, int cursor)
    {
        ArgumentNullException.ThrowIfNull(text);
        lock (_gate)
        {
            if (!_active) return;
            _editorText = text;
            _editorCursor = Math.Clamp(cursor, 0, text.Length);
            RenderLocked();
        }
    }

    internal void SetAssistantText(string markdown)
    {
        ArgumentNullException.ThrowIfNull(markdown);
        var rendered = TerminalMarkdownRenderer.Render(TerminalSafeText.Normalize(markdown));
        lock (_gate)
        {
            if (!_active) return;
            _liveAssistant = rendered;
            RenderLocked();
        }
    }

    internal void CommitAssistantText(string markdown)
    {
        ArgumentNullException.ThrowIfNull(markdown);
        var rendered = TerminalMarkdownRenderer.Render(TerminalSafeText.Normalize(markdown));
        lock (_gate)
        {
            if (!_active) return;
            _liveAssistant = "";
            if (rendered.Length > 0)
            {
                Capture(rendered, isError: false);
                AppendTranscriptLocked(rendered);
            }
            RenderLocked();
        }
    }

    public void SetFooter(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        lock (_gate)
        {
            if (!_active) return;
            _footer = TerminalSafeText.Normalize(text);
            RenderLocked();
        }
    }

    public void RefreshIfResized()
    {
        lock (_gate)
        {
            if (_active && (Columns() != _lastColumns || Rows() != _lastRows)) RenderLocked();
        }
    }

    internal void ScrollPage(bool up)
    {
        lock (_gate)
        {
            if (!_active) return;
            var pageRows = Math.Max(1, _lastRows * 2 / 3 - 2);
            SetScrollOffsetLocked(_scrollOffset + (up ? pageRows : -pageRows));
        }
    }

    internal void ScrollToTop()
    {
        lock (_gate)
        {
            if (!_active) return;
            SetScrollOffsetLocked(MaxTranscriptScrollOffset);
        }
    }

    internal void ScrollToBottom()
    {
        lock (_gate)
        {
            if (!_active || _scrollOffset == 0) return;
            _scrollOffset = 0;
            RenderLocked();
        }
    }

    internal TranscriptSearchState SearchTranscript(string query, int direction = 0)
    {
        ArgumentNullException.ThrowIfNull(query);
        query = TerminalSafeText.Normalize(query);
        lock (_gate)
        {
            if (!_active) return default;
            if (!query.Equals(_searchQuery, StringComparison.Ordinal)) _searchSelection = 0;
            else if (direction != 0 && _searchMatchCount > 0)
                _searchSelection = ((_searchSelection + direction) % _searchMatchCount + _searchMatchCount) % _searchMatchCount;
            _searchQuery = query;
            RenderLocked();
            return new(_searchMatchCount, _searchMatchCount == 0 ? 0 : _searchSelection + 1, _searchHasMoreMatches);
        }
    }

    internal void ClearTranscriptSearch()
    {
        lock (_gate)
        {
            if (!_active || _searchQuery.Length == 0) return;
            _searchQuery = "";
            _searchSelection = 0;
            _searchMatchCount = 0;
            _searchHasMoreMatches = false;
            RenderLocked();
        }
    }

    internal void AppendToolResult(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var safe = TerminalSafeText.Normalize(text);
        lock (_gate)
        {
            if (!_active || safe.Length == 0) return;
            Capture(safe, isError: true);
            AppendTranscriptLocked(safe, isToolResult: true);
            RenderLocked();
        }
    }

    internal bool ToggleToolResultsExpanded()
    {
        lock (_gate)
        {
            if (!_active) return _toolResultsExpanded;
            _toolResultsExpanded = !_toolResultsExpanded;
            RenderLocked();
            return _toolResultsExpanded;
        }
    }

    internal void SetToolResultsExpanded(bool expanded)
    {
        lock (_gate)
        {
            if (!_active || _toolResultsExpanded == expanded) return;
            _toolResultsExpanded = expanded;
            RenderLocked();
        }
    }

    private void SetScrollOffsetLocked(int value)
    {
        _scrollOffset = Math.Clamp(value, 0, MaxTranscriptScrollOffset);
        RenderLocked();
    }

    internal void WriteControl(string value)
    {
        lock (_gate)
        {
            if (_active) _originalOut.Write(value);
        }
    }

    public void Dispose()
    {
        List<CapturedChunk> captured;
        bool truncated;
        lock (_gate)
        {
            if (!_active) return;
            _active = false;
            if (_activated)
            {
                if (ReferenceEquals(Console.Out, _installedOut)) Console.SetOut(_originalOut);
                if (ReferenceEquals(Console.Error, _installedError)) Console.SetError(_originalError);
            }
            _originalOut.Write("\u001b[?25h\u001b[?1049l");
            captured = _captured;
            truncated = _captureTruncated;
        }

        foreach (var chunk in captured)
        {
            var writer = chunk.IsError ? _originalError : _originalOut;
            writer.Write(chunk.Text.ToString());
            writer.Flush();
        }
        if (truncated)
        {
            _originalError.WriteLine("Interactive transcript exceeded the scrollback restore limit; the active screen was still rendered in full.");
            _originalError.Flush();
        }
    }

    private int _lastColumns;
    private int _lastRows;

    private void Append(string value, bool isError)
    {
        var safe = TerminalSafeText.Normalize(value);
        lock (_gate)
        {
            if (!_active || safe.Length == 0) return;
            Capture(safe, isError);
            AppendTranscriptLocked(safe);
            RenderLocked();
        }
    }

    private void AppendTranscriptLocked(string text, bool isToolResult = false)
    {
        if (text.Length == 0) return;
        if (!isToolResult && _transcript.Count > 0 && !_transcript[^1].IsToolResult)
            _transcript[^1].Text.Append(text);
        else
            _transcript.Add(new(isToolResult, new StringBuilder(text), isToolResult ? PreviewToolResult(text) : null));
        _transcriptCharacters += text.Length;
        TrimTranscriptLocked();
    }

    private void TrimTranscriptLocked()
    {
        var excess = _transcriptCharacters - MaxScreenTranscriptCharacters;
        while (excess > 0 && _transcript.Count > 0)
        {
            var first = _transcript[0];
            if (first.Text.Length <= excess)
            {
                excess -= first.Text.Length;
                _transcriptCharacters -= first.Text.Length;
                _transcript.RemoveAt(0);
                continue;
            }

            var remove = excess;
            var lookLength = Math.Min(first.Text.Length - remove, 64 * 1024);
            var trim = first.Text.ToString(remove, lookLength).IndexOf('\n');
            if (trim >= 0) remove += trim + 1;
            first.Text.Remove(0, remove);
            if (first.IsToolResult) first.CollapsedPreview = PreviewToolResult(first.Text.ToString());
            _transcriptCharacters -= remove;
            break;
        }

        while (_transcript.Count > MaxScreenTranscriptSegments)
        {
            _transcriptCharacters -= _transcript[0].Text.Length;
            _transcript.RemoveAt(0);
        }
    }

    private void Capture(string safe, bool isError)
    {
        var remaining = MaxRestoredTranscriptCharacters - _capturedCharacters;
        if (remaining <= 0)
        {
            _captureTruncated = true;
            return;
        }
        if (safe.Length > remaining)
        {
            safe = safe[..remaining];
            _captureTruncated = true;
        }
        if (_captured.Count > 0 && _captured[^1].IsError == isError)
            _captured[^1].Text.Append(safe);
        else
            _captured.Add(new(isError, new StringBuilder(safe)));
        _capturedCharacters += safe.Length;
    }

    private string GetTranscriptTextLocked()
    {
        var output = new StringBuilder(_transcriptCharacters);
        foreach (var segment in _transcript)
        {
            output.Append(segment.IsToolResult && !_toolResultsExpanded
                ? segment.CollapsedPreview
                : segment.Text.ToString());
        }
        return output.ToString();
    }

    private static string PreviewToolResult(string text)
    {
        var endsWithNewline = text.EndsWith('\n');
        var lines = text.Split('\n');
        var visibleLines = lines.Length - (endsWithNewline ? 1 : 0);
        if (visibleLines <= MaxToolResultPreviewLines) return text;
        var remaining = visibleLines - MaxToolResultPreviewLines;
        var preview = string.Join('\n', lines.Take(MaxToolResultPreviewLines));
        return $"{preview}\n... ({remaining} more lines; tool output collapsed){(endsWithNewline ? "\n" : "")}";
    }

    private void RenderLocked()
    {
        var columns = _lastColumns = Columns();
        var height = _lastRows = Rows();
        var editorHeight = Math.Clamp(height / 3, 1, Math.Max(1, height - 2));
        var footerHeight = height > 2 ? 1 : 0;
        var transcriptHeight = Math.Max(1, height - editorHeight - footerHeight);
        var editor = EditorViewport.Layout(_editorText, _editorCursor, columns, editorHeight);
        var transcriptText = GetTranscriptTextLocked() + _liveAssistant;
        var transcriptWidth = Math.Max(1, columns - 1);
        var visibleTranscript = transcriptText;
        if (_searchQuery.Length > 0)
        {
            var projection = CreateSearchProjection(transcriptText);
            var matches = FindSearchMatches(projection, _searchQuery, out _searchHasMoreMatches);
            _searchMatchCount = matches.Count;
            if (matches.Count > 0)
            {
                _searchSelection = Math.Clamp(_searchSelection, 0, matches.Count - 1);
                var selected = matches[_searchSelection];
                var totalRows = CountVisualRows(projection.Text, transcriptWidth);
                var selectedRow = VisualRowAt(projection.Text, selected.TextStart, transcriptWidth);
                var startRow = Math.Clamp(selectedRow - transcriptHeight / 2, 0, Math.Max(0, totalRows - transcriptHeight));
                _scrollOffset = Math.Clamp(totalRows - Math.Min(totalRows, startRow + transcriptHeight), 0, MaxTranscriptScrollOffset);
                visibleTranscript = HighlightSearchMatches(transcriptText, matches, _searchSelection);
            }
            else
            {
                _searchSelection = 0;
                _searchHasMoreMatches = false;
            }
        }
        else
        {
            _searchMatchCount = 0;
            _searchHasMoreMatches = false;
        }
        var transcriptRows = WrapWindow(visibleTranscript, transcriptWidth, transcriptHeight, _scrollOffset, out _scrollOffset);
        var screenRows = Enumerable.Repeat("", height).ToArray();
        var transcriptStart = _scrollOffset > 0 ? 0 : transcriptHeight - transcriptRows.Count;
        for (var index = 0; index < transcriptRows.Count; index++)
            screenRows[transcriptStart + index] = transcriptRows[index];
        var editorStart = transcriptHeight + editorHeight - editor.Rows.Count;
        for (var index = 0; index < editor.Rows.Count; index++)
            screenRows[editorStart + index] = editor.Rows[index];
        if (footerHeight > 0)
        {
            var footer = _scrollOffset == 0 ? _footer : $"↑ {_scrollOffset} rows · live output follows at bottom · {_footer}";
            screenRows[^1] = Clip(footer, columns - 1);
        }

        _originalOut.Write("\u001b[?2026h\u001b[2J\u001b[H");
        for (var index = 0; index < screenRows.Length; index++)
        {
            _originalOut.Write(screenRows[index]);
            _originalOut.Write("\u001b[0m");
            _originalOut.Write("\u001b[K");
            if (index < screenRows.Length - 1) _originalOut.Write("\r\n");
        }
        var cursorRow = editorStart + editor.CursorRow + 1;
        _originalOut.Write($"\u001b[{cursorRow};{Math.Clamp(editor.CursorColumn, 1, columns)}H\u001b[?25h\u001b[?2026l");
        _originalOut.Flush();
    }

    private static List<string> WrapWindow(string text, int width, int maxRows, int scrollOffset, out int actualScrollOffset)
    {
        var capacity = Math.Max(1, maxRows + Math.Clamp(scrollOffset, 0, MaxTranscriptScrollOffset));
        var rows = new Queue<string>(capacity);
        var totalRows = 0;
        void Add(string value)
        {
            if (rows.Count == capacity) rows.Dequeue();
            rows.Enqueue(value);
            totalRows++;
        }

        foreach (var line in text.Split('\n'))
        {
            if (line.Length == 0)
            {
                Add("");
                continue;
            }
            var current = new StringBuilder();
            var used = 0;
            for (var offset = 0; offset < line.Length;)
            {
                if (TryReadSgr(line, offset, out var sequenceLength))
                {
                    current.Append(line, offset, sequenceLength);
                    offset += sequenceLength;
                    continue;
                }
                var element = StringInfo.GetNextTextElement(line, offset);
                var cells = Math.Max(0, TerminalCells.Width(element));
                if (used + cells > width && current.Length > 0)
                {
                    Add(current + "\u001b[0m");
                    current.Clear();
                    used = 0;
                }
                current.Append(element);
                used += cells;
                offset += element.Length;
            }
            Add(current + "\u001b[0m");
        }
        actualScrollOffset = Math.Min(scrollOffset, Math.Max(0, totalRows - maxRows));
        var end = rows.Count - actualScrollOffset;
        var start = Math.Max(0, end - maxRows);
        return rows.Skip(start).Take(end - start).ToList();
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

    private static SearchProjection CreateSearchProjection(string text)
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

    private static List<SearchMatch> FindSearchMatches(SearchProjection projection, string query, out bool hasMore)
    {
        var matches = new List<SearchMatch>();
        hasMore = false;
        var cursor = 0;
        while (cursor <= projection.Text.Length - query.Length)
        {
            var start = projection.Text.IndexOf(query, cursor, StringComparison.OrdinalIgnoreCase);
            if (start < 0) break;
            if (matches.Count == MaxTranscriptSearchMatches)
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

    private static string HighlightSearchMatches(string text, IReadOnlyList<SearchMatch> matches, int selected)
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

    private static int CountVisualRows(string text, int width)
    {
        var rows = 1;
        var used = 0;
        for (var offset = 0; offset < text.Length;)
        {
            if (text[offset] == '\n')
            {
                rows++;
                used = 0;
                offset++;
                continue;
            }
            var element = StringInfo.GetNextTextElement(text, offset);
            var cells = Math.Max(0, TerminalCells.Width(element));
            if (used + cells > width && used > 0)
            {
                rows++;
                used = 0;
            }
            used += cells;
            offset += element.Length;
        }
        return rows;
    }

    private static int VisualRowAt(string text, int index, int width)
    {
        var row = 0;
        var used = 0;
        for (var offset = 0; offset < index;)
        {
            if (text[offset] == '\n')
            {
                row++;
                used = 0;
                offset++;
                continue;
            }
            var element = StringInfo.GetNextTextElement(text, offset);
            if (offset + element.Length > index) return row;
            var cells = Math.Max(0, TerminalCells.Width(element));
            if (used + cells > width && used > 0)
            {
                row++;
                used = 0;
            }
            used += cells;
            offset += element.Length;
        }
        return row;
    }

    private static string Clip(string value, int width)
    {
        var result = new StringBuilder();
        var used = 0;
        var elements = StringInfo.GetTextElementEnumerator(value);
        while (elements.MoveNext())
        {
            var element = (string)elements.Current;
            var cells = Math.Max(0, TerminalCells.Width(element));
            if (used + cells > width) break;
            result.Append(element);
            used += cells;
        }
        return result.ToString();
    }

    private int Columns()
    {
        try { return Math.Clamp(_getColumns(), 20, 400); }
        catch (IOException) { return 80; }
    }

    private int Rows()
    {
        try { return Math.Clamp(_getRows(), 3, 200); }
        catch (IOException) { return 24; }
    }

    private static int ReadColumns()
    {
        try { return Console.WindowWidth; }
        catch (IOException) { return 80; }
    }

    private static int ReadRows()
    {
        try { return Console.WindowHeight; }
        catch (IOException) { return 24; }
    }

    private sealed class CapturedChunk(bool isError, StringBuilder text)
    {
        public bool IsError { get; } = isError;
        public StringBuilder Text { get; } = text;
    }

    private sealed record SearchProjection(string Text, int[] RawOffsets);
    private readonly record struct SearchMatch(int TextStart, int RawStart, int RawEnd);
    private sealed class TranscriptSegment(bool isToolResult, StringBuilder text, string? collapsedPreview)
    {
        public bool IsToolResult { get; } = isToolResult;
        public StringBuilder Text { get; } = text;
        public string? CollapsedPreview { get; set; } = collapsedPreview;
    }

    private sealed class ScreenWriter(TerminalScreen screen, bool isError) : TextWriter
    {
        public override Encoding Encoding => Encoding.UTF8;
        public override void Write(char value) => screen.Append(value.ToString(), isError);
        public override void Write(string? value)
        {
            if (value is not null) screen.Append(value, isError);
        }
        public override void Write(char[] buffer, int index, int count) => screen.Append(new string(buffer, index, count), isError);
        public override void WriteLine() => screen.Append(Environment.NewLine, isError);
        public override void WriteLine(string? value) => screen.Append((value ?? "") + Environment.NewLine, isError);
        public override void Flush() { }
    }
}

internal readonly record struct TranscriptSearchState(int MatchCount, int SelectedMatch, bool HasMoreMatches);
