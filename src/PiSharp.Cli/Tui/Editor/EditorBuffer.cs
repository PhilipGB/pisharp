using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace PiSharp.Cli.Tui;

internal readonly record struct EditorBufferSnapshot(string Text, int Cursor, int? SelectionStart,
    int? SelectionEnd, long Version);

/// <summary>Testable editor state, independent of console rendering and model execution.</summary>
public sealed class EditorBuffer
{
    private sealed record Snapshot(string Text, int Cursor, int? SelectionAnchor,
        Dictionary<int, string> Pastes, int PasteCounter);
    private sealed record ExtensionEditState(string Text, int Cursor, Snapshot[] Undo, string[] History,
        int HistoryIndex, string Draft, int DraftCursor, int? SelectionAnchor, int? PreferredColumn,
        int? SnappedFromCursorColumn, bool CoalesceTypedWord, LastEditAction LastEditAction,
        string? LastYankedText, string[] KillRing, Dictionary<int, string> Pastes, int PasteCounter);
    private enum LastEditAction { None, Kill, Yank }
    private enum KillDirection { Backward, Forward }
    private enum JumpDirection { Forward, Backward }

    private readonly List<string> _history = [];
    private readonly object _gate = new();
    private readonly List<Snapshot> _undo = [];
    private readonly EditorKeymap _keymap;
    private readonly EditorKillRing _killRing = new();
    private readonly Dictionary<int, string> _pastes = [];
    private int _pasteCounter;
    private static readonly Regex s_pasteMarker = new(
        @"\[paste #(?<id>\d+)(?: (?:\+\d+ lines|\d+ chars))?\]", RegexOptions.Compiled);
    private string _text = "";
    private int _historyIndex;
    private string _draft = "";
    private int _draftCursor;
    private int? _selectionAnchor;
    private int? _preferredVisualColumn;
    private int? _snappedFromCursorColumn;
    private int _layoutWidth = int.MaxValue;
    private int _terminalRows = 24;
    private JumpDirection? _jumpDirection;
    private bool _coalesceTypedWord;
    private LastEditAction _lastEditAction;
    private string? _lastYankedText;
    private long _version;

    public EditorBuffer(EditorKeymap? keymap = null) => _keymap = keymap ?? new EditorKeymap();

    public string Text { get { lock (_gate) return _text; } }
    public int Cursor
    {
        get { lock (_gate) return _cursor; }
        private set { lock (_gate) _cursor = value; }
    }
    private int _cursor;
    public int? SelectionStart
    {
        get
        {
            lock (_gate)
                return _selectionAnchor is { } anchor && anchor != Cursor ? Math.Min(anchor, Cursor) : null;
        }
    }
    public int? SelectionEnd
    {
        get
        {
            lock (_gate)
                return _selectionAnchor is { } anchor && anchor != Cursor ? Math.Max(anchor, Cursor) : null;
        }
    }
    public string? SelectedText
    {
        get
        {
            lock (_gate)
            {
                var start = _selectionAnchor is { } anchor && anchor != Cursor ? Math.Min(anchor, Cursor) : (int?)null;
                var end = _selectionAnchor is { } selectionAnchor && selectionAnchor != Cursor
                    ? Math.Max(selectionAnchor, Cursor) : (int?)null;
                return start is { } first && end is { } last ? _text[first..last] : null;
            }
        }
    }
    public IReadOnlyList<string> History { get { lock (_gate) return _history.ToArray(); } }
    internal object SyncRoot => _gate;

    public int LayoutWidth { get { lock (_gate) return _layoutWidth; } }
    public int TerminalRows { get { lock (_gate) return _terminalRows; } }
    internal bool IsJumpModeActive { get { lock (_gate) return _jumpDirection is not null; } }

    internal EditorAction HandleJumpText(string text)
    {
        lock (_gate)
        {
            if (_jumpDirection is not { } direction || text.Length == 0) return EditorAction.None;
            _version++;
            var target = StringInfo.GetNextTextElement(text);
            var remainder = text[target.Length..];
            _jumpDirection = null;
            JumpToChar(target, direction);
            if (remainder.Length > 0) InsertTyped(remainder);
            return EditorAction.Render;
        }
    }

    public void SetLayoutWidth(int width)
    {
        if (width <= 0) throw new ArgumentOutOfRangeException(nameof(width));
        lock (_gate) _layoutWidth = width;
    }

    public void SetTerminalRows(int rows)
    {
        if (rows <= 0) throw new ArgumentOutOfRangeException(nameof(rows));
        lock (_gate) _terminalRows = rows;
    }

    internal void AddHistoryEntry(string text)
    {
        lock (_gate) AddHistory(text);
    }

    internal EditorBufferSnapshot CaptureSnapshot()
    {
        lock (_gate)
            return new(_text, Cursor,
                _selectionAnchor is { } anchor && anchor != Cursor ? Math.Min(anchor, Cursor) : null,
                _selectionAnchor is { } selectionAnchor && selectionAnchor != Cursor
                    ? Math.Max(selectionAnchor, Cursor) : null, _version);
    }

