namespace PiSharp.Cli.Tui;

/// <summary>Tracks a terminal-cell selection over the transcript rows currently visible on screen.</summary>
internal sealed class TranscriptSelectionController(TimeProvider? timeProvider = null)
{
    private readonly record struct Point(int Row, int StartCell, int EndCell);

    private IReadOnlyList<string> _visibleRows = [];
    private int _firstVisualRow;
    private Point? _anchor;
    private Point? _focus;
    private bool _dragging;
    private bool _hasSelection;
    private enum Granularity { Character, Word, Line }
    private Granularity _granularity;
    private (Point Start, Point End)? _initialRange;
    private (long Timestamp, int Count, int Row, int Start, int End)? _lastClick;

    public string? SelectedText { get; private set; }

    public void Clear()
    {
        _anchor = null;
        _focus = null;
        _dragging = false;
        _hasSelection = false;
        SelectedText = null;
        _initialRange = null;
        _lastClick = null;
        _granularity = Granularity.Character;
    }

    public void SetVisibleRows(IReadOnlyList<string> rows, int firstVisualRow)
    {
        _visibleRows = rows;
        _firstVisualRow = firstVisualRow;
    }

    public (bool Changed, bool Copy) HandleMouse(TerminalMouseEvent mouse, int transcriptScreenStart,
        int transcriptHeight, int transcriptCellOffset)
    {
        if (mouse.IsWheel) return default;

        if (!mouse.IsRelease && !mouse.IsMotion)
        {
            if ((mouse.Button & 3) != 0) return default;
            if (!TryGetPoint(mouse, transcriptScreenStart, transcriptHeight, transcriptCellOffset,
                clampToTranscript: false, out var point)) return default;
            var word = WordRange(point);
            var clock = timeProvider ?? TimeProvider.System;
            var now = clock.GetTimestamp();
            var count = word is { } span && _lastClick is { } previous &&
                clock.GetElapsedTime(previous.Timestamp, now) <= TimeSpan.FromMilliseconds(500) &&
                previous.Row == point.Row && previous.Start == span.Start.StartCell && previous.End == span.End.StartCell
                ? previous.Count % 3 + 1 : 1;
            _lastClick = word is { } clicked
                ? (now, count, point.Row, clicked.Start.StartCell, clicked.End.StartCell) : null;
            _granularity = count == 2 ? Granularity.Word : count == 3 ? Granularity.Line : Granularity.Character;
            _initialRange = count == 2 ? word : count == 3 ? LineRange(point) : null;
            _anchor = _initialRange?.Start ?? point;
            _focus = _initialRange?.End ?? point;
            _dragging = true;
            _hasSelection = _anchor != _focus;
            SelectedText = null;
            return (true, false);
        }

        if (mouse.IsMotion)
        {
            if (!_dragging || !TryGetPoint(mouse, transcriptScreenStart, transcriptHeight, transcriptCellOffset,
                clampToTranscript: true, out var moved)) return default;
            _lastClick = null;
            var previousAnchor = _anchor;
            var previousFocus = _focus;
            UpdateFocus(moved);
            if (previousAnchor == _anchor && previousFocus == _focus) return default;
            _hasSelection = _anchor != _focus;
            return (true, false);
        }

        if (!_dragging) return default;
        if (TryGetPoint(mouse, transcriptScreenStart, transcriptHeight, transcriptCellOffset,
            clampToTranscript: true, out var released)) UpdateFocus(released);
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

    private void UpdateFocus(Point point)
    {
        if (_granularity == Granularity.Character || _initialRange is not { } initial)
        {
            _focus = point;
            return;
        }
        var range = _granularity == Granularity.Word ? WordRange(point) : LineRange(point);
        if (range is not { } selected) return;
        if (Compare(selected.Start, initial.Start) < 0)
        {
            _anchor = initial.End;
            _focus = selected.Start;
        }
        else
        {
            _anchor = initial.Start;
            _focus = selected.End;
        }
    }

    private (Point Start, Point End)? WordRange(Point point)
    {
        var localRow = point.Row - _firstVisualRow;
        if (localRow < 0 || localRow >= _visibleRows.Count ||
            TerminalWordSelection.RangeAt(_visibleRows[localRow], point.StartCell) is not { } range) return null;
        return (new(point.Row, range.Start, range.Start), new(point.Row, range.End, range.End));
    }

    private (Point Start, Point End) LineRange(Point point)
    {
        var width = TerminalTextLayout.Width(_visibleRows[point.Row - _firstVisualRow]);
        return (new(point.Row, 0, 0), new(point.Row, width, width));
    }

    private bool TryGetPoint(TerminalMouseEvent mouse, int transcriptScreenStart, int transcriptHeight,
        int transcriptCellOffset, bool clampToTranscript, out Point point)
    {
        point = default;
        if (_visibleRows.Count == 0 || transcriptHeight <= 0) return false;
        var screenRow = mouse.Row - 1;
        if (!clampToTranscript && (screenRow < transcriptScreenStart || screenRow >= transcriptScreenStart + transcriptHeight))
            return false;
        var localRow = Math.Clamp(screenRow - transcriptScreenStart, 0, Math.Min(transcriptHeight, _visibleRows.Count) - 1);
        var cells = TerminalTextLayout.CellRangeAt(_visibleRows[localRow],
            Math.Max(0, mouse.Column - 1 - transcriptCellOffset));
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
            lines.Add(TerminalTextLayout.SliceCells(row, start, end).TrimEnd());
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
