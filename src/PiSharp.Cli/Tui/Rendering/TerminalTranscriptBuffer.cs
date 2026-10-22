using System.Text;

namespace PiSharp.Cli.Tui;

/// <summary>Owns bounded active transcript state, tool previews, and post-screen scrollback capture.</summary>
internal sealed class TerminalTranscriptBuffer
{
    private const int MaximumTranscriptCharacters = 1_000_000;
    private const int MaximumTranscriptSegments = 10_000;
    private const int MaximumCapturedCharacters = 4_000_000;
    private const int ToolPreviewContentRows = 10;
    private const int MaximumRememberedLiveTools = 1_024;
    private readonly List<CapturedChunkBuilder> _captured = [];
    private readonly List<TranscriptSegment> _segments = [];
    private readonly Queue<string> _liveToolOrder = [];
    private int _capturedCharacters;
    private int _transcriptCharacters;
    private bool _captureTruncated;
    private Func<TerminalTheme, string>? _latestStatusRenderer;
    private string? _latestCapturedStatus;
    private readonly Dictionary<string, (TranscriptSegment Segment, CapturedChunkBuilder? Capture)> _liveTools = new(StringComparer.Ordinal);

    public bool IsExpanded { get; private set; }
    public bool HasContent => _segments.Count > 0;
    public bool CaptureTruncated => _captureTruncated;
    public int Revision { get; private set; }

    public void Append(string text, bool isError, bool isToolResult = false,
        string? capturedText = null, string? collapsedPreviewText = null)
    {
        if (text.Length == 0) return;
        Capture(capturedText ?? text, isError);
        if (!isToolResult && _segments.Count > 0 && !_segments[^1].IsCollapsible &&
            _segments[^1].MarkdownSource is null && _segments[^1].ThemeRenderer is null)
            _segments[^1].Text.Append(text);
        else
            _segments.Add(new(isToolResult, new StringBuilder(text),
                isToolResult ? collapsedPreviewText ?? text : null));
        _transcriptCharacters += text.Length;
        TrimTranscript();
        Revision++;
    }

    public void AppendCaptured(string text, bool isError) => Capture(text, isError);

    public void AppendMarkdown(string source, string rendered, bool isError)
    {
        if (rendered.Length == 0) return;
        Capture(rendered, isError);
        _segments.Add(new(false, new StringBuilder(rendered), null,
            source.Length <= MaximumTranscriptCharacters ? source : null));
        _transcriptCharacters += rendered.Length;
        TrimTranscript();
        Revision++;
    }

    public void AppendThinkingBlock(string source, string hiddenText,
        Func<TerminalTheme, string> visibleRenderer, Func<TerminalTheme, string> hiddenRenderer,
        TerminalTheme theme, bool hidden)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentException.ThrowIfNullOrWhiteSpace(hiddenText);
        ArgumentNullException.ThrowIfNull(visibleRenderer);
        ArgumentNullException.ThrowIfNull(hiddenRenderer);
        ArgumentNullException.ThrowIfNull(theme);

