using Markdig.Syntax;

namespace PiSharp.Cli.Tui;

internal static class TerminalMarkdownSource
{
    public static string Slice(Block block, string source)
    {
        var span = block.Span;
        return span.Start >= 0 && span.End >= span.Start && span.End < source.Length
            ? source.Substring(span.Start, span.End - span.Start + 1)
            : "";
    }
}
