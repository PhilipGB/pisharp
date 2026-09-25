using System.Globalization;
using System.Text;
using Markdig.Extensions.Mathematics;
using Markdig.Syntax;

namespace PiSharp.Cli.Tui;

/// <summary>Renders parsed CommonMark/GFM block nodes into terminal content.</summary>
internal static class TerminalMarkdownRenderer
{
    private const string Reset = "\u001b[0m";

    public static string Render(string markdown, int availableWidth = 80)
    {
        ArgumentNullException.ThrowIfNull(markdown);
        return Render(TerminalMarkdownParser.Parse(markdown), markdown, availableWidth);
    }

    public static string Render(MarkdownDocument document, string source, int availableWidth = 80)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(source);
        return RenderBlocks(document, source, Math.Clamp(availableWidth, 1, 400), "\n\n");
    }

    private static string RenderBlocks(ContainerBlock container, string source, int width, string separator)
    {
        var output = new StringBuilder();
        foreach (var block in container)
        {
            if (block is BlankLineBlock) continue;
            var rendered = RenderBlock(block, source, width);
            if (rendered.Length == 0) continue;
            if (output.Length > 0) output.Append(separator);
            output.Append(rendered);
        }
        return output.ToString();
    }

    private static string RenderBlock(Block block, string source, int width)
    {
        switch (block)
        {
            case MathBlock math:
                return TerminalMarkdownInlineRenderer.Visible(TerminalMarkdownSource.Slice(math, source));
            case FencedCodeBlock fenced:
                return RenderCodeBlock(fenced, Language(fenced.Info?.ToString() ?? ""));
            case CodeBlock code:
                return RenderCodeBlock(code, null);
            case HeadingBlock heading:
                return "\u001b[1;36m" + TerminalMarkdownInlineRenderer.Render(heading.Inline?.FirstChild, styled: true) + Reset;
            case ParagraphBlock paragraph:
                return TerminalMarkdownInlineRenderer.Render(paragraph.Inline?.FirstChild, styled: true);
            case Markdig.Extensions.Tables.Table table:
                return TerminalMarkdownTableRenderer.Render(table, source, width);
            case ListBlock list:
                return RenderList(list, source, width);
            case QuoteBlock quote:
                return PrefixLines(RenderBlocks(quote, source, Math.Max(1, width - 2), "\n\n"), "│ ");
            case ThematicBreakBlock:
                return "\u001b[90m────────────────────────\u001b[0m";
            case HtmlBlock html:
                return PrefixLines(TerminalMarkdownInlineRenderer.Visible(html.Lines.ToString()), "  │ ");
            case ContainerBlock nested:
                return RenderBlocks(nested, source, width, "\n\n");
            case LeafBlock leaf:
                return leaf.Inline is not null
                    ? TerminalMarkdownInlineRenderer.Render(leaf.Inline.FirstChild, styled: true)
                    : TerminalMarkdownInlineRenderer.Visible(leaf.Lines.ToString());
            default:
                return "";
        }
    }

    private static string RenderList(ListBlock list, string source, int width)
    {
        var output = new List<string>();
        var fallbackOrder = int.TryParse(list.OrderedStart, NumberStyles.None, CultureInfo.InvariantCulture, out var start)
            ? start
            : 1;
        foreach (var item in list.OfType<ListItemBlock>())
        {
            var order = item.Order > 0 ? item.Order : fallbackOrder;
            fallbackOrder = order + 1;
            var marker = list.IsOrdered ? $"{order}{list.OrderedDelimiter}" : "•";
            var content = RenderBlocks(item, source, Math.Max(1, width - TerminalTextLayout.Width(marker) - 1), "\n");
            if (content.Length == 0)
            {
                output.Add(marker);
                continue;
            }

            var lines = content.Split('\n');
            var continuation = new string(' ', TerminalTextLayout.Width(marker) + 1);
            output.Add(marker + " " + lines[0]);
            for (var index = 1; index < lines.Length; index++) output.Add(continuation + lines[index]);
        }
        return string.Join('\n', output);
    }

    private static string RenderCodeBlock(CodeBlock block, string? language)
    {
        var output = new List<string>();
        if (!string.IsNullOrEmpty(language)) output.Add($"\u001b[2m  [{TerminalMarkdownInlineRenderer.Visible(language)}]\u001b[0m");
        var text = block.Lines.ToString().Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        var lines = text.Split('\n');
        if (text.EndsWith('\n') && lines.Length > 1) lines = lines[..^1];
        foreach (var line in lines)
            output.Add("\u001b[90m  │ \u001b[0m" + TerminalMarkdownInlineRenderer.Visible(line) + Reset);
        return string.Join('\n', output);
    }

    private static string Language(string info)
    {
        var trimmed = info.Trim();
        var separator = trimmed.IndexOfAny([' ', '\t']);
        return separator < 0 ? trimmed : trimmed[..separator];
    }

    private static string PrefixLines(string text, string prefix)
    {
        if (text.Length == 0) return prefix.TrimEnd();
        return string.Join('\n', text.Split('\n').Select(line => line.Length == 0 ? prefix.TrimEnd() : prefix + line));
    }
}
