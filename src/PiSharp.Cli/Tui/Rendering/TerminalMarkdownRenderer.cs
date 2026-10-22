using System.Globalization;
using System.Text;
using Markdig.Extensions.Mathematics;
using Markdig.Syntax;

namespace PiSharp.Cli.Tui;

/// <summary>Renders parsed CommonMark/GFM block nodes into terminal content.</summary>
internal static class TerminalMarkdownRenderer
{
    public static string Render(string markdown, int availableWidth = 80, TerminalTheme? theme = null,
        string? codeBlockIndent = null, string? baseForegroundToken = null)
    {
        ArgumentNullException.ThrowIfNull(markdown);
        return Render(TerminalMarkdownParser.Parse(markdown), markdown, availableWidth, theme, codeBlockIndent,
            baseForegroundToken);
    }

    public static string RenderStreaming(string markdown, int availableWidth = 80, TerminalTheme? theme = null,
        string? codeBlockIndent = null, string? baseForegroundToken = null)
    {
        ArgumentNullException.ThrowIfNull(markdown);
        return Render(PreserveIncompleteLinks(markdown), availableWidth, theme, codeBlockIndent, baseForegroundToken);
    }

    public static string Render(MarkdownDocument document, string source, int availableWidth = 80,
        TerminalTheme? theme = null, string? codeBlockIndent = null, string? baseForegroundToken = null)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(source);
        return RenderBlocks(document, source, Math.Clamp(availableWidth, 1, 400), "\n\n", theme ?? TerminalTheme.Default,
            codeBlockIndent ?? "  ", baseForegroundToken);
    }

    private static string RenderBlocks(ContainerBlock container, string source, int width, string separator,
        TerminalTheme theme, string codeBlockIndent, string? baseForegroundToken)
    {
        var output = new StringBuilder();
        foreach (var block in container)
        {
            if (block is BlankLineBlock) continue;
            var rendered = RenderBlock(block, source, width, theme, codeBlockIndent, baseForegroundToken);
            if (rendered.Length == 0) continue;
            if (output.Length > 0) output.Append(separator);
            output.Append(rendered);
        }
        return output.ToString();
    }

    private static string RenderBlock(Block block, string source, int width, TerminalTheme theme, string codeBlockIndent,
        string? baseForegroundToken)
    {
        switch (block)
        {
            case MathBlock math:
                var rawMath = TerminalMarkdownSource.Slice(math, source).Trim();
                var mathSource = StripMathDelimiters(rawMath);
                return string.Join('\n', (TerminalLatexRenderer.Render(mathSource, display: true) ?? rawMath)
                    .Split('\n').Select(TerminalMarkdownInlineRenderer.Visible));
            case FencedCodeBlock fenced:
                return RenderCodeBlock(fenced, Language(fenced.Info?.ToString() ?? ""), theme, codeBlockIndent);
            case CodeBlock code:
                return RenderCodeBlock(code, null, theme, codeBlockIndent);
            case HeadingBlock heading:
                return TerminalMarkdownInlineRenderer.Render(heading.Inline?.FirstChild, styled: true, theme: theme,
                    baseStyle: "\u001b[1m" + (heading.Level == 1 ? "\u001b[4m" : "") + theme.Fg("mdHeading"));
            case ParagraphBlock paragraph:
                return TerminalMarkdownInlineRenderer.Render(paragraph.Inline?.FirstChild, styled: true, theme,
                    baseStyle: baseForegroundToken is null ? null : theme.Fg(baseForegroundToken));
            case Markdig.Extensions.Tables.Table table:
                return TerminalMarkdownTableRenderer.Render(table, source, width, theme);
            case ListBlock list:
                return RenderList(list, source, width, theme, codeBlockIndent, baseForegroundToken);
            case QuoteBlock quote:
                return RenderQuote(quote, source, width, theme, codeBlockIndent, baseForegroundToken);
            case ThematicBreakBlock:
                return theme.Style("mdHr", new string('─', Math.Min(width, 80)));
            case HtmlBlock html:
                return PrefixLines(TerminalMarkdownInlineRenderer.Visible(html.Lines.ToString()), "  │ ");
            case ContainerBlock nested:
                return RenderBlocks(nested, source, width, "\n\n", theme, codeBlockIndent, baseForegroundToken);
            case LeafBlock leaf:
                return leaf.Inline is not null
                    ? TerminalMarkdownInlineRenderer.Render(leaf.Inline.FirstChild, styled: true, theme: theme,
                        baseStyle: baseForegroundToken is null ? null : theme.Fg(baseForegroundToken))
                    : TerminalMarkdownInlineRenderer.Visible(leaf.Lines.ToString());
            default:
                return "";
        }
    }

    private static string RenderList(ListBlock list, string source, int width, TerminalTheme theme, string codeBlockIndent,
        string? baseForegroundToken)
    {
        var output = new List<string>();
        var fallbackOrder = int.TryParse(list.OrderedStart, NumberStyles.None, CultureInfo.InvariantCulture, out var start)
            ? start
            : 1;
        foreach (var item in list.OfType<ListItemBlock>())
        {
            var order = item.Order > 0 ? item.Order : fallbackOrder;
            fallbackOrder = order + 1;
            var marker = list.IsOrdered ? $"{order}{list.OrderedDelimiter}" : "-";
            var content = RenderBlocks(item, source, Math.Max(1, width - TerminalTextLayout.Width(marker) - 1), "\n",
                theme, codeBlockIndent, baseForegroundToken);
            if (content.Length == 0)
            {
                output.Add(marker);
                continue;
            }

            var lines = content.Split('\n');
            var continuation = new string(' ', TerminalTextLayout.Width(marker) + 1);
            var taskMarker = lines[0].StartsWith("[ ] ", StringComparison.Ordinal) ? "[ ] " :
                lines[0].StartsWith("[x] ", StringComparison.Ordinal) ? "[x] " : "";
            output.Add(theme.Style("mdListBullet", marker + " " + taskMarker) + lines[0][taskMarker.Length..]);
            for (var index = 1; index < lines.Length; index++) output.Add(continuation + lines[index]);
        }
        var nestedIndent = NestedListExtraIndent(list);
        if (nestedIndent > 0)
        {
            var prefix = new string(' ', nestedIndent);
            for (var index = 0; index < output.Count; index++) output[index] = prefix + output[index];
        }
        return string.Join('\n', output);
    }

    private static int NestedListExtraIndent(ListBlock list)
    {
        var ancestorMarkerIndents = new List<int>();
        for (var parent = list.Parent; parent is not null; parent = parent.Parent)
        {
            if (parent is not ListItemBlock item || item.Parent is not ListBlock parentList) continue;
            var order = item.Order > 0 ? item.Order : 1;
            var marker = parentList.IsOrdered ? $"{order}{parentList.OrderedDelimiter}" : "-";
            ancestorMarkerIndents.Add(TerminalTextLayout.Width(marker) + 1);
        }
        var inheritedIndent = 0;
        var ancestorExtraIndent = 0;
        for (var depth = 1; depth <= ancestorMarkerIndents.Count; depth++)
        {
            inheritedIndent += ancestorMarkerIndents[ancestorMarkerIndents.Count - depth];
            var extraIndent = Math.Max(0, depth * 4 - inheritedIndent - ancestorExtraIndent);
            if (depth == ancestorMarkerIndents.Count) return extraIndent;
            ancestorExtraIndent += extraIndent;
        }
        return 0;
    }

    private static string RenderQuote(QuoteBlock quote, string source, int width, TerminalTheme theme, string codeBlockIndent,
        string? baseForegroundToken)
    {
        var text = RenderBlocks(quote, source, Math.Max(1, width - 2), "\n\n", theme, codeBlockIndent,
            baseForegroundToken: null);
        if (text.Length == 0) return theme.Style("mdQuoteBorder", "│ ");
        return string.Join('\n', text.Split('\n').Select(line =>
            theme.Style("mdQuoteBorder", "│ ") + theme.Style("mdQuote", line, italic: true)));
    }

    private static string RenderCodeBlock(CodeBlock block, string? language, TerminalTheme theme, string codeBlockIndent)
    {
        var output = new List<string>();
        output.Add(theme.Style("mdCodeBlockBorder", $"```{TerminalMarkdownInlineRenderer.Visible(language ?? "")}"));
        var text = block.Lines.ToString().Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        var lines = text.Split('\n');
        if (text.EndsWith('\n') && lines.Length > 1) lines = lines[..^1];
        foreach (var line in lines)
            output.Add(codeBlockIndent + TerminalSyntaxHighlighter.Highlight(
                TerminalMarkdownInlineRenderer.Visible(line), language, theme));
        output.Add(theme.Style("mdCodeBlockBorder", "```"));
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

    private static string StripMathDelimiters(string source)
    {
        if (source.StartsWith("$$", StringComparison.Ordinal) && source.EndsWith("$$", StringComparison.Ordinal) && source.Length >= 4)
            return source[2..^2].Trim();
        if (source.StartsWith("\\[", StringComparison.Ordinal) && source.EndsWith("\\]", StringComparison.Ordinal) && source.Length >= 4)
            return source[2..^2].Trim();
        return source;
    }

    private static string PreserveIncompleteLinks(string source)
    {
        var openBrackets = new Stack<int>();
        var escapedOpeners = new HashSet<int>();
        var inlineCodeTicks = 0;
        var fencedCodeCharacter = '\0';
        var fencedCodeLength = 0;
        var lineStart = 0;
        while (lineStart < source.Length)
        {
            var lineEnd = source.IndexOf('\n', lineStart);
            if (lineEnd < 0) lineEnd = source.Length;
            var first = lineStart;
            while (first < lineEnd && source[first] == ' ') first++;
            var fenceLength = CountRun(source, first, lineEnd, '`');
            var fenceCharacter = '`';
            if (fenceLength < 3)
            {
                fenceCharacter = '~';
                fenceLength = CountRun(source, first, lineEnd, '~');
            }
            if (fenceLength >= 3 && (fencedCodeCharacter == '\0' ||
                fencedCodeCharacter == fenceCharacter && fenceLength >= fencedCodeLength))
            {
                if (fencedCodeCharacter == '\0')
                {
                    fencedCodeCharacter = fenceCharacter;
                    fencedCodeLength = fenceLength;
                }
                else
                {
                    fencedCodeCharacter = '\0';
                    fencedCodeLength = 0;
                }
                lineStart = lineEnd + 1;
                continue;
            }

            if (fencedCodeCharacter == '\0')
            {
                for (var index = lineStart; index < lineEnd; index++)
                {
                    var current = source[index];
                    if (current == '\\')
                    {
                        index++;
                        continue;
                    }
                    if (current == '`')
                    {
                        var ticks = CountRun(source, index, lineEnd, '`');
                        if (inlineCodeTicks == 0) inlineCodeTicks = ticks;
                        else if (inlineCodeTicks == ticks) inlineCodeTicks = 0;
                        index += ticks - 1;
                        continue;
                    }
                    if (inlineCodeTicks > 0) continue;
                    if (current == '[') openBrackets.Push(index);
                    else if (current == ']' && openBrackets.TryPop(out var opener))
                    {
                        if (index + 1 < lineEnd && source[index + 1] == '(' &&
                            !HasClosingLinkDestination(source, index + 2, lineEnd))
                            escapedOpeners.Add(opener);
                    }
                }
            }
            lineStart = lineEnd + 1;
        }

        foreach (var opener in openBrackets) escapedOpeners.Add(opener);
        if (escapedOpeners.Count == 0) return source;
        var result = new StringBuilder(source.Length + escapedOpeners.Count * 5);
        for (var index = 0; index < source.Length; index++)
        {
            if (escapedOpeners.Contains(index)) result.Append("&#91;");
            else result.Append(source[index]);
        }
        return result.ToString();
    }

    private static int CountRun(string source, int start, int end, char value)
    {
        var index = start;
        while (index < end && source[index] == value) index++;
        return index - start;
    }

    private static bool HasClosingLinkDestination(string source, int start, int end)
    {
        var depth = 1;
        for (var index = start; index < end; index++)
        {
            if (source[index] == '\\') { index++; continue; }
            if (source[index] == '(') depth++;
            else if (source[index] == ')' && --depth == 0) return true;
        }
        return false;
    }
}
