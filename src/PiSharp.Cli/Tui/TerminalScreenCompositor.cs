namespace PiSharp.Cli.Tui;

internal enum TerminalStatusNotificationKind
{
    Info,
    Warning,
    Error
}

/// <summary>Builds complete terminal frames from transcript, editor, footer and overlay state.</summary>
internal sealed class TerminalScreenCompositor
{
    private readonly TextWriter _output;
    private readonly TerminalImageRenderer _images;
    private IReadOnlyList<string>? _previousRows;
    private IReadOnlyList<string>? _previousDocumentRows;
    private int _previousCursorRow;
    private int _previousRestoreStartRow;
    private int _previousColumns;
    private int _previousHeight;

    public TerminalScreenCompositor(TextWriter output, TerminalImageRenderer images)
    {
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(images);
        _output = output;
        _images = images;
    }

    internal sealed record Frame(IReadOnlyList<string> Rows, int CursorRow, int CursorColumn,
        int ScrollOffset, int Columns, int Height, bool CursorVisible = true, int RestoreStartRow = 0);

    public Frame Compose(string editorText, int editorCursor, int? editorSelectionStart, int? editorSelectionEnd,
        string transcriptText, string footer,
        IReadOnlyList<string>? overlay, int scrollOffset, int columns, int height,
        TranscriptSearchController search, TerminalMouseRouter mouse, TerminalTheme? theme = null,
        IReadOnlyList<string>? editorPanel = null, int? panelCursorRow = null,
        int? panelCursorColumn = null, bool panelCursorVisible = false, int panelBottomMargin = 1,
        string? statusNotification = null,
        TerminalStatusNotificationKind statusNotificationKind = TerminalStatusNotificationKind.Info,
        bool statusNotificationIsExtension = false,
        string? startupHeader = null, string? startupContent = null,
        IReadOnlyList<EditorCompletionSuggestion>? editorSuggestions = null,
        string? workingStatus = null)
    {
        var activeTheme = theme ?? TerminalTheme.Default;
        var footerHeight = height >= 5 ? 2 : height > 2 ? 1 : 0;
        var borderHeight = height - footerHeight >= 4 ? 2 : 0;
        var suggestionHeight = Math.Min(editorSuggestions?.Count ?? 0,
            Math.Max(0, height - footerHeight - borderHeight - 2));
        var suggestionNameWidth = editorSuggestions is { Count: > 0 }
            ? editorSuggestions.Max(suggestion => Math.Min(30, TerminalTextLayout.Width(
                suggestion.Command.StartsWith('/') ? suggestion.Command[1..] : suggestion.Command)))
            : 0;
        var maxEditorLines = Math.Max(1,
            Math.Min((int)(height * 0.3), height - footerHeight - borderHeight - suggestionHeight - 1));
        var editor = EditorViewport.Layout(editorText, editorCursor, columns, maxEditorLines, showPrompt: false);
        var editorHeight = editor.Rows.Count + borderHeight;
        if (height - editorHeight - footerHeight < 1 && borderHeight > 0)
        {
            borderHeight = 0;
            editorHeight = editor.Rows.Count;
        }
        var transcriptWidth = Math.Max(1, columns);
        var startupRows = string.IsNullOrEmpty(startupHeader)
            ? Array.Empty<string>()
            : TerminalTextLayout.Create(startupHeader, transcriptWidth).Rows;
        var startupHeight = Math.Min(startupRows.Count,
            Math.Max(0, height - editorHeight - footerHeight - suggestionHeight - 1));
        var transcriptHeight = Math.Max(1,
            height - editorHeight - footerHeight - startupHeight - suggestionHeight);
        var startupPrefix = string.IsNullOrEmpty(startupContent) ? "" : startupContent + "\n";
        var startupContentRows = startupPrefix.Length == 0 ? 0 :
            TerminalTextLayout.Create(startupPrefix, transcriptWidth).RowCount;
        var transcriptLayout = TerminalTextLayout.Create(startupPrefix + transcriptText, transcriptWidth);
        var visibleLayout = transcriptLayout;
        var highlightedSearch = search.Highlight(transcriptText);
        if (search.Query.Length > 0 && highlightedSearch.MatchCount > 0)
        {
            var searchLayout = TerminalTextLayout.Create(transcriptText, transcriptWidth);
            var selectedRow = startupContentRows + searchLayout.VisualRowAt(highlightedSearch.SelectedTextStart);
            scrollOffset = TerminalTranscriptViewport.ScrollOffsetToShow(transcriptLayout.RowCount, selectedRow, transcriptHeight);
            visibleLayout = TerminalTextLayout.Create(startupPrefix + highlightedSearch.HighlightedText, transcriptWidth);
        }

        var transcriptRows = TerminalTranscriptViewport.WrapWindow(visibleLayout,
            transcriptHeight, scrollOffset, out scrollOffset, out var firstVisualRow);
        var rows = Enumerable.Repeat("", height).ToArray();
        for (var index = 0; index < startupHeight; index++)
            rows[index] = TerminalTranscriptViewport.Clip(startupRows[index], columns);
        var transcriptStart = startupHeight;
        mouse.SetTranscript(transcriptRows, firstVisualRow, transcriptStart, transcriptHeight);
        var displayedTranscriptRows = mouse.HighlightTranscript();
        for (var index = 0; index < transcriptRows.Count; index++)
        {
            var row = displayedTranscriptRows[index];
            var rowWidth = TerminalTextLayout.Width(row);
            if (rowWidth is > 0 && rowWidth < columns) row += new string(' ', columns - rowWidth);
            rows[transcriptStart + index] = row;
        }

        var editorStart = startupHeight + transcriptHeight;
        for (var index = 0; index < suggestionHeight; index++)
        {
            var suggestion = editorSuggestions![index];
            var selected = index == 0;
            var marker = selected ? "→ " : "  ";
            var name = suggestion.Command.StartsWith('/') ? suggestion.Command[1..] : suggestion.Command;
            if (TerminalTextLayout.Width(name) > 30) name = name[..Math.Min(name.Length, 30)];
            var hasDescription = !string.IsNullOrEmpty(suggestion.Description);
            var spacing = hasDescription
                ? new string(' ', Math.Max(0, suggestionNameWidth + 2 - TerminalTextLayout.Width(name)))
                : "";
            var description = hasDescription ? TerminalSafeText.Normalize(suggestion.Description!) : "";
            var suggestionText = selected
                ? activeTheme.Style("accent", marker + name + spacing + description)
                : marker + name + (hasDescription ? activeTheme.Style("muted", spacing + description) : "");
            var suggestionRow = TerminalTranscriptViewport.Clip(suggestionText, Math.Max(0, columns - 2));
            if (suggestionText.Contains("\u001b[", StringComparison.Ordinal) &&
                !suggestionRow.EndsWith("\u001b[0m", StringComparison.Ordinal))
                suggestionRow += "\u001b[0m";
            var suggestionWidth = TerminalTextLayout.Width(suggestionRow);
            if (suggestionWidth < columns) suggestionRow += new string(' ', columns - suggestionWidth);
            rows[editorStart + editorHeight + index] = suggestionRow;
        }
        mouse.SetEditor(editorText, editor, editorStart + borderHeight / 2, editorSelectionStart, editorSelectionEnd);
        var displayedEditorRows = mouse.HighlightEditor();
        for (var index = 0; index < editor.Rows.Count; index++)
        {
            var row = displayedEditorRows[index];
            if (index == editor.CursorRow)
                row = HighlightCursor(row, editor.RowMaps[index], editorCursor);
            var width = TerminalTextLayout.Width(row);
            if (width < columns) row += new string(' ', columns - width);
            rows[editorStart + borderHeight / 2 + index] = row;
        }

        if (borderHeight > 0)
        {
            var border = activeTheme.Style("thinkingOff", new string('─', columns));
            rows[editorStart] = border;
            rows[editorStart + editorHeight - 1] = border;
            if (!string.IsNullOrWhiteSpace(workingStatus))
            {
                var status = $"⠋ {workingStatus}";
                var statusWidth = TerminalTextLayout.Width(status);
                if (columns >= statusWidth + 5)
                {
                    var tail = new string('─', columns - statusWidth - 4);
                    rows[editorStart] = activeTheme.Style("thinkingOff", "── ") +
                        activeTheme.Style("thinkingOff", "⠋") + " " +
                        activeTheme.Style("thinkingOff", workingStatus) +
                        activeTheme.Style("thinkingOff", $" {tail}");
                }
            }
        }

        if (footerHeight > 0)
        {
            var footerText = scrollOffset == 0 ? footer : $"↑ {scrollOffset} rows · live output follows at bottom · {footer}";
            rows[^1] = activeTheme.Style("dim", TerminalTranscriptViewport.Clip(footerText, columns));
            if (footerHeight > 1)
                rows[^2] = activeTheme.Style("dim",
                    TerminalTranscriptViewport.Clip(Environment.CurrentDirectory, columns));
        }
        if (statusNotification is not null && height > 1)
        {
            var (style, prefix) = statusNotificationKind switch
            {
                TerminalStatusNotificationKind.Warning => ("warning", "Warning: "),
                TerminalStatusNotificationKind.Error => ("error", "Error: "),
                _ => ("dim", "")
            };
            var statusText = prefix + statusNotification;
            var statusRow = Math.Min(startupHeight + 1, height - 1);
            rows[statusRow] = statusNotificationIsExtension
                ? " " + activeTheme.Style(style, TerminalTranscriptViewport.Clip(statusText, columns - 1)) +
                  new string(' ', Math.Max(0, columns - 1 - TerminalTextLayout.Width(statusText)))
                : activeTheme.Style(style, TerminalTranscriptViewport.Clip(" " + statusText, columns));
        }
        if (overlay is not null) TerminalOverlayLayout.Apply(rows, overlay, columns, activeTheme);

        if (editorPanel is { Count: > 0 })
        {
            var visibleCount = Math.Min(editorPanel.Count, Math.Max(height, 1));
            var sourceStart = editorPanel.Count - visibleCount;
            var panelStart = Math.Max(0, height - Math.Clamp(panelBottomMargin, footerHeight, height) - visibleCount);
            for (var index = 0; index < visibleCount; index++)
                rows[panelStart + index] = TerminalTranscriptViewport.Clip(editorPanel[sourceStart + index], columns);
            if (height > 2 && panelBottomMargin > 1)
                rows[^2] = activeTheme.Style("dim",
                    TerminalTranscriptViewport.Clip(Environment.CurrentDirectory, columns - 1));

            var cursorRow = panelCursorRow is { } requestedRow
                ? panelStart + requestedRow - sourceStart
                : panelStart + visibleCount - 1;
            var cursorColumn = panelCursorColumn ?? 1;
            return new(rows, Math.Clamp(cursorRow, 0, Math.Max(0, height - 1)),
                Math.Clamp(cursorColumn, 1, columns + 1), scrollOffset, columns, height, panelCursorVisible,
                RestoreStartRow: panelStart);
        }

        return new(rows, editorStart + borderHeight / 2 + editor.CursorRow, editor.CursorColumn + 1,
            scrollOffset, columns, height, CursorVisible: false, RestoreStartRow: editorStart);
    }