    internal object CaptureExtensionEditState()
    {
        lock (_gate)
            return new ExtensionEditState(_text, Cursor, [.. _undo],
                [.. _history], _historyIndex, _draft, _draftCursor, _selectionAnchor, _preferredVisualColumn,
                _snappedFromCursorColumn, _coalesceTypedWord, _lastEditAction, _lastYankedText,
                _killRing.Capture(), new Dictionary<int, string>(_pastes), _pasteCounter);
    }

    internal void BeginExtensionEdit(string? text)
    {
        lock (_gate)
        {
            _version++;
            _text = NormalizeText(text ?? string.Empty);
            _pastes.Clear();
            _pasteCounter = 0;
            Cursor = _text.Length;
            _historyIndex = _history.Count;
            _draft = _text;
            _draftCursor = Cursor;
            _undo.Clear();
            ClearSelection();
            _preferredVisualColumn = null;
            _snappedFromCursorColumn = null;
            _coalesceTypedWord = false;
            BreakKillYankChain();
        }
    }

    internal void RestoreExtensionEditState(object capturedState)
    {
        ArgumentNullException.ThrowIfNull(capturedState);
        lock (_gate)
        {
            _version++;
            if (capturedState is not ExtensionEditState state)
                throw new ArgumentException("The editor state was not captured by this editor.", nameof(capturedState));
            _text = state.Text;
            Cursor = state.Cursor;
            _undo.Clear();
            _undo.AddRange(state.Undo);
            _history.Clear();
            _history.AddRange(state.History);
            _historyIndex = state.HistoryIndex;
            _draft = state.Draft;
            _draftCursor = state.DraftCursor;
            _selectionAnchor = state.SelectionAnchor;
            _preferredVisualColumn = state.PreferredColumn;
            _snappedFromCursorColumn = state.SnappedFromCursorColumn;
            _coalesceTypedWord = state.CoalesceTypedWord;
            _lastEditAction = state.LastEditAction;
            _lastYankedText = state.LastYankedText;
            _killRing.Restore(state.KillRing);
            _pastes.Clear();
            foreach (var (id, content) in state.Pastes) _pastes[id] = content;
            _pasteCounter = state.PasteCounter;
        }
    }

