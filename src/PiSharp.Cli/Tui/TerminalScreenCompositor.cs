namespace PiSharp.Cli.Tui;

/// <summary>Builds complete terminal frames from transcript, editor, footer and overlay state.</summary>
internal sealed class TerminalScreenCompositor
{
    private readonly TextWriter _output;
    private readonly TerminalImageRenderer _images;

    public TerminalScreenCompositor(TextWriter output, TerminalImageRenderer images)
    {
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(images);
        _output = output;
        _images = images;
    }

    internal sealed record Frame(IReadOnlyList<string> Rows, int CursorRow, int CursorColumn,
        int ScrollOffset, int Columns, int Height, bool CursorVisible = true);

    public Frame Compose(string editorText, int editorCursor, int? editorSelectionStart, int? editorSelectionEnd,
        string transcriptText, string footer,
        IReadOnlyList<string>? overlay, int scrollOffset, int columns, int height,
        TranscriptSearchController search, TerminalMouseRouter mouse, TerminalTheme? theme = null,
        IReadOnlyList<string>? editorPanel = null, int? panelCursorRow = null,
        int? panelCursorColumn = null, bool panelCursorVisible = false, int panelBottomMargin = 1)
    {
        var editorHeight = Math.Clamp(height / 3, 1, Math.Max(1, height - 2));
        var footerHeight = height > 2 ? 1 : 0;
        var transcriptHeight = Math.Max(1, height - editorHeight - footerHeight);
        var editor = EditorViewport.Layout(editorText, editorCursor, columns, editorHeight);
        var transcriptWidth = Math.Max(1, columns - 1);
        var transcriptLayout = TerminalTextLayout.Create(transcriptText, transcriptWidth);
        var visibleLayout = transcriptLayout;
        var highlightedSearch = search.Highlight(transcriptText);
        if (search.Query.Length > 0 && highlightedSearch.MatchCount > 0)
        {
            var selectedRow = transcriptLayout.VisualRowAt(highlightedSearch.SelectedTextStart);
            scrollOffset = TerminalTranscriptViewport.ScrollOffsetToShow(transcriptLayout.RowCount, selectedRow, transcriptHeight);
            visibleLayout = TerminalTextLayout.Create(highlightedSearch.HighlightedText, transcriptWidth);
        }

        var transcriptRows = TerminalTranscriptViewport.WrapWindow(visibleLayout,
            transcriptHeight, scrollOffset, out scrollOffset, out var firstVisualRow);
        var rows = Enumerable.Repeat("", height).ToArray();
        var transcriptStart = scrollOffset > 0 ? 0 : transcriptHeight - transcriptRows.Count;
        mouse.SetTranscript(transcriptRows, firstVisualRow, transcriptStart, transcriptHeight);
        var displayedTranscriptRows = mouse.HighlightTranscript();
        for (var index = 0; index < transcriptRows.Count; index++)
            rows[transcriptStart + index] = displayedTranscriptRows[index];

        var editorStart = transcriptHeight + editorHeight - editor.Rows.Count;
        mouse.SetEditor(editorText, editor, editorStart, editorSelectionStart, editorSelectionEnd);
        var displayedEditorRows = mouse.HighlightEditor();
        for (var index = 0; index < editor.Rows.Count; index++)
            rows[editorStart + index] = editor.Rows[index][..2] + displayedEditorRows[index];

        if (footerHeight > 0)
        {
            var footerText = scrollOffset == 0 ? footer : $"↑ {scrollOffset} rows · live output follows at bottom · {footer}";
            rows[^1] = (theme ?? TerminalTheme.Default).Style("muted", TerminalTranscriptViewport.Clip(footerText, columns - 1));
            if (footerHeight > 1)
                rows[^2] = (theme ?? TerminalTheme.Default).Style("dim",
                    TerminalTranscriptViewport.Clip(Environment.CurrentDirectory, columns - 1));
        }
        if (overlay is not null) TerminalOverlayLayout.Apply(rows, overlay, columns, theme ?? TerminalTheme.Default);

        if (editorPanel is { Count: > 0 })
        {
            var visibleCount = Math.Min(editorPanel.Count, Math.Max(height, 1));
            var sourceStart = editorPanel.Count - visibleCount;
            var panelStart = Math.Max(0, height - Math.Clamp(panelBottomMargin, footerHeight, height) - visibleCount);
            for (var index = 0; index < visibleCount; index++)
                rows[panelStart + index] = TerminalTranscriptViewport.Clip(editorPanel[sourceStart + index], columns);
            if (height > 2 && panelBottomMargin > 1)
                rows[^2] = (theme ?? TerminalTheme.Default).Style("dim",
                    TerminalTranscriptViewport.Clip(Environment.CurrentDirectory, columns - 1));

            var cursorRow = panelCursorRow is { } requestedRow
                ? panelStart + requestedRow - sourceStart
                : panelStart + visibleCount - 1;
            var cursorColumn = panelCursorColumn ?? 1;
            return new(rows, Math.Clamp(cursorRow, 0, Math.Max(0, height - 1)),
                Math.Clamp(cursorColumn, 1, columns + 1), scrollOffset, columns, height, panelCursorVisible);
        }

        return new(rows, editorStart + editor.CursorRow, editor.CursorColumn, scrollOffset, columns, height);
    }

    public void Render(Frame frame)
    {
        var rendered = _images.PrepareFrame(frame.Rows);
        _output.Write("\u001b[?2026h");
        if (rendered.Preamble.Length > 0) _output.Write(rendered.Preamble);
        _output.Write("\u001b[2J\u001b[H");
        for (var index = 0; index < frame.Rows.Count; index++)
        {
            _output.Write($"\u001b[{index + 1};1H");
            _output.Write(rendered.Rows[index]);
            _output.Write("\u001b[0m");
        }
        if (frame.CursorColumn > frame.Columns)
        {
            // Preserve the terminal's pending-wrap cursor state on a fully padded panel row.
            _output.Write($"\u001b[{frame.CursorRow + 1};1H");
            _output.Write(rendered.Rows[frame.CursorRow]);
        }
        else
            _output.Write($"\u001b[{frame.CursorRow + 1};{Math.Clamp(frame.CursorColumn, 1, frame.Columns)}H");
        _output.Write($"\u001b[?25{(frame.CursorVisible ? 'h' : 'l')}\u001b[?2026l");
        _output.Flush();
    }
}
