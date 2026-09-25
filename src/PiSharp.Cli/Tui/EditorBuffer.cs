using System.Globalization;

namespace PiSharp.Cli.Tui;

/// <summary>Testable editor state, independent of console rendering and model execution.</summary>
public sealed class EditorBuffer
{
    private readonly List<string> _history = [];
    private readonly EditorKeymap _keymap;
    private string _text = "";
    private int _historyIndex;
    private string _draft = "";

    public EditorBuffer(EditorKeymap? keymap = null) => _keymap = keymap ?? new EditorKeymap();

    public string Text => _text;
    public int Cursor { get; private set; }
    public IReadOnlyList<string> History => _history;

    public EditorAction Handle(ConsoleKeyInfo key)
    {
        var control = key.Modifiers.HasFlag(ConsoleModifiers.Control);
        var alt = key.Modifiers.HasFlag(ConsoleModifiers.Alt);
        if (_keymap.Matches("tui.input.newLine", key) || key.Key == ConsoleKey.Enter && alt)
        {
            Insert("\n");
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
        if (_keymap.Matches("tui.editor.cursorUp", key)) return Navigate(-1);
        if (_keymap.Matches("tui.editor.cursorDown", key)) return Navigate(1);
        if (_keymap.Matches("tui.editor.cursorLeft", key)) Cursor = PreviousBoundary(Cursor);
        else if (_keymap.Matches("tui.editor.cursorRight", key)) Cursor = NextBoundary(Cursor);
        else if (_keymap.Matches("tui.editor.cursorWordLeft", key)) Cursor = MoveWordLeft(Cursor);
        else if (_keymap.Matches("tui.editor.cursorWordRight", key)) Cursor = MoveWordRight(Cursor);
        else if (_keymap.Matches("tui.editor.cursorLineStart", key)) Cursor = _text.LastIndexOf('\n', Math.Max(0, Cursor - 1)) + 1;
        else if (_keymap.Matches("tui.editor.cursorLineEnd", key))
        {
            var end = _text.IndexOf('\n', Cursor);
            Cursor = end < 0 ? _text.Length : end;
        }
        else if (_keymap.Matches("tui.editor.deleteCharBackward", key) && Cursor > 0)
        {
            var begin = PreviousBoundary(Cursor);
            _text = _text.Remove(begin, Cursor - begin);
            Cursor = begin;
        }
        else if (_keymap.Matches("tui.editor.deleteCharForward", key) && Cursor < _text.Length)
            _text = _text.Remove(Cursor, NextBoundary(Cursor) - Cursor);
        else if (_keymap.Matches("tui.editor.deleteWordBackward", key))
        {
            var begin = MoveWordLeft(Cursor);
            _text = _text.Remove(begin, Cursor - begin);
            Cursor = begin;
        }
        else if (_keymap.Matches("tui.editor.deleteWordForward", key))
        {
            var end = MoveWordRight(Cursor);
            _text = _text.Remove(Cursor, end - Cursor);
        }
        else if (_keymap.Matches("tui.editor.deleteToLineStart", key))
        {
            var begin = _text.LastIndexOf('\n', Math.Max(0, Cursor - 1)) + 1;
            _text = _text.Remove(begin, Cursor - begin);
            Cursor = begin;
        }
        else if (_keymap.Matches("tui.editor.deleteToLineEnd", key))
        {
            var end = _text.IndexOf('\n', Cursor);
            _text = _text.Remove(Cursor, (end < 0 ? _text.Length : end) - Cursor);
        }
        else if (_keymap.Matches("tui.input.tab", key)) Insert("    ");
        else if (!control && !alt && !char.IsControl(key.KeyChar)) Insert(key.KeyChar.ToString());
        else return EditorAction.None;
        return EditorAction.Render;
    }

    public EditorAction InsertText(string text)
    {
        var safe = new string(text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n')
            .Where(c => c is '\n' or '\t' || !char.IsControl(c)).ToArray());
        if (safe.Length == 0) return EditorAction.None;
        Insert(safe);
        return EditorAction.Render;
    }

    public bool TrySubmit(out string text)
    {
        text = _text;
        if (string.IsNullOrWhiteSpace(text)) return false;
        _history.Add(text);
        _historyIndex = _history.Count;
        return true;
    }

    public void Clear() { _text = ""; Cursor = 0; _historyIndex = _history.Count; _draft = ""; }

    public void Replace(int start, int length, string replacement)
    {
        if (start < 0 || length < 0 || start + length > _text.Length || start + length > Cursor)
            throw new ArgumentOutOfRangeException(nameof(start));
        _text = _text.Remove(start, length).Insert(start, replacement);
        Cursor = start + replacement.Length;
    }

    public void SetText(string text, int? cursor = null)
    {
        ArgumentNullException.ThrowIfNull(text);
        _text = text;
        Cursor = Math.Clamp(cursor ?? text.Length, 0, text.Length);
    }

    private void Insert(string text) { _text = _text.Insert(Cursor, text); Cursor += text.Length; }

    private EditorAction Navigate(int direction)
    {
        if (_history.Count == 0) return EditorAction.None;
        if (_historyIndex == _history.Count) _draft = _text;
        _historyIndex = Math.Clamp(_historyIndex + direction, 0, _history.Count);
        SetText(_historyIndex == _history.Count ? _draft : _history[_historyIndex]);
        return EditorAction.Render;
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