    public EditorAction Handle(ConsoleKeyInfo key)
    {
        lock (_gate)
        {
            _version++;
            var control = key.Modifiers.HasFlag(ConsoleModifiers.Control);
            var alt = key.Modifiers.HasFlag(ConsoleModifiers.Alt);
            var kill = _keymap.Matches("tui.editor.deleteWordBackward", key) ||
                _keymap.Matches("tui.editor.deleteWordForward", key) ||
                _keymap.Matches("tui.editor.deleteToLineStart", key) ||
                _keymap.Matches("tui.editor.deleteToLineEnd", key);
            var yank = _keymap.Matches("tui.editor.yank", key) || _keymap.Matches("tui.editor.yankPop", key);
            if (!kill && !yank) BreakKillYankChain();
            var printable = !control && !alt && !char.IsControl(key.KeyChar) && key.KeyChar != '\0';
            if (!printable) _coalesceTypedWord = false;

            if (_jumpDirection is { } pendingJump)
            {
                if (_keymap.Matches("tui.editor.jumpForward", key) || _keymap.Matches("tui.editor.jumpBackward", key))
                {
                    _jumpDirection = null;
                    return EditorAction.None;
                }
                if (printable)
                {
                    _jumpDirection = null;
                    JumpToChar(key.KeyChar.ToString(), pendingJump);
                    return EditorAction.Render;
                }
                _jumpDirection = null;
            }

            if (_keymap.Matches("tui.input.newLine", key) || key.Key == ConsoleKey.Enter && alt)
            {
                InsertTyped("\n");
                return EditorAction.Render;
            }
            if (_keymap.Matches("tui.input.submit", key))
                return TrySubmit(out _) ? EditorAction.Submit : EditorAction.None;
            if (_keymap.Matches("app.interrupt", key) || _keymap.Matches("app.clear", key))
            {
                Clear();
                return EditorAction.Cancel;
            }
            if (_keymap.Matches("app.exit", key) && _text.Length == 0) return EditorAction.Exit;
            if (_keymap.Matches("tui.editor.undo", key)) return Undo();
            if (_keymap.Matches("tui.editor.yank", key)) return Yank();
            if (_keymap.Matches("tui.editor.yankPop", key)) return YankPop();
            if (_keymap.Matches("tui.editor.historyPrevious", key)) return NavigateHistory(-1);
            if (_keymap.Matches("tui.editor.historyNext", key)) return NavigateHistory(1);
            if (_keymap.Matches("tui.editor.selectUp", key)) return MoveVertical(-1, extendSelection: true);
            if (_keymap.Matches("tui.editor.selectDown", key)) return MoveVertical(1, extendSelection: true);
            if (_keymap.Matches("tui.editor.cursorUp", key)) return MoveVertical(-1);
            if (_keymap.Matches("tui.editor.cursorDown", key)) return MoveVertical(1);

            if (_keymap.Matches("tui.editor.selectLeft", key)) return ExtendSelection(PreviousBoundary(Cursor));
            if (_keymap.Matches("tui.editor.selectRight", key)) return ExtendSelection(NextBoundary(Cursor));
            if (_keymap.Matches("tui.editor.selectWordLeft", key)) return ExtendSelection(MoveWordLeft(Cursor));
            if (_keymap.Matches("tui.editor.selectWordRight", key)) return ExtendSelection(MoveWordRight(Cursor));
            if (_keymap.Matches("tui.editor.selectLineStart", key)) return ExtendSelection(LineStart(Cursor));
            if (_keymap.Matches("tui.editor.selectLineEnd", key)) return ExtendSelection(LineEnd(Cursor));

            if (_keymap.Matches("tui.editor.cursorLeft", key)) MoveCursor(SelectionStart ?? PreviousBoundary(Cursor));
            else if (_keymap.Matches("tui.editor.cursorRight", key)) MoveCursor(SelectionEnd ?? NextBoundary(Cursor));
            else if (_keymap.Matches("tui.editor.cursorWordLeft", key)) MoveCursor(SelectionStart ?? MoveWordLeft(Cursor));
            else if (_keymap.Matches("tui.editor.cursorWordRight", key)) MoveCursor(SelectionEnd ?? MoveWordRight(Cursor));
            else if (_keymap.Matches("tui.editor.cursorLineStart", key)) MoveCursor(LineStart(Cursor));
            else if (_keymap.Matches("tui.editor.cursorLineEnd", key)) MoveCursor(LineEnd(Cursor));
            else if (_keymap.Matches("tui.editor.pageUp", key)) return MovePage(-1);
            else if (_keymap.Matches("tui.editor.pageDown", key)) return MovePage(1);
            else if (_keymap.Matches("tui.editor.jumpForward", key))
            {
                _jumpDirection = JumpDirection.Forward;
                return EditorAction.None;
            }
            else if (_keymap.Matches("tui.editor.jumpBackward", key))
            {
                _jumpDirection = JumpDirection.Backward;
                return EditorAction.None;
            }
            else if (_keymap.Matches("tui.editor.deleteCharBackward", key))
            {
                var start = SelectionStart ?? PreviousBoundary(Cursor);
                var end = SelectionEnd ?? Cursor;
                var removedMarker = RangeContainsPasteMarker(start, end);
                DeleteRange(start, end);
                if (removedMarker) NormalizePasteRegistry();
            }
            else if (_keymap.Matches("tui.editor.deleteCharForward", key))
                DeleteRange(SelectionStart ?? Cursor, SelectionEnd ?? NextBoundary(Cursor));
            else if (_keymap.Matches("tui.editor.deleteWordBackward", key))
                DeleteWordBackward();
            else if (_keymap.Matches("tui.editor.deleteWordForward", key))
                DeleteWordForward();
            else if (_keymap.Matches("tui.editor.deleteToLineStart", key))
                DeleteToLineStart();
            else if (_keymap.Matches("tui.editor.deleteToLineEnd", key))
                DeleteToLineEnd();
            else if (_keymap.Matches("tui.input.tab", key))
                InsertAtomic("    ");
            else if (printable)
                InsertTyped(key.KeyChar.ToString());
            else
                return EditorAction.None;

            return EditorAction.Render;
        }
    }

    public EditorAction InsertText(string text)
    {
        lock (_gate)
        {
            _version++;
            var safe = new string(NormalizeText(text)
                .Where(c => c is '\n' || !char.IsControl(c)).ToArray());
            if (safe.Length == 0) return EditorAction.None;
            InsertAtomic(safe);
            BreakKillYankChain();
            return EditorAction.Render;
        }
    }

    public EditorAction InsertPaste(string text)
    {
        lock (_gate)
        {
            ArgumentNullException.ThrowIfNull(text);
            var normalized = NormalizeText(text);
            var safe = new string(normalized.Where(character => character is '\n' || !char.IsControl(character))
                .ToArray());
            if (safe.Length == 0) return EditorAction.None;
            var lines = safe.Split('\n').Length;
            var characters = safe.Length;
            if (lines <= 10 && characters <= 1000) return InsertText(safe);

            _version++;
            _pasteCounter++;
            var id = _pasteCounter;
            var marker = lines > 10
                ? $"[paste #{id} +{lines} lines]"
                : $"[paste #{id} {characters} chars]";
            InsertAtomic(marker);
            _pastes[id] = safe;
            BreakKillYankChain();
            return EditorAction.Render;
        }
    }

    public string ExpandedText
    {
        get { lock (_gate) return ExpandPasteMarkers(_text); }
    }