    private static string HighlightCursor(string row, EditorViewport.RowMap rowMap, int cursor)
    {
        var cell = rowMap.Cells.FirstOrDefault(span => span.StartOffset == cursor);
        var start = cell?.StartCell ?? (cursor >= rowMap.EndOffset ? rowMap.Cells.LastOrDefault()?.EndCell ?? 0 : 0);
        var end = cell?.EndCell ?? start + 1;
        if (cell is null && TerminalTextLayout.Width(row) <= start) row += " ";
        return TerminalTextLayout.HighlightCells(row, start, end);
    }

    public void Render(Frame frame)
    {
        var rendered = _images.PrepareFrame(frame.Rows);
        _output.Write("\u001b[?2026h");
        if (rendered.Preamble.Length > 0) _output.Write(rendered.Preamble);
        var fullRedraw = _previousRows is null || _previousColumns != frame.Columns || _previousHeight != frame.Height;
        var redrawAllRows = fullRedraw || rendered.Preamble.Length > 0;
        if (fullRedraw) _output.Write("\u001b[2J");
        for (var index = 0; index < frame.Rows.Count; index++)
        {
            if (!redrawAllRows && string.Equals(_previousRows![index], rendered.Rows[index], StringComparison.Ordinal))
                continue;
            _output.Write($"\u001b[{index + 1};1H");
            _output.Write("\u001b[2K");
            _output.Write(rendered.Rows[index]);
            _output.Write("\u001b[0m");
        }
        if (frame.CursorColumn > frame.Columns)
        {
            // Preserve the terminal's pending-wrap cursor state on a fully padded panel row.
            _output.Write($"\u001b[{frame.CursorRow + 1};1H");
            _output.Write(rendered.Rows[frame.CursorRow]);
            _output.Write("\u001b[0m");
        }
        else
            _output.Write($"\u001b[{frame.CursorRow + 1};{Math.Clamp(frame.CursorColumn, 1, frame.Columns)}H");
        _output.Write($"\u001b[?25{(frame.CursorVisible ? 'h' : 'l')}\u001b[?2026l");
        _output.Flush();
        _previousRows = rendered.Rows.ToArray();
        _previousDocumentRows = frame.Rows.ToArray();
        _previousCursorRow = frame.CursorRow;
        _previousRestoreStartRow = frame.RestoreStartRow;
        _previousColumns = frame.Columns;
        _previousHeight = frame.Height;
    }

