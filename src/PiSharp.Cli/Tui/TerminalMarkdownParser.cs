using Markdig;
using Markdig.Extensions.EmphasisExtras;
using Markdig.Syntax;

namespace PiSharp.Cli.Tui;

/// <summary>Builds Markdig's CommonMark/GFM AST for active-screen Markdown.</summary>
internal static class TerminalMarkdownParser
{
    private static readonly MarkdownPipeline Pipeline = CreatePipeline();

    public static MarkdownDocument Parse(string markdown)
    {
        ArgumentNullException.ThrowIfNull(markdown);
        return Markdown.Parse(markdown, Pipeline);
    }

    private static MarkdownPipeline CreatePipeline()
    {
        var builder = new MarkdownPipelineBuilder()
            .UsePipeTables()
            .UseTaskLists()
            .UseEmphasisExtras(EmphasisExtraOptions.Strikethrough)
            .UseMathematics()
            .UseAutoLinks()
            .DisableHtml();
        builder.InlineParsers.Insert(0, new TerminalLatexDelimiterParser());
        return builder.Build();
    }
}
