using System.Text;
using PiSharp.Cli.Tui;

namespace PiSharp.Tests;

public sealed class TerminalTextLayoutTests
{
    [Fact]
    public void WidthIgnoresAnsiAndMeasuresWideAndJoinedGraphemesInCells()
    {
        const string family = "👩‍👩‍👧‍👦";
        var styled = "\u001b[1mA\u001b[0m" +
            "\u001b]8;;https://example.test\u001b\\e\u0301界" + family +
            "\u001b]8;;\u001b\\";

        Assert.Equal(6, TerminalTextLayout.Width(styled));
    }

    [Fact]
    public void WidthMatchesEmojiPresentationAndStreamingRegionalIndicatorWidths()
    {
        var samples = new[] { "👍", "👍🏻", "✅", "⚡", "⚡️", "👨", "👨‍💻", "🏳️‍🌈", "🇨", "🇨🇳" };
        foreach (var sample in samples)
            Assert.Equal(2, TerminalTextLayout.Width(sample));
        for (var codePoint = 0x1F1E6; codePoint <= 0x1F1FF; codePoint++)
            Assert.Equal(2, TerminalTextLayout.Width(new Rune(codePoint).ToString()));
        Assert.Equal(1, TerminalTextLayout.Width("⚡︎"));
    }

    [Fact]
    public void WrapSplitsLongTokensAndCarriesAnsiStateWithoutSplittingGraphemes()
    {
        const string family = "👩‍👩‍👧‍👦";
        const string text = "\u001b[31mA界e\u0301" + family + "BC\u001b[0m";

        var lines = TerminalTextLayout.Wrap(text, 4);

        Assert.Equal(2, lines.Count);
        Assert.All(lines, line => Assert.InRange(TerminalTextLayout.Width(line), 0, 4));
        Assert.Contains("\u001b[31m", lines[0]);
        Assert.Contains("\u001b[31m", lines[1]);
        Assert.Equal("A界e\u0301" + family + "BC",
            string.Concat(lines.Select(StripTerminalSequences)));
    }

    [Fact]
    public void WrapPrefersWordBoundariesAndDropsWhitespaceUsedForWrapping()
    {
        Assert.Equal(["one two", "three"], TerminalTextLayout.Wrap("one two three", 8)
            .Select(StripTerminalSequences));
        Assert.Equal(["alpha", "beta"], TerminalTextLayout.Wrap("alpha   beta", 8)
            .Select(StripTerminalSequences));
        Assert.Equal(["one", "two"], TerminalTextLayout.Wrap("one two", 4)
            .Select(StripTerminalSequences));
    }

    [Fact]
    public void WrapPreservesSgrAndOsc8StateAcrossWordBoundaries()
    {
        const string red = "\u001b[31m";
        const string reset = "\u001b[0m";
        const string open = "\u001b]8;;https://example.test\u001b\\";
        const string close = "\u001b]8;;\u001b\\";

        var styled = TerminalTextLayout.Wrap(red + "red blue green" + reset, 8);
        Assert.Equal(["red blue", "green"], styled.Select(StripTerminalSequences));
        Assert.Contains(red, styled[0]);
        Assert.Contains(red, styled[1]);

        var linked = TerminalTextLayout.Wrap(open + "one two" + close, 5);
        Assert.Equal(["one", "two"], linked.Select(StripTerminalSequences));
        Assert.All(linked, line => Assert.StartsWith(open, line));
        Assert.All(linked, line => Assert.Contains(close, line));
    }