    public void RestoreLastFrameToNormalBuffer()
    {
        var documentRows = _previousDocumentRows is null
            ? Array.Empty<string>()
            : _images.PrepareFallbackFrame(_previousDocumentRows);
        _output.Write("\u001b[?2026h");
        var firstRow = Math.Clamp(_previousRestoreStartRow, 0, documentRows.Count);
        if (firstRow < documentRows.Count)
        {
            // The saved primary-buffer cursor begins one row above Pi's restored document.
            _output.Write("\r\n");
            for (var index = firstRow; index < documentRows.Count; index++)
            {
                if (index > firstRow) _output.Write("\r\n");
                _output.Write("\r\u001b[2K");
                var row = index == _previousCursorRow
                    ? documentRows[index].Replace("\u001b[7m", "", StringComparison.Ordinal)
                        .Replace("\u001b[27m", "", StringComparison.Ordinal)
                    : documentRows[index];
                _output.Write(row);
            }
        }
        _output.Write("\u001b[0m\u001b[?7h\r\n\r\n\u001b[?25h\u001b[?2026l");
        _output.Flush();
    }

    public void RestoreDocumentToNormalBuffer(IReadOnlyList<string> documentRows)
    {
        ArgumentNullException.ThrowIfNull(documentRows);
        var document = documentRows.ToArray();
        for (var index = 0; index < document.Length; index++)
        {
            var width = TerminalTextLayout.Width(document[index]);
            if (width is > 0 && width < _previousColumns)
                document[index] += new string(' ', _previousColumns - width);
        }
        var tail = _previousDocumentRows is null
            ? Array.Empty<string>()
            : _previousDocumentRows.Skip(Math.Clamp(_previousRestoreStartRow, 0, _previousDocumentRows.Count)).ToArray();
        var cursorRow = document.Length + _previousCursorRow - _previousRestoreStartRow;
        var rows = document.Concat(tail).ToArray();
        var renderedRows = _images.PrepareFallbackFrame(rows);
        _output.Write("\u001b[?2026h\u001b[2J\u001b[H\u001b[3J\u001b[?7l");
        for (var index = 0; index < renderedRows.Count; index++)
        {
            if (index > 0) _output.Write("\r\n");
            _output.Write("\r");
            _output.Write(renderedRows[index]);
        }
        _output.Write("\u001b[0m\u001b[?7h\u001b[?25h\u001b[?2026l");
        _output.Flush();
        if (cursorRow >= 0 && cursorRow < renderedRows.Count)
        {
            var rowsAboveCursor = Math.Max(0, renderedRows.Count - 1 - cursorRow);
            if (rowsAboveCursor > 0) _output.Write($"\u001b[{rowsAboveCursor}A");
            _output.Write("\u001b[1G\u001b[?25l ");
            _output.Write($"\u001b[{rowsAboveCursor + 1}B\r\n\u001b[?25h");
            _output.Flush();
        }
    }

    public void LeaveAlternateScreen(string terminalModeRestore = "", bool restoreDocumentFollows = false)
    {
        _output.Write("\u001b[?2026h" + terminalModeRestore + "\u001b[?1049l" +
            (restoreDocumentFollows ? "" : "\u001b[?7l") + "\u001b[?2026l");
        _output.Flush();
    }

}