    public bool TrySubmit(out string text)
    {
        lock (_gate)
        {
            _version++;
            text = ExpandPasteMarkers(_text);
            if (string.IsNullOrWhiteSpace(text)) return false;
            AddHistory(text);
            _historyIndex = _history.Count;
            _undo.Clear();
            _coalesceTypedWord = false;
            _preferredVisualColumn = null;
            _snappedFromCursorColumn = null;
            ClearSelection();
            BreakKillYankChain();
            return true;
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            _version++;
            _text = "";
            Cursor = 0;
            _historyIndex = _history.Count;
            _draft = "";
            _draftCursor = 0;
            _undo.Clear();
            _pastes.Clear();
            _pasteCounter = 0;
            _coalesceTypedWord = false;
            _preferredVisualColumn = null;
            _snappedFromCursorColumn = null;
            ClearSelection();
            BreakKillYankChain();
        }
    }

    public void Replace(int start, int length, string replacement)
    {
        lock (_gate)
        {
            _version++;
            ArgumentNullException.ThrowIfNull(replacement);
            if (start < 0 || length < 0 || start + length > _text.Length || start + length > Cursor)
                throw new ArgumentOutOfRangeException(nameof(start));
            if (length == 0 && replacement.Length == 0) return;
            PushUndoSnapshot();
            _text = _text.Remove(start, length).Insert(start, replacement);
            Cursor = start + replacement.Length;
            ClearSelection();
            ExitHistoryBrowsing();
            RememberDraft();
            _coalesceTypedWord = false;
            _preferredVisualColumn = null;
            _snappedFromCursorColumn = null;
            BreakKillYankChain();
        }
    }

    public void SetText(string text, int? cursor = null)
    {
        lock (_gate)
        {
            _version++;
            ArgumentNullException.ThrowIfNull(text);
            text = NormalizeText(text);
            var nextCursor = Math.Clamp(cursor ?? text.Length, 0, text.Length);
            if (!string.Equals(_text, text, StringComparison.Ordinal)) PushUndoSnapshot();
            _text = text;
            Cursor = nextCursor;
            _pastes.Clear();
            _pasteCounter = 0;
            ClearSelection();
            ExitHistoryBrowsing();
            RememberDraft();
            _coalesceTypedWord = false;
            _preferredVisualColumn = null;
            _snappedFromCursorColumn = null;
            BreakKillYankChain();
        }
    }

    public void SetCursor(int cursor)
    {
        lock (_gate)
        {
            _version++;
            if (cursor < 0 || cursor > _text.Length ||
                cursor != _text.Length && !StringInfo.ParseCombiningCharacters(_text).Contains(cursor))
                throw new ArgumentOutOfRangeException(nameof(cursor), "Cursor must be at a grapheme boundary in the editor text.");
            Cursor = cursor;
            ClearSelection();
            ExitHistoryBrowsing();
            RememberDraft();
            _coalesceTypedWord = false;
            _preferredVisualColumn = null;
            _snappedFromCursorColumn = null;
            BreakKillYankChain();
        }
    }

    private void AddHistory(string text)
    {
        var trimmed = text.Trim();
        if (trimmed.Length == 0 || _history.Count > 0 && string.Equals(_history[^1], trimmed, StringComparison.Ordinal)) return;
        _history.Add(trimmed);
        if (_history.Count > 100) _history.RemoveAt(0);
        _historyIndex = _history.Count;
    }

    private EditorAction NavigateHistory(int direction)
    {
        if (_history.Count == 0) return EditorAction.None;
        if (_historyIndex == _history.Count)
        {
            if (direction > 0) return EditorAction.None;
            PushUndoSnapshot();
            _draft = _text;
            _draftCursor = Cursor;
            _historyIndex = _history.Count - 1;
        }
        else
        {
            var next = _historyIndex + Math.Sign(direction);
            if (next < 0) return EditorAction.None;
            if (next >= _history.Count)
            {
                _historyIndex = _history.Count;
                _text = _draft;
                Cursor = _draftCursor;
                ClearSelection();
                _preferredVisualColumn = null;
                _snappedFromCursorColumn = null;
                _coalesceTypedWord = false;
                return EditorAction.Render;
            }
            _historyIndex = next;
        }

        _text = _history[_historyIndex];
        Cursor = direction < 0 ? 0 : _text.Length;
        ClearSelection();
        _preferredVisualColumn = null;
        _snappedFromCursorColumn = null;
        _coalesceTypedWord = false;
        return EditorAction.Render;
    }

    private EditorAction MoveVertical(int direction, bool extendSelection = false)
    {
        var lines = EditorVisualLines.Build(_text, _layoutWidth);
        var current = FindCurrentVisualLine(lines);
        var lineStart = LineStart(Cursor);
        var lineEnd = LineEnd(Cursor);
        if (direction < 0)
        {
            if (current == 0)
            {
                if (extendSelection)
                {
                    if (Cursor > lineStart)
                    {
                        SetCursor(lineStart, extendSelection: true);
                        return EditorAction.Render;
                    }
                    return EditorAction.None;
                }

                if (_historyIndex < _history.Count || _text.Length == 0 || Cursor == lineStart)
                    return NavigateHistory(-1);
                SetCursor(lineStart, extendSelection: false);
                return EditorAction.Render;
            }

            MoveToVisualLine(lines, current, current - 1, extendSelection);
            return EditorAction.Render;
        }

        if (current == lines.Count - 1)
        {
            if (extendSelection)
            {
                if (Cursor < lineEnd)
                {
                    SetCursor(lineEnd, extendSelection: true);
                    return EditorAction.Render;
                }
                return EditorAction.None;
            }

            if (_historyIndex < _history.Count) return NavigateHistory(1);
            if (Cursor < lineEnd)
            {
                SetCursor(lineEnd, extendSelection: false);
                return EditorAction.Render;
            }
            return EditorAction.None;
        }

        MoveToVisualLine(lines, current, current + 1, extendSelection);
        return EditorAction.Render;
    }

