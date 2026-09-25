namespace PiSharp.Cli.Tui;

internal readonly record struct TerminalMouseResult(bool Changed = false, bool Copy = false,
    int? EditorCursorOffset = null, bool ClearEditorSelection = false);

/// <summary>Routes terminal mouse gestures to transcript selection or the prompt editor.</summary>
internal sealed class TerminalMouseRouter
{
    private enum Target { None, Transcript, Editor }

    private readonly TranscriptSelectionController _transcript = new();
    private readonly EditorSelectionController _editor = new();
    private Target _selectionTarget;
    private Target _dragTarget;
    private int _transcriptScreenStart;
    private int _transcriptHeight;
    private int _editorScreenStart;
    private int _editorHeight;
    private EditorViewport.Frame? _editorFrame;

    public string? SelectedText => _selectionTarget switch
    {
        Target.Editor => _editor.SelectedText,
        Target.Transcript => _transcript.SelectedText,
        _ => null
    };

    public void SetTranscript(IReadOnlyList<string> rows, int firstVisualRow, int screenStart, int height)
    {
        _transcript.SetVisibleRows(rows, firstVisualRow);
        _transcriptScreenStart = screenStart;
        _transcriptHeight = height;
    }

    public IReadOnlyList<string> HighlightTranscript() => _transcript.HighlightVisibleRows();

    public void SetEditor(string text, EditorViewport.Frame frame, int screenStart,
        int? selectionStart = null, int? selectionEnd = null)
    {
        _editor.SetFrame(text, frame);
        _editor.SetBufferSelection(selectionStart, selectionEnd);
        if (selectionStart is { } start && selectionEnd is { } end && start < end)
        {
            _transcript.Clear();
            _selectionTarget = Target.Editor;
        }
        else if (_selectionTarget == Target.Editor && _editor.SelectedText is null && _dragTarget != Target.Editor)
        {
            _selectionTarget = Target.None;
        }
        _editorFrame = frame;
        _editorScreenStart = screenStart;
        _editorHeight = frame.Rows.Count;
    }

    public IReadOnlyList<string> HighlightEditor() => _editor.HighlightRows();

    public TerminalMouseResult Handle(TerminalMouseEvent mouse)
    {
        if (mouse.IsWheel) return default;
        if (!mouse.IsRelease && !mouse.IsMotion) return Begin(mouse);
        if (_dragTarget == Target.None) return default;

        if (_dragTarget == Target.Editor)
        {
            var (row, cell) = EditorPoint(mouse, clamp: true);
            if (mouse.IsMotion) return new(_editor.Move(row, cell));
            var ended = _editor.End(row, cell);
            _dragTarget = Target.None;
            return new(ended.Changed, ended.Copy, ended.CursorOffset);
        }

        var result = _transcript.HandleMouse(mouse, _transcriptScreenStart, _transcriptHeight);
        if (mouse.IsRelease) _dragTarget = Target.None;
        return new(result.Changed, result.Copy);
    }

    private TerminalMouseResult Begin(TerminalMouseEvent mouse)
    {
        if ((mouse.Button & 3) != 0) return default;
        var row = mouse.Row - 1;
        if (ContainsEditor(row))
        {
            var (editorRow, cell) = EditorPoint(mouse, clamp: false);
            if (!_editor.Begin(editorRow, cell)) return default;
            _transcript.Clear();
            _selectionTarget = _dragTarget = Target.Editor;
            return new(Changed: true, ClearEditorSelection: true);
        }

        if (ContainsTranscript(row))
        {
            _editor.Clear();
            _selectionTarget = _dragTarget = Target.Transcript;
            var result = _transcript.HandleMouse(mouse, _transcriptScreenStart, _transcriptHeight);
            return new(result.Changed, result.Copy);
        }

        _editor.Clear();
        _transcript.Clear();
        _selectionTarget = _dragTarget = Target.None;
        return default;
    }

    private bool ContainsEditor(int row) => _editorFrame is not null &&
        row >= _editorScreenStart && row < _editorScreenStart + _editorHeight;

    private bool ContainsTranscript(int row)
    {
        var rows = _transcript.HighlightVisibleRows();
        return row >= _transcriptScreenStart && row < _transcriptScreenStart + rows.Count &&
            row < _transcriptScreenStart + _transcriptHeight;
    }

    private (int Row, int Cell) EditorPoint(TerminalMouseEvent mouse, bool clamp)
    {
        var row = mouse.Row - 1 - _editorScreenStart;
        if (clamp) row = Math.Clamp(row, 0, Math.Max(0, _editorHeight - 1));
        var cell = mouse.Column - 3; // Terminal coordinates include the two-cell prompt prefix.
        return (row, Math.Max(0, cell));
    }
}
