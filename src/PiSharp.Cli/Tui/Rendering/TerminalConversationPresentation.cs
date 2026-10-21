using System.Text;
using Microsoft.Extensions.AI;
using PiSharp.Runtime.Extensions;

namespace PiSharp.Cli.Tui;

/// <summary>Projects user, tool and streamed assistant content into the retained transcript.</summary>
internal sealed class TerminalConversationPresentation(TerminalTranscriptBuffer transcript,
    TerminalImageRenderer imageRenderer, Func<int> columns, Func<TerminalTheme> getTheme)
{
    private string _markdownCodeBlockIndent = "  ";
    private int _outputPadding = 1;
    private string _toolExpandKeyLabel = "Ctrl+O";
    private string _liveAssistant = "";
    private string _liveAssistantSource = "";
    private bool _liveAssistantStreaming;
    private bool _liveAssistantNeedsSpacer;
    private string _liveThinking = "";
    private string _liveThinkingSource = "";
    private bool _liveThinkingNeedsSpacer;
    private string _hiddenThinkingLabel = "Thinking...";
    private bool _thinkingBlocksHidden;
    private bool _hasConversationContent;
    private bool _lastSegmentWasToolResult;
    private bool _historyContinuationNeedsSpacer;
    private readonly Dictionary<string, ToolPanelState> _toolPanelsByCall = new(StringComparer.Ordinal);

    public string LiveText => _liveThinking + _liveAssistant;
    public bool ThinkingBlocksHidden => _thinkingBlocksHidden;
    public bool HasContent => _hasConversationContent;

    public void Reset()
    {
        _liveAssistant = "";
        _liveAssistantSource = "";
        _liveAssistantStreaming = false;
        _liveAssistantNeedsSpacer = false;
        _liveThinking = "";
        _liveThinkingSource = "";
        _liveThinkingNeedsSpacer = false;
        _lastSegmentWasToolResult = false;
        _historyContinuationNeedsSpacer = false;
        _toolPanelsByCall.Clear();
        _hasConversationContent = false;
    }

    public void MarkHistoryLoaded(bool endsWithAssistantContent) =>
        _historyContinuationNeedsSpacer = endsWithAssistantContent;

    public void ReRender()
    {
        transcript.ReRenderMarkdown(markdown => RenderMarkdown(markdown));
        transcript.ReRenderThemedSegments(getTheme());
        _liveAssistant = _liveAssistantSource.Length == 0 ? "" :
            (_liveAssistantNeedsSpacer ? Environment.NewLine : "") + RenderMarkdown(_liveAssistantSource, _liveAssistantStreaming);
        _liveThinking = _liveThinkingSource.Length == 0 ? "" :
            (_liveThinkingNeedsSpacer ? Environment.NewLine : "") +
            RenderThinking(getTheme(), _liveThinkingSource, _thinkingBlocksHidden);
    }

    public void ReRenderMarkdown()
    {
        transcript.ReRenderMarkdown(markdown => RenderMarkdown(markdown));
        _liveAssistant = _liveAssistantSource.Length == 0 ? "" :
            (_liveAssistantNeedsSpacer ? Environment.NewLine : "") + RenderMarkdown(_liveAssistantSource, _liveAssistantStreaming);
    }

    public bool SetCodeBlockIndent(string indent)
    {
        if (string.Equals(_markdownCodeBlockIndent, indent, StringComparison.Ordinal)) return false;
        _markdownCodeBlockIndent = indent;
        ReRenderMarkdown();
        return true;
    }

    public bool SetOutputPadding(int padding)
    {
        var bounded = Math.Clamp(padding, 0, Math.Max(0, (columns() - 2) / 2));
        if (_outputPadding == bounded) return false;
        _outputPadding = bounded;
        ReRenderMarkdown();
        return true;
    }

    public void SetAssistantText(string markdown)
    {
        if (_liveAssistantSource.Length == 0) _liveAssistantNeedsSpacer = _lastSegmentWasToolResult;
        _liveAssistantSource = TerminalSafeText.Normalize(markdown);
        _liveAssistantStreaming = true;
        _liveAssistant = (_liveAssistantNeedsSpacer ? Environment.NewLine : "") +
            RenderMarkdown(_liveAssistantSource, streaming: true);
    }

    public void SetAssistantThinkingText(string text)
    {
        if (_liveThinkingSource.Length == 0) _liveThinkingNeedsSpacer = _lastSegmentWasToolResult;
        _liveThinkingSource = TerminalSafeText.Normalize(text);
        _liveThinking = (_liveThinkingNeedsSpacer ? Environment.NewLine : "") +
            RenderThinking(getTheme(), _liveThinkingSource, _thinkingBlocksHidden);
    }

    public void CommitAssistantThinkingText(string safe)
    {
        _liveThinking = "";
        _liveThinkingSource = "";
        _liveThinkingNeedsSpacer = false;
        AppendThinkingBlock(safe);
    }

    public bool SetThinkingBlocksHidden(bool hidden)
    {
        if (_thinkingBlocksHidden == hidden) return false;
        _thinkingBlocksHidden = hidden;
        transcript.SetThinkingBlocksHidden(hidden, getTheme());
        _liveThinking = _liveThinkingSource.Length == 0 ? "" :
            (_liveThinkingNeedsSpacer ? Environment.NewLine : "") +
            RenderThinking(getTheme(), _liveThinkingSource, _thinkingBlocksHidden);
        return true;
    }

    public bool SetHiddenThinkingLabel(string? label)
    {
        var safe = string.IsNullOrWhiteSpace(label) ? "Thinking..." : TerminalSafeText.Normalize(label);
        if (string.Equals(_hiddenThinkingLabel, safe, StringComparison.Ordinal)) return false;
        _hiddenThinkingLabel = safe;
        transcript.SetHiddenThinkingLabel(safe, getTheme());
        _liveThinking = _liveThinkingSource.Length == 0 ? "" :
            (_liveThinkingNeedsSpacer ? Environment.NewLine : "") +
            RenderThinking(getTheme(), _liveThinkingSource, _thinkingBlocksHidden);
        return true;
    }

    public void CommitAssistantText(string markdown)
    {
        var safe = TerminalSafeText.Normalize(markdown);
        var rendered = RenderMarkdown(safe, streaming: false);
        _liveAssistant = "";
        _liveAssistantSource = "";
        _liveAssistantStreaming = false;
        _liveAssistantNeedsSpacer = false;
        var displayed = _lastSegmentWasToolResult ? Environment.NewLine + rendered : rendered;
        _lastSegmentWasToolResult = false;
        if (displayed.Length > 0) _hasConversationContent = true;
        transcript.AppendMarkdown(safe, displayed, isError: false);
    }

    public bool AppendUserMessage(string safe, IReadOnlyList<DataContent>? images)
    {
        var activeText = new StringBuilder();
        var capturedText = new StringBuilder();
        var skillInvocation = TerminalSkillInvocationParser.Parse(safe);
        if (skillInvocation is not null || safe.Length > 0 || images is { Count: > 0 })
        {
            if (_historyContinuationNeedsSpacer)
            {
                transcript.Append(Environment.NewLine, isError: false);
                _historyContinuationNeedsSpacer = false;
            }
        }
        if (skillInvocation is not null)
        {
            AppendSkillInvocation(skillInvocation);
            if (skillInvocation.UserMessage is { Length: > 0 } userMessage)
                AppendSkillUserMessage(userMessage);
        }
        else if (safe.Length > 0) AppendUserText(safe);
        if (images is not null)
        {
            foreach (var image in images)
            {
                var marker = imageRenderer.Register(image, out var fallback);
                activeText.Append(marker ?? fallback).Append(Environment.NewLine);
                capturedText.Append(fallback).Append(Environment.NewLine);
            }
        }
        if (skillInvocation is null && safe.Length == 0 && activeText.Length == 0) return false;
        if (activeText.Length > 0)
            transcript.Append(activeText.ToString(), isError: false, capturedText: capturedText.ToString());
        if (skillInvocation is not null || safe.Length > 0 || images is { Count: > 0 })
        {
            _lastSegmentWasToolResult = false;
            _hasConversationContent = true;
        }
        return true;
    }

    public void AppendExtensionError(string path, string error)
    {
        var message = TerminalExtensionErrorText.Message(path, error);
        string Render(TerminalTheme theme) => TerminalExtensionErrorText.Render(message, columns(), theme);
        var rendered = Render(getTheme());
        transcript.AppendThemed(rendered, isError: true, isToolResult: false, Render, rendered);
        _lastSegmentWasToolResult = false;
        _hasConversationContent = true;
    }

    public bool SetToolExpandKeyLabel(string safe)
    {
        if (_toolExpandKeyLabel.Equals(safe, StringComparison.Ordinal)) return false;
        _toolExpandKeyLabel = safe;
        transcript.ReRenderThemedSegments(getTheme());
        return true;
    }

    public bool AppendToolResult(string safe, IReadOnlyList<DataContent>? images)
    {
        var activeText = new StringBuilder(safe);
        var capturedText = new StringBuilder(safe);
        if (images is not null)
        {
            foreach (var image in images)
            {
                var marker = imageRenderer.Register(image, out var fallback);
                activeText.Append(Environment.NewLine).Append(marker ?? fallback);
                capturedText.Append(Environment.NewLine).Append(fallback);
            }
        }
        if (activeText.Length == 0) return false;
        var previewText = capturedText.ToString();
        transcript.Append(activeText.ToString(), isError: true, isToolResult: true,
            previewText, collapsedPreviewText: previewText);
        _lastSegmentWasToolResult = true;
        return true;
    }

    public void AppendToolCall(PiSharpToolRenderView view, string? operationId = null)
    {
        var panel = RegisterToolPanel(operationId);
        string Render(TerminalTheme theme) => Environment.NewLine +
            RenderToolPanel(Environment.NewLine + TerminalToolPresentation.Render(view, theme) + Environment.NewLine,
                theme, panel.BackgroundToken);
        var rendered = Render(getTheme());
        var captured = Environment.NewLine + Environment.NewLine +
            EnsureTrailingNewline(TerminalToolPresentation.Render(view, theme: null));
        if (operationId is { Length: > 0 })
            transcript.AppendLiveTool(operationId, rendered, rendered, Render, Render, captured);
        else transcript.AppendThemed(rendered, isError: true, isToolResult: false, Render, captured);
        _lastSegmentWasToolResult = false;
    }

    public void AppendToolCall(PiSharpToolRenderView collapsed, PiSharpToolRenderView expanded,
        string? operationId = null)
    {
        var panel = RegisterToolPanel(operationId);
        string RenderExpanded(TerminalTheme theme) => Environment.NewLine +
            RenderToolPanel(Environment.NewLine + TerminalToolPresentation.Render(expanded, theme) + Environment.NewLine,
                theme, panel.BackgroundToken);
        string RenderCollapsed(TerminalTheme theme) => Environment.NewLine +
            RenderToolPanel(Environment.NewLine + TerminalToolPresentation.Render(collapsed, theme) + Environment.NewLine,
                theme, panel.BackgroundToken);
        var captured = Environment.NewLine + Environment.NewLine +
            EnsureTrailingNewline(TerminalToolPresentation.Render(collapsed, theme: null));
        if (operationId is { Length: > 0 })
            transcript.AppendLiveTool(operationId, RenderExpanded(getTheme()), RenderCollapsed(getTheme()),
                RenderExpanded, RenderCollapsed, captured);
        else transcript.AppendThemed(RenderExpanded(getTheme()), isError: true, isToolResult: true,
            RenderExpanded, captured, collapsedPreviewText: RenderCollapsed(getTheme()),
            collapsedRenderer: RenderCollapsed);
        _lastSegmentWasToolResult = false;
    }

    public bool AppendToolResult(PiSharpToolRenderView collapsed, PiSharpToolRenderView expanded,
        IReadOnlyList<DataContent>? images, bool suppressUnsupportedImageFallbackWhenCollapsed = false,
        string? operationId = null, bool isError = false)
    {
        ToolPanelState panel;
        if (operationId is { Length: > 0 } && _toolPanelsByCall.Remove(operationId, out var existingPanel))
        {
            panel = existingPanel;
            panel.BackgroundToken = isError ? "toolErrorBg" : "toolSuccessBg";
            transcript.ReRenderThemedSegments(getTheme());
        }
        else panel = new(isError ? "toolErrorBg" : "toolSuccessBg");

        var activeImageText = new StringBuilder();
        var fallbackImages = new List<string>();
        var capturedImageText = new StringBuilder();
        var collapsedFallbackImages = new List<string>();
        if (images is not null)
        {
            foreach (var image in images)
            {
                var marker = imageRenderer.Register(image, out var fallback);
                if (marker is not null)
                {
                    activeImageText.Append(Environment.NewLine).Append(marker);
                    collapsedFallbackImages.Add(fallback);
                }
                else
                {
                    fallbackImages.Add(fallback);
                    if (!suppressUnsupportedImageFallbackWhenCollapsed)
                        collapsedFallbackImages.Add(fallback);
                }
                capturedImageText.Append(Environment.NewLine).Append(fallback);
            }
        }
        string RenderImageFallbacks(IReadOnlyList<string> fallbacks, TerminalTheme theme)
        {
            var text = new StringBuilder();
            foreach (var fallback in fallbacks)
                text.Append(Environment.NewLine).Append(theme.Style("toolOutput", fallback));
            return text.ToString();
        }
        string RenderExpanded(TerminalTheme theme)
        {
            var text = TerminalToolPresentation.Render(expanded, theme) + RenderImageFallbacks(fallbackImages, theme);
            if (activeImageText.Length > 0)
                return RenderToolPanel(text, theme, panel.BackgroundToken) + activeImageText +
                    Environment.NewLine;
            return RenderToolPanel(text + Environment.NewLine, theme, panel.BackgroundToken) +
                (fallbackImages.Count > 0 ? "" : Environment.NewLine);
        }
        string RenderCollapsed(TerminalTheme theme)
        {
            var text = TerminalToolPresentation.Render(collapsed, theme) +
                RenderImageFallbacks(collapsedFallbackImages, theme);
            if (text.Length == 0) return "";
            var panelText = RenderToolPanel(EnsureTrailingNewline(text), theme, panel.BackgroundToken);
            return panelText + (collapsedFallbackImages.Count > 0 ? "" : Environment.NewLine);
        }
        var rendered = RenderExpanded(getTheme());
        var captured = EnsureTrailingNewline(TerminalToolPresentation.Render(expanded, theme: null) + capturedImageText) +
            Environment.NewLine;
        if (rendered.Length == 0) return false;
        transcript.AppendThemed(rendered, isError: true, isToolResult: true, RenderExpanded, captured,
            collapsedPreviewText: RenderCollapsed(getTheme()), collapsedRenderer: RenderCollapsed);
        _lastSegmentWasToolResult = true;
        return true;
    }

    private ToolPanelState RegisterToolPanel(string? operationId)
    {
        var panel = new ToolPanelState("toolPendingBg");
        if (operationId is { Length: > 0 })
        {
            _toolPanelsByCall[operationId] = panel;
            while (_toolPanelsByCall.Count > 1_024)
                _toolPanelsByCall.Remove(_toolPanelsByCall.Keys.First());
        }
        return panel;
    }

    private static string RenderToolPanel(string text, TerminalTheme theme, string backgroundToken)
    {
        var background = theme.Bg(backgroundToken);
        var output = new StringBuilder(text.Length + 32);
        output.Append(background);
        for (var offset = 0; offset < text.Length;)
        {
            if (text[offset] == '\n')
            {
                output.Append('\n').Append(background);
                offset++;
                continue;
            }
            if (text.AsSpan(offset).StartsWith("\u001b[0m", StringComparison.Ordinal))
            {
                output.Append("\u001b[0m").Append(background);
                offset += 4;
                continue;
            }
            output.Append(text[offset++]);
        }
        output.Append("\u001b[0m");
        return output.ToString();
    }

    private sealed class ToolPanelState(string backgroundToken)
    {
        public string BackgroundToken { get; set; } = backgroundToken;
    }

    internal void SetToolResult(string id, PiSharpToolRenderView collapsed, PiSharpToolRenderView expanded,
        bool isError, bool finished, IReadOnlyList<DataContent>? images = null)
    {
        if (!_toolPanelsByCall.TryGetValue(id, out var panel)) panel = RegisterToolPanel(id);
        panel.BackgroundToken = finished ? isError ? "toolErrorBg" : "toolSuccessBg" : "toolPendingBg";
        if (finished) _toolPanelsByCall.Remove(id);

        var activeImages = new StringBuilder();
        var capturedImages = new StringBuilder();
        foreach (var image in images ?? [])
        {
            var marker = imageRenderer.Register(image, out var fallback);
            activeImages.Append(Environment.NewLine).Append(marker ?? fallback);
            capturedImages.Append(Environment.NewLine).Append(fallback);
        }
        string Expanded(TerminalTheme theme) => Environment.NewLine + RenderToolPanel(
            Environment.NewLine + EnsureTrailingNewline(TerminalToolPresentation.Render(expanded, theme)) +
            Environment.NewLine, theme, panel.BackgroundToken) + activeImages + Environment.NewLine;
        string Collapsed(TerminalTheme theme) => Environment.NewLine + RenderToolPanel(
            Environment.NewLine + EnsureTrailingNewline(TerminalToolPresentation.Render(collapsed, theme)) +
            Environment.NewLine, theme, panel.BackgroundToken) + capturedImages + Environment.NewLine;
        transcript.SetToolResult(id, Expanded(getTheme()), Collapsed(getTheme()), Expanded, Collapsed,
            Environment.NewLine + Environment.NewLine +
            EnsureTrailingNewline(TerminalToolPresentation.Render(expanded, null) + capturedImages), isError, finished);
        _lastSegmentWasToolResult = true;
    }

    public void AppendThinkingBlock(string text)
    {
        var leadingSpacer = _lastSegmentWasToolResult ? Environment.NewLine : "";
        string RenderVisible(TerminalTheme theme) => leadingSpacer + RenderThinking(theme, text, hidden: false);
        string RenderHidden(TerminalTheme theme) => leadingSpacer + RenderThinking(theme, text, hidden: true);
        transcript.AppendThinkingBlock(text, _hiddenThinkingLabel, RenderVisible, RenderHidden, getTheme(),
            _thinkingBlocksHidden);
        _lastSegmentWasToolResult = false;
        _hasConversationContent = true;
    }

    private void AppendSkillInvocation(TerminalSkillInvocation skillInvocation)
    {
        string Render(TerminalTheme theme, bool expanded) => TerminalSkillInvocationRenderer.RenderSkill(
            skillInvocation, columns(), theme, _toolExpandKeyLabel, _markdownCodeBlockIndent, expanded,
            includeLeadingSpacer: _hasConversationContent);
        var keyHint = string.IsNullOrWhiteSpace(_toolExpandKeyLabel)
            ? ""
            : $" ({_toolExpandKeyLabel.ToLowerInvariant()} to expand)";
        var captured = Environment.NewLine + $" [skill] {skillInvocation.Name}{keyHint}" + Environment.NewLine;
        var expanded = Render(getTheme(), true);
        var collapsed = Render(getTheme(), false);
        transcript.AppendThemed(expanded, isError: false, isToolResult: false,
            theme => Render(theme, true), captured, collapsedPreviewText: collapsed,
            collapsedRenderer: theme => Render(theme, false), isCollapsible: true);
    }

    private void AppendSkillUserMessage(string text)
    {
        string Render(TerminalTheme theme) => TerminalSkillInvocationRenderer.RenderUserMessage(
            text, columns(), theme, _markdownCodeBlockIndent);
        transcript.AppendThemed(Render(getTheme()), isError: false, isToolResult: false, Render,
            Environment.NewLine + Environment.NewLine + " " + text + Environment.NewLine);
    }

    private void AppendUserText(string text)
    {
        var includeLeadingSpacer = _hasConversationContent;
        string Render(TerminalTheme theme) => TerminalSkillInvocationRenderer.RenderUserMessage(
            text, columns(), theme, _markdownCodeBlockIndent, includeLeadingSpacer);
        transcript.AppendThemed(Render(getTheme()), isError: false, isToolResult: false, Render,
            Environment.NewLine + Environment.NewLine + " " + text + Environment.NewLine);
    }

    private static string EnsureTrailingNewline(string text) =>
        text.Length == 0 || text.EndsWith('\n') ? text : text + Environment.NewLine;

    private string RenderMarkdown(string markdown, bool streaming = false)
    {
        var width = Math.Max(1, columns() - 2 * _outputPadding);
        var rendered = streaming
            ? TerminalMarkdownRenderer.RenderStreaming(markdown, width, getTheme(), _markdownCodeBlockIndent)
            : TerminalMarkdownRenderer.Render(markdown, width, getTheme(), _markdownCodeBlockIndent);
        return "\n" + rendered;
    }

    private string RenderThinking(TerminalTheme theme, string text, bool hidden) =>
        Environment.NewLine + " " + theme.Style("thinkingText", hidden ? _hiddenThinkingLabel : text, italic: true) +
        Environment.NewLine;
}
