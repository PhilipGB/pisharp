using System.Text.RegularExpressions;
using PiSharp.Extensions.Abstractions;

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
    private readonly bool _useAlternateScreen;
    private IReadOnlyList<string>? _previousRows;
    private IReadOnlyList<string>? _previousDocumentRows;
    private int _previousCursorRow;
    private int _previousRestorationCursorRow;
    private int _previousRestorationCursorColumn;
    private int _previousColumns;
    private int _previousHeight;
    private bool _clearOnShrink;
    private int? _previousContentHeight;
    private int _regularViewportTop;
    private TerminalTextLayout.LayoutResult? _previousTranscriptLayout;
    private int _previousTranscriptFirstVisualRow;
    private int _previousTranscriptScrollOffset;

    public TerminalScreenCompositor(TextWriter output, TerminalImageRenderer images, bool useAlternateScreen = true)
    {
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(images);
        _output = output;
        _images = images;
        _useAlternateScreen = useAlternateScreen;
    }

    public void SetClearOnShrink(bool enabled) => _clearOnShrink = enabled;

    public void InvalidateRenderedFrame() => _previousRows = null;

    internal sealed record Frame(IReadOnlyList<string> Rows, int CursorRow, int CursorColumn,
        int ScrollOffset, int Columns, int Height, bool CursorVisible = true, int RestoreStartRow = 0,
        int ContentHeight = 0, IReadOnlyList<string>? RestorationRows = null,
        int TranscriptStartRow = 0, int TranscriptHeight = 0,
        int RestorationCursorRow = -1, IReadOnlyList<string>? DocumentRows = null,
        int DocumentCursorRow = -1, int DocumentCursorColumn = -1);

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
        string? workingStatus = null,
        string? authorizationUrlCopyStatus = null,
        TerminalStatusNotificationKind authorizationUrlCopyStatusKind = TerminalStatusNotificationKind.Info,
        int editorPaddingX = 0, int outputPad = 0, int autocompleteMaxVisible = 5,
        bool showHardwareCursor = false,
        IReadOnlyList<(string Key, string Text)>? extensionStatuses = null,
        IReadOnlyList<PiSharpExtensionUiLine>? extensionHeader = null,
        IReadOnlyList<PiSharpExtensionUiLine>? extensionWidgetsAbove = null,
        IReadOnlyList<PiSharpExtensionUiLine>? extensionWidgetsBelow = null,
        IReadOnlyList<PiSharpExtensionUiLine>? extensionFooter = null,
        string? workingFrame = "⠋",
        IReadOnlyList<PiSharpExtensionUiLine>? extensionCustomOverlay = null,
        PiSharpExtensionUiOverlayOptions? extensionCustomOverlayOptions = null,
        IReadOnlyList<PiSharpExtensionUiLine>? extensionCustomPanel = null,
        bool workingFrameVerbatim = false,
        string transcriptBottomKeyLabel = "Ctrl+End",
        IReadOnlyList<string>? transcriptSearchPanel = null,
        bool transientScrollbarVisible = false,
        int? transcriptSearchCursorColumn = null,
        string? transientFlash = null)
    {
        var activeTheme = theme ?? TerminalTheme.Default;
        var initialScrollOffset = scrollOffset;
        var homeDirectory = Environment.GetEnvironmentVariable("HOME") ??
            Environment.GetEnvironmentVariable("USERPROFILE");
        var workingDirectory = TerminalPathDisplay.FormatForFooter(Environment.CurrentDirectory, homeDirectory);
        var hasExtensionStatuses = extensionStatuses is { Count: > 0 };
        var baseFooterHeight = height >= 5 ? 2 : height > 2 ? 1 : 0;
        var extensionStatusHeight = hasExtensionStatuses && height > 3 ? 1 : 0;
        var headerRows = LayoutExtensionLines(extensionHeader, columns, activeTheme, maximumRows: height);
        var aboveRows = new[] { "" }.Concat(
            LayoutExtensionLines(extensionWidgetsAbove, columns, activeTheme, maximumRows: height)).ToArray();
        var belowRows = LayoutExtensionLines(extensionWidgetsBelow, columns, activeTheme, maximumRows: height);
        var extensionFooterRows = LayoutExtensionLines(extensionFooter, columns, activeTheme, maximumRows: height)
            .Take(Math.Max(0, height - baseFooterHeight - extensionStatusHeight - 1)).ToArray();
        var footerHeight = baseFooterHeight + extensionStatusHeight + extensionFooterRows.Length;
        var extensionHeaderHeight = Math.Min(headerRows.Count, Math.Max(0, height - footerHeight - 3));
        var borderHeight = height - footerHeight >= 4 ? 2 : 0;
        var suggestionHeight = Math.Min(Math.Min(editorSuggestions?.Count ?? 0, Math.Max(0, autocompleteMaxVisible)),
            Math.Max(0, height - footerHeight - extensionHeaderHeight - borderHeight - 2));
        var editorLineLimit = Math.Max(1,
            Math.Min((int)(height * 0.3), height - footerHeight - extensionHeaderHeight - borderHeight - suggestionHeight - 1));
        var widgetCapacity = Math.Max(0, height - footerHeight - extensionHeaderHeight - borderHeight - suggestionHeight - editorLineLimit - 2);
        var aboveWidgetHeight = Math.Min(aboveRows.Length, widgetCapacity);
        var belowWidgetHeight = Math.Min(belowRows.Count, Math.Max(0, widgetCapacity - aboveWidgetHeight));
        var suggestionNameWidth = editorSuggestions is { Count: > 0 }
            ? Math.Clamp(editorSuggestions.Max(suggestion => TerminalTextLayout.Width(
                suggestion.Command.StartsWith('/') ? suggestion.Command[1..] : suggestion.Command)), 10, 30)
            : 0;
        var boundedEditorPadding = Math.Clamp(editorPaddingX, 0, Math.Max(0, (columns - 2) / 2));
        var editorColumns = Math.Max(1, columns - boundedEditorPadding * 2);
        var maxEditorLines = Math.Max(1,
            Math.Min((int)(height * 0.3), height - footerHeight - extensionHeaderHeight - aboveWidgetHeight -
                belowWidgetHeight - borderHeight - suggestionHeight - 1));
        var allFocusedComponentRows = extensionCustomPanel is null
            ? null : LayoutExtensionLines(extensionCustomPanel, columns, activeTheme, maximumRows: height);
        List<string>? focusedComponentRows = allFocusedComponentRows?.Take(maxEditorLines).ToList();
        if (focusedComponentRows is { Count: 0 }) focusedComponentRows.Add(new string(' ', columns));
        if (focusedComponentRows is { Count: > 0 } && allFocusedComponentRows!.Count > focusedComponentRows.Count)
            focusedComponentRows[^1] = activeTheme.Style("muted", "…") + new string(' ', Math.Max(0, columns - 1));
        var editorLayout = EditorViewport.Layout(focusedComponentRows is null ? editorText : string.Empty,
            focusedComponentRows is null ? editorCursor : 0, editorColumns, maxEditorLines, showPrompt: false);
        var editor = editorLayout with { MouseCellOffset = Math.Max(0, editorLayout.MouseCellOffset + boundedEditorPadding) };
        var editorContentHeight = focusedComponentRows?.Count ?? editor.Rows.Count;
        var editorHeight = editorContentHeight + borderHeight;
        if (height - editorHeight - footerHeight < 1 && borderHeight > 0)
        {
            borderHeight = 0;
            editorHeight = editorContentHeight;
        }
        var boundedOutputPadding = Math.Clamp(outputPad, 0, Math.Max(0, (columns - 2) / 2));
        var transcriptWidth = Math.Max(1, columns - boundedOutputPadding * 2);
        IReadOnlyList<string> startupRows = string.IsNullOrEmpty(startupHeader)
            ? Array.Empty<string>()
            : TerminalTextLayout.Create(startupHeader, transcriptWidth).Rows.Prepend(string.Empty).ToArray();
        var startupHeight = Math.Min(startupRows.Count,
            Math.Max(0, height - editorHeight - footerHeight - suggestionHeight - extensionHeaderHeight -
                aboveWidgetHeight - belowWidgetHeight - 1));
        var transcriptHeight = Math.Max(0,
            height - editorHeight - footerHeight - startupHeight - suggestionHeight - extensionHeaderHeight -
                aboveWidgetHeight - belowWidgetHeight);
        var startupPrefix = string.IsNullOrEmpty(startupContent) ? "" : startupContent + "\n";
        var startupContentRows = startupPrefix.Length == 0 ? 0 :
            TerminalTextLayout.Create(startupPrefix, transcriptWidth).RowCount;
        var startupContentVisualRows = string.IsNullOrEmpty(startupContent) ? 0 :
            TerminalTextLayout.Create(startupContent, transcriptWidth).RowCount;
        var transcriptSource = startupPrefix + transcriptText;
        var transcriptLayout = TerminalTextLayout.Create(transcriptSource, transcriptWidth);
        var visibleTranscriptSource = transcriptSource;
        var visibleLayout = transcriptLayout;
        var highlightedSearch = search.Highlight(transcriptText, activeTheme);
        var hasSearchResult = highlightedSearch.MatchCount > 0;
        var transcriptViewportHeight = transcriptHeight;
        if (search.Query.Length > 0 && highlightedSearch.MatchCount > 0)
        {
            var searchLayout = TerminalTextLayout.Create(transcriptText, transcriptWidth);
            var selectedFirstRow = startupContentRows + searchLayout.VisualRowAt(highlightedSearch.SelectedTextStart);
            var selectedLastRow = startupContentRows + searchLayout.VisualRowAt(
                Math.Max(highlightedSearch.SelectedTextStart, highlightedSearch.SelectedTextEnd - 1));
            scrollOffset = TerminalTranscriptViewport.ScrollOffsetToReveal(transcriptLayout.RowCount,
                selectedFirstRow, selectedLastRow, Math.Max(1, transcriptViewportHeight), scrollOffset);
            visibleTranscriptSource = startupPrefix + highlightedSearch.HighlightedText;
            visibleLayout = TerminalTextLayout.Create(visibleTranscriptSource, transcriptWidth);
        }

        // Pi's follow-end scroll view keeps its absolute scrollTop while new content arrives.
        // This screen stores distance from the bottom, so compensate that offset to keep the
        // first visible source position stable while the reader is away from live output.
        if (highlightedSearch.MatchCount == 0 && scrollOffset > 0 &&
            scrollOffset == _previousTranscriptScrollOffset && _previousTranscriptLayout is { } previousLayout)
        {
            var anchorSourceIndex = previousLayout.SourceIndexAtVisualRow(_previousTranscriptFirstVisualRow);
            var anchoredFirstRow = transcriptLayout.VisualRowAt(anchorSourceIndex);
            scrollOffset = TerminalTranscriptViewport.ScrollOffsetForFirstRow(
                transcriptLayout.RowCount, anchoredFirstRow, transcriptHeight);
        }
        var transcriptRows = TerminalTranscriptViewport.WrapWindow(visibleLayout,
            transcriptViewportHeight, scrollOffset, out var renderedScrollOffset,
            out var firstVisualRow);
        scrollOffset = renderedScrollOffset;
        var showScrollbar = transientScrollbarVisible || scrollOffset != initialScrollOffset;
        _previousTranscriptLayout = transcriptLayout;
        _previousTranscriptFirstVisualRow = firstVisualRow;
        _previousTranscriptScrollOffset = scrollOffset;
        var rows = Enumerable.Repeat("", height).ToArray();
        for (var index = 0; index < extensionHeaderHeight; index++)
            rows[index] = headerRows[index];
        for (var index = 0; index < startupHeight; index++)
            rows[extensionHeaderHeight + index] = TerminalTranscriptViewport.Clip(startupRows[index], columns);
        var transcriptStart = extensionHeaderHeight + startupHeight;
        mouse.SetTranscript(transcriptRows, firstVisualRow, transcriptStart, transcriptViewportHeight,
            boundedOutputPadding);
        var displayedTranscriptRows = mouse.HighlightTranscript();
        for (var index = 0; index < transcriptRows.Count; index++)
        {
            var visualRow = firstVisualRow + index;
            var isStartupContent = visualRow < startupContentVisualRows;
            var continuationIndent = isStartupContent && IsWrappedLineContinuation(
                transcriptSource, transcriptLayout.SourceIndexAtVisualRow(visualRow)) ? " " : "";
            rows[transcriptStart + index] = isStartupContent
                ? PadTranscriptRow(continuationIndent + displayedTranscriptRows[index], columns, outputPadding: 0)
                : PadTranscriptRow(displayedTranscriptRows[index], transcriptWidth, boundedOutputPadding,
                    preserveActiveStyleOnPadding: visualRow + 1 < visibleLayout.RowCount &&
                        IsWrappedLineContinuation(visibleTranscriptSource,
                            visibleLayout.SourceIndexAtVisualRow(visualRow + 1)));
        }
        if (_useAlternateScreen && (scrollOffset > 0 || showScrollbar) &&
            transcriptViewportHeight > 0 && columns > 1)
            ApplyTranscriptScrollIndicators(rows, transcriptStart, transcriptHeight, transcriptLayout.RowCount,
                scrollOffset, columns, transcriptBottomKeyLabel, activeTheme,
                showScrollbar, showEndHint: scrollOffset > 0);

        var aboveStart = transcriptStart + transcriptHeight;
        for (var index = 0; index < aboveWidgetHeight; index++)
            rows[aboveStart + index] = aboveRows[index];
        var editorStart = aboveStart + aboveWidgetHeight;
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
            var description = hasDescription
                ? TerminalSafeText.Normalize(Regex.Replace(suggestion.Description!, @"[\r\n]+", " ").Trim()) : "";
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
        if (focusedComponentRows is null)
        {
            mouse.SetEditor(editorText, editor, editorStart + borderHeight / 2, editorSelectionStart, editorSelectionEnd);
            var displayedEditorRows = mouse.HighlightEditor();
            for (var index = 0; index < editor.Rows.Count; index++)
            {
                var row = displayedEditorRows[index];
                if (index == editor.CursorRow && !showHardwareCursor)
                    row = HighlightCursor(row, editor.RowMaps[index], editorCursor);
                if (boundedEditorPadding > 0) row = new string(' ', boundedEditorPadding) + row;
                var width = TerminalTextLayout.Width(row);
                if (width < columns) row += new string(' ', columns - width);
                rows[editorStart + borderHeight / 2 + index] = row;
            }
        }
        else
        {
            for (var index = 0; index < focusedComponentRows.Count; index++)
                rows[editorStart + borderHeight / 2 + index] = focusedComponentRows[index];
        }

        var belowStart = editorStart + editorHeight + suggestionHeight;
        for (var index = 0; index < belowWidgetHeight; index++)
            rows[belowStart + index] = belowRows[index];

        if (borderHeight > 0)
        {
            var border = activeTheme.Style("thinkingOff", new string('─', columns));
            rows[editorStart] = border;
            rows[editorStart + editorHeight - 1] = border;
            if (!string.IsNullOrWhiteSpace(workingStatus))
            {
                var indicator = workingFrame ?? "";
                var status = indicator.Length == 0 ? workingStatus : $"{indicator} {workingStatus}";
                var statusWidth = TerminalTextLayout.Width(status);
                if (columns >= statusWidth + 5)
                {
                    var tail = new string('─', columns - statusWidth - 4);
                    rows[editorStart] = activeTheme.Style("thinkingOff", "── ") +
                        (workingFrameVerbatim ? indicator : activeTheme.Style("thinkingOff", indicator)) +
                        (indicator.Length == 0 ? "" : " ") +
                        activeTheme.Style("thinkingOff", workingStatus) +
                        activeTheme.Style("thinkingOff", $" {tail}");
                }
            }
        }

        if (footerHeight > 0)
        {
            var footerText = footer;
            if (hasExtensionStatuses && extensionStatusHeight == 0)
                footerText += "  " + string.Join("  ", extensionStatuses!.Select(item => $"{item.Key}: {item.Text}"));
            rows[^1] = activeTheme.Style("dim", TerminalTranscriptViewport.Clip(footerText, columns));
            if (baseFooterHeight > 1)
                rows[^2] = activeTheme.Style("dim",
                    TerminalTranscriptViewport.ClipWithEllipsis(workingDirectory, columns));
        }
        if (extensionStatusHeight > 0)
        {
            var statusText = string.Join("  ", extensionStatuses!.Select(item => $"{item.Key}: {item.Text}"));
            rows[height - baseFooterHeight - extensionStatusHeight] = activeTheme.Style("dim",
                TerminalTranscriptViewport.Clip(statusText, columns));
        }
        var extensionFooterStart = height - baseFooterHeight - extensionStatusHeight - extensionFooterRows.Length;
        for (var index = 0; index < extensionFooterRows.Length; index++)
            rows[extensionFooterStart + index] = extensionFooterRows[index];
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
        var restorationRows = rows.ToArray();
        var documentTranscriptRows = transcriptSource.Length == 0
            ? new List<string>()
            : transcriptLayout.Rows.Select((row, index) =>
            {
                if (index >= startupContentVisualRows)
                    return PadTranscriptRow(row, transcriptWidth, boundedOutputPadding,
                        preserveActiveStyleOnPadding: index + 1 < transcriptLayout.RowCount &&
                            IsWrappedLineContinuation(transcriptSource,
                                transcriptLayout.SourceIndexAtVisualRow(index + 1)));
                var continuationIndent = IsWrappedLineContinuation(
                    transcriptSource, transcriptLayout.SourceIndexAtVisualRow(index)) ? " " : "";
                return PadTranscriptRow(continuationIndent + row, columns, outputPadding: 0);
            }).ToList();
        while (documentTranscriptRows.Count > startupContentVisualRows && IsBlankRow(documentTranscriptRows[^1]))
            documentTranscriptRows.RemoveAt(documentTranscriptRows.Count - 1);
        var documentRows = restorationRows.Take(transcriptStart)
            .Concat(documentTranscriptRows)
            .Concat(restorationRows.Skip(transcriptStart + transcriptHeight)).ToArray();
        var documentCursorRow = editorStart + borderHeight / 2 + editor.CursorRow;
        if (documentCursorRow >= transcriptStart + transcriptHeight)
            documentCursorRow += documentTranscriptRows.Count - transcriptHeight;
        else if (documentCursorRow >= transcriptStart)
            documentCursorRow = transcriptStart + Math.Clamp(documentCursorRow - transcriptStart,
                0, Math.Max(0, documentTranscriptRows.Count - 1));
        if (overlay is not null) TerminalOverlayLayout.Apply(rows, overlay, columns, activeTheme);
        if (transcriptSearchPanel is { Count: > 0 })
            TerminalOverlayLayout.ApplyTranscriptSearch(rows, transcriptSearchPanel, columns);
        var frameCursorRow = editorStart + borderHeight / 2 + editor.CursorRow;
        var frameCursorColumn = editor.CursorColumn + boundedEditorPadding + 1;
        if (transcriptSearchPanel is { Count: > 0 } && transcriptSearchCursorColumn is { } searchCursorColumn)
        {
            var searchPanelTop = Math.Min(1, Math.Max(0, height - transcriptSearchPanel.Count));
            frameCursorRow = Math.Clamp(searchPanelTop + 1, 0, Math.Max(0, height - 1));
            frameCursorColumn = Math.Clamp(searchCursorColumn + 1, 1, columns);
        }

        if (editorPanel is { Count: > 0 })
        {
            var reservedBottom = Math.Clamp(panelBottomMargin, footerHeight, height);
            var panelOverflowsAvailableHeight = editorPanel.Count > height - reservedBottom;
            var visibleCount = panelOverflowsAvailableHeight
                ? Math.Min(editorPanel.Count, Math.Max(1, height - reservedBottom))
                : Math.Min(editorPanel.Count, Math.Max(height, 1));
            var sourceStart = panelOverflowsAvailableHeight ? 0 : editorPanel.Count - visibleCount;
            var panelStart = panelOverflowsAvailableHeight
                ? 1
                : Math.Max(0, height - reservedBottom - visibleCount);
            for (var index = 0; index < visibleCount; index++)
                rows[panelStart + index] = TerminalTranscriptViewport.Clip(editorPanel[sourceStart + index], columns);
            if (height > 2 && panelBottomMargin > 1)
            {
                var directoryRow = panelOverflowsAvailableHeight ? rows.Length - 1 : rows.Length - 2;
                rows[directoryRow] = activeTheme.Style("dim",
                    TerminalTranscriptViewport.ClipWithEllipsis(workingDirectory, columns - 1));
            }
            if (authorizationUrlCopyStatus is not null && panelStart > 0)
            {
                var (style, prefix) = authorizationUrlCopyStatusKind switch
                {
                    TerminalStatusNotificationKind.Warning => ("warning", "Warning: "),
                    TerminalStatusNotificationKind.Error => ("error", "Error: "),
                    _ => ("dim", "")
                };
                rows[panelStart - 1] = activeTheme.Style(style,
                    TerminalTranscriptViewport.Clip(" " + prefix + authorizationUrlCopyStatus, columns));
            }

            var cursorRow = panelCursorRow is { } requestedRow
                ? panelStart + requestedRow - sourceStart
                : panelStart + visibleCount - 1;
            var cursorColumn = panelCursorColumn ?? 1;
            var panelFrame = new Frame(rows, Math.Clamp(cursorRow, 0, Math.Max(0, height - 1)),
                Math.Clamp(cursorColumn, 1, columns + 1), scrollOffset, columns, height, panelCursorVisible,
                RestoreStartRow: panelStart, ContentHeight: startupHeight + transcriptRows.Count,
                RestorationRows: restorationRows, TranscriptStartRow: transcriptStart,
                TranscriptHeight: hasSearchResult ? transcriptViewportHeight : transcriptHeight,
                RestorationCursorRow: editorStart + borderHeight / 2 + editor.CursorRow,
                DocumentRows: documentRows, DocumentCursorRow: documentCursorRow,
                DocumentCursorColumn: editor.CursorColumn + boundedEditorPadding + 1);
            return ApplyTransientFlash(
                ApplyExtensionOverlay(panelFrame, extensionCustomOverlay, extensionCustomOverlayOptions, activeTheme),
                transientFlash);
        }

        var frame = new Frame(rows, frameCursorRow,
            frameCursorColumn, scrollOffset, columns, height,
            CursorVisible: focusedComponentRows is null && showHardwareCursor, RestoreStartRow: editorStart,
            ContentHeight: startupHeight + transcriptRows.Count, RestorationRows: restorationRows,
            TranscriptStartRow: transcriptStart,
            TranscriptHeight: hasSearchResult ? transcriptViewportHeight : transcriptHeight,
            RestorationCursorRow: editorStart + borderHeight / 2 + editor.CursorRow,
            DocumentRows: documentRows, DocumentCursorRow: documentCursorRow,
            DocumentCursorColumn: editor.CursorColumn + boundedEditorPadding + 1);
        var presented = ApplyExtensionOverlay(frame, extensionCustomOverlay, extensionCustomOverlayOptions, activeTheme);
        if (focusedComponentRows is not null) presented = presented with { CursorVisible = false };
        return ApplyTransientFlash(presented, transientFlash);
    }

    private static Frame ApplyTransientFlash(Frame frame, string? message)
    {
        if (string.IsNullOrEmpty(message) || frame.Rows.Count == 0 || frame.Columns <= 0) return frame;
        var text = TerminalTranscriptViewport.Clip($" {message} ", frame.Columns);
        var textWidth = TerminalTextLayout.Width(text);
        if (textWidth == 0) return frame;
        var start = Math.Max(0, frame.Columns - textWidth);
        var rows = frame.Rows.ToArray();
        var prefix = TerminalTextLayout.SliceStyledCells(rows[0], 0, start);
        prefix += new string(' ', Math.Max(0, start - TerminalTextLayout.Width(prefix)));
        rows[0] = prefix + "\u001b[7m" + text + "\u001b[27m";
        return frame with { Rows = rows };
    }

    private static void ApplyTranscriptScrollIndicators(string[] rows, int transcriptStart, int transcriptHeight,
        int contentHeight, int scrollOffset, int columns, string bottomKeyLabel, TerminalTheme theme,
        bool showScrollbar, bool showEndHint)
    {
        var trackColumn = showScrollbar ? columns - 1 : columns;
        var trackHeight = Math.Max(1, transcriptHeight);
        var thumbHeight = Math.Max(Math.Min(2, trackHeight), Math.Min(trackHeight,
            (int)Math.Round((double)trackHeight * trackHeight / Math.Max(1, contentHeight))));
        var maximumScrollTop = Math.Max(0, contentHeight - transcriptHeight);
        var scrollTop = Math.Max(0, maximumScrollTop - scrollOffset);
        var maximumThumbTop = trackHeight - thumbHeight;
        var thumbOffset = maximumScrollTop == 0 ? 0 :
            (int)Math.Round((double)scrollTop / maximumScrollTop * maximumThumbTop);
        for (var index = 0; showScrollbar && index < trackHeight; index++)
        {
            var row = transcriptStart + index;
            if (row < 0 || row >= rows.Length) continue;
            var glyph = index >= thumbOffset && index < thumbOffset + thumbHeight ? "┃" : "│";
            var style = glyph == "┃" ? "scrollbarThumb" : "scrollbarTrack";
            var rowBackgroundLength = LeadingBackgroundSequenceLength(rows[row]);
            var rowBackground = rows[row][..rowBackgroundLength];
            var track = TerminalTranscriptViewport.Clip(rows[row], trackColumn);
            track += new string(' ', Math.Max(0, trackColumn - TerminalTextLayout.Width(track)));
            rows[row] = track + rowBackground + theme.Style(style, glyph);
        }

        if (!showEndHint) return;
        var label = $" ↓ Jump to latest message{(string.IsNullOrWhiteSpace(bottomKeyLabel) ? "" : $" · {bottomKeyLabel}")} ";
        label = TerminalTranscriptViewport.Clip(label, trackColumn);
        var labelWidth = TerminalTextLayout.Width(label);
        var labelStart = Math.Max(0, (columns - labelWidth) / 2);
        var indicatorRow = transcriptStart + trackHeight - 1;
        if (indicatorRow < 0 || indicatorRow >= rows.Length || labelWidth == 0) return;
        var baseRow = rows[indicatorRow];
        var left = TerminalTextLayout.SliceStyledCells(baseRow, 0, labelStart);
        if (TerminalTextLayout.Width(left) < labelStart)
            left += new string(' ', labelStart - TerminalTextLayout.Width(left));
        var rightWidth = Math.Max(0, trackColumn - labelStart - labelWidth);
        var right = TerminalTextLayout.SliceStyledCells(baseRow, labelStart + labelWidth, trackColumn);
        if (TerminalTextLayout.Width(right) < rightWidth)
            right += new string(' ', rightWidth - TerminalTextLayout.Width(right));
        var indicatorIndex = trackHeight - 1;
        var indicatorIsThumb = indicatorIndex >= thumbOffset && indicatorIndex < thumbOffset + thumbHeight;
        var indicatorGlyph = indicatorIsThumb ? "┃" : "│";
        var indicatorStyle = indicatorIsThumb ? "scrollbarThumb" : "scrollbarTrack";
        var backgroundLength = LeadingBackgroundSequenceLength(baseRow);
        var background = baseRow[..backgroundLength];
        var indicator = left +
            theme.Style("selectedBg", theme.Style("text", label), background: true) +
            right + background + (showScrollbar ? theme.Style(indicatorStyle, indicatorGlyph) : "");
        rows[indicatorRow] = indicator;
    }

    private static Frame ApplyExtensionOverlay(Frame frame,
        IReadOnlyList<PiSharpExtensionUiLine>? overlay, PiSharpExtensionUiOverlayOptions? options,
        TerminalTheme theme)
    {
        if (overlay is not { Count: > 0 }) return frame;
        var content = LayoutExtensionLines(overlay, frame.Columns, theme, padRows: false,
            maximumRows: frame.Height);
        var rows = frame.Rows.ToArray();
        TerminalOverlayLayout.ApplyExtension(rows, content, frame.Columns, frame.Height, theme, options);
        return frame with
        {
            Rows = rows,
            CursorRow = Math.Clamp(frame.Height / 2, 0, Math.Max(0, frame.Height - 1)),
            CursorColumn = 1,
            CursorVisible = false
        };
    }

    private static bool IsWrappedLineContinuation(string text, int sourceIndex)
    {
        sourceIndex = Math.Clamp(sourceIndex, 0, text.Length);
        var lineStart = text.LastIndexOf('\n', Math.Max(0, sourceIndex - 1));
        return sourceIndex > lineStart + 1;
    }

    internal static string PadTranscriptRow(string row, int transcriptWidth, int outputPadding,
        bool preserveActiveStyleOnPadding = false)
    {
        var rowWidth = TerminalTextLayout.Width(row);
        var terminalWidth = transcriptWidth + outputPadding * 2;
        if (outputPadding > 0 && rowWidth >= terminalWidth) return row;
        var leadingBackgroundLength = LeadingPanelBackgroundLength(row);
        var hasPanelBackground = leadingBackgroundLength > 0;
        if (rowWidth == 0 && hasPanelBackground)
            return row[..leadingBackgroundLength] + new string(' ', terminalWidth) + "\u001b[0m";

        var transcriptPadding = rowWidth < transcriptWidth ? transcriptWidth - rowWidth : 0;
        var trailingPadding = transcriptPadding + outputPadding;
        var leadingPadding = outputPadding > 0 ? new string(' ', outputPadding) : "";
        if (trailingPadding == 0) return leadingPadding + row;

        const string reset = "\u001b[0m";
        var hasTrailingReset = row.EndsWith(reset, StringComparison.Ordinal);
        var keepStyleOnPadding = hasTrailingReset &&
            TerminalTextLayout.HasActiveSgrAtEnd(row[..^reset.Length]);
        if (hasPanelBackground)
        {
            if (hasTrailingReset) row = row[..^reset.Length];
            if (preserveActiveStyleOnPadding || rowWidth >= transcriptWidth)
            {
                var cellStyle = TerminalTextLayout.SgrStateAtLastCell(row);
                row += (cellStyle.Length > 0 ? cellStyle : row[..leadingBackgroundLength]) +
                    new string(' ', trailingPadding) + reset;
            }
            else row += reset + row[..leadingBackgroundLength] + new string(' ', trailingPadding) + reset;
        }
        else
        {
            if (keepStyleOnPadding) row = row[..^reset.Length];
            row += new string(' ', trailingPadding);
            if (keepStyleOnPadding) row += reset;
        }
        return leadingBackgroundLength > 0
            ? row[..leadingBackgroundLength] + leadingPadding + row[leadingBackgroundLength..]
            : leadingPadding + row;
    }

    private static int LeadingPanelBackgroundLength(string row)
    {
        const string reset = "\u001b[0m";
        var offset = 0;
        while (row.AsSpan(offset).StartsWith(reset, StringComparison.Ordinal)) offset += reset.Length;
        if (!row.AsSpan(offset).StartsWith("\u001b[48;", StringComparison.Ordinal)) return 0;
        var end = row.IndexOf('m', offset);
        return end < 0 ? 0 : end + 1;
    }

    private static int LeadingBackgroundSequenceLength(string row)
    {
        if (!row.StartsWith("\u001b[48;", StringComparison.Ordinal)) return 0;
        var end = row.IndexOf('m');
        return end < 0 ? 0 : end + 1;
    }

    internal static bool IsBlankRow(string row)
    {
        for (var offset = 0; offset < row.Length; offset++)
            if (TerminalImageRenderer.TryReadMarker(row, offset, out _, out _, out _))
                return false;

        if (TerminalTextLayout.HasBackgroundOnWhitespace(row)) return false;
        return TerminalTextLayout.StripFormatting(row).All(char.IsWhiteSpace);
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
        if (!_useAlternateScreen)
        {
            RenderMainScreen(frame);
            return;
        }

        var contentShrank = _clearOnShrink && _previousContentHeight is { } previousContentHeight &&
            frame.ContentHeight < previousContentHeight;
        var fullRedraw = _previousRows is null || _previousColumns != frame.Columns || _previousHeight != frame.Height ||
            contentShrank;
        var rendered = _images.PrepareFrame(frame.Rows, forcePlacementRedraw: fullRedraw);
        _output.Write("\u001b[?2026h");
        if (rendered.Preamble.Length > 0) _output.Write(rendered.Preamble);
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
        _previousDocumentRows = (frame.DocumentRows ?? frame.RestorationRows ?? frame.Rows).ToArray();
        _previousCursorRow = frame.CursorRow;
        _previousRestorationCursorRow = frame.DocumentCursorRow >= 0
            ? frame.DocumentCursorRow
            : frame.RestorationCursorRow >= 0 ? frame.RestorationCursorRow : frame.CursorRow;
        _previousRestorationCursorColumn = frame.DocumentCursorColumn >= 0
            ? frame.DocumentCursorColumn
            : frame.CursorColumn;
        _previousColumns = frame.Columns;
        _previousHeight = frame.Height;
        _previousContentHeight = frame.ContentHeight;
    }

    private void RenderMainScreen(Frame frame)
    {
        var sourceRows = frame.DocumentRows ?? frame.RestorationRows ?? frame.Rows;
        var cursorRow = frame.DocumentCursorRow >= 0
            ? frame.DocumentCursorRow
            : frame.RestorationCursorRow >= 0 ? frame.RestorationCursorRow : frame.CursorRow;
        var cursorColumn = frame.DocumentCursorColumn >= 0 ? frame.DocumentCursorColumn : frame.CursorColumn;
        var widthChanged = _previousColumns != 0 && _previousColumns != frame.Columns;
        var heightChanged = _previousHeight != 0 && _previousHeight != frame.Height;
        var contentShrank = _clearOnShrink && _previousContentHeight is { } previousContentHeight &&
            frame.ContentHeight < previousContentHeight;
        var fullRedraw = _previousRows is null || widthChanged || heightChanged || contentShrank ||
            _previousRows is { } previousRows && previousRows.Count > sourceRows.Count;
        var rendered = _images.PrepareFrame(sourceRows, forcePlacementRedraw: fullRedraw);
        fullRedraw |= rendered.Preamble.Length > 0;
        var rows = rendered.Rows;

        _output.Write("\u001b[?2026h");
        if (rendered.Preamble.Length > 0) _output.Write(rendered.Preamble);
        if (fullRedraw)
        {
            if (_previousRows is not null) _output.Write("\u001b[2J\u001b[H\u001b[3J");
            for (var index = 0; index < rows.Count; index++)
            {
                if (index > 0) _output.Write("\r\n");
                _output.Write(rows[index]);
            }
        }
        else
        {
            var firstChanged = -1;
            var lastChanged = -1;
            for (var index = 0; index < Math.Min(rows.Count, _previousRows!.Count); index++)
            {
                if (string.Equals(_previousRows[index], rows[index], StringComparison.Ordinal)) continue;
                if (firstChanged < 0) firstChanged = index;
                lastChanged = index;
            }

            var appended = rows.Count > _previousRows!.Count;
            if (appended)
            {
                if (firstChanged < 0) firstChanged = _previousRows.Count;
                lastChanged = rows.Count - 1;
            }

            var previousViewportTop = _regularViewportTop;
            var currentCursorRow = _previousCursorRow;
            if (firstChanged >= 0 && firstChanged < previousViewportTop)
            {
                _output.Write("\u001b[2J\u001b[H\u001b[3J");
                for (var index = 0; index < rows.Count; index++)
                {
                    if (index > 0) _output.Write("\r\n");
                    _output.Write(rows[index]);
                }
                fullRedraw = true;
            }
            else if (firstChanged >= 0)
            {
                var previousViewportBottom = previousViewportTop + frame.Height - 1;
                var appendedAtEnd = appended && firstChanged == _previousRows.Count;
                var moveTargetRow = appendedAtEnd ? firstChanged - 1 : firstChanged;
                if (moveTargetRow > previousViewportBottom)
                {
                    var currentScreenRow = Math.Clamp(currentCursorRow - previousViewportTop, 0, frame.Height - 1);
                    var moveToBottom = frame.Height - 1 - currentScreenRow;
                    if (moveToBottom > 0) _output.Write($"\u001b[{moveToBottom}B");
                    var scroll = moveTargetRow - previousViewportBottom;
                    for (var index = 0; index < scroll; index++) _output.Write("\r\n");
                    previousViewportTop += scroll;
                    currentCursorRow = moveTargetRow;
                }

                var rowDelta = moveTargetRow - currentCursorRow;
                if (rowDelta > 0) _output.Write($"\u001b[{rowDelta}B");
                else if (rowDelta < 0) _output.Write($"\u001b[{-rowDelta}A");
                _output.Write(appendedAtEnd ? "\r\n" : "\r");

                for (var index = firstChanged; index <= lastChanged; index++)
                {
                    if (index > firstChanged) _output.Write("\r\n");
                    _output.Write("\u001b[2K");
                    _output.Write(rows[index]);
                }

                var renderedCursorRow = Math.Min(lastChanged, rows.Count - 1);
                if (_previousRows.Count > rows.Count)
                {
                    var extraLines = _previousRows.Count - rows.Count;
                    for (var index = 0; index < extraLines; index++) _output.Write("\r\n\u001b[2K");
                    _output.Write($"\u001b[{extraLines}A");
                }
                _previousCursorRow = renderedCursorRow;
                currentCursorRow = renderedCursorRow;
                _regularViewportTop = Math.Max(previousViewportTop, renderedCursorRow - frame.Height + 1);
            }
            else
            {
                _regularViewportTop = previousViewportTop;
            }
        }

        var viewportTop = _regularViewportTop;
        if (fullRedraw)
        {
            _previousCursorRow = Math.Max(0, rows.Count - 1);
            _regularViewportTop = Math.Max(0, rows.Count - frame.Height);
            viewportTop = _regularViewportTop;
        }

        if (rows.Count > 0)
        {
            var currentScreenRow = Math.Clamp(_previousCursorRow - viewportTop, 0, frame.Height - 1);
            var targetScreenRow = Math.Clamp(cursorRow - viewportTop, 0, frame.Height - 1);
            var cursorDelta = targetScreenRow - currentScreenRow;
            if (cursorDelta > 0) _output.Write($"\u001b[{cursorDelta}B");
            else if (cursorDelta < 0) _output.Write($"\u001b[{-cursorDelta}A");
            _output.Write($"\r\u001b[{Math.Clamp(cursorColumn, 1, frame.Columns)}G");
            _previousCursorRow = viewportTop + targetScreenRow;
        }

        _output.Write($"\u001b[?25{(frame.CursorVisible ? 'h' : 'l')}\u001b[?2026l");
        _output.Flush();
        _previousRows = rows.ToArray();
        _previousDocumentRows = rows.ToArray();
        _previousRestorationCursorRow = cursorRow;
        _previousRestorationCursorColumn = cursorColumn;
        _previousColumns = frame.Columns;
        _previousHeight = frame.Height;
        _previousContentHeight = frame.ContentHeight;
    }

    public void LeaveMainScreen()
    {
        if (_previousRows is not { Count: > 0 } rows)
        {
            _output.Write("\u001b[?25h");
            _output.Flush();
            return;
        }

        var targetDocumentRow = rows.Count;
        var viewportBottom = _regularViewportTop + Math.Max(1, _previousHeight) - 1;
        var currentScreenRow = Math.Clamp(_previousCursorRow - _regularViewportTop, 0, Math.Max(0, _previousHeight - 1));
        var targetScreenRow = Math.Clamp(targetDocumentRow - _regularViewportTop, 0, Math.Max(0, _previousHeight - 1));
        _output.Write(" ");
        if (targetDocumentRow > viewportBottom)
        {
            var moveToBottom = Math.Max(0, _previousHeight - 1 - currentScreenRow);
            if (moveToBottom > 0) _output.Write($"\u001b[{moveToBottom}B");
            _output.Write("\r\n");
        }
        else
        {
            var rowDelta = targetScreenRow - currentScreenRow;
            if (rowDelta > 0) _output.Write($"\u001b[{rowDelta}B");
            else if (rowDelta < 0) _output.Write($"\u001b[{-rowDelta}A");
            _output.Write("\r\n");
        }
        _output.Write("\u001b[?25h");
        _output.Flush();
    }

    public void LeaveAlternateScreen(string terminalModeRestore = "")
    {
        _output.Write("\u001b[?2026h" + terminalModeRestore + "\u001b[?1049l\u001b[?25h\u001b[?2026l");
        _output.Flush();
    }

    public void RestoreLastFrameToNormalBuffer()
    {
        var documentRows = _previousDocumentRows is null
            ? Array.Empty<string>()
            : _images.PrepareFallbackFrame(_previousDocumentRows);
        _output.Write("\u001b[?2026h\u001b[?7l");
        for (var index = 0; index < documentRows.Count; index++)
        {
            if (index > 0) _output.Write("\r\n");
            _output.Write("\r\u001b[2K");
            _output.Write(documentRows[index]);
        }
        _output.Write("\u001b[0m\u001b[?7h\u001b[?25h\u001b[?2026l");
        _output.Flush();

        if (documentRows.Count > 0)
        {
            var cursorRow = Math.Clamp(_previousRestorationCursorRow, 0, documentRows.Count - 1);
            var rowsBelowCursor = documentRows.Count - 1 - cursorRow;
            var rowsToLeaveDocument = documentRows.Count - cursorRow;
            _output.Write("\u001b[?25l");
            if (rowsBelowCursor > 0) _output.Write($"\u001b[{rowsBelowCursor}A");
            _output.Write($"\u001b[{Math.Max(1, _previousRestorationCursorColumn)}G\u001b[0m ");
            if (rowsToLeaveDocument > 0) _output.Write($"\u001b[{rowsToLeaveDocument}B");
            _output.Write("\r\n\u001b[?25h");
            _output.Flush();
        }
    }

    private static IReadOnlyList<string> LayoutExtensionLines(
        IReadOnlyList<PiSharpExtensionUiLine>? lines, int columns, TerminalTheme theme, bool padRows = true,
        int maximumRows = int.MaxValue)
    {
        if (lines is null || lines.Count == 0 || maximumRows <= 0) return [];
        var rendered = new List<string>();
        foreach (var line in lines.Take(200))
        {
            var safe = TerminalSafeText.Normalize(line.Text ?? string.Empty);
            foreach (var logicalLine in safe.Split('\n').Take(200))
            {
                var wrapped = TerminalTextLayout.Create(logicalLine, Math.Max(1, columns)).Rows;
                foreach (var row in wrapped)
                {
                    if (rendered.Count >= maximumRows) return rendered;
                    var styled = row;
                    if (!string.IsNullOrWhiteSpace(line.Style))
                    {
                        try { styled = theme.Style(line.Style, row); }
                        catch (ArgumentOutOfRangeException) { }
                    }
                    var clipped = TerminalTranscriptViewport.Clip(styled, columns);
                    if (clipped.Contains("\u001b[", StringComparison.Ordinal) &&
                        !clipped.EndsWith("\u001b[0m", StringComparison.Ordinal))
                        clipped += "\u001b[0m";
                    var width = TerminalTextLayout.Width(clipped);
                    if (padRows && width < columns) clipped += new string(' ', columns - width);
                    rendered.Add(clipped);
                }
            }
        }
        return rendered;
    }

}