    private EditorAction MovePage(int direction)
    {
        var lines = EditorVisualLines.Build(_text, _layoutWidth);
        var current = FindCurrentVisualLine(lines);
        var pageSize = Math.Max(5, (int)Math.Floor(_terminalRows * 0.3));
        var target = Math.Clamp(current + direction * pageSize, 0, lines.Count - 1);
        MoveToVisualLine(lines, current, target, extendSelection: false);
        return EditorAction.Render;
    }

    private void MoveToVisualLine(IReadOnlyList<EditorVisualLines.VisualLine> lines, int current, int target,
        bool extendSelection)
    {
        var currentLine = lines[current];
        var targetLine = lines[target];
        int currentVisualColumn;
        if (_snappedFromCursorColumn is { } snapped)
        {
            var snappedLine = EditorVisualLines.FindVisualLine(lines, currentLine.LogicalLine, snapped, current);
            currentVisualColumn = snapped - lines[snappedLine].StartOffset;
        }
        else
        {
            currentVisualColumn = Cursor - currentLine.StartOffset;
        }

        var lastSourceSegment = current == lines.Count - 1 ||
            lines[current + 1].LogicalLine != currentLine.LogicalLine;
        var sourceMax = lastSourceSegment ? currentLine.Length : Math.Max(0, currentLine.Length - 1);
        var lastTargetSegment = target == lines.Count - 1 ||
            lines[target + 1].LogicalLine != targetLine.LogicalLine;
        var targetMax = lastTargetSegment ? targetLine.Length : Math.Max(0, targetLine.Length - 1);
        var moveToColumn = ComputeVerticalMoveColumn(currentVisualColumn, sourceMax, targetMax);

        var logicalEnd = LineEnd(targetLine.StartOffset);
        var targetOffset = Math.Min(targetLine.StartOffset + moveToColumn, logicalEnd);
        SetVerticalCursor(targetOffset, extendSelection);

        var logicalStart = LineStart(targetLine.StartOffset);
        var starts = StringInfo.ParseCombiningCharacters(_text[logicalStart..logicalEnd]);
        for (var index = 0; index < starts.Length; index++)
        {
            var start = logicalStart + starts[index];
            if (start > Cursor) break;
            var end = index + 1 < starts.Length ? logicalStart + starts[index + 1] : logicalEnd;
            var length = end - start;
            if (length <= 1 || Cursor >= end) continue;
            var isContinuation = start < targetLine.StartOffset;
            var movingDown = target > current;
            if (isContinuation && movingDown)
            {
                var next = target + 1;
                while (next < lines.Count && lines[next].LogicalLine == targetLine.LogicalLine &&
                       lines[next].StartOffset < end) next++;
                if (next < lines.Count)
                {
                    MoveToVisualLine(lines, current, next, extendSelection);
                    return;
                }
            }

            _snappedFromCursorColumn = Cursor;
            SetVerticalCursor(start, extendSelection);
            return;
        }

        _snappedFromCursorColumn = null;
    }

    private int ComputeVerticalMoveColumn(int currentVisualColumn, int sourceMaxVisualColumn, int targetMaxVisualColumn)
    {
        var hasPreferred = _preferredVisualColumn is not null;
        var cursorInMiddle = currentVisualColumn < sourceMaxVisualColumn;
        var targetTooShort = targetMaxVisualColumn < currentVisualColumn;
        if (!hasPreferred || cursorInMiddle)
        {
            if (targetTooShort)
            {
                _preferredVisualColumn = currentVisualColumn;
                return targetMaxVisualColumn;
            }

            _preferredVisualColumn = null;
            _snappedFromCursorColumn = null;
            return currentVisualColumn;
        }

        var targetCantFitPreferred = targetMaxVisualColumn < _preferredVisualColumn;
        if (targetTooShort || targetCantFitPreferred) return targetMaxVisualColumn;
        var result = _preferredVisualColumn ?? currentVisualColumn;
        _preferredVisualColumn = null;
        _snappedFromCursorColumn = null;
        return result;
    }

    private void SetVerticalCursor(int offset, bool extendSelection)
    {
        if (!extendSelection)
        {
            Cursor = offset;
            ClearSelection();
            return;
        }

        _selectionAnchor ??= Cursor;
        Cursor = offset;
        if (_selectionAnchor == Cursor) _selectionAnchor = null;
    }

