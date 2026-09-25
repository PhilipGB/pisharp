using System.Text;

namespace PiSharp.Cli.Tui;

/// <summary>Owns the active-run screen and composes transcript, status, and editor rows.</summary>
public sealed class TerminalScreen : IDisposable
{
    private const int MaxTranscriptScrollOffset = 60_000;
    private readonly object _gate = new();
    private readonly TextWriter _originalOut;
    private readonly TextWriter _originalError;
    private readonly Func<int> _getColumns;
    private readonly Func<int> _getRows;
    private readonly TerminalTranscriptBuffer _transcript = new();
    private readonly ScreenWriter _out;
    private readonly ScreenWriter _error;
    private TextWriter? _installedOut;
    private TextWriter? _installedError;
    private string _editorText = "";
    private string _liveAssistant = "";
    private readonly TranscriptSearchController _search = new();
    private int _editorCursor;
    private int _scrollOffset;
    private string _footer = "Enter steers · follow-up queues · Escape aborts";
    private IReadOnlyList<string>? _overlay;
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
    internal int TerminalWidth => Columns();
    internal int TerminalHeight => Rows();

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
        var rendered = TerminalMarkdownRenderer.Render(TerminalSafeText.Normalize(markdown), Math.Max(1, Columns() - 1));
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
        var rendered = TerminalMarkdownRenderer.Render(TerminalSafeText.Normalize(markdown), Math.Max(1, Columns() - 1));
        lock (_gate)
        {
            if (!_active) return;
            _liveAssistant = "";
            if (rendered.Length > 0) _transcript.Append(rendered, isError: false);
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

    internal void SetOverlay(IReadOnlyList<string>? lines)
    {
        lock (_gate)
        {
            if (!_active) return;
            _overlay = lines?.Select(line => TerminalSafeText.Normalize(line).Replace('\n', ' ')).ToArray();
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
            _search.SetQuery(query, direction);
            RenderLocked();
            return _search.State;
        }
    }

    internal void ClearTranscriptSearch()
    {
        lock (_gate)
        {
            if (!_active || _search.Query.Length == 0) return;
            _search.Clear();
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
            _transcript.Append(safe, isError: true, isToolResult: true);
            RenderLocked();
        }
    }

    internal bool ToggleToolResultsExpanded()
    {
        lock (_gate)
        {
            if (!_active) return _transcript.IsExpanded;
            var expanded = _transcript.ToggleExpanded();
            RenderLocked();
            return expanded;
        }
    }

    internal void SetToolResultsExpanded(bool expanded)
    {
        lock (_gate)
        {
            if (!_active || _transcript.IsExpanded == expanded) return;
            _transcript.SetExpanded(expanded);
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
        IReadOnlyList<TerminalTranscriptBuffer.CapturedChunk> captured;
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
            captured = _transcript.CaptureSnapshot();
            truncated = _transcript.CaptureTruncated;
        }

        foreach (var chunk in captured)
        {
            var writer = chunk.IsError ? _originalError : _originalOut;
            writer.Write(chunk.Text);
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
            _transcript.Append(safe, isError);
            RenderLocked();
        }
    }

    private string GetTranscriptTextLocked() => _transcript.GetText();

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
        var search = _search.Highlight(transcriptText);
        if (_search.Query.Length > 0)
        {
            if (search.MatchCount > 0)
            {
                var totalRows = TerminalTranscriptViewport.CountVisualRows(transcriptText, transcriptWidth);
                var selectedRow = TerminalTranscriptViewport.VisualRowAt(transcriptText, search.SelectedTextStart, transcriptWidth);
                _scrollOffset = TerminalTranscriptViewport.ScrollOffsetToShow(totalRows, selectedRow, transcriptHeight);
                visibleTranscript = search.HighlightedText;
            }
        }
        var transcriptRows = TerminalTranscriptViewport.WrapWindow(visibleTranscript, transcriptWidth,
            transcriptHeight, _scrollOffset, out _scrollOffset);
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
            screenRows[^1] = TerminalTranscriptViewport.Clip(footer, columns - 1);
        }
        if (_overlay is { } overlay) TerminalOverlayLayout.Apply(screenRows, overlay, columns);

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
