using System.Text;
using Microsoft.Extensions.AI;
using PiSharp.Runtime.Extensions;

namespace PiSharp.Cli.Tui;

/// <summary>Projects user, tool and streamed assistant content into the retained transcript.</summary>
internal sealed class TerminalConversationPresentation(TerminalTranscriptBuffer transcript,
    TerminalImageRenderer imageRenderer, Func<int> columns, Func<TerminalTheme> getTheme)
{
    private string _markdownCodeBlockIndent = "  ";
    private string _toolExpandKeyLabel = "Ctrl+O";
    private string _liveAssistant = "";
    private string _liveAssistantSource = "";
    private string _liveThinking = "";
    private string _liveThinkingSource = "";
    private bool _thinkingBlocksHidden;
    private bool _hasConversationContent;

    public string LiveText => _liveThinking + _liveAssistant;
    public bool ThinkingBlocksHidden => _thinkingBlocksHidden;
    public bool HasContent => _hasConversationContent;

    public void Reset()
    {
        _liveAssistant = "";
        _liveAssistantSource = "";
        _liveThinking = "";
        _liveThinkingSource = "";
        _hasConversationContent = false;
    }

    public void ReRender()
    {
        transcript.ReRenderMarkdown(RenderMarkdown);
        transcript.ReRenderThemedSegments(getTheme());
        _liveAssistant = _liveAssistantSource.Length == 0 ? "" : RenderMarkdown(_liveAssistantSource);
        _liveThinking = _liveThinkingSource.Length == 0 ? "" :
            RenderThinking(getTheme(), _liveThinkingSource, _thinkingBlocksHidden);
    }

    public void ReRenderMarkdown()
    {
        transcript.ReRenderMarkdown(RenderMarkdown);
        _liveAssistant = _liveAssistantSource.Length == 0 ? "" : RenderMarkdown(_liveAssistantSource);
    }

    public bool SetCodeBlockIndent(string indent)
    {
        if (string.Equals(_markdownCodeBlockIndent, indent, StringComparison.Ordinal)) return false;
        _markdownCodeBlockIndent = indent;
        ReRenderMarkdown();
        return true;
    }

    public void SetAssistantText(string markdown)
    {
        _liveAssistantSource = TerminalSafeText.Normalize(markdown);
        _liveAssistant = RenderMarkdown(_liveAssistantSource) + "\n";
    }

    public void SetAssistantThinkingText(string text)
    {
        _liveThinkingSource = TerminalSafeText.Normalize(text);
        _liveThinking = RenderThinking(getTheme(), _liveThinkingSource, _thinkingBlocksHidden);
    }

    public void CommitAssistantThinkingText(string safe)
    {
        _liveThinking = "";
        _liveThinkingSource = "";
        AppendThinkingBlock(safe);
    }

    public bool SetThinkingBlocksHidden(bool hidden)
    {
        if (_thinkingBlocksHidden == hidden) return false;
        _thinkingBlocksHidden = hidden;
        transcript.SetThinkingBlocksHidden(hidden, getTheme());
        _liveThinking = _liveThinkingSource.Length == 0 ? "" :
            RenderThinking(getTheme(), _liveThinkingSource, _thinkingBlocksHidden);
        return true;
    }

    public void CommitAssistantText(string markdown)
    {
        var safe = TerminalSafeText.Normalize(markdown);
        var rendered = RenderMarkdown(safe);
        _liveAssistant = "";
        _liveAssistantSource = "";
        if (rendered.Length > 0) _hasConversationContent = true;
        transcript.AppendMarkdown(safe, rendered, isError: false);
    }

    public bool AppendUserMessage(string safe, IReadOnlyList<DataContent>? images)
    {
        var activeText = new StringBuilder();
        var capturedText = new StringBuilder();
        var skillInvocation = TerminalSkillInvocationParser.Parse(safe);
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
            _hasConversationContent = true;
        return true;
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
        return true;
    }

    public void AppendToolCall(PiSharpToolRenderView view)
    {
        string Render(TerminalTheme theme) => Environment.NewLine +
            EnsureTrailingNewline(TerminalToolPresentation.Render(view, theme));
        var rendered = Render(getTheme());
        var captured = Environment.NewLine + EnsureTrailingNewline(TerminalToolPresentation.Render(view, theme: null));
        transcript.AppendThemed(rendered, isError: true, isToolResult: false, Render, captured);
    }

    public void AppendToolCall(PiSharpToolRenderView collapsed, PiSharpToolRenderView expanded)
    {
        string RenderExpanded(TerminalTheme theme) => Environment.NewLine +
            EnsureTrailingNewline(TerminalToolPresentation.Render(expanded, theme));
        string RenderCollapsed(TerminalTheme theme) => Environment.NewLine +
            EnsureTrailingNewline(TerminalToolPresentation.Render(collapsed, theme));
        transcript.AppendThemed(RenderExpanded(getTheme()), isError: true, isToolResult: true,
            RenderExpanded, RenderCollapsed(getTheme()), collapsedPreviewText: RenderCollapsed(getTheme()),
            collapsedRenderer: RenderCollapsed);
    }

    public bool AppendToolResult(PiSharpToolRenderView collapsed, PiSharpToolRenderView expanded, IReadOnlyList<DataContent>? images)
    {
        var activeImageText = new StringBuilder();
        var capturedImageText = new StringBuilder();
        if (images is not null)
        {
            foreach (var image in images)
            {
                var marker = imageRenderer.Register(image, out var fallback);
                activeImageText.Append(Environment.NewLine).Append(marker ?? fallback);
                capturedImageText.Append(Environment.NewLine).Append(fallback);
            }
        }
        string RenderExpanded(TerminalTheme theme) => EnsureTrailingNewline(
            TerminalToolPresentation.Render(expanded, theme) + activeImageText);
        string RenderCollapsed(TerminalTheme theme) => EnsureTrailingNewline(
            TerminalToolPresentation.Render(collapsed, theme) + capturedImageText);
        var rendered = RenderExpanded(getTheme());
        var captured = EnsureTrailingNewline(TerminalToolPresentation.Render(expanded, theme: null) + capturedImageText);
        if (rendered.Length == 0) return false;
        transcript.AppendThemed(rendered, isError: true, isToolResult: true, RenderExpanded, captured,
            collapsedPreviewText: RenderCollapsed(getTheme()), collapsedRenderer: RenderCollapsed);
        return true;
    }

    internal void SetToolResult(string id, PiSharpToolRenderView collapsed, PiSharpToolRenderView expanded,
        bool isError, bool finished, IReadOnlyList<DataContent>? images = null)
    {
        var activeImages = new StringBuilder();
        var capturedImages = new StringBuilder();
        foreach (var image in images ?? [])
        {
            var marker = imageRenderer.Register(image, out var fallback);
            activeImages.Append(Environment.NewLine).Append(marker ?? fallback);
            capturedImages.Append(Environment.NewLine).Append(fallback);
        }
        string Expanded(TerminalTheme theme) => EnsureTrailingNewline(TerminalToolPresentation.Render(expanded, theme) + activeImages);
        string Collapsed(TerminalTheme theme) => EnsureTrailingNewline(TerminalToolPresentation.Render(collapsed, theme) + capturedImages);
        transcript.SetToolResult(id, Expanded(getTheme()), Collapsed(getTheme()), Expanded, Collapsed,
            EnsureTrailingNewline(TerminalToolPresentation.Render(expanded, null) + capturedImages), isError, finished);
    }

    public void AppendThinkingBlock(string text)
    {
        string RenderVisible(TerminalTheme theme) => RenderThinking(theme, text, hidden: false);
        string RenderHidden(TerminalTheme theme) => RenderThinking(theme, text, hidden: true);
        transcript.AppendThinkingBlock(text, "Thinking...", RenderVisible, RenderHidden, getTheme(),
            _thinkingBlocksHidden);
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

    private string RenderMarkdown(string markdown)
    {
        var width = Math.Max(1, columns() - 2);
        var rendered = TerminalMarkdownRenderer.Render(markdown, width, getTheme(), _markdownCodeBlockIndent);
        var output = new StringBuilder("\n");
        var lines = rendered.Split('\n');
        for (var index = 0; index < lines.Length; index++)
        {
            if (index > 0) output.Append('\n');
            var padded = " " + lines[index] + " ";
            output.Append(padded).Append(' ', Math.Max(0, columns() - TerminalTextLayout.Width(padded)));
        }
        return output.ToString();
    }

    private static string RenderThinking(TerminalTheme theme, string text, bool hidden) =>
        Environment.NewLine + " " + theme.Style("thinkingText", hidden ? "Thinking..." : text, italic: true) +
        Environment.NewLine;
}