    [Fact]
    public void WrapPreservesSgrSourceOrderAtPiSliceBoundaryVectors()
    {
        const string green = "\u001b[32m";
        const string magenta = "\u001b[35m";
        const string foregroundReset = "\u001b[39m";

        var first = TerminalTextLayout.Wrap(green + "foo" + foregroundReset + " bar", 4);
        Assert.Equal(2, first.Count);
        Assert.StartsWith(green + foregroundReset, first[1]);
        Assert.Equal("bar", StripTerminalSequences(first[1]));

        const string searchLine = "Another \u001b[35malpha\u001b[39m line with \u001b[35mbeta\u001b[39m later.";
        var searchRows = TerminalTextLayout.Wrap(searchLine, 13);
        Assert.True(searchRows.Count > 1);
        Assert.StartsWith(magenta + foregroundReset, searchRows[1]);
        Assert.Equal("line", StripTerminalSequences(searchRows[1]).Split(' ')[0]);

        const string selectionOn = "\u001b[7m";
        const string selectionOff = "\u001b[27m";
        Assert.Equal(green + "foo" + foregroundReset + selectionOn + " bar" + selectionOff,
            TerminalTextLayout.HighlightCells(green + "foo" + foregroundReset + " bar", 3, 7));
        Assert.Equal("Another " + magenta + "alpha" + foregroundReset + selectionOn + " line" + selectionOff +
            " with " + magenta + "beta" + foregroundReset + " later.",
            TerminalTextLayout.HighlightCells(searchLine, 13, 18));
    }

    [Fact]
    public void WrapHardSplitsOnlyOverlongTokensAndKeepsCjkAndEmojiGraphemes()
    {
        Assert.Equal(["abcd", "efgh", "ij"], TerminalTextLayout.Wrap("abcdefghij", 4)
            .Select(StripTerminalSequences));
        var mixed = TerminalTextLayout.Wrap("hi 世界🙂there", 6).Select(StripTerminalSequences).ToArray();
        Assert.Equal("hi 世", mixed[0]);
        Assert.Equal("界🙂there", string.Concat(mixed.Skip(1)));
        Assert.All(mixed, line => Assert.InRange(TerminalTextLayout.Width(line), 0, 6));
    }

    [Fact]
    public void WrapMatchesCurrentPiCjkAndEmojiWordBoundaryVectors()
    {
        const string cjk = "This is an example 中文汉字测试段落内容中文汉字测试段落内容.";
        const string emoji = "hi 👩‍💻 there";

        Assert.Equal(["This is an example 中文汉字测试段落内容", "中文汉字测试段落内容."],
            TerminalTextLayout.Wrap(cjk, 40).Select(StripTerminalSequences));
        Assert.Equal(["hi 👩‍💻", "there"],
            TerminalTextLayout.Wrap(emoji, 7).Select(StripTerminalSequences));
    }

    [Fact]
    public void LayoutUsesOneAnsiAwareMappingForWrappedRowsSearchAndImageMarkers()
    {
        const string open = "\u001b]8;;https://example.test\u001b\\";
        const string close = "\u001b]8;;\u001b\\";
        var marker = $"\uE000psimg-{Guid.NewGuid():N}-0\uE001";
        var text = "one two " + open + "three" + close + "\n" + marker + "\nnext";

        var layout = TerminalTextLayout.Create(text, 8);

        Assert.Equal(4, layout.RowCount);
        Assert.Equal(1, layout.VisualRowAt(text.IndexOf("three", StringComparison.Ordinal)));
        Assert.Equal(2, layout.VisualRowAt(text.IndexOf(marker, StringComparison.Ordinal)));
        Assert.Equal(3, layout.VisualRowAt(text.IndexOf("next", StringComparison.Ordinal)));
        Assert.Equal(layout.RowCount, TerminalTranscriptViewport.CountVisualRows(text, 8));

        var window = TerminalTranscriptViewport.WrapWindow(layout, 2, 1, out var scrollOffset, out var firstRow);
        Assert.Equal(1, scrollOffset);
        Assert.Equal(1, firstRow);
        Assert.Equal(layout.Rows.Skip(1).Take(2), window);
    }

    [Fact]
    public void TranscriptSearchTreatsOscHyperlinksAsFormattingForTextAndRowPosition()
    {
        const string open = "\u001b]8;;https://example.test\u001b\\";
        const string close = "\u001b]8;;\u001b\\";
        var text = "a" + open + "b" + close;
        var search = new TranscriptSearchController();
        search.SetQuery("b", 0);

        var result = search.Highlight(text);
        var layout = TerminalTextLayout.Create(text, 1);

        Assert.Equal(1, result.MatchCount);
        Assert.Equal(text.IndexOf('b'), result.SelectedTextStart);
        Assert.Equal(1, layout.VisualRowAt(result.SelectedTextStart));
        Assert.Equal(layout.RowCount, TerminalTextLayout.Create(result.HighlightedText, 1).RowCount);
    }

