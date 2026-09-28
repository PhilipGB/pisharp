using System.Text;
using Markdig.Extensions.Mathematics;
using Markdig.Extensions.TaskLists;
using Markdig.Syntax.Inlines;

namespace PiSharp.Cli.Tui;

/// <summary>Renders Markdown inline AST nodes using terminal-safe text and PiSharp styles.</summary>
internal static class TerminalMarkdownInlineRenderer
{
    private const string Reset = "\u001b[0m";

    public static string Render(Inline? first, bool styled, TerminalTheme? theme = null, string? baseStyle = null)
    {
        theme ??= TerminalTheme.Default;
        var output = new StringBuilder();
        var style = new InlineStyleWriter(output);
        if (styled && !string.IsNullOrEmpty(baseStyle)) style.Push(baseStyle);
        AppendInlines(style, first, styled, theme);
        if (styled && !string.IsNullOrEmpty(baseStyle)) style.Pop();
        return output.ToString();
    }

    private static void AppendInlines(InlineStyleWriter output, Inline? first, bool styled, TerminalTheme theme)
    {
        for (var inline = first; inline is not null; inline = inline.NextSibling)
        {
            switch (inline)
            {
                case LiteralInline literal:
                    output.Append(Visible(literal.Content.ToString()));
                    break;
                case CodeInline code:
                    AppendStyled(output, Visible(code.Content), styled ? theme.Fg("mdCode") : "", "\u001b[39m");
                    break;
                case MathInline math:
                    var (opening, closing) = math.Delimiter switch
                    {
                        '(' => ("\\(", "\\)"),
                        '[' => ("\\[", "\\]"),
                        _ => (new string('$', Math.Max(1, math.DelimiterCount)),
                            new string('$', Math.Max(1, math.DelimiterCount)))
                    };
                    AppendStyled(output, Visible(opening + math.Content.ToString() + closing), styled ? theme.Fg("mdCode") : "", "\u001b[39m");
                    break;
                case TaskList task:
                    output.Append(task.Checked ? "[x]" : "[ ]");
                    break;
                case AutolinkInline autoLink:
                    output.Append(Visible(autoLink.Url));
                    break;
                case LinkInline link:
                    RenderLink(output, link, styled, theme);
                    break;
                case EmphasisInline emphasis:
                    RenderEmphasis(output, emphasis, styled, theme);
                    break;
                case LineBreakInline:
                    output.Append("\n");
                    break;
                case HtmlInline html:
                    output.Append(Visible(html.Tag));
                    break;
                case HtmlEntityInline entity:
                    output.Append(Visible(entity.Transcoded.ToString()));
                    break;
                case ContainerInline container:
                    AppendInlines(output, container.FirstChild, styled, theme);
                    break;
                default:
                    output.Append(Visible(inline.ToString() ?? ""));
                    break;
            }
        }
    }

    public static string Visible(string text)
    {
        var output = new StringBuilder(text.Length);
        AppendVisible(output, text);
        return output.ToString();
    }

    public static string StyleVisible(string text, string? baseStyle = null, string? childStyle = null)
    {
        var output = new StringBuilder(text.Length + 32);
        var writer = new InlineStyleWriter(output);
        if (!string.IsNullOrEmpty(baseStyle)) writer.Push(baseStyle);
        if (!string.IsNullOrEmpty(childStyle)) writer.Push(childStyle);
        writer.Append(Visible(text));
        if (!string.IsNullOrEmpty(childStyle)) writer.Pop();
        if (!string.IsNullOrEmpty(baseStyle)) writer.Pop();
        return output.ToString();
    }

    private static void RenderLink(InlineStyleWriter output, LinkInline link, bool styled, TerminalTheme theme)
    {
        var label = Render(link.FirstChild, styled: false, theme: theme);
        var url = Visible(link.Url ?? "");
        if (link.IsImage)
        {
            output.Append("[image: ");
            AppendInlines(output, link.FirstChild, styled, theme);
            output.Append("]");
            if (url.Length > 0)
            {
                output.Append(" ");
                AppendStyled(output, $"({url})", styled ? "\u001b[2m" + theme.Fg("mdLinkUrl") : "", "\u001b[22m\u001b[39m");
            }
            return;
        }

        AppendStyledChildren(output, link.FirstChild, styled, theme,
            styled ? "\u001b[4m" + theme.Fg("mdLink") : "", "\u001b[24m\u001b[39m");
        if (url.Length > 0 && !string.Equals(label, url, StringComparison.Ordinal))
        {
            output.Append(" ");
            AppendStyled(output, $"({url})", styled ? "\u001b[2m" + theme.Fg("mdLinkUrl") : "", "\u001b[22m\u001b[39m");
        }
    }

    private static void RenderEmphasis(InlineStyleWriter output, EmphasisInline emphasis, bool styled, TerminalTheme theme)
    {
        var delimiter = emphasis.DelimiterChar;
        var count = emphasis.DelimiterCount;
        var opening = !styled ? "" : delimiter == '~' && count >= 2
            ? "\u001b[9m"
            : count >= 2 ? "\u001b[1m" : "\u001b[3m";
        var closing = !styled ? "" : delimiter == '~' && count >= 2
            ? "\u001b[29m"
            : count >= 2 ? "\u001b[22m" : "\u001b[23m";
        AppendStyledChildren(output, emphasis.FirstChild, styled, theme, opening, closing);
    }

    private static void AppendStyledChildren(InlineStyleWriter output, Inline? first, bool styled,
        TerminalTheme theme, string opening, string closing = "\u001b[0m")
    {
        if (opening.Length == 0)
        {
            AppendInlines(output, first, styled, theme);
            return;
        }
        output.Push(opening, closing);
        AppendInlines(output, first, styled, theme);
        output.Pop();
    }

    private static void AppendStyled(InlineStyleWriter output, string text, string opening,
        string closing = "\u001b[0m")
    {
        if (opening.Length == 0)
        {
            output.Append(text);
            return;
        }
        output.Push(opening, closing);
        output.Append(text);
        output.Pop();
    }

    private sealed class InlineStyleWriter(StringBuilder output)
    {
        private readonly List<(string Opening, string Closing)> _styles = [];

        public void Append(string value) => output.Append(value);

        public void Push(string opening, string closing = Reset)
        {
            output.Append(opening);
            _styles.Add((opening, closing));
        }

        public void Pop()
        {
            var closing = _styles[^1].Closing;
            _styles.RemoveAt(_styles.Count - 1);
            output.Append(closing);
            foreach (var (opening, _) in _styles) output.Append(opening);
        }
    }

    private static void AppendVisible(StringBuilder output, string text)
    {
        foreach (var rune in text.EnumerateRunes())
        {
            if (Rune.IsControl(rune)) output.Append(' ');
            else output.Append(rune.ToString());
        }
    }
}
