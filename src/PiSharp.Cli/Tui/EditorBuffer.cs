using System.Globalization;

namespace PiSharp.Cli.Tui;

/// <summary>Testable editor state, independent of console rendering and model execution.</summary>
public sealed class EditorBuffer
{
    private sealed record Snapshot(string Text, int Cursor, int? SelectionAnchor);

    private readonly List<string> _history = [];
    private readonly List<Snapshot> _undo = [];
    private readonly EditorKeymap _keymap;
    private string _text = "";
    private int _historyIndex;
    private string _draft = "";
    private int _draftCursor;
    private int? _selectionAnchor;
    private int? _preferredColumn;
    private bool _coalesceTypedWord;

    public EditorBuffer(EditorKeymap? keymap = null) => _keymap = keymap ?? new EditorKeymap();

    public string Text => _text;
    public int Cursor { get; private set; }
    public int? SelectionStart => _selectionAnchor is { } anchor && anchor != Cursor ? Math.Min(anchor, Cursor) : null;
    public int? SelectionEnd => _selectionAnchor is { } anchor && anchor != Cursor ? Math.Max(anchor, Cursor) : null;
    public string? SelectedText => SelectionStart is { } start && SelectionEnd is { } end ? _text[start..end] : null;
    public IReadOnlyList<string> History => _history;

    public EditorAction Handle(ConsoleKeyInfo key)
    {
        var control = key.Modifiers.HasFlag(ConsoleModifiers.Control);
        var alt = key.Modifiers.HasFlag(ConsoleModifiers.Alt);
        var vertical = _keymap.Matches("tui.editor.cursorUp", key) || _keymap.Matches("tui.editor.cursorDown", key) ||
            _keymap.Matches("tui.editor.selectUp", key) || _keymap.Matches("tui.editor.selectDown", key);
        var printable = !control && !alt && !char.IsControl(key.KeyChar);
        if (!printable) _coalesceTypedWord = false;
        if (!vertical) _preferredColumn = null;

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
        else if (_keymap.Matches("tui.editor.deleteCharBackward", key))
            DeleteRange(SelectionStart ?? PreviousBoundary(Cursor), SelectionEnd ?? Cursor);
        else if (_keymap.Matches("tui.editor.deleteCharForward", key))
            DeleteRange(SelectionStart ?? Cursor, SelectionEnd ?? NextBoundary(Cursor));
        else if (_keymap.Matches("tui.editor.deleteWordBackward", key))
            DeleteRange(MoveWordLeft(Cursor), Cursor);
        else if (_keymap.Matches("tui.editor.deleteWordForward", key))
            DeleteRange(Cursor, MoveWordRight(Cursor));
        else if (_keymap.Matches("tui.editor.deleteToLineStart", key))
            DeleteRange(LineStart(Cursor), Cursor);
        else if (_keymap.Matches("tui.editor.deleteToLineEnd", key))
            DeleteRange(Cursor, LineEnd(Cursor));
        else if (_keymap.Matches("tui.input.tab", key))
            InsertAtomic("    ");
        else if (printable)
            InsertTyped(key.KeyChar.ToString());
        else
            return EditorAction.None;

        return EditorAction.Render;
    }

    public EditorAction InsertText(string text)
    {
        var safe = new string(text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n')
            .Where(c => c is '\n' or '\t' || !char.IsControl(c)).ToArray());
        if (safe.Length == 0) return EditorAction.None;
        InsertAtomic(safe);
        return EditorAction.Render;
    }

    public bool TrySubmit(out string text)
    {
        text = _text;
        if (string.IsNullOrWhiteSpace(text)) return false;
        AddHistory(text);
        _historyIndex = _history.Count;
        _undo.Clear();
        _coalesceTypedWord = false;
        _preferredColumn = null;
        ClearSelection();
        return true;
    }

    public void Clear()
    {
        _text = "";
        Cursor = 0;
        _historyIndex = _history.Count;
        _draft = "";
        _draftCursor = 0;
        _undo.Clear();
        _coalesceTypedWord = false;
        _preferredColumn = null;
        ClearSelection();
    }

    public void Replace(int start, int length, string replacement)
    {
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
        _preferredColumn = null;
    }

    public void SetText(string text, int? cursor = null)
    {
        ArgumentNullException.ThrowIfNull(text);
        var nextCursor = Math.Clamp(cursor ?? text.Length, 0, text.Length);
        if (!string.Equals(_text, text, StringComparison.Ordinal)) PushUndoSnapshot();
        _text = text;
        Cursor = nextCursor;
        ClearSelection();
        ExitHistoryBrowsing();
        RememberDraft();
        _coalesceTypedWord = false;
        _preferredColumn = null;
    }