        const string truncated = "[earlier thinking text omitted]\n";
        var boundedSource = source.Length <= MaximumTranscriptCharacters ? source :
            truncated + source[^Math.Max(0, MaximumTranscriptCharacters - truncated.Length)..];
        var selectedRenderer = hidden ? hiddenRenderer : visibleRenderer;
        var rendered = selectedRenderer(theme);
        var captured = CaptureThinking(hidden ? hiddenText : boundedSource);
        _segments.Add(new(false, new StringBuilder(rendered), null,
            themeRenderer: selectedRenderer, isThinkingBlock: true, thinkingSource: boundedSource,
            hiddenThinkingText: hiddenText, visibleThinkingRenderer: visibleRenderer,
            hiddenThinkingRenderer: hiddenRenderer, thinkingHidden: hidden, capturedThinkingChunk: captured));
        _transcriptCharacters += rendered.Length;
        TrimTranscript();
        Revision++;
    }

    public void AppendThemed(string rendered, bool isError, bool isToolResult,
        Func<TerminalTheme, string> themeRenderer, string capturedText, string? collapsedPreviewText = null,
        Func<TerminalTheme, string>? collapsedRenderer = null, bool isCollapsible = false)
    {
        ArgumentNullException.ThrowIfNull(themeRenderer);
        if (rendered.Length == 0) return;
        Capture(capturedText, isError);
        var collapsible = isToolResult || isCollapsible;
        _segments.Add(new(collapsible, new StringBuilder(rendered),
            collapsible ? collapsedPreviewText ?? rendered : null,
            themeRenderer: themeRenderer, collapsedRenderer: collapsedRenderer));
        _transcriptCharacters += rendered.Length;
        TrimTranscript();
        Revision++;
    }

    internal void AppendLiveTool(string id, string rendered, string collapsed,
        Func<TerminalTheme, string> expandedRenderer, Func<TerminalTheme, string> collapsedRenderer,
        string captured)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentNullException.ThrowIfNull(expandedRenderer);
        ArgumentNullException.ThrowIfNull(collapsedRenderer);
        if (rendered.Length == 0) return;

        var capture = CaptureReplaceable(captured, isError: true);
        var segment = new TranscriptSegment(true, new StringBuilder(rendered), collapsed,
            themeRenderer: expandedRenderer, collapsedRenderer: collapsedRenderer);
        _segments.Add(segment);
        TrackLiveTool(id, (segment, capture));
        _transcriptCharacters += rendered.Length;
        TrimTranscript();
        Revision++;
    }

    public void AppendThemedStatus(string rendered, Func<TerminalTheme, string> themeRenderer, string capturedText)
    {
        ArgumentNullException.ThrowIfNull(themeRenderer);
        if (rendered.Length == 0) return;

        _latestStatusRenderer = themeRenderer;
        _latestCapturedStatus = capturedText;
        if (_segments.Count > 0 && _segments[^1].IsStatusMessage)
        {
            var last = _segments[^1];
            _transcriptCharacters += rendered.Length - last.Text.Length;
            last.Text.Clear().Append(rendered);
            last.ThemeRenderer = themeRenderer;
            last.CapturedStatusChunk = ReplaceCapturedStatus(last.CapturedStatusChunk, capturedText);
        }
        else
        {
            var capturedStatus = CaptureStatus(capturedText);
            _segments.Add(new(false, new StringBuilder(rendered), null,
                themeRenderer: themeRenderer, isStatusMessage: true, capturedStatusChunk: capturedStatus));
            _transcriptCharacters += rendered.Length;
        }

        TrimTranscript();
        Revision++;
    }

    internal void SetToolResult(string id, string rendered, string collapsed,
        Func<TerminalTheme, string> expandedRenderer, Func<TerminalTheme, string> collapsedRenderer,
        string captured, bool isError, bool finished)
    {
        if (!_liveTools.TryGetValue(id, out var row) || !_segments.Contains(row.Segment))
        {
            var chunk = row.Capture;
            if (chunk is null && _capturedCharacters < MaximumCapturedCharacters)
            {
                chunk = new CapturedChunkBuilder(isError, new StringBuilder(), isReplaceable: true);
                _captured.Add(chunk);
            }
            if (chunk is null) _captureTruncated = true;
            var segment = new TranscriptSegment(true, new StringBuilder(), collapsed) { KeepLastPreviewLine = true };
            _segments.Add(segment);
            row = (segment, chunk);
        }
        _transcriptCharacters += rendered.Length - row.Segment.Text.Length;
        row.Segment.Text.Clear().Append(rendered);
        row.Segment.KeepLastPreviewLine = true;
        row.Segment.CollapsedPreviewSource = collapsed;
        row.Segment.InvalidateCollapsedPreview();
        row.Segment.ThemeRenderer = expandedRenderer;
        row.Segment.CollapsedRenderer = collapsedRenderer;
        if (row.Capture is { } capture)
        {
            capture.IsError = isError;
            ReplaceCapturedThinking(capture, captured);
        }
        if (finished) _liveTools.Remove(id);
        else TrackLiveTool(id, row);
        TrimTranscript();
        Revision++;
    }

    public void ReRenderMarkdown(Func<string, string> render)
    {
        ArgumentNullException.ThrowIfNull(render);
        foreach (var segment in _segments)
        {
            if (segment.MarkdownSource is not { } source) continue;
            var previousLength = segment.Text.Length;
            var next = render(source);
            segment.Text.Clear().Append(next);
            _transcriptCharacters += next.Length - previousLength;
        }
        TrimTranscript();
        Revision++;
    }

    public void ReRenderThemedSegments(TerminalTheme theme)
    {
        ArgumentNullException.ThrowIfNull(theme);
        foreach (var segment in _segments)
        {
            if (segment.ThemeRenderer is not { } render) continue;
            var previousLength = segment.Text.Length;
            var next = render(theme);
            segment.Text.Clear().Append(next);
            if (segment.IsCollapsible)
            {
                segment.CollapsedPreviewSource = segment.CollapsedRenderer?.Invoke(theme) ?? next;
                segment.InvalidateCollapsedPreview();
            }
            _transcriptCharacters += next.Length - previousLength;
        }
        TrimTranscript();
        Revision++;
    }

    public string GetText(int width)
    {
        return GetText(width, latestStatus: null);
    }

    public string GetTextForScrollback(int width, TerminalTheme theme)
    {
        ArgumentNullException.ThrowIfNull(theme);
        return GetText(width, _latestStatusRenderer is { } renderLatest ? () => renderLatest(theme) : null);
    }

    private string GetText(int width, Func<string>? latestStatus)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(width, 1);
        var output = new StringBuilder(_transcriptCharacters);
        foreach (var segment in _segments)
        {
            if (segment.IsStatusMessage && latestStatus is not null)
                output.Append(latestStatus());
            else
                output.Append(segment.IsCollapsible && !IsExpanded
                    ? GetCollapsedPreview(segment, width)
                    : segment.Text.ToString());
        }
        return output.ToString();
    }

    public string GetRetainedText()
    {
        var output = new StringBuilder(_transcriptCharacters);
        foreach (var segment in _segments) output.Append(segment.Text);
        return output.ToString();
    }

    public bool ToggleExpanded()
    {
        IsExpanded = !IsExpanded;
        return IsExpanded;
    }

    public void SetExpanded(bool expanded) => IsExpanded = expanded;

    public void SetThinkingBlocksHidden(bool hidden, TerminalTheme theme)
    {
        ArgumentNullException.ThrowIfNull(theme);
        var changed = false;
        foreach (var segment in _segments.Where(segment => segment.IsThinkingBlock))
        {
            if (segment.ThinkingHidden == hidden) continue;
            var renderer = hidden ? segment.HiddenThinkingRenderer! : segment.VisibleThinkingRenderer!;
            var rendered = renderer(theme);
            _transcriptCharacters += rendered.Length - segment.Text.Length;
            segment.Text.Clear().Append(rendered);
            segment.ThemeRenderer = renderer;
            segment.ThinkingHidden = hidden;
            ReplaceCapturedThinking(segment.CapturedThinkingChunk,
                hidden ? segment.HiddenThinkingText! : segment.ThinkingSource!);
            changed = true;
        }
        if (!changed) return;
        TrimTranscript();
        Revision++;
    }

    public void SetHiddenThinkingLabel(string label, TerminalTheme theme)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(label);
        ArgumentNullException.ThrowIfNull(theme);
        var changed = false;
        foreach (var segment in _segments.Where(segment => segment.IsThinkingBlock))
        {
            segment.HiddenThinkingText = label;
            if (!segment.ThinkingHidden) continue;
            var renderer = segment.HiddenThinkingRenderer!;
            var rendered = renderer(theme);
            _transcriptCharacters += rendered.Length - segment.Text.Length;
            segment.Text.Clear().Append(rendered);
            segment.ThemeRenderer = renderer;
            ReplaceCapturedThinking(segment.CapturedThinkingChunk, label);
            changed = true;
        }
        if (!changed) return;
        TrimTranscript();
        Revision++;
    }

    public void Clear()
    {
        _liveTools.Clear();
        _captured.Clear();
        _segments.Clear();
        _capturedCharacters = 0;
        _transcriptCharacters = 0;
        _captureTruncated = false;
        _latestStatusRenderer = null;
        _latestCapturedStatus = null;
        _liveToolOrder.Clear();
        Revision++;
    }

    public IReadOnlyList<CapturedChunk> CaptureSnapshot() => _captured
        .Select(chunk => new CapturedChunk(chunk.IsError, chunk.Text.ToString())).ToArray();

    public IReadOnlyList<CapturedChunk> CaptureScrollbackSnapshot() => _captured
        .Select(chunk => new CapturedChunk(chunk.IsError,
            chunk.IsStatusMessage ? _latestCapturedStatus ?? chunk.Text.ToString() : chunk.Text.ToString()))
        .ToArray();

    private void TrimTranscript()
    {
        var excess = _transcriptCharacters - MaximumTranscriptCharacters;
        while (excess > 0 && _segments.Count > 0)
        {
            var first = _segments[0];
            if (first.Text.Length <= excess)
            {
                excess -= first.Text.Length;
                _transcriptCharacters -= first.Text.Length;
                _segments.RemoveAt(0);
                continue;
            }

            var remove = excess;
            var lookLength = Math.Min(first.Text.Length - remove, 64 * 1024);
            var trim = first.Text.ToString(remove, lookLength).IndexOf('\n');
            if (trim >= 0) remove += trim + 1;
            first.Text.Remove(0, remove);
            first.MarkdownSource = null;
            first.ThemeRenderer = null;
            first.CollapsedRenderer = null;
            first.IsThinkingBlock = false;
            first.ThinkingSource = null;
            first.VisibleThinkingRenderer = null;
            first.HiddenThinkingRenderer = null;
            first.CapturedThinkingChunk = null;
            if (first.IsCollapsible)
            {
                first.CollapsedPreviewSource = first.Text.ToString();
                first.InvalidateCollapsedPreview();
            }
            _transcriptCharacters -= remove;
            break;
        }

        while (_segments.Count > MaximumTranscriptSegments)
        {
            _transcriptCharacters -= _segments[0].Text.Length;
            _segments.RemoveAt(0);
        }
    }

    private void Capture(string text, bool isError)
    {
        var remaining = MaximumCapturedCharacters - _capturedCharacters;
        if (remaining <= 0)
        {
            _captureTruncated = true;
            return;
        }
        if (text.Length > remaining)
        {
            text = text[..remaining];
            _captureTruncated = true;
        }
        if (_captured.Count > 0 && _captured[^1].IsError == isError && !_captured[^1].IsStatusMessage && !_captured[^1].IsReplaceable)
            _captured[^1].Text.Append(text);
        else
            _captured.Add(new(isError, new StringBuilder(text)));
        _capturedCharacters += text.Length;
    }

    private CapturedChunkBuilder? CaptureReplaceable(string text, bool isError)
    {
        var remaining = MaximumCapturedCharacters - _capturedCharacters;
        if (remaining <= 0)
        {
            _captureTruncated = true;
            return null;
        }
        if (text.Length > remaining)
        {
            text = text[..remaining];
            _captureTruncated = true;
        }
        var chunk = new CapturedChunkBuilder(isError, new StringBuilder(text), isReplaceable: true);
        _captured.Add(chunk);
        _capturedCharacters += text.Length;
        return chunk;
    }

    private void TrackLiveTool(string id, (TranscriptSegment Segment, CapturedChunkBuilder? Capture) row)
    {
        if (!_liveTools.ContainsKey(id)) _liveToolOrder.Enqueue(id);
        _liveTools[id] = row;
        while (_liveToolOrder.Count > MaximumRememberedLiveTools)
            _liveTools.Remove(_liveToolOrder.Dequeue());
    }

    private CapturedChunkBuilder? CaptureThinking(string text)
    {
        var remaining = MaximumCapturedCharacters - _capturedCharacters;
        if (remaining <= 0)
        {
            _captureTruncated = true;
            return null;
        }
        if (text.Length > remaining)
        {
            text = text[..remaining];
            _captureTruncated = true;
        }
        var chunk = new CapturedChunkBuilder(false, new StringBuilder(text));
        _captured.Add(chunk);
        _capturedCharacters += text.Length;
        return chunk;
    }

    private void ReplaceCapturedThinking(CapturedChunkBuilder? chunk, string text)
    {
        if (chunk is null) return;
        var remaining = MaximumCapturedCharacters - (_capturedCharacters - chunk.Text.Length);
        if (text.Length > remaining)
        {
            text = text[..Math.Max(0, remaining)];
            _captureTruncated = true;
        }
        _capturedCharacters += text.Length - chunk.Text.Length;
        chunk.Text.Clear().Append(text);
    }

    private CapturedChunkBuilder? CaptureStatus(string text)
    {
        var remaining = MaximumCapturedCharacters - _capturedCharacters;
        if (remaining <= 0)
        {
            _captureTruncated = true;
            return null;
        }
        if (text.Length > remaining)
        {
            text = text[..remaining];
            _captureTruncated = true;
        }
        var chunk = new CapturedChunkBuilder(false, new StringBuilder(text), isStatusMessage: true);
        _captured.Add(chunk);
        _capturedCharacters += text.Length;
        return chunk;
    }

    private CapturedChunkBuilder? ReplaceCapturedStatus(CapturedChunkBuilder? chunk, string text)
    {
        if (chunk is null) return CaptureStatus(text);

        var remaining = Math.Max(0, MaximumCapturedCharacters - (_capturedCharacters - chunk.Text.Length));
        if (text.Length > remaining)
        {
            text = text[..remaining];
            _captureTruncated = true;
        }
        _capturedCharacters += text.Length - chunk.Text.Length;
        chunk.Text.Clear().Append(text);
        return chunk;
    }

    private static string GetCollapsedPreview(TranscriptSegment segment, int width)
    {
        if (segment.CollapsedPreview is { } cached && segment.CollapsedPreviewWidth == width) return cached;

        var text = segment.CollapsedPreviewSource ?? segment.Text.ToString();
        var layout = TerminalTextLayout.Create(text, width);
        var hasTrailingNewline = text.EndsWith('\n');
        var visibleRows = layout.RowCount - (hasTrailingNewline ? 1 : 0);
        var hiddenRows = visibleRows - ToolPreviewContentRows;
        if (hiddenRows > 0 && segment.KeepLastPreviewLine)
        {
            var footerIndex = visibleRows - 1;
            // Styled panel padding remains below the footer; it must not replace it.
            while (footerIndex > 0 && IsWhitespaceVisualRow(layout.Rows[footerIndex])) footerIndex--;
            var bodyRowCount = Math.Min(ToolPreviewContentRows, footerIndex);
            hiddenRows = Math.Max(0, footerIndex - bodyRowCount);
            var footer = layout.Rows[footerIndex];
            var body = string.Join('\n', layout.Rows.Take(bodyRowCount));
            var notice = hiddenRows > 0 ? "\n" + FormatPreviewHint(hiddenRows, width) : "";
            var trailingRows = layout.Rows.Skip(footerIndex + 1).Take(visibleRows - footerIndex - 1);
            var trailing = string.Join('\n', trailingRows);
            segment.CollapsedPreview = body + notice + "\n" + footer +
                (trailing.Length > 0 ? "\n" + trailing : "") + (hasTrailingNewline ? "\n" : "");
            segment.CollapsedPreviewWidth = width;
            return segment.CollapsedPreview;
        }
        var preview = hiddenRows <= 0
            ? text
            : string.Join('\n', layout.Rows.Take(ToolPreviewContentRows)) + "\n" + FormatPreviewHint(hiddenRows, width) +
                (hasTrailingNewline ? "\n" : "");
        segment.CollapsedPreview = preview;
        segment.CollapsedPreviewWidth = width;
        return preview;
    }

    private static bool IsWhitespaceVisualRow(string row)
    {
        for (var offset = 0; offset < row.Length;)
        {
            if (TerminalTextLayout.TryReadEscape(row, offset, out var escapeLength))
            {
                offset += escapeLength;
                continue;
            }
            if (!char.IsWhiteSpace(row[offset])) return false;
            offset++;
        }
        return true;
    }

    private static string FormatPreviewHint(int hiddenRows, int width)
    {
        var hint = $"... ({hiddenRows} more visual rows; tool output collapsed)";
        if (TerminalTextLayout.Width(hint) <= width) return hint;

        var compactHint = $"… +{hiddenRows} more rows";
        return TerminalTranscriptViewport.Clip(compactHint, width);
    }

    private sealed class TranscriptSegment(bool isCollapsible, StringBuilder text, string? collapsedPreviewSource,
        string? markdownSource = null, Func<TerminalTheme, string>? themeRenderer = null,
        Func<TerminalTheme, string>? collapsedRenderer = null, bool isStatusMessage = false,
        CapturedChunkBuilder? capturedStatusChunk = null, bool isThinkingBlock = false,
        string? thinkingSource = null, string? hiddenThinkingText = null,
        Func<TerminalTheme, string>? visibleThinkingRenderer = null,
        Func<TerminalTheme, string>? hiddenThinkingRenderer = null, bool thinkingHidden = false,
        CapturedChunkBuilder? capturedThinkingChunk = null)
    {
        public bool IsCollapsible { get; } = isCollapsible;
        public bool KeepLastPreviewLine { get; set; }
        public bool IsStatusMessage { get; } = isStatusMessage;
        public bool IsThinkingBlock { get; set; } = isThinkingBlock;
        public bool ThinkingHidden { get; set; } = thinkingHidden;
        public StringBuilder Text { get; } = text;
        public string? CollapsedPreviewSource { get; set; } = collapsedPreviewSource;
        public string? CollapsedPreview { get; set; }
        public int CollapsedPreviewWidth { get; set; } = -1;
        public string? MarkdownSource { get; set; } = markdownSource;
        public Func<TerminalTheme, string>? ThemeRenderer { get; set; } = themeRenderer;
        public Func<TerminalTheme, string>? CollapsedRenderer { get; set; } = collapsedRenderer;
        public CapturedChunkBuilder? CapturedStatusChunk { get; set; } = capturedStatusChunk;
        public string? ThinkingSource { get; set; } = thinkingSource;
        public string? HiddenThinkingText { get; set; } = hiddenThinkingText;
        public Func<TerminalTheme, string>? VisibleThinkingRenderer { get; set; } = visibleThinkingRenderer;
        public Func<TerminalTheme, string>? HiddenThinkingRenderer { get; set; } = hiddenThinkingRenderer;
        public CapturedChunkBuilder? CapturedThinkingChunk { get; set; } = capturedThinkingChunk;

        public void InvalidateCollapsedPreview()
        {
            CollapsedPreview = null;
            CollapsedPreviewWidth = -1;
        }
    }

    private sealed class CapturedChunkBuilder(bool isError, StringBuilder text, bool isStatusMessage = false, bool isReplaceable = false)
    {
        public bool IsError { get; set; } = isError;
        public bool IsStatusMessage { get; } = isStatusMessage;
        public bool IsReplaceable { get; } = isReplaceable;
        public StringBuilder Text { get; } = text;
    }

    internal readonly record struct CapturedChunk(bool IsError, string Text);
}
