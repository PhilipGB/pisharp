using System.Text;

namespace PiSharp.Cli.Tui;

/// <summary>Owns bounded active transcript state, tool previews, and post-screen scrollback capture.</summary>
internal sealed class TerminalTranscriptBuffer
{
    private const int MaximumTranscriptCharacters = 1_000_000;
    private const int MaximumTranscriptSegments = 10_000;
    private const int MaximumCapturedCharacters = 4_000_000;
    private const int ToolPreviewLines = 10;
    private readonly List<CapturedChunkBuilder> _captured = [];
    private readonly List<TranscriptSegment> _segments = [];
    private int _capturedCharacters;
    private int _transcriptCharacters;
    private bool _captureTruncated;

    public bool IsExpanded { get; private set; }
    public bool CaptureTruncated => _captureTruncated;
    public int Revision { get; private set; }

    public void Append(string text, bool isError, bool isToolResult = false,
        string? capturedText = null, string? collapsedPreviewText = null)
    {
        if (text.Length == 0) return;
        Capture(capturedText ?? text, isError);
        if (!isToolResult && _segments.Count > 0 && !_segments[^1].IsToolResult && _segments[^1].MarkdownSource is null)
            _segments[^1].Text.Append(text);
        else
            _segments.Add(new(isToolResult, new StringBuilder(text),
                isToolResult ? PreviewToolResult(collapsedPreviewText ?? text) : null));
        _transcriptCharacters += text.Length;
        TrimTranscript();
        Revision++;
    }

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

    public string GetText()
    {
        var output = new StringBuilder(_transcriptCharacters);
        foreach (var segment in _segments)
            output.Append(segment.IsToolResult && !IsExpanded ? segment.CollapsedPreview : segment.Text.ToString());
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

    public IReadOnlyList<CapturedChunk> CaptureSnapshot() => _captured
        .Select(chunk => new CapturedChunk(chunk.IsError, chunk.Text.ToString())).ToArray();

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
            if (first.IsToolResult) first.CollapsedPreview = PreviewToolResult(first.Text.ToString());
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
        if (_captured.Count > 0 && _captured[^1].IsError == isError)
            _captured[^1].Text.Append(text);
        else
            _captured.Add(new(isError, new StringBuilder(text)));
        _capturedCharacters += text.Length;
    }

    private static string PreviewToolResult(string text)
    {
        var endsWithNewline = text.EndsWith('\n');
        var lines = text.Split('\n');
        var visibleLines = lines.Length - (endsWithNewline ? 1 : 0);
        if (visibleLines <= ToolPreviewLines) return text;
        var remaining = visibleLines - ToolPreviewLines;
        var preview = string.Join('\n', lines.Take(ToolPreviewLines));
        return $"{preview}\n... ({remaining} more lines; tool output collapsed){(endsWithNewline ? "\n" : "")}";
    }

    private sealed class TranscriptSegment(bool isToolResult, StringBuilder text, string? collapsedPreview,
        string? markdownSource = null)
    {
        public bool IsToolResult { get; } = isToolResult;
        public StringBuilder Text { get; } = text;
        public string? CollapsedPreview { get; set; } = collapsedPreview;
        public string? MarkdownSource { get; set; } = markdownSource;
    }

    private sealed class CapturedChunkBuilder(bool isError, StringBuilder text)
    {
        public bool IsError { get; } = isError;
        public StringBuilder Text { get; } = text;
    }

    internal readonly record struct CapturedChunk(bool IsError, string Text);
}
