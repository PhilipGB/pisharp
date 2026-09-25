using Markdig.Extensions.Mathematics;
using Markdig.Helpers;
using Markdig.Parsers;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;

namespace PiSharp.Cli.Tui;

/// <summary>Preserves Pi's backslash-delimited math tokens inside Markdig's inline AST.</summary>
internal sealed class TerminalLatexDelimiterParser : InlineParser
{
    public TerminalLatexDelimiterParser() => OpeningCharacters = ['\\'];

    public override bool Match(InlineProcessor processor, ref StringSlice slice)
    {
        if (slice.CurrentChar != '\\') return false;
        var opening = slice.PeekChar();
        if (opening is not ('(' or '[')) return false;

        var closing = opening == '(' ? ')' : ']';
        var sourceStart = slice.Start;
        var current = slice.NextChar();
        if (current != opening) return false;
        current = slice.NextChar();
        var contentStart = slice.Start;

        while (current != '\0')
        {
            if (current == '\\' && slice.PeekChar() == closing)
            {
                var closingStart = slice.Start;
                slice.NextChar();
                slice.NextChar();
                if (closingStart == contentStart)
                {
                    processor.Inline = new LiteralInline("\\" + opening + "\\" + closing);
                    return true;
                }
                var inline = new MathInline
                {
                    Span = new SourceSpan(
                        processor.GetSourcePosition(sourceStart, out var line, out var column),
                        processor.GetSourcePosition(slice.Start - 1)),
                    Line = line,
                    Column = column,
                    Delimiter = opening,
                    DelimiterCount = 1,
                    Content = slice
                };
                inline.Content.Start = contentStart;
                inline.Content.End = closingStart - 1;
                processor.Inline = inline;
                return true;
            }

            current = slice.NextChar();
        }

        slice.Start = sourceStart + 2;
        processor.Inline = new LiteralInline("\\" + opening);
        return true;
    }
}
