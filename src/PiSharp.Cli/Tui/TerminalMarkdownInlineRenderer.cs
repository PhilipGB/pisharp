using System.Text;
using Markdig.Extensions.Mathematics;
using Markdig.Extensions.TaskLists;
using Markdig.Syntax.Inlines;

namespace PiSharp.Cli.Tui;

/// <summary>Renders Markdown inline AST nodes using terminal-safe text and PiSharp styles.</summary>
internal static class TerminalMarkdownInlineRenderer
{
    private const string Reset = "\u001b[0m";

    public static string Render(Inline? first, bool styled, TerminalTheme? theme = null)
    {
        theme ??= TerminalTheme.Default;
        var output = new StringBuilder();
        for (var inline = first; inline is not null; inline = inline.NextSibling)
        {
            switch (inline)
            {
                case LiteralInline literal:
                    AppendVisible(output, literal.Content.ToString());
                    break;
                case CodeInline code:
                    AppendStyled(output, Visible(code.Content), styled ? theme.Fg("mdCode") : "");
                    break;
                case MathInline math:
                    var (opening, closing) = math.Delimiter switch
                    {
                        '(' => ("\\(", "\\)"),
                        '[' => ("\\[", "\\]"),
                        _ => (new string('$', Math.Max(1, math.DelimiterCount)),
                            new string('$', Math.Max(1, math.DelimiterCount)))
                    };
                    AppendStyled(output, Visible(opening + math.Content.ToString() + closing), styled ? theme.Fg("mdCode") : "");
                    break;
                case TaskList task:
                    AppendVisible(output, task.Checked ? "[x]" : "[ ]");
                    break;
                case AutolinkInline autoLink:
                    AppendVisible(output, autoLink.Url);
                    break;
                case LinkInline link:
                    RenderLink(output, link, styled, theme);
                    break;
                case EmphasisInline emphasis:
                    RenderEmphasis(output, emphasis, styled, theme);
                    break;
                case LineBreakInline:
                    output.Append('\n');
                    break;
                case HtmlInline html:
                    AppendVisible(output, html.Tag);
                    break;
                case HtmlEntityInline entity:
                    AppendVisible(output, entity.Transcoded.ToString());
                    break;
                case ContainerInline container:
                    output.Append(Render(container.FirstChild, styled, theme));
                    break;
                default:
                    AppendVisible(output, inline.ToString() ?? "");
                    break;
            }
        }
        return output.ToString();
    }

    public static string Visible(string text)
    {
        var output = new StringBuilder(text.Length);
        AppendVisible(output, text);
        return output.ToString();
    }

    private static void RenderLink(StringBuilder output, LinkInline link, bool styled, TerminalTheme theme)
    {
        var label = Render(link.FirstChild, styled, theme);
        var url = Visible(link.Url ?? "");
        if (link.IsImage)
        {
            output.Append("[image: ").Append(label).Append(']');
            if (url.Length > 0) output.Append(' ').Append(styled ? theme.Style("mdLinkUrl", $"({url})", dim: true) : $"({url})");
            return;
        }

        AppendStyled(output, label, styled ? "\u001b[4m" + theme.Fg("mdLink") : "");
        if (url.Length > 0 && !string.Equals(label, url, StringComparison.Ordinal))
            output.Append(' ').Append(styled ? theme.Style("mdLinkUrl", $"({url})", dim: true) : $"({url})");
    }

    private static void RenderEmphasis(StringBuilder output, EmphasisInline emphasis, bool styled, TerminalTheme theme)
    {
        var delimiter = emphasis.DelimiterChar;
        var count = emphasis.DelimiterCount;
        var opening = !styled ? "" : delimiter == '~' && count >= 2
            ? "\u001b[9m"
            : count >= 2 ? "\u001b[1m" : "\u001b[3m";
        var closing = !styled ? "" : delimiter == '~' && count >= 2
            ? "\u001b[29m"
            : count >= 2 ? "\u001b[22m" : "\u001b[23m";
        output.Append(opening).Append(Render(emphasis.FirstChild, styled, theme)).Append(closing);
    }

    private static void AppendStyled(StringBuilder output, string text, string opening)
    {
        if (opening.Length > 0) output.Append(opening).Append(text).Append(Reset);
        else output.Append(text);
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
