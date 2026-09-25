using System.Globalization;

namespace PiSharp.Cli.Tui;

/// <summary>Testable editor state, independent of console rendering and model execution.</summary>
public sealed class EditorBuffer
{
    private sealed record Snapshot(string Text, int Cursor);

    private readonly List<string> _history = [];
    private readonly List<Snapshot> _undo = [];
    private readonly EditorKeymap _keymap;
    private string _text = "";
    private int _historyIndex;
    private string _draft = "";
    private int _draftCursor;
    private int? _preferredColumn;
    private bool _coalesceTypedWord;

    public EditorBuffer(EditorKeymap? keymap = null) => _keymap = keymap ?? new EditorKeymap();

    public string Text => _text;
    public int Cursor { get; private set; }
    public IReadOnlyList<string> History => _history;

    public EditorAction Handle(ConsoleKeyInfo key)
    {
        var control = key.Modifiers.HasFlag(ConsoleModifiers.Control);
        var alt = key.Modifiers.HasFlag(ConsoleModifiers.Alt);
        var vertical = _keymap.Matches("tui.editor.cursorUp", key) || _keymap.Matches("tui.editor.cursorDown", key);
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
        if (_keymap.Matches("tui.editor.cursorUp", key)) return MoveVertical(-1);
        if (_keymap.Matches("tui.editor.cursorDown", key)) return MoveVertical(1);

        if (_keymap.Matches("tui.editor.cursorLeft", key)) Cursor = PreviousBoundary(Cursor);
        else if (_keymap.Matches("tui.editor.cursorRight", key)) Cursor = NextBoundary(Cursor);
        else if (_keymap.Matches("tui.editor.cursorWordLeft", key)) Cursor = MoveWordLeft(Cursor);
        else if (_keymap.Matches("tui.editor.cursorWordRight", key)) Cursor = MoveWordRight(Cursor);
        else if (_keymap.Matches("tui.editor.cursorLineStart", key)) Cursor = LineStart(Cursor);
        else if (_keymap.Matches("tui.editor.cursorLineEnd", key)) Cursor = LineEnd(Cursor);
        else if (_keymap.Matches("tui.editor.deleteCharBackward", key) && Cursor > 0)
            DeleteRange(PreviousBoundary(Cursor), Cursor);
        else if (_keymap.Matches("tui.editor.deleteCharForward", key) && Cursor < _text.Length)
            DeleteRange(Cursor, NextBoundary(Cursor));
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
                _preferredColumn = null;
                _coalesceTypedWord = false;
                return EditorAction.Render;
            }
            _historyIndex = next;
        }

        _text = _history[_historyIndex];
        Cursor = direction < 0 ? 0 : _text.Length;
        _preferredColumn = null;
        _coalesceTypedWord = false;
        return EditorAction.Render;
    }

    private EditorAction MoveVertical(int direction)
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
                Cursor = SnapToBoundary(previousStart + Math.Min(preferred, previousEnd - previousStart));
                _preferredColumn = preferred;
                return EditorAction.Render;
            }

            if (_historyIndex < _history.Count || _text.Length == 0 || Cursor == currentStart)
                return NavigateHistory(-1);
            Cursor = currentStart;
            _preferredColumn = null;
            return EditorAction.Render;
        }

        if (currentEnd < _text.Length)
        {
            var preferred = _preferredColumn ?? Cursor - currentStart;
            var nextStart = currentEnd + 1;
            var nextEnd = LineEnd(nextStart);
            Cursor = SnapToBoundary(nextStart + Math.Min(preferred, nextEnd - nextStart));
            _preferredColumn = preferred;
            return EditorAction.Render;
        }

        if (_historyIndex < _history.Count) return NavigateHistory(1);
        if (Cursor < currentEnd)
        {
            Cursor = currentEnd;
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
        if (string.IsNullOrWhiteSpace(text) || !_coalesceTypedWord) PushUndoSnapshot();
        _text = _text.Insert(Cursor, text);
        Cursor += text.Length;
        RememberDraft();
        _coalesceTypedWord = text != "\n";
        _preferredColumn = null;
    }

    private void InsertAtomic(string text)
    {
        if (string.IsNullOrEmpty(text)) return;
        ExitHistoryBrowsing();
        PushUndoSnapshot();
        _text = _text.Insert(Cursor, text);
        Cursor += text.Length;
        RememberDraft();
        _coalesceTypedWord = false;
        _preferredColumn = null;
    }

    private void DeleteRange(int start, int end)
    {
        if (start >= end) return;
        PushUndoSnapshot();
        _text = _text.Remove(start, end - start);
        Cursor = start;
        ExitHistoryBrowsing();
        RememberDraft();
        _coalesceTypedWord = false;
        _preferredColumn = null;
    }

    private void PushUndoSnapshot()
    {
        if (_undo.Count > 0 && _undo[^1].Text == _text && _undo[^1].Cursor == Cursor) return;
        _undo.Add(new(_text, Cursor));
    }

    private void ExitHistoryBrowsing() => _historyIndex = _history.Count;

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
