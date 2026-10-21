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
        Assert.Contains("    - nested", rendered);
        Assert.Contains("2. bold italic", rendered);
        Assert.Contains("- [ ] pending", rendered);
        Assert.Contains("- [x] complete", rendered);
        Assert.Contains("docs (https://reference.test)", rendered);
        Assert.Contains("https://example.test", rendered);
    }

    [Fact]
    public void TaskListMarkerInheritsThePinnedListBulletStyle()
    {
        var theme = TerminalThemeCatalog.LoadBuiltIn("dark", TerminalColorMode.TrueColor);
        var rendered = TerminalMarkdownRenderer.Render("- [ ] pending\n- [x] complete", theme: theme);

        Assert.Contains(theme.Style("mdListBullet", "- [ ] "), rendered);
        Assert.Contains(theme.Style("mdListBullet", "- [x] "), rendered);
    }

    [Fact]
    public void BlockquoteTextRemainsItalicAroundNestedEmphasis()
    {
        var rendered = TerminalMarkdownRenderer.Render("> quote text with **bold emphasis**.");

        AssertStyleAt(rendered, "quote text with ", italic: true);
        AssertStyleAt(rendered, "bold emphasis", italic: true, bold: true);
        AssertStyleAt(rendered, ".", italic: true);
    }

    [Fact]
    public void EmptyBlockquoteKeepsItsBorderPaddingStyled()
    {
        var theme = TerminalThemeCatalog.LoadBuiltIn("dark", TerminalColorMode.TrueColor);

        var rendered = TerminalMarkdownRenderer.Render(">", theme: theme);

        Assert.Contains(theme.Style("mdQuoteBorder", "│ "), rendered);
    }

    [Fact]
    public void RendererMatchesPinnedPiNestedListDepthAndHorizontalRuleWidth()
    {
        const string markdown = "- parent\n  - nested\n    1. ordered grandchild\n\n---";

        var rendered = StripSgr(TerminalMarkdownRenderer.Render(markdown, availableWidth: 96));

        var nested = Assert.Single(rendered.Split('\n'), line => line.Contains("- nested", StringComparison.Ordinal));
        var grandchild = Assert.Single(rendered.Split('\n'),
            line => line.Contains("1. ordered grandchild", StringComparison.Ordinal));
        Assert.Equal(4, nested.TakeWhile(char.IsWhiteSpace).Count());
        Assert.Equal(8, grandchild.TakeWhile(char.IsWhiteSpace).Count());
        Assert.Contains(new string('─', 80), rendered);
        Assert.DoesNotContain(new string('─', 81), rendered);
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
        AssertStyleAt(link, " (https://example.test)", dim: false, foreground: true);
        AssertStyleAt(link, "after", italic: true);
        AssertStyleAt(heading, "after", bold: true, underline: true, foreground: true);
        var subheading = TerminalMarkdownRenderer.Render("## section", theme: theme);
        AssertStyleAt(subheading, "section", bold: true, underline: false, foreground: true);
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
        Assert.Contains("```csharp", rendered);
    }

    [Fact]
    public void StreamingMarkdownKeepsIncompleteLinkSyntaxVisibleWithoutChangingCodeOrCompleteLinks()
    {
        const string incomplete = "Partial **bold and [an unfinished";
        const string complete = "Partial **bold and [a link](https://fixture.invalid)";
        const string code = "```text\n[an unfinished\n```";

        Assert.Equal(incomplete, StripSgr(TerminalMarkdownRenderer.RenderStreaming(incomplete)));
        Assert.Contains("a link (https://fixture.invalid)",
            StripSgr(TerminalMarkdownRenderer.RenderStreaming(complete)));
        Assert.Contains("[an unfinished", StripSgr(TerminalMarkdownRenderer.RenderStreaming(code)));
        Assert.DoesNotContain("\\[an unfinished", StripSgr(TerminalMarkdownRenderer.RenderStreaming(code)));
    }

    [Fact]
    public void RendererUsesTheConfiguredCodeBlockIndentAsALiteralPrefix()
    {
        const string markdown = "```text\nline\n```";

        var defaulted = StripSgr(TerminalMarkdownRenderer.Render(markdown));
        var configured = StripSgr(TerminalMarkdownRenderer.Render(markdown, codeBlockIndent: ">>"));
        var unindented = StripSgr(TerminalMarkdownRenderer.Render(markdown, codeBlockIndent: ""));

        Assert.Contains("```text\n  line", defaulted);
        Assert.Contains("```text\n>>line", configured);
        Assert.Contains("```text\nline", unindented);
        Assert.Contains("\n```", defaulted);
    }

    [Fact]
    public void CodeFencesUseSyntaxThemeTokensForSupportedLanguagesAndPlainFallbackForUnknownOnes()
    {
        var theme = TerminalThemeCatalog.LoadBuiltIn("dark", TerminalColorMode.TrueColor);
        var csharp = TerminalMarkdownRenderer.Render("```csharp\npublic sealed class Widget { int Count = 2; }\n```", theme: theme);
        var javascript = TerminalMarkdownRenderer.Render("```javascript\nconst total = 3;\nconsole.log(total);\n```", theme: theme);
        var python = TerminalMarkdownRenderer.Render("```python\ndef add(value): # comment\n    return value + 1\n```", theme: theme);
        var json = TerminalMarkdownRenderer.Render("```json\n{\"name\": \"pi\", \"active\": true}\n```", theme: theme);
        var jsonCode = TerminalSyntaxHighlighter.Highlight("{\"name\": \"pi\", \"active\": true}", "json", theme);
        var bash = TerminalMarkdownRenderer.Render("```bash\nif echo \"ready\"; then # comment\n  echo 2\nfi\n```", theme: theme);
        var unknown = TerminalMarkdownRenderer.Render("```unknown\nplain text\n```", theme: theme);
        var plaintext = TerminalMarkdownRenderer.Render("```text\nplain text\n```", theme: theme);

        Assert.Contains(theme.Fg("syntaxKeyword") + "public", csharp);
        Assert.Contains(theme.Fg("syntaxKeyword") + "sealed", csharp);
        Assert.Contains(theme.Fg("syntaxFunction") + "Widget", csharp);
        Assert.Contains(theme.Fg("syntaxType") + "int", csharp);
        Assert.Contains(theme.Fg("syntaxNumber") + "2", csharp);
        Assert.Contains(theme.Fg("syntaxKeyword") + "const", javascript);
        Assert.Contains(theme.Fg("syntaxType") + "console", javascript);
        Assert.Contains(theme.Fg("syntaxKeyword") + "def", python);
        Assert.Contains(theme.Fg("syntaxFunction") + "add", python);
        Assert.Contains(theme.Fg("syntaxVariable") + "value", python);
        Assert.Contains(theme.Fg("syntaxComment") + "# comment", python);
        Assert.Contains(theme.Fg("syntaxVariable") + "\"name\"", json);
        Assert.Contains(theme.Fg("syntaxString") + "\"pi\"", json);
        Assert.Contains(theme.Fg("syntaxNumber") + "true", json);
        Assert.Contains(theme.Fg("syntaxKeyword") + "if", bash);
        Assert.Contains(theme.Fg("syntaxType") + "echo", bash);
        Assert.Contains(theme.Fg("syntaxComment") + "# comment", bash);
        Assert.Contains(theme.Fg("mdCodeBlock") + "plain text", unknown);
        Assert.DoesNotContain(theme.Fg("mdCodeBlock") + "plain text", plaintext);
        Assert.Contains("  plain text", StripSgr(plaintext));
        Assert.DoesNotContain(theme.Fg("syntaxPunctuation"), jsonCode);
    }

    [Fact]
    public void TableCellsUsePinnedPiLeftAlignmentAndUncoloredBorders()
    {
        var theme = TerminalThemeCatalog.LoadBuiltIn("dark", TerminalColorMode.TrueColor);
        const string markdown = "| Formula | Value |\n| --- | ---: |\n| $x^2$ | 4 |";

        var rendered = TerminalMarkdownRenderer.Render(markdown, theme: theme);

        Assert.Contains("│ x²      │ 4     │", StripSgr(rendered));
        Assert.DoesNotContain(theme.Fg("mdCodeBlockBorder"), rendered);
        AssertStyleAt(rendered, "Formula", bold: true);
    }

    [Fact]
    public void AssistantMarkdownLeavesTranscriptPaddingToTheScreenComposer()
    {
        var presentation = new TerminalConversationPresentation(new TerminalTranscriptBuffer(),
            new TerminalImageRenderer(), () => 80, () => TerminalTheme.Default);

        presentation.SetAssistantText("plain output");

        Assert.Equal("\nplain output", presentation.LiveText);
    }

    [Fact]
    public void MathExtensionLeavesCurrencyAndCodeSpansAsText()
    {
        const string markdown = "Costs $5 and $10; use `$x$`, $HOME, and $x + y$.";

        var rendered = StripSgr(TerminalMarkdownRenderer.Render(markdown));

        Assert.Contains("Costs $5 and $10; use $x$, $HOME, and x + y.", rendered);
    }

    [Fact]
    public void SupportedInlineAndDisplayMathRenderAsUnicodeAndUnsupportedMathKeepsItsSource()
    {
        const string complete = @"A map \(s \to \infty\), then \[x^2 + 1\].";
        const string display = "Before\n\n\\[x^2 + 1\\]\n\nafter";
        const string incomplete = @"Streaming \(\mathbb{C}^3";
        const string fraction = "$$\n\\frac{x^2+1}{x-1}\n$$";
        const string unsupported = "Keep $x + \\unknown{y}$ visible.";

        Assert.Equal("x² + 1", TerminalLatexRenderer.Render("x^2 + 1"));
        Assert.Equal("x²+1\n────\nx-1", TerminalLatexRenderer.Render("\\frac{x^2+1}{x-1}", display: true));
        Assert.Equal("A map s → ∞, then x² + 1.", StripSgr(TerminalMarkdownRenderer.Render(complete)));
        Assert.Equal("Before\n\nx² + 1\n\nafter", StripSgr(TerminalMarkdownRenderer.Render(display)));
        Assert.Equal(incomplete, StripSgr(TerminalMarkdownRenderer.Render(incomplete)));
        Assert.Equal("x²+1\n────\nx-1", StripSgr(TerminalMarkdownRenderer.Render(fraction)));
        Assert.Equal(unsupported, StripSgr(TerminalMarkdownRenderer.Render(unsupported)));
    }

    [Fact]
    public void InlineMathUsesPinnedPiUnicodeSymbolsScriptsAndFractions()
    {
        const string markdown = "Answer $\\mathbb{C}^3 \\to \\mathbb{C}^3$, with $F_1 = \\frac{1}{4x^2}$.";

        var rendered = StripSgr(TerminalMarkdownRenderer.Render(markdown));

        Assert.Contains("ℂ³ → ℂ³", rendered);
        Assert.Contains("F₁ = 1/(4x²)", rendered);
    }

    [Theory]
    [InlineData(@"\alpha + \beta + \Gamma", "α + β + Γ")]
    [InlineData(@"\mathbb{C}^3 \to \mathbb{C}^3", "ℂ³ → ℂ³")]
    [InlineData(@"x \notin A \Longrightarrow x \neq 0", "x ∉ A ⇒ x ≠ 0")]
    [InlineData(@"\sum_{i=0}^n", "∑ᵢ₌₀ⁿ")]
    [InlineData(@"\int_0^\infty e^{-x^2}\,dx", "∫₀^∞ e^(-x²) dx")]
    [InlineData(@"\sqrt{1+x^2}", "√(1+x²)")]
    public void InlineMathMatchesPinnedPiSymbolAndOperatorVectors(string source, string expected)
    {
        Assert.Equal(expected, TerminalLatexRenderer.Render(source));
    }

    private static string StripSgr(string text) => Regex.Replace(text, "\\u001b\\[[0-9;]*m", "");

    private static void AssertStyleAt(string text, string target, bool? bold = null, bool? italic = null,
        bool? underline = null, bool? foreground = null, bool? dim = null, int occurrence = 0)
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
                var parameters = text[(index + 2)..end].Split(';');
                for (var parameterIndex = 0; parameterIndex < parameters.Length; parameterIndex++)
                {
                    var parameter = parameters[parameterIndex];
                    var value = parameter.Length == 0 ? 0 : int.Parse(parameter);
                    if (value is 38 or 48 or 58)
                    {
                        if (parameterIndex + 1 < parameters.Length)
                        {
                            var colorMode = int.Parse(parameters[++parameterIndex]);
                            if (colorMode == 2) parameterIndex = Math.Min(parameters.Length, parameterIndex + 3);
                            else if (colorMode == 5) parameterIndex = Math.Min(parameters.Length, parameterIndex + 1);
                        }
                        if (value == 38) state = state with { Foreground = true };
                        continue;
                    }
                    state = value switch
                    {
                        0 => default,
                        1 => state with { Bold = true },
                        2 => state with { Dim = true },
                        3 => state with { Italic = true },
                        4 => state with { Underline = true },
                        9 => state with { Strike = true },
                        22 => state with { Bold = false, Dim = false },
                        23 => state with { Italic = false },
                        24 => state with { Underline = false },
                        29 => state with { Strike = false },
                        39 => state with { Foreground = false },
                        >= 30 and <= 37 or >= 90 and <= 97 => state with { Foreground = true },
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
        if (dim is { } expectedDim) Assert.Equal(expectedDim, state.Dim);
        if (foreground is { } expectedForeground)
            Assert.True(expectedForeground == state.Foreground,
                $"Foreground state before '{target}': {string.Join(' ', Regex.Matches(text[..offset], "\\u001b\\[[0-9;]*m").Select(match => match.Value))}");
    }

    private readonly record struct SgrState(bool Bold, bool Dim, bool Italic, bool Underline, bool Strike, bool Foreground);
}
