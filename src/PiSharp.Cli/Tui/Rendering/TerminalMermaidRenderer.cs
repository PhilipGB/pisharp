namespace PiSharp.Cli.Tui;

/// <summary>Markdown transformer for top-level Mermaid fences, backed by grok-mermaid 0.2.3.</summary>
internal static class TerminalMermaidRenderer
{
    public const string ModeOff = "off";
    public const string ModeFinal = "final";
    public const string ModeStreaming = "streaming";

    public static bool IsMermaidInfo(string info)
    {
        var trimmed = info.Trim();
        if (trimmed.Length == 0)
            return false;
        var first = trimmed.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault();
        return string.Equals(first, "mermaid", StringComparison.OrdinalIgnoreCase);
    }

    public static IReadOnlyList<string> CollectEligibleSources(string markdown)
    {
        var sources = new List<string>();
        Markdig.Syntax.MarkdownDocument document;
        try
        {
            document = PiSharp.Cli.Tui.TerminalMarkdownParser.Parse(markdown);
        }
        catch
        {
            return sources;
        }
        foreach (var block in document)
        {
            if (block is not Markdig.Syntax.FencedCodeBlock fenced)
                continue;
            if (fenced.Parent is not Markdig.Syntax.MarkdownDocument)
                continue;
            if (!IsMermaidInfo(fenced.Info?.ToString() ?? ""))
                continue;
            sources.Add(fenced.Lines.ToString());
        }
        return sources;
    }

    public static bool ShouldAttemptRender(string? mode, bool isStreaming, bool isThinking)
    {
        if (isThinking)
            return false;
        var effective = mode is ModeOff or ModeFinal ? mode : ModeStreaming;
        if (effective == ModeOff)
            return false;
        if (isStreaming && effective != ModeStreaming)
            return false;
        return true;
    }

    /// <summary>
    /// Try to render a Mermaid source block. Returns true with themed diagram lines when the fence
    /// should be replaced. Returns false for fallback; warning is non-null only for the final-render
    /// partially-invalid case where Pi keeps the source fence and appends a condensed warning.
    /// </summary>
    public static bool TryRender(
        string source,
        int availableWidth,
        TerminalTheme theme,
        string? mode,
        bool isStreaming,
        bool isThinking,
        out string? rendered,
        out string? warning,
        Func<string, TerminalMermaidArt?>? render = null)
    {
        rendered = null;
        warning = null;
        if (!ShouldAttemptRender(mode, isStreaming, isThinking))
            return false;
        var art = (render ?? TerminalMermaidEngine.TryRender)(source);
        if (art is null || art.Width > availableWidth)
            return false;
        if (!isStreaming && art.Warnings.Count > 0)
        {
            var suffix = art.Warnings.Count > 1 ? $" (+{art.Warnings.Count - 1} more)" : "";
            warning = $"Mermaid diagram not rendered: {art.Warnings[0]}{suffix}";
            return false;
        }
        rendered = string.Join('\n', ThemedLines(art, theme));
        return true;
    }

    internal static IReadOnlyList<string> ThemedLines(TerminalMermaidArt art, TerminalTheme theme)
    {
        var lines = new List<string>(art.Styled.Count);
        foreach (var row in art.Styled)
        {
            var builder = new System.Text.StringBuilder();
            foreach (var span in row)
                builder.Append(StyleSpan(span, theme));
            lines.Add(builder.ToString());
        }
        return lines;
    }

    private static string StyleSpan(TerminalMermaidSpan span, TerminalTheme theme) => span.Cls switch
    {
        "border" => theme.Style("borderMuted", span.Text),
        "text" => theme.Style("text", span.Text),
        "edge" => theme.Style("accent", span.Text),
        "edgeLabel" => theme.Style("muted", span.Text),
        "title" => theme.Style("accent", span.Text, bold: true),
        _ => span.Text,
    };
}