    public void SetCursor(int cursor)
    {
        if (cursor < 0 || cursor > _text.Length ||
            cursor != _text.Length && !StringInfo.ParseCombiningCharacters(_text).Contains(cursor))
            throw new ArgumentOutOfRangeException(nameof(cursor), "Cursor must be at a grapheme boundary in the editor text.");
        Cursor = cursor;
        ClearSelection();
        ExitHistoryBrowsing();
        RememberDraft();
        _coalesceTypedWord = false;
        _preferredColumn = null;
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
                _preferredColumn = null;
                _coalesceTypedWord = false;
                return EditorAction.Render;
            }
            _historyIndex = next;
        }

        _text = _history[_historyIndex];
        Cursor = direction < 0 ? 0 : _text.Length;
        ClearSelection();
        _preferredColumn = null;
        _coalesceTypedWord = false;
        return EditorAction.Render;
    }

    private EditorAction MoveVertical(int direction, bool extendSelection = false)
    {
        var currentStart = LineStart(Cursor);
        var currentEnd = LineEnd(Cursor);
        if (direction < 0)
        {
            if (currentStart > 0)
            {
                var preferred = _preferredColumn ?? Cursor - currentStart;
                var previousEnd = currentStart - 1;
                var previousStart = LineStart(previousEnd);
                SetCursor(SnapToBoundary(previousStart + Math.Min(preferred, previousEnd - previousStart)), extendSelection);
                _preferredColumn = preferred;
                return EditorAction.Render;
            }

            if (extendSelection)
            {
                if (Cursor > currentStart)
                {
                    SetCursor(currentStart, extendSelection: true);
                    _preferredColumn = null;
                    return EditorAction.Render;
                }
                return EditorAction.None;
            }

            if (_historyIndex < _history.Count || _text.Length == 0 || Cursor == currentStart)
                return NavigateHistory(-1);
            Cursor = currentStart;
            ClearSelection();
            _preferredColumn = null;
            return EditorAction.Render;
        }

        if (currentEnd < _text.Length)
        {
            var preferred = _preferredColumn ?? Cursor - currentStart;
            var nextStart = currentEnd + 1;
            var nextEnd = LineEnd(nextStart);
            SetCursor(SnapToBoundary(nextStart + Math.Min(preferred, nextEnd - nextStart)), extendSelection);
            _preferredColumn = preferred;
            return EditorAction.Render;
        }

        if (extendSelection)
        {
            if (Cursor < currentEnd)
            {
                SetCursor(currentEnd, extendSelection: true);
                _preferredColumn = null;
                return EditorAction.Render;
            }
            return EditorAction.None;
        }

        if (_historyIndex < _history.Count) return NavigateHistory(1);
        if (Cursor < currentEnd)
        {
            Cursor = currentEnd;
            ClearSelection();
            _preferredColumn = null;
            return EditorAction.Render;
        }
        return EditorAction.None;
    }

    private EditorAction Undo()
    {
        if (_undo.Count == 0) return EditorAction.None;
        var snapshot = _undo[^1];
        _undo.RemoveAt(_undo.Count - 1);
        _text = snapshot.Text;
        Cursor = snapshot.Cursor;
        _selectionAnchor = snapshot.SelectionAnchor;
        _historyIndex = _history.Count;
        RememberDraft();
        _coalesceTypedWord = false;
        _preferredColumn = null;
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
        _preferredColumn = null;
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
        _preferredColumn = null;
    }

    private void DeleteRange(int start, int end)
    {
        if (SelectionStart is { } selectionStart && SelectionEnd is { } selectionEnd)
        {
            start = selectionStart;
            end = selectionEnd;
        }
        if (start >= end) return;
        PushUndoSnapshot();
        _text = _text.Remove(start, end - start);
        Cursor = start;
        ClearSelection();
        ExitHistoryBrowsing();
        RememberDraft();
        _coalesceTypedWord = false;
        _preferredColumn = null;
    }

    private void PushUndoSnapshot()
    {
        if (_undo.Count > 0 && _undo[^1].Text == _text && _undo[^1].Cursor == Cursor &&
            _undo[^1].SelectionAnchor == _selectionAnchor) return;
        _undo.Add(new(_text, Cursor, _selectionAnchor));
    }

    private void ExitHistoryBrowsing() => _historyIndex = _history.Count;

    public void ClearSelection() => _selectionAnchor = null;

    private EditorAction ExtendSelection(int cursor)
    {
        SetCursor(cursor, extendSelection: true);
        return EditorAction.Render;
    }

    private void MoveCursor(int cursor) => SetCursor(cursor, extendSelection: false);

    private void SetCursor(int cursor, bool extendSelection)
    {
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
        var starts = StringInfo.ParseCombiningCharacters(_text);
        return starts.LastOrDefault(position => position < index);
    }

    private int NextBoundary(int index)
    {
        var starts = StringInfo.ParseCombiningCharacters(_text);
        return starts.FirstOrDefault(position => position > index, _text.Length);
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
        while (index > 0)
        {
            var previous = PreviousBoundary(index);
            if (!string.IsNullOrWhiteSpace(_text[previous..index])) break;
            index = previous;
        }
        while (index > 0)
        {
            var previous = PreviousBoundary(index);
            if (string.IsNullOrWhiteSpace(_text[previous..index])) break;
            index = previous;
        }
        return index;
    }

    private int MoveWordRight(int index)
    {
        while (index < _text.Length)
        {
            var next = NextBoundary(index);
            if (string.IsNullOrWhiteSpace(_text[index..next])) break;
            index = next;
        }
        while (index < _text.Length)
        {
            var next = NextBoundary(index);
            if (!string.IsNullOrWhiteSpace(_text[index..next])) break;
            index = next;
        }
        return index;
    }
}

public enum EditorAction { None, Render, Submit, Cancel, Exit }
