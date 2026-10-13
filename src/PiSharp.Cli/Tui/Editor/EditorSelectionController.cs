namespace PiSharp.Cli.Tui;

/// <summary>Maps mouse selection and cursor placement back to the editor's grapheme offsets.</summary>
internal sealed class EditorSelectionController
{
    private readonly record struct Point(int StartOffset, int EndOffset);

    private string _text = "";
    private EditorViewport.Frame? _frame;
    private Point? _anchor;
    private Point? _focus;
    private int? _bufferSelectionStart;
    private int? _bufferSelectionEnd;
    private string? _mouseSelectedText;
    private bool _dragging;

    public string? SelectedText => _bufferSelectionStart is { } start && _bufferSelectionEnd is { } end && start < end
        ? _text[start..end]
        : _mouseSelectedText;

    public void SetFrame(string text, EditorViewport.Frame frame)
    {
        if (!string.Equals(_text, text, StringComparison.Ordinal)) Clear();
        _text = text;
        _frame = frame;
    }

    public void Clear()
    {
        _anchor = null;
        _focus = null;
        _bufferSelectionStart = null;
        _bufferSelectionEnd = null;
        _dragging = false;
        _mouseSelectedText = null;
    }

    public void SetBufferSelection(int? start, int? end)
    {
        if (_dragging) return;
        var hadBufferSelection = _bufferSelectionStart is not null && _bufferSelectionEnd is not null;
        if (start is { } selectionStart && end is { } selectionEnd && selectionStart < selectionEnd)
        {
            _anchor = null;
            _focus = null;
            _mouseSelectedText = null;
            _bufferSelectionStart = selectionStart;
            _bufferSelectionEnd = selectionEnd;
            return;
        }

        _bufferSelectionStart = null;
        _bufferSelectionEnd = null;
        if (hadBufferSelection)
        {
            _anchor = null;
            _focus = null;
            _mouseSelectedText = null;
        }
    }

    public bool Begin(int row, int cell)
    {
        if (_frame is null || !TryGetPoint(row, cell, out var point)) return false;
        _bufferSelectionStart = null;
        _bufferSelectionEnd = null;
        _anchor = _focus = point;
        _dragging = true;
        _mouseSelectedText = null;
        return true;
    }

    public bool Move(int row, int cell)
    {
        if (!_dragging || !TryGetPoint(row, cell, out var point) || _focus == point) return false;
        _focus = point;
        return true;
    }

    public (bool Changed, bool Copy, int? CursorOffset) End(int row, int cell)
    {
        if (!_dragging) return default;
        _ = TryGetPoint(row, cell, out var point);
        _focus = point;
        _dragging = false;
        GetRange(out var start, out var end);
        if (start < end)
        {
            _mouseSelectedText = _text[start..end];
            return (true, _mouseSelectedText.Length > 0, null);
        }

        _mouseSelectedText = null;
        var cursor = _frame is not null && row >= 0 && row < _frame.RowMaps.Count
            ? _frame.RowMaps[row].OffsetAt(Math.Max(0, cell))
            : _anchor?.StartOffset ?? 0;
        return (true, false, cursor);
    }

    public IReadOnlyList<string> HighlightRows()
    {
        if (_frame is null) return [];
        var start = _bufferSelectionStart;
        var end = _bufferSelectionEnd;
        if (start is null || end is null)
        {
            if (_anchor is null || _focus is null) return _frame.ContentRows;
            GetRange(out var mouseStart, out var mouseEnd);
            start = mouseStart;
            end = mouseEnd;
        }
        var selectedStart = start.Value;
        var selectedEnd = end.Value;
        if (selectedStart >= selectedEnd) return _frame.ContentRows;

        return _frame.ContentRows.Select((text, row) =>
        {
            var selected = _frame.RowMaps[row].Cells
                .Where(span => span.StartOffset < selectedEnd && span.EndOffset > selectedStart)
                .ToArray();
            return selected.Length == 0
                ? text
                : TerminalTextLayout.HighlightCells(text, selected[0].StartCell, selected[^1].EndCell);
        }).ToArray();
    }

    private bool TryGetPoint(int row, int cell, out Point point)
    {
        point = default;
        if (_frame is null || _frame.RowMaps.Count == 0) return false;
        row = Math.Clamp(row, 0, _frame.RowMaps.Count - 1);
        var rowMap = _frame.RowMaps[row];
        if (rowMap.CellAt(Math.Max(0, cell)) is { } span)
        {
            point = new(span.StartOffset, span.EndOffset);
            return true;
        }

        point = new(rowMap.EndOffset, rowMap.EndOffset);
        return true;
    }

    private void GetRange(out int start, out int end)
    {
        var anchor = _anchor ?? default;
        var focus = _focus ?? anchor;
        if (anchor == focus)
        {
            start = end = anchor.StartOffset;
            return;
        }
        if (anchor.StartOffset <= focus.StartOffset)
        {
            start = anchor.StartOffset;
            end = focus.EndOffset;
        }
        else
        {
            start = focus.StartOffset;
            end = anchor.EndOffset;
        }
    }
}