    private int FindCurrentVisualLine(IReadOnlyList<EditorVisualLines.VisualLine> lines) =>
        EditorVisualLines.FindVisualLine(lines, LogicalLineAt(Cursor), Cursor, lines.Count - 1);

    private int LogicalLineAt(int offset)
    {
        var line = 0;
        for (var index = 0; index < offset; index++)
            if (_text[index] == '\n') line++;
        return line;
    }

    private void JumpToChar(string character, JumpDirection direction)
    {
        if (character.Length == 0) return;
        if (direction == JumpDirection.Forward)
        {
            if (Cursor >= _text.Length) return;
            var index = _text.IndexOf(character, Cursor + 1, StringComparison.Ordinal);
            if (index >= 0) SetCursor(index, extendSelection: false);
            return;
        }

        if (Cursor == 0) return;
        var previous = _text.LastIndexOf(character, Cursor - 1, StringComparison.Ordinal);
        if (previous >= 0) SetCursor(previous, extendSelection: false);
    }

    private EditorAction Undo()
    {
        if (_undo.Count == 0) return EditorAction.None;
        var snapshot = _undo[^1];
        _undo.RemoveAt(_undo.Count - 1);
        _text = snapshot.Text;
        Cursor = snapshot.Cursor;
        _selectionAnchor = snapshot.SelectionAnchor;
        _pastes.Clear();
        foreach (var (id, content) in snapshot.Pastes) _pastes[id] = content;
        _pasteCounter = snapshot.PasteCounter;
        _historyIndex = _history.Count;
        RememberDraft();
        _coalesceTypedWord = false;
        _preferredVisualColumn = null;
        _snappedFromCursorColumn = null;
        return EditorAction.Render;
    }

    private void InsertTyped(string text)
    {
        if (string.IsNullOrEmpty(text)) return;
        ExitHistoryBrowsing();
        var start = SelectionStart ?? Cursor;
        var end = SelectionEnd ?? Cursor;
        if (start != end || string.IsNullOrWhiteSpace(text) || !_coalesceTypedWord) PushUndoSnapshot();
        _text = _text.Remove(start, end - start).Insert(start, text);
        Cursor = start + text.Length;
        ClearSelection();
        RememberDraft();
        _coalesceTypedWord = text != "\n";
        _preferredVisualColumn = null;
        _snappedFromCursorColumn = null;
    }

    private void InsertAtomic(string text)
    {
        if (string.IsNullOrEmpty(text)) return;
        ExitHistoryBrowsing();
        PushUndoSnapshot();
        var start = SelectionStart ?? Cursor;
        var end = SelectionEnd ?? Cursor;
        _text = _text.Remove(start, end - start).Insert(start, text);
        Cursor = start + text.Length;
        ClearSelection();
        RememberDraft();
        _coalesceTypedWord = false;
        _preferredVisualColumn = null;
        _snappedFromCursorColumn = null;
    }

    private void DeleteRange(int start, int end)
        => DeleteRange(start, end, killDirection: null);

    private void DeleteRange(int start, int end, KillDirection? killDirection)
    {
        if (SelectionStart is { } selectionStart && SelectionEnd is { } selectionEnd)
        {
            start = selectionStart;
            end = selectionEnd;
        }
        if (start >= end) return;
        PushUndoSnapshot();
        if (killDirection is { } direction)
        {
            _killRing.Push(_text[start..end], prepend: direction == KillDirection.Backward,
                accumulate: _lastEditAction == LastEditAction.Kill);
            _lastEditAction = LastEditAction.Kill;
            _lastYankedText = null;
        }
        _text = _text.Remove(start, end - start);
        Cursor = start;
        ClearSelection();
        ExitHistoryBrowsing();
        RememberDraft();
        _coalesceTypedWord = false;
        _preferredVisualColumn = null;
        _snappedFromCursorColumn = null;
    }

    private void DeleteWordBackward()
    {
        if (SelectionStart is { } selectionStart && SelectionEnd is { } selectionEnd)
        {
            DeleteRange(selectionStart, selectionEnd, KillDirection.Backward);
            return;
        }

        var lineStart = LineStart(Cursor);
        if (Cursor == lineStart && lineStart > 0)
            DeleteRange(lineStart - 1, lineStart, KillDirection.Backward);
        else
            DeleteRange(MoveWordLeft(Cursor), Cursor, KillDirection.Backward);
    }

    private void DeleteWordForward()
    {
        if (SelectionStart is { } selectionStart && SelectionEnd is { } selectionEnd)
        {
            DeleteRange(selectionStart, selectionEnd, KillDirection.Forward);
            return;
        }

        var lineEnd = LineEnd(Cursor);
        if (Cursor == lineEnd && lineEnd < _text.Length)
            DeleteRange(lineEnd, lineEnd + 1, KillDirection.Forward);
        else
            DeleteRange(Cursor, MoveWordRight(Cursor), KillDirection.Forward);
    }