    [Fact]
    public void WrapReplacesAnUnfittableWideGraphemeAtOneCell()
    {
        var lines = TerminalTextLayout.Wrap("界", 1);

        Assert.Single(lines);
        Assert.Equal(1, TerminalTextLayout.Width(lines[0]));
        Assert.Equal("?", StripTerminalSequences(lines[0]));
    }

    [Fact]
    public void WrapKeepsPartialRegionalIndicatorsAtTwoCells()
    {
        const string partialFlag = "🇨";

        var lines = TerminalTextLayout.Wrap("      - " + partialFlag, 9);

        Assert.Equal(2, lines.Count);
        Assert.All(lines, line => Assert.InRange(TerminalTextLayout.Width(line), 0, 9));
        Assert.Equal(["      -", partialFlag], lines.Select(StripTerminalSequences));
        Assert.Equal(2, TerminalTextLayout.Width(partialFlag));
    }

    [Fact]
    public void WrapReopensAndClosesOsc8WithItsOriginalTerminator()
    {
        const string open = "\u001b]8;;https://example.test\u001b\\";
        const string close = "\u001b]8;;\u001b\\";

        var lines = TerminalTextLayout.Wrap(open + "0123456789" + close, 6);

        Assert.Equal(2, lines.Count);
        Assert.All(lines, line => Assert.StartsWith(open, line));
        Assert.All(lines.Take(lines.Count - 1), line => Assert.EndsWith(close + "\u001b[0m", line));
        Assert.All(lines, line => Assert.InRange(TerminalTextLayout.Width(line), 0, 6));

        const string belOpen = "\u001b]8;;https://example.test\a";
        const string belClose = "\u001b]8;;\a";
        var belLines = TerminalTextLayout.Wrap(belOpen + "0123456789" + belClose, 6);
        Assert.Equal(2, belLines.Count);
        Assert.All(belLines, line => Assert.StartsWith(belOpen, line));
        Assert.All(belLines.Take(belLines.Count - 1), line => Assert.EndsWith(belClose + "\u001b[0m", line));
    }

    [Fact]
    public void SelectionUsesDisplayCellsAndReappliesInverseAfterContentResets()
    {
        const string styled = "\u001b[31ma\u001b[0m界e\u0301";

        Assert.Equal("a界e\u0301", TerminalTextLayout.StripFormatting(styled));
        Assert.Equal((1, 3), TerminalTextLayout.CellRangeAt(styled, 2));
        Assert.Equal("界", TerminalTextLayout.SliceCells(styled, 1, 3));
        var highlighted = TerminalTextLayout.HighlightCells(styled, 1, 3);
        Assert.Contains("\u001b[7m界\u001b[27m", highlighted);

        var resetInsideSelection = TerminalTextLayout.HighlightCells("\u001b[31mal\u001b[0mpha", 1, 4);
        Assert.Contains("l\u001b[0m\u001b[7mph\u001b[27m", resetInsideSelection);

        const string family = "👩‍👩‍👧‍👦";
        var graphemeLine = "x" + family + "e\u0301";
        Assert.Equal((1, 3), TerminalTextLayout.CellRangeAt(graphemeLine, 2));
        Assert.Equal(family, TerminalTextLayout.SliceCells(graphemeLine, 1, 3));
        Assert.Equal((3, 4), TerminalTextLayout.CellRangeAt(graphemeLine, 3));
    }

    private static string StripTerminalSequences(string text)
    {
        var output = new System.Text.StringBuilder(text.Length);
        for (var index = 0; index < text.Length;)
        {
            if (text[index] == '\u001b')
            {
                index++;
                if (index < text.Length && text[index] == '[')
                {
                    index++;
                    while (index < text.Length && text[index] is not (>= '\u0040' and <= '\u007e')) index++;
                    if (index < text.Length) index++;
                    continue;
                }
                if (index < text.Length && text[index] == ']')
                {
                    index++;
                    while (index < text.Length && text[index] != '\a' &&
                        !(text[index] == '\u001b' && index + 1 < text.Length && text[index + 1] == '\\')) index++;
                    if (index < text.Length) index += text[index] == '\a' ? 1 : 2;
                    continue;
                }
                continue;
            }
            output.Append(text[index++]);
        }
        return output.ToString();
    }
}
