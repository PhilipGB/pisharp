using System.Globalization;

namespace PiSharp.Cli.Tui;

/// <summary>Testable editor state, independent of console rendering and model execution.</summary>
public sealed class EditorBuffer
{
    private readonly List<string> _history = [];
    private string _text = "";
    private int _historyIndex;
    private string _draft = "";

    public string Text => _text;
    public int Cursor { get; private set; }
    public IReadOnlyList<string> History => _history;

    public EditorAction Handle(ConsoleKeyInfo key)
    {
        var control = key.Modifiers.HasFlag(ConsoleModifiers.Control);
        var alt = key.Modifiers.HasFlag(ConsoleModifiers.Alt);
        if (key.Key == ConsoleKey.Enter)
        {
            if (alt || key.Modifiers.HasFlag(ConsoleModifiers.Shift)) { Insert("\n"); return EditorAction.Render; }
            return TrySubmit(out _) ? EditorAction.Submit : EditorAction.None;
        }
        if (control && key.Key == ConsoleKey.J) { Insert("\n"); return EditorAction.Render; }
        if (control && key.Key == ConsoleKey.C) { Clear(); return EditorAction.Cancel; }
        if (control && key.Key == ConsoleKey.D && _text.Length == 0) return EditorAction.Exit;
        if (key.Key == ConsoleKey.Escape) { Clear(); return EditorAction.Cancel; }
        if (key.Key == ConsoleKey.UpArrow) return Navigate(-1);
        if (key.Key == ConsoleKey.DownArrow) return Navigate(1);
        if (key.Key == ConsoleKey.LeftArrow || (control && key.Key == ConsoleKey.B)) Cursor = PreviousBoundary(Cursor);
        else if (key.Key == ConsoleKey.RightArrow || (control && key.Key == ConsoleKey.F)) Cursor = NextBoundary(Cursor);
        else if (key.Key == ConsoleKey.Home || (control && key.Key == ConsoleKey.A)) Cursor = _text.LastIndexOf('\n', Math.Max(0, Cursor - 1)) + 1;
        else if (key.Key == ConsoleKey.End || (control && key.Key == ConsoleKey.E))
        {
            var end = _text.IndexOf('\n', Cursor);
            Cursor = end < 0 ? _text.Length : end;
        }
        else if (key.Key == ConsoleKey.Backspace && Cursor > 0)
        {
            var begin = PreviousBoundary(Cursor);
            _text = _text.Remove(begin, Cursor - begin);
            Cursor = begin;
        }
        else if (key.Key == ConsoleKey.Delete && Cursor < _text.Length)
            _text = _text.Remove(Cursor, NextBoundary(Cursor) - Cursor);
        else if (control && key.Key == ConsoleKey.W)
        {
            var begin = Cursor;
            while (begin > 0 && char.IsWhiteSpace(_text[begin - 1])) begin--;
            while (begin > 0 && !char.IsWhiteSpace(_text[begin - 1])) begin--;
            _text = _text.Remove(begin, Cursor - begin);
            Cursor = begin;
        }
        else if (control && key.Key == ConsoleKey.U)
        {
            var begin = _text.LastIndexOf('\n', Math.Max(0, Cursor - 1)) + 1;
            _text = _text.Remove(begin, Cursor - begin);
            Cursor = begin;
        }
        else if (control && key.Key == ConsoleKey.K)
        {
            var end = _text.IndexOf('\n', Cursor);
            _text = _text.Remove(Cursor, (end < 0 ? _text.Length : end) - Cursor);
        }
        else if (key.Key == ConsoleKey.Tab) Insert("    ");
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

    public void SetText(string text) { _text = text; Cursor = text.Length; }

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
}

public enum EditorAction { None, Render, Submit, Cancel, Exit }
