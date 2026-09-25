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
}
