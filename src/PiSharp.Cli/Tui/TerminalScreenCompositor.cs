namespace PiSharp.Cli.Tui;

/// <summary>Builds complete terminal frames from transcript, editor, footer and overlay state.</summary>
internal sealed class TerminalScreenCompositor(TextWriter output)
{
    internal sealed record Frame(IReadOnlyList<string> Rows, int CursorRow, int CursorColumn,
        int ScrollOffset, int Columns, int Height);

    public Frame Compose(string editorText, int editorCursor, string transcriptText, string footer,
        IReadOnlyList<string>? overlay, int scrollOffset, int columns, int height,
        TranscriptSearchController search, TerminalMouseRouter mouse)
    {
        var editorHeight = Math.Clamp(height / 3, 1, Math.Max(1, height - 2));
        var footerHeight = height > 2 ? 1 : 0;
        var transcriptHeight = Math.Max(1, height - editorHeight - footerHeight);
        var editor = EditorViewport.Layout(editorText, editorCursor, columns, editorHeight);
        var transcriptWidth = Math.Max(1, columns - 1);
        var visibleTranscript = transcriptText;
        var highlightedSearch = search.Highlight(transcriptText);
        if (search.Query.Length > 0 && highlightedSearch.MatchCount > 0)
        {
            var totalRows = TerminalTranscriptViewport.CountVisualRows(transcriptText, transcriptWidth);
            var selectedRow = TerminalTranscriptViewport.VisualRowAt(transcriptText, highlightedSearch.SelectedTextStart, transcriptWidth);
            scrollOffset = TerminalTranscriptViewport.ScrollOffsetToShow(totalRows, selectedRow, transcriptHeight);
            visibleTranscript = highlightedSearch.HighlightedText;
        }

        var transcriptRows = TerminalTranscriptViewport.WrapWindow(visibleTranscript, transcriptWidth,
            transcriptHeight, scrollOffset, out scrollOffset, out var firstVisualRow);
        var rows = Enumerable.Repeat("", height).ToArray();
        var transcriptStart = scrollOffset > 0 ? 0 : transcriptHeight - transcriptRows.Count;
        mouse.SetTranscript(transcriptRows, firstVisualRow, transcriptStart, transcriptHeight);
        var displayedTranscriptRows = mouse.HighlightTranscript();
        for (var index = 0; index < transcriptRows.Count; index++)
            rows[transcriptStart + index] = displayedTranscriptRows[index];

        var editorStart = transcriptHeight + editorHeight - editor.Rows.Count;
        mouse.SetEditor(editorText, editor, editorStart);
        var displayedEditorRows = mouse.HighlightEditor();
        for (var index = 0; index < editor.Rows.Count; index++)
            rows[editorStart + index] = editor.Rows[index][..2] + displayedEditorRows[index];

        if (footerHeight > 0)
        {
            var footerText = scrollOffset == 0 ? footer : $"↑ {scrollOffset} rows · live output follows at bottom · {footer}";
            rows[^1] = TerminalTranscriptViewport.Clip(footerText, columns - 1);
        }
        if (overlay is not null) TerminalOverlayLayout.Apply(rows, overlay, columns);

        return new(rows, editorStart + editor.CursorRow, editor.CursorColumn, scrollOffset, columns, height);
    }

    public void Render(Frame frame)
    {
        output.Write("\u001b[?2026h\u001b[2J\u001b[H");
        for (var index = 0; index < frame.Rows.Count; index++)
        {
            output.Write(frame.Rows[index]);
            output.Write("\u001b[0m");
            output.Write("\u001b[K");
            if (index < frame.Rows.Count - 1) output.Write("\r\n");
        }
        output.Write($"\u001b[{frame.CursorRow + 1};{Math.Clamp(frame.CursorColumn, 1, frame.Columns)}H\u001b[?25h\u001b[?2026l");
        output.Flush();
    }
}
