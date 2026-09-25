namespace PiSharp.Cli.Tui;

/// <summary>Tracks a terminal-cell selection over the transcript rows currently visible on screen.</summary>
internal sealed class TranscriptSelectionController
{
    private readonly record struct Point(int Row, int StartCell, int EndCell);

    private IReadOnlyList<string> _visibleRows = [];
    private int _firstVisualRow;
    private Point? _anchor;
    private Point? _focus;
    private bool _dragging;
    private bool _hasSelection;

    public string? SelectedText { get; private set; }

    public void SetVisibleRows(IReadOnlyList<string> rows, int firstVisualRow)
    {
        _visibleRows = rows;
        _firstVisualRow = firstVisualRow;
    }

    public (bool Changed, bool Copy) HandleMouse(TerminalMouseEvent mouse, int transcriptScreenStart, int transcriptHeight)
    {
        if (mouse.IsWheel) return default;

        if (!mouse.IsRelease && !mouse.IsMotion)
        {
            if ((mouse.Button & 3) != 0) return default;
            if (!TryGetPoint(mouse, transcriptScreenStart, transcriptHeight, clampToTranscript: false, out var point)) return default;
            _anchor = _focus = point;
            _dragging = true;
            _hasSelection = false;
            SelectedText = null;
            return (true, false);
        }

        if (mouse.IsMotion)
        {
            if (!_dragging || !TryGetPoint(mouse, transcriptScreenStart, transcriptHeight, clampToTranscript: true, out var moved)) return default;
            if (_focus == moved) return default;
            _focus = moved;
            _hasSelection = _anchor != moved;
            return (true, false);
        }

        if (!_dragging) return default;
        if (TryGetPoint(mouse, transcriptScreenStart, transcriptHeight, clampToTranscript: true, out var released)) _focus = released;
        _dragging = false;
        _hasSelection = _anchor != _focus;
        SelectedText = _hasSelection ? BuildSelection() : null;
        return (true, SelectedText is { Length: > 0 });
    }

    public IReadOnlyList<string> HighlightVisibleRows()
    {
        if (!_hasSelection || _anchor is not { } anchor || _focus is not { } focus) return _visibleRows;
        GetRange(anchor, focus, out var first, out var startCell, out var last, out var endCell);
        var highlighted = new List<string>(_visibleRows.Count);
        for (var index = 0; index < _visibleRows.Count; index++)
        {
            var row = _firstVisualRow + index;
            var start = row == first ? startCell : 0;
            var end = row == last ? endCell : TerminalTextLayout.Width(_visibleRows[index]);
            highlighted.Add(row >= first && row <= last
                ? TerminalTextLayout.HighlightCells(_visibleRows[index], start, end)
                : _visibleRows[index]);
        }
        return highlighted;
    }

    private bool TryGetPoint(TerminalMouseEvent mouse, int transcriptScreenStart, int transcriptHeight,
        bool clampToTranscript, out Point point)
    {
        point = default;
        if (_visibleRows.Count == 0 || transcriptHeight <= 0) return false;
        var screenRow = mouse.Row - 1;
        if (!clampToTranscript && (screenRow < transcriptScreenStart || screenRow >= transcriptScreenStart + transcriptHeight))
            return false;
        var localRow = Math.Clamp(screenRow - transcriptScreenStart, 0, Math.Min(transcriptHeight, _visibleRows.Count) - 1);
        var cells = TerminalTextLayout.CellRangeAt(_visibleRows[localRow], mouse.Column - 1);
        point = new(_firstVisualRow + localRow, cells.Start, cells.End);
        return true;
    }

    private string BuildSelection()
    {
        if (!_hasSelection || _anchor is not { } anchor || _focus is not { } focus) return "";
        GetRange(anchor, focus, out var first, out var startCell, out var last, out var endCell);
        var firstIndex = first - _firstVisualRow;
        var lastIndex = last - _firstVisualRow;
        if (firstIndex < 0 || lastIndex >= _visibleRows.Count || firstIndex > lastIndex) return "";

        var lines = new List<string>(lastIndex - firstIndex + 1);
        for (var index = firstIndex; index <= lastIndex; index++)
        {
            var row = _visibleRows[index];
            var start = index == firstIndex ? startCell : 0;
            var end = index == lastIndex ? endCell : TerminalTextLayout.Width(row);
            lines.Add(TerminalTextLayout.SliceCells(row, start, end));
        }
        return string.Join('\n', lines);
    }

    private static void GetRange(Point anchor, Point focus, out int firstRow, out int startCell,
        out int lastRow, out int endCell)
    {
        if (Compare(anchor, focus) <= 0)
        {
            firstRow = anchor.Row;
            startCell = anchor.StartCell;
            lastRow = focus.Row;
            endCell = focus.EndCell;
        }
        else
        {
            firstRow = focus.Row;
            startCell = focus.StartCell;
            lastRow = anchor.Row;
            endCell = anchor.EndCell;
        }
    }

    private static int Compare(Point left, Point right)
    {
        var row = left.Row.CompareTo(right.Row);
        return row != 0 ? row : left.StartCell.CompareTo(right.StartCell);
    }
}
