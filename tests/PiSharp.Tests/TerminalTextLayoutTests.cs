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
        Assert.Equal("      - " + partialFlag, string.Concat(lines.Select(StripTerminalSequences)));
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