    private void DeleteToLineStart()
    {
        if (SelectionStart is { } selectionStart && SelectionEnd is { } selectionEnd)
        {
            DeleteRange(selectionStart, selectionEnd, KillDirection.Backward);
            return;
        }

        var lineStart = LineStart(Cursor);
        if (Cursor > lineStart)
            DeleteRange(lineStart, Cursor, KillDirection.Backward);
        else if (lineStart > 0)
            DeleteRange(lineStart - 1, lineStart, KillDirection.Backward);
    }

    private void DeleteToLineEnd()
    {
        if (SelectionStart is { } selectionStart && SelectionEnd is { } selectionEnd)
        {
            DeleteRange(selectionStart, selectionEnd, KillDirection.Forward);
            return;
        }

        var lineEnd = LineEnd(Cursor);
        if (Cursor < lineEnd)
            DeleteRange(Cursor, lineEnd, KillDirection.Forward);
        else if (lineEnd < _text.Length)
            DeleteRange(lineEnd, lineEnd + 1, KillDirection.Forward);
    }

    private EditorAction Yank()
    {
        if (_killRing.Peek() is not { } text) return EditorAction.None;
        PushUndoSnapshot();
        var start = SelectionStart ?? Cursor;
        var end = SelectionEnd ?? Cursor;
        _text = _text.Remove(start, end - start).Insert(start, text);
        Cursor = start + text.Length;
        ClearSelection();
        ExitHistoryBrowsing();
        RememberDraft();
        _coalesceTypedWord = false;
        _preferredVisualColumn = null;
        _snappedFromCursorColumn = null;
        _lastEditAction = LastEditAction.Yank;
        _lastYankedText = text;
        return EditorAction.Render;
    }

    private EditorAction YankPop()
    {
        if (_lastEditAction != LastEditAction.Yank || _killRing.Count <= 1 || _lastYankedText is not { Length: > 0 } previous)
            return EditorAction.None;

        var start = Cursor - previous.Length;
        if (start < 0 || !string.Equals(_text[start..Cursor], previous, StringComparison.Ordinal))
            return EditorAction.None;
        PushUndoSnapshot();
        _killRing.Rotate();
        var text = _killRing.Peek()!;
        _text = _text.Remove(start, previous.Length).Insert(start, text);
        Cursor = start + text.Length;
        ClearSelection();
        ExitHistoryBrowsing();
        RememberDraft();
        _coalesceTypedWord = false;
        _preferredVisualColumn = null;
        _snappedFromCursorColumn = null;
        _lastEditAction = LastEditAction.Yank;
        _lastYankedText = text;
        return EditorAction.Render;
    }

    private void BreakKillYankChain()
    {
        _lastEditAction = LastEditAction.None;
        _lastYankedText = null;
    }

    private void PushUndoSnapshot()
    {
        if (_undo.Count > 0 && _undo[^1].Text == _text && _undo[^1].Cursor == Cursor &&
            _undo[^1].SelectionAnchor == _selectionAnchor) return;
        _undo.Add(new(_text, Cursor, _selectionAnchor, new Dictionary<int, string>(_pastes), _pasteCounter));
    }

    private void ExitHistoryBrowsing() => _historyIndex = _history.Count;

    public void ClearSelection()
    {
        lock (_gate)
        {
            _version++;
            _selectionAnchor = null;
        }
    }

    private EditorAction ExtendSelection(int cursor)
    {
        SetCursor(cursor, extendSelection: true);
        return EditorAction.Render;
    }

    private void MoveCursor(int cursor) => SetCursor(cursor, extendSelection: false);

    private void SetCursor(int cursor, bool extendSelection)
    {
        _preferredVisualColumn = null;
        _snappedFromCursorColumn = null;
        if (extendSelection)
        {
            _selectionAnchor ??= Cursor;
            Cursor = cursor;
            if (_selectionAnchor == Cursor) _selectionAnchor = null;
        }
        else
        {
            Cursor = cursor;
            ClearSelection();
        }
    }

    private void RememberDraft()
    {
        _draft = _text;
        _draftCursor = Cursor;
    }

    private int LineStart(int index)
    {
        var newline = index == 0 ? -1 : _text.LastIndexOf('\n', index - 1);
        return newline + 1;
    }

    private int LineEnd(int index)
    {
        var newline = _text.IndexOf('\n', index);
        return newline < 0 ? _text.Length : newline;
    }

    private int PreviousBoundary(int index)
    {
        if (index == 0) return 0;
        if (TryFindPasteMarker(index - 1, out var markerStart, out _)) return markerStart;
        var starts = StringInfo.ParseCombiningCharacters(_text);
        return starts.LastOrDefault(position => position < index);
    }

    private int NextBoundary(int index)
    {
        if (TryFindPasteMarker(index, out _, out var markerEnd)) return markerEnd;
        var starts = StringInfo.ParseCombiningCharacters(_text);
        return starts.FirstOrDefault(position => position > index, _text.Length);
    }

    private bool RangeContainsPasteMarker(int start, int end)
    {
        if (_pastes.Count == 0) return false;
        foreach (Match match in s_pasteMarker.Matches(_text))
        {
            var id = int.Parse(match.Groups["id"].Value, CultureInfo.InvariantCulture);
            if (!_pastes.ContainsKey(id)) continue;
            if (match.Index >= start && match.Index + match.Length <= end) return true;
        }
        return false;
    }

