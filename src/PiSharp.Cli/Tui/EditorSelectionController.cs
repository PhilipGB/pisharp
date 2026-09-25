namespace PiSharp.Cli.Tui;

/// <summary>Maps mouse selection and cursor placement back to the editor's grapheme offsets.</summary>
internal sealed class EditorSelectionController
{
    private readonly record struct Point(int StartOffset, int EndOffset);

    private string _text = "";
    private EditorViewport.Frame? _frame;
    private Point? _anchor;
    private Point? _focus;
    private bool _dragging;

    public string? SelectedText { get; private set; }

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
        _dragging = false;
        SelectedText = null;
    }

    public bool Begin(int row, int cell)
    {
        if (_frame is null || !TryGetPoint(row, cell, out var point)) return false;
        _anchor = _focus = point;
        _dragging = true;
        SelectedText = null;
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
            SelectedText = _text[start..end];
            return (true, SelectedText.Length > 0, null);
        }

        SelectedText = null;
        var cursor = _frame is not null && row >= 0 && row < _frame.RowMaps.Count
            ? _frame.RowMaps[row].OffsetAt(Math.Max(0, cell))
            : _anchor?.StartOffset ?? 0;
        return (true, false, cursor);
    }

    public IReadOnlyList<string> HighlightRows()
    {
        if (_frame is null || _anchor is null || _focus is null) return _frame?.ContentRows ?? [];
        GetRange(out var start, out var end);
        if (start >= end) return _frame.ContentRows;

        return _frame.ContentRows.Select((text, row) =>
        {
            var selected = _frame.RowMaps[row].Cells
                .Where(span => span.StartOffset < end && span.EndOffset > start)
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
