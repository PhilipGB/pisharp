using System.Text.RegularExpressions;
using Markdig.Extensions.Tables;
using Markdig.Syntax;
using PiSharp.Cli.Tui;

namespace PiSharp.Tests;

public sealed class TerminalMarkdownTests
{
    [Fact]
    public void ParserProducesCommonMarkAndGfmAstNodes()
    {
        const string markdown = "# Heading\n\n- [x] task\n\n| Name | Value |\n| --- | ---: |\n| item | 2 |";

        var document = TerminalMarkdownParser.Parse(markdown);

        Assert.Contains(document, block => block is HeadingBlock);
        Assert.Contains(document.OfType<ListBlock>(), _ => true);
        Assert.Contains(document.OfType<Table>(), _ => true);
    }

    [Fact]
    public void RendererHandlesNestedListsReferenceLinksAndAutolinks()
    {
        const string markdown = "1. first\n   - nested\n2. **bold _italic_**\n\n- [ ] pending\n- [X] complete\n\n[docs][ref] and https://example.test\n\n[ref]: https://reference.test";

        var rendered = StripSgr(TerminalMarkdownRenderer.Render(markdown));

        Assert.Contains("1. first", rendered);
        Assert.Contains("   • nested", rendered);
        Assert.Contains("2. bold italic", rendered);
        Assert.Contains("• [ ] pending", rendered);
        Assert.Contains("• [x] complete", rendered);
        Assert.Contains("docs (https://reference.test)", rendered);
        Assert.Contains("https://example.test", rendered);
    }

    [Fact]
    public void InlineStylesRestoreEnclosingSgrStateAfterNestedSpans()
    {
        var theme = TerminalThemeCatalog.LoadBuiltIn("dark", TerminalColorMode.TrueColor);
        var bold = TerminalMarkdownRenderer.Render("**before `code` after**", theme: theme);
        var link = TerminalMarkdownRenderer.Render("*before [link `code` tail](https://example.test) after*", theme: theme);
        var heading = TerminalMarkdownRenderer.Render("# before `code` after", theme: theme);
        var nested = TerminalMarkdownRenderer.Render("**bold _italic `code` italic_ bold**", theme: theme);

        AssertStyleAt(bold, "after", bold: true);
        AssertStyleAt(link, "tail", italic: true, underline: true, foreground: true);
        AssertStyleAt(link, "after", italic: true);
        AssertStyleAt(heading, "after", bold: true, foreground: true);
        AssertStyleAt(nested, "italic", bold: true, italic: true, occurrence: 1);
        AssertStyleAt(nested, "bold", bold: true, italic: false, occurrence: 1);
    }

    [Fact]
    public void RendererKeepsRawHtmlVisibleAndDoesNotEmitModelEscapeSequences()
    {
        var rendered = TerminalMarkdownRenderer.Render("before \u001b[2J after\n\n<script>safe text</script>");

        Assert.DoesNotContain("\u001b[2J", rendered);
        Assert.Contains("<script>safe text</script>", StripSgr(rendered));
    }

    [Fact]
    public void StreamingMarkdownKeepsOpenFencesAndDollarMathReadable()
    {
        const string markdown = "Partial **bold**\n\n```csharp\nvar value = 1;\n\nFormula $\\frac{1}{2}$";

        var rendered = StripSgr(TerminalMarkdownRenderer.Render(markdown));

        Assert.Contains("Partial bold", rendered);
        Assert.Contains("var value = 1;", rendered);
        Assert.Contains("Formula $\\frac{1}{2}$", rendered);
        Assert.DoesNotContain("```", rendered);
    }

    [Fact]
    public void RendererUsesTheConfiguredCodeBlockIndentAsALiteralPrefix()
    {
        const string markdown = "```text\nline\n```";

        var defaulted = StripSgr(TerminalMarkdownRenderer.Render(markdown));
        var configured = StripSgr(TerminalMarkdownRenderer.Render(markdown, codeBlockIndent: ">>"));
        var unindented = StripSgr(TerminalMarkdownRenderer.Render(markdown, codeBlockIndent: ""));

        Assert.Contains("  │  line", defaulted);
        Assert.Contains("  │>>line", configured);
        Assert.Contains("  │line", unindented);
    }

    [Fact]
    public void MathExtensionLeavesCurrencyAndCodeSpansAsText()
    {
        const string markdown = "Costs $5 and $10; use `$x$`, $HOME, and $x + y$.";

        var rendered = StripSgr(TerminalMarkdownRenderer.Render(markdown));

        Assert.Contains("Costs $5 and $10; use $x$, $HOME, and $x + y$.", rendered);
    }

    [Fact]
    public void BackslashDelimitedMathAndIncompleteStreamingInputRemainIntact()
    {
        const string complete = @"A map \(s \to \infty\), then \[x^2 + 1\].";
        const string display = "Before\n\n\\[x^2 + 1\\]\n\nafter";
        const string incomplete = @"Streaming \(\mathbb{C}^3";

        Assert.Equal(complete, StripSgr(TerminalMarkdownRenderer.Render(complete)));
        Assert.Equal(display, StripSgr(TerminalMarkdownRenderer.Render(display)));
        Assert.Equal(incomplete, StripSgr(TerminalMarkdownRenderer.Render(incomplete)));
    }

    private static string StripSgr(string text) => Regex.Replace(text, "\\u001b\\[[0-9;]*m", "");

    private static void AssertStyleAt(string text, string target, bool? bold = null, bool? italic = null,
        bool? underline = null, bool? foreground = null, int occurrence = 0)
    {
        var offset = 0;
        for (var index = 0; index <= occurrence; index++)
        {
            offset = text.IndexOf(target, offset, StringComparison.Ordinal);
            Assert.NotEqual(-1, offset);
            if (index < occurrence) offset += target.Length;
        }

        var state = default(SgrState);
        for (var index = 0; index < offset;)
        {
            if (index + 1 < text.Length && text[index] == '\u001b' && text[index + 1] == '[')
            {
                var end = text.IndexOf('m', index + 2);
                if (end < 0 || end >= offset) break;
                foreach (var parameter in text[(index + 2)..end].Split(';'))
                {
                    var value = parameter.Length == 0 ? 0 : int.Parse(parameter);
                    state = value switch
                    {
                        0 => default,
                        1 => state with { Bold = true },
                        3 => state with { Italic = true },
                        4 => state with { Underline = true },
                        9 => state with { Strike = true },
                        22 => state with { Bold = false },
                        23 => state with { Italic = false },
                        24 => state with { Underline = false },
                        29 => state with { Strike = false },
                        39 => state with { Foreground = false },
                        38 or >= 30 and <= 37 or >= 90 and <= 97 => state with { Foreground = true },
                        _ => state
                    };
                }
                index = end + 1;
                continue;
            }
            index++;
        }

        if (bold is { } expectedBold) Assert.Equal(expectedBold, state.Bold);
        if (italic is { } expectedItalic) Assert.Equal(expectedItalic, state.Italic);
        if (underline is { } expectedUnderline) Assert.Equal(expectedUnderline, state.Underline);
        if (foreground is { } expectedForeground)
            Assert.True(expectedForeground == state.Foreground,
                $"Foreground state before '{target}': {string.Join(' ', Regex.Matches(text[..offset], "\\u001b\\[[0-9;]*m").Select(match => match.Value))}");
    }

    private readonly record struct SgrState(bool Bold, bool Italic, bool Underline, bool Strike, bool Foreground);
}