    private bool TryFindPasteMarker(int index, out int start, out int end)
    {
        start = end = 0;
        if (index < 0 || index >= _text.Length || _pastes.Count == 0) return false;
        foreach (Match match in s_pasteMarker.Matches(_text))
        {
            var id = int.Parse(match.Groups["id"].Value, CultureInfo.InvariantCulture);
            if (!_pastes.ContainsKey(id)) continue;
            if (index < match.Index || index >= match.Index + match.Length) continue;
            start = match.Index;
            end = match.Index + match.Length;
            return true;
        }
        return false;
    }

    private string ExpandPasteMarkers(string text)
    {
        if (_pastes.Count == 0 || !text.Contains("[paste #", StringComparison.Ordinal)) return text;
        return s_pasteMarker.Replace(text, match =>
        {
            var id = int.Parse(match.Groups["id"].Value, CultureInfo.InvariantCulture);
            return _pastes.TryGetValue(id, out var content) ? content : match.Value;
        });
    }

    private void NormalizePasteRegistry()
    {
        if (_pastes.Count == 0) return;
        var present = new List<int>();
        foreach (Match match in s_pasteMarker.Matches(_text))
        {
            var id = int.Parse(match.Groups["id"].Value, CultureInfo.InvariantCulture);
            if (_pastes.ContainsKey(id) && !present.Contains(id)) present.Add(id);
        }
        foreach (var id in _pastes.Keys.Where(id => !present.Contains(id)).ToArray()) _pastes.Remove(id);
        if (present.Count == 0)
        {
            _pasteCounter = 0;
            return;
        }
        present.Sort();
        var map = present.Select((id, index) => (id, next: index + 1)).ToDictionary(item => item.id, item => item.next);
        if (map.All(item => item.Key == item.Value))
        {
            _pasteCounter = present.Count;
            return;
        }
        var remapped = present.Select((oldId, index) => (Id: index + 1, Content: _pastes[oldId]))
            .ToDictionary(item => item.Id, item => item.Content);
        _pastes.Clear();
        foreach (var (id, content) in remapped) _pastes[id] = content;
        var cursor = Cursor;
        _text = s_pasteMarker.Replace(_text, match =>
        {
            var id = int.Parse(match.Groups["id"].Value, CultureInfo.InvariantCulture);
            if (!map.TryGetValue(id, out var next)) return match.Value;
            var idStart = match.Groups["id"].Index - match.Index;
            var idLength = match.Groups["id"].Length;
            var replacement = match.Value[..idStart] + next.ToString(CultureInfo.InvariantCulture) +
                match.Value[(idStart + idLength)..];
            if (match.Index + match.Length <= cursor) cursor += replacement.Length - match.Length;
            return replacement;
        });
        _pasteCounter = present.Count;
        Cursor = Math.Clamp(cursor, 0, _text.Length);
    }

    private int SnapToBoundary(int index)
    {
        if (index <= 0) return 0;
        if (index >= _text.Length) return _text.Length;
        var starts = StringInfo.ParseCombiningCharacters(_text);
        return starts.LastOrDefault(position => position <= index);
    }

    private int MoveWordLeft(int index)
    {
        var lineStart = LineStart(index);
        if (index == lineStart && lineStart > 0) return lineStart - 1;

        while (index > lineStart)
        {
            var previous = PreviousBoundary(index);
            if (!string.IsNullOrWhiteSpace(_text[previous..index])) break;
            index = previous;
        }
        if (index == lineStart) return index;

        var word = IsWordElement(_text[PreviousBoundary(index)..index]);
        while (index > lineStart)
        {
            var previous = PreviousBoundary(index);
            var element = _text[previous..index];
            if (string.IsNullOrWhiteSpace(element) || IsWordElement(element) != word) break;
            index = previous;
        }
        return index;
    }

    private int MoveWordRight(int index)
    {
        var lineEnd = LineEnd(index);
        if (index == lineEnd && lineEnd < _text.Length) return lineEnd + 1;

        while (index < lineEnd)
        {
            var next = NextBoundary(index);
            if (!string.IsNullOrWhiteSpace(_text[index..next])) break;
            index = next;
        }
        if (index >= lineEnd) return lineEnd;

        var word = IsWordElement(_text[index..NextBoundary(index)]);
        while (index < lineEnd)
        {
            var next = NextBoundary(index);
            var element = _text[index..next];
            if (string.IsNullOrWhiteSpace(element) || IsWordElement(element) != word) break;
            index = next;
        }
        return index;
    }

    private static string NormalizeText(string text) =>
        text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Replace("\t", "    ", StringComparison.Ordinal);

    private static bool IsWordElement(string element) => element.EnumerateRunes().Any(rune =>
    {
        var category = Rune.GetUnicodeCategory(rune);
        return Rune.IsLetterOrDigit(rune) || category == UnicodeCategory.ConnectorPunctuation;
    });
}

public enum EditorAction { None, Render, Submit, Cancel, Exit }
