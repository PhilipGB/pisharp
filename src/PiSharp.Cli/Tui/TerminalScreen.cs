using System.Text;
using Microsoft.Extensions.AI;

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
    private readonly TerminalImageRenderer _images;
    private readonly TerminalScreenCompositor _compositor;
    private readonly ScreenWriter _out;
    private readonly ScreenWriter _error;
    private readonly TerminalMouseRouter _mouse = new();
    private TextWriter? _installedOut;
    private TextWriter? _installedError;
    private string _editorText = "";
    private string _liveAssistant = "";
    private readonly TranscriptSearchController _search = new();
    private int _editorCursor;
    private int? _editorSelectionStart;
    private int? _editorSelectionEnd;
    private int _scrollOffset;
    private int _lastImagePruneRevision = -1;
    private string _footer = "Enter steers · follow-up queues · Escape aborts";
    private IReadOnlyList<string>? _overlay;
    private bool _activated;
    private bool _suspended;
    private volatile bool _active = true;

    public TerminalScreen(TextWriter originalOut, TextWriter originalError,
        Func<int>? getColumns = null, Func<int>? getRows = null)
        : this(originalOut, originalError, getColumns, getRows, new TerminalImageRenderer())
    {
    }

    internal TerminalScreen(TextWriter originalOut, TextWriter originalError,
        Func<int>? getColumns, Func<int>? getRows, TerminalImageRenderer imageRenderer)
    {
        ArgumentNullException.ThrowIfNull(originalOut);
        ArgumentNullException.ThrowIfNull(originalError);
        ArgumentNullException.ThrowIfNull(imageRenderer);
        _originalOut = originalOut;
        _originalError = originalError;
        _images = imageRenderer;
        _getColumns = getColumns ?? ReadColumns;
        _getRows = getRows ?? ReadRows;
        _compositor = new(_originalOut, _images);
        _out = new(this, isError: false);
        _error = new(this, isError: true);
        try
        {
            _originalOut.Write("\u001b[?1049h\u001b[?25l" + TerminalMouseMode.Enable);
            lock (_gate) RenderLocked();
        }
        catch
        {
            _originalOut.Write(TerminalMouseMode.Disable + "\u001b[?25h\u001b[?1049l");
            _originalOut.Flush();
            _active = false;
            throw;
        }
    }

    public TextWriter Output => _out;
    public TextWriter Error => _error;
    public bool IsActive => _active;
    internal int TerminalWidth => Columns();
    internal int TerminalHeight => Rows();
    internal string? SelectedText
    {
        get { lock (_gate) return _mouse.SelectedText; }
    }

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

    internal void Suspend()
    {
        lock (_gate)
        {
            if (!_active || _suspended) return;
            _originalOut.Write(_images.HidePlacements() + TerminalMouseMode.Disable + "\u001b[?25h\u001b[?1049l");
            _originalOut.Flush();
            _suspended = true;
        }
    }

    internal void Resume()
    {
        lock (_gate)
        {
            if (!_active || !_suspended) return;
            _originalOut.Write("\u001b[?1049h\u001b[?25l" + TerminalMouseMode.Enable);
            _suspended = false;
            RenderLocked();
        }
    }

    public void SetEditor(string text, int cursor, int? selectionStart = null, int? selectionEnd = null)
    {
        ArgumentNullException.ThrowIfNull(text);
        lock (_gate)
        {
            if (!_active) return;
            _editorText = text;
            _editorCursor = Math.Clamp(cursor, 0, text.Length);
            _editorSelectionStart = null;
            _editorSelectionEnd = null;
            if (selectionStart is { } start && selectionEnd is { } end && start < end)
            {
                var boundedStart = Math.Clamp(start, 0, text.Length);
                var boundedEnd = Math.Clamp(end, 0, text.Length);
                if (boundedStart < boundedEnd)
                {
                    _editorSelectionStart = boundedStart;
                    _editorSelectionEnd = boundedEnd;
                }
            }
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

    internal TerminalMouseResult HandleMouse(TerminalMouseEvent mouse)
    {
        lock (_gate)
        {
            if (!_active || _suspended) return default;
            if (mouse.IsWheel)
            {
                var delta = mouse.WheelScrollDelta;
                if (delta != 0) SetScrollOffsetLocked(_scrollOffset + delta);
                return new(delta != 0);
            }
            var result = _mouse.Handle(mouse);
            if (result.Changed) RenderLocked();
            return result;
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

    internal void AppendUserMessage(string text, IReadOnlyList<DataContent>? images = null)
    {
        ArgumentNullException.ThrowIfNull(text);
        var safe = TerminalSafeText.Normalize(text);
        lock (_gate)
        {
            if (!_active) return;
            var activeText = new StringBuilder();
            var capturedText = new StringBuilder();
            if (safe.Length > 0)
            {
                foreach (var line in safe.Split('\n'))
                {
                    activeText.Append("› ").Append(line).Append(Environment.NewLine);
                    capturedText.Append("› ").Append(line).Append(Environment.NewLine);
                }
            }
            if (images is not null)
            {
                foreach (var image in images)
                {
                    var marker = _images.Register(image, out var fallback);
                    activeText.Append(marker ?? fallback).Append(Environment.NewLine);
                    capturedText.Append(fallback).Append(Environment.NewLine);
                }
            }
            if (activeText.Length == 0) return;
            _transcript.Append(activeText.ToString(), isError: false, capturedText: capturedText.ToString());
            RenderLocked();
        }
    }

    internal void AppendToolResult(string text, IReadOnlyList<DataContent>? images = null)
    {
        ArgumentNullException.ThrowIfNull(text);
        var safe = TerminalSafeText.Normalize(text);
        lock (_gate)
        {
            if (!_active) return;
            var activeText = new StringBuilder(safe);
            var capturedText = new StringBuilder(safe);
            if (images is not null)
            {
                foreach (var image in images)
                {
                    var marker = _images.Register(image, out var fallback);
                    activeText.Append(Environment.NewLine).Append(marker ?? fallback);
                    capturedText.Append(Environment.NewLine).Append(fallback);
                }
            }
            if (activeText.Length == 0) return;
            var previewText = capturedText.ToString();
            _transcript.Append(activeText.ToString(), isError: true, isToolResult: true,
                previewText, collapsedPreviewText: previewText);
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
            _originalOut.Write(_images.CleanupControlSequence() + TerminalMouseMode.Disable + "\u001b[?25h\u001b[?1049l");
            _originalOut.Flush();
            captured = _transcript.CaptureSnapshot();
            truncated = _transcript.CaptureTruncated;
        }

        foreach (var chunk in captured)
        {
            var writer = chunk.IsError ? _originalError : _originalOut;
            writer.Write(chunk.Text);
            writer.Flush();
        }
        _images.Clear();
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
        if (!_active || _suspended) return;
        var columns = Columns();
        var rows = Rows();
        var editorHeight = Math.Clamp(rows / 3, 1, Math.Max(1, rows - 2));
        var footerHeight = rows > 2 ? 1 : 0;
        var transcriptHeight = Math.Max(1, rows - editorHeight - footerHeight);
        if (_lastImagePruneRevision != _transcript.Revision)
        {
            _images.PruneUnreferenced(_transcript.GetRetainedText());
            _lastImagePruneRevision = _transcript.Revision;
        }
        var transcript = _images.LayoutTranscript(GetTranscriptTextLocked() + _liveAssistant,
            Math.Max(1, columns - 1), Math.Max(1, transcriptHeight - 2));
        var frame = _compositor.Compose(_editorText, _editorCursor, _editorSelectionStart, _editorSelectionEnd,
            transcript, _footer, _overlay, _scrollOffset, columns, rows, _search, _mouse);
        _scrollOffset = frame.ScrollOffset;
        _lastColumns = frame.Columns;
        _lastRows = frame.Height;
        _compositor.Render(frame);
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
