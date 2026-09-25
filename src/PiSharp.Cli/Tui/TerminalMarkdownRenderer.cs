using System.Text;
using System.Text.RegularExpressions;

namespace PiSharp.Cli.Tui;

/// <summary>Renders the streamed Markdown subset used in assistant transcript blocks.</summary>
internal static class TerminalMarkdownRenderer
{
    private static readonly TimeSpan InlineMatchTimeout = TimeSpan.FromMilliseconds(100);
    private static readonly Regex FenceStart = new("^ {0,3}(`{3,}|~{3,})(.*)$", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex FenceEnd = new("^ {0,3}(`{3,}|~{3,})[ \\t]*$", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex Heading = new("^ {0,3}#{1,6}[ \\t]+(.+?)\\s*#*\\s*$", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex Quote = new("^ {0,3}> ?(.*)$", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex ListItem = new("^(?<indent>[ \\t]*)(?<marker>[-+*]|[0-9]{1,9}[.)])[ \\t]+(?<text>.*)$", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex TaskListItem = new("^\\[(?<checked>[ xX])\\][ \\t]+(?<text>.*)$", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex HorizontalRule = new("^ {0,3}(?:(?:\\*\\s*){3,}|(?:-\\s*){3,}|(?:_\\s*){3,})$", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex Inline = new(
        "(?<image>!\\[(?<imageText>[^\\]]*)\\]\\((?<imageUrl>[^)\\s]+)(?:\\s+[\\\"'][^\\\"']*[\\\"'])?\\))" +
        "|(?<link>\\[(?<linkText>[^\\]]+)\\]\\((?<linkUrl>[^)\\s]+)(?:\\s+[\\\"'][^\\\"']*[\\\"'])?\\))" +
        "|(?<code>`+(?<codeText>[^`]+)`+)" +
        "|(?<strong>\\*\\*.+?\\*\\*|(?<![\\p{L}\\p{N}_])__.+?__(?![\\p{L}\\p{N}_]))" +
        "|(?<strike>~~.+?~~)" +
        "|(?<emphasis>(?<!\\*)\\*(?!\\s)(?:\\\\.|[^*])+?(?<!\\s)\\*(?!\\*)|(?<![\\p{L}\\p{N}_])_(?!\\s)(?:\\\\.|[^_])+?(?<!\\s)_(?![\\p{L}\\p{N}_]))" +
        "|(?<escape>\\\\[\\\\`*_{}\\[\\]()#+.!|>~-])",
        RegexOptions.Compiled | RegexOptions.CultureInvariant, InlineMatchTimeout);

    public static string Render(string markdown)
    {
        ArgumentNullException.ThrowIfNull(markdown);
        var lines = markdown.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n');
        var output = new StringBuilder(markdown.Length + 32);
        var inFence = false;
        var fenceCharacter = '\0';
        var fenceLength = 0;

        for (var index = 0; index < lines.Length; index++)
        {
            var line = lines[index];
            var closing = FenceEnd.Match(line);
            if (inFence)
            {
                if (closing.Success && closing.Groups[1].Value[0] == fenceCharacter && closing.Groups[1].Value.Length >= fenceLength)
                {
                    inFence = false;
                }
                else
                {
                    output.Append("\u001b[90m  │ \u001b[0m").Append(line).Append("\u001b[0m");
                }
            }
            else
            {
                var opening = FenceStart.Match(line);
                if (opening.Success)
                {
                    var marker = opening.Groups[1].Value;
                    var language = opening.Groups[2].Value.Trim().Split(' ', '\t')[0];
                    inFence = true;
                    fenceCharacter = marker[0];
                    fenceLength = marker.Length;
                    if (language.Length > 0) output.Append("\u001b[2m  [").Append(language).Append("]\u001b[0m");
                }
                else if (Heading.Match(line) is { Success: true } heading)
                {
                    output.Append("\u001b[1;36m").Append(RenderInline(heading.Groups[1].Value)).Append("\u001b[0m");
                }
                else if (HorizontalRule.IsMatch(line))
                {
                    output.Append("\u001b[90m────────────────────────\u001b[0m");
                }
                else if (Quote.Match(line) is { Success: true } quote)
                {
                    output.Append("\u001b[90m│ \u001b[0m").Append(RenderInline(quote.Groups[1].Value));
                }
                else if (ListItem.Match(line) is { Success: true } item)
                {
                    var indent = item.Groups["indent"].Value.Replace("\t", "  ", StringComparison.Ordinal);
                    var marker = item.Groups["marker"].Value;
                    var itemText = item.Groups["text"].Value;
                    var task = TaskListItem.Match(itemText);
                    var taskMarker = task.Success ? $"[{(task.Groups["checked"].Value is "x" or "X" ? "x" : " ")}] " : "";
                    if (task.Success) itemText = task.Groups["text"].Value;
                    output.Append(indent).Append(char.IsAsciiDigit(marker[0]) ? marker : "•").Append(' ')
                        .Append(taskMarker).Append(RenderInline(itemText));
                }
                else
                {
                    output.Append(RenderInline(line));
                }
            }

            if (index < lines.Length - 1) output.Append('\n');
        }
        return output.ToString();
    }

    private static string RenderInline(string text)
    {
        MatchCollection matches;
        try { matches = Inline.Matches(text); }
        catch (RegexMatchTimeoutException) { return text; }
        if (matches.Count == 0) return text;
        var output = new StringBuilder(text.Length + 16);
        var previous = 0;
        foreach (Match match in matches)
        {
            output.Append(text, previous, match.Index - previous);
            if (match.Groups["image"].Success)
            {
                output.Append("[image: ").Append(match.Groups["imageText"].Value).Append("] ")
                    .Append("\u001b[2m(").Append(match.Groups["imageUrl"].Value).Append(")\u001b[0m");
            }
            else if (match.Groups["link"].Success)
            {
                output.Append("\u001b[4;36m").Append(match.Groups["linkText"].Value).Append("\u001b[0m ")
                    .Append("\u001b[2m(").Append(match.Groups["linkUrl"].Value).Append(")\u001b[0m");
            }
            else if (match.Groups["code"].Success)
            {
                output.Append("\u001b[33m").Append(match.Groups["codeText"].Value).Append("\u001b[0m");
            }
            else if (match.Groups["strong"].Success)
            {
                var value = match.Value[2..^2];
                output.Append("\u001b[1m").Append(RenderInline(value)).Append("\u001b[22m");
            }
            else if (match.Groups["strike"].Success)
            {
                output.Append("\u001b[9m").Append(RenderInline(match.Value[2..^2])).Append("\u001b[29m");
            }
            else if (match.Groups["emphasis"].Success)
            {
                output.Append("\u001b[3m").Append(RenderInline(match.Value[1..^1])).Append("\u001b[23m");
            }
            else if (match.Groups["escape"].Success)
            {
                output.Append(match.Value[1]);
            }
            previous = match.Index + match.Length;
        }
        output.Append(text, previous, text.Length - previous);
        return output.ToString();
    }
}
