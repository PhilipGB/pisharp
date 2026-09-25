using System.Globalization;
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
    private static readonly Regex TableSeparator = new("^:?-{3,}:?$", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex Sgr = new("\\u001b\\[[0-9;]*m", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex Inline = new(
        "(?<image>!\\[(?<imageText>[^\\]]*)\\]\\((?<imageUrl>[^)\\s]+)(?:\\s+[\\\"'][^\\\"']*[\\\"'])?\\))" +
        "|(?<link>\\[(?<linkText>[^\\]]+)\\]\\((?<linkUrl>[^)\\s]+)(?:\\s+[\\\"'][^\\\"']*[\\\"'])?\\))" +
        "|(?<code>`+(?<codeText>[^`]+)`+)" +
        "|(?<strong>\\*\\*.+?\\*\\*|(?<![\\p{L}\\p{N}_])__.+?__(?![\\p{L}\\p{N}_]))" +
        "|(?<strike>~~.+?~~)" +
        "|(?<emphasis>(?<!\\*)\\*(?!\\s)(?:\\\\.|[^*])+?(?<!\\s)\\*(?!\\*)|(?<![\\p{L}\\p{N}_])_(?!\\s)(?:\\\\.|[^_])+?(?<!\\s)_(?![\\p{L}\\p{N}_]))" +
        "|(?<escape>\\\\[\\\\`*_{}\\[\\]()#+.!|>~-])",
        RegexOptions.Compiled | RegexOptions.CultureInvariant, InlineMatchTimeout);

    public static string Render(string markdown, int availableWidth = 80)
    {
        ArgumentNullException.ThrowIfNull(markdown);
        availableWidth = Math.Clamp(availableWidth, 1, 400);
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
                else if (TryReadTable(lines, index, out var header, out var alignments, out var rows, out var tableEnd))
                {
                    output.Append(RenderTable(header, alignments, rows, lines[index..(tableEnd + 1)], availableWidth));
                    index = tableEnd;
                    if (index < lines.Length - 1)
                    {
                        output.Append('\n');
                        if (!string.IsNullOrWhiteSpace(lines[index + 1])) output.Append('\n');
                    }
                    continue;
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

    private static bool TryReadTable(string[] lines, int start, out string[] header,
        out TableAlignment[] alignments, out List<string[]> rows, out int tableEnd)
    {
        header = [];
        alignments = [];
        rows = [];
        tableEnd = start;
        if (start + 1 >= lines.Length || !TrySplitTableRow(lines[start], out header) ||
            !TrySplitTableRow(lines[start + 1], out var separator) || header.Length == 0 ||
            header.Length > 16 || separator.Length != header.Length)
            return false;

        alignments = new TableAlignment[header.Length];
        for (var index = 0; index < separator.Length; index++)
        {
            var cell = separator[index].Trim();
            if (!TableSeparator.IsMatch(cell)) return false;
            alignments[index] = cell.StartsWith(':') && cell.EndsWith(':')
                ? TableAlignment.Center
                : cell.EndsWith(':') ? TableAlignment.Right : TableAlignment.Left;
        }

        tableEnd = start + 1;
        for (var index = start + 2; index < lines.Length && rows.Count < 1_000; index++)
        {
            if (!TrySplitTableRow(lines[index], out var row)) break;
            rows.Add(NormalizeTableRow(row, header.Length));
            tableEnd = index;
        }
        return true;
    }

    private static bool TrySplitTableRow(string line, out string[] cells)
    {
        cells = [];
        if (line.Length == 0) return false;
        var parsed = new List<string>();
        var cell = new StringBuilder();
        var codeDelimiterLength = 0;
        var hasSeparator = false;
        for (var index = 0; index < line.Length; index++)
        {
            var character = line[index];
            if (character == '\\' && index + 1 < line.Length)
            {
                cell.Append(character).Append(line[++index]);
                continue;
            }
            if (character == '`')
            {
                var runLength = 1;
                while (index + runLength < line.Length && line[index + runLength] == '`') runLength++;
                if (codeDelimiterLength == 0) codeDelimiterLength = runLength;
                else if (runLength == codeDelimiterLength) codeDelimiterLength = 0;
                cell.Append('`', runLength);
                index += runLength - 1;
                continue;
            }
            if (character == '|' && codeDelimiterLength == 0)
            {
                parsed.Add(cell.ToString().Trim());
                cell.Clear();
                hasSeparator = true;
                continue;
            }
            cell.Append(character);
        }
        if (!hasSeparator) return false;
        parsed.Add(cell.ToString().Trim());
        if (line.TrimStart().StartsWith('|') && parsed.Count > 0) parsed.RemoveAt(0);
        if (HasUnescapedTrailingPipe(line) && parsed.Count > 0 && parsed[^1].Length == 0)
            parsed.RemoveAt(parsed.Count - 1);
        cells = parsed.ToArray();
        return cells.Length > 0;
    }

    private static bool HasUnescapedTrailingPipe(string line)
    {
        var end = line.Length - 1;
        while (end >= 0 && char.IsWhiteSpace(line[end])) end--;
        if (end < 0 || line[end] != '|') return false;
        var backslashes = 0;
        for (var index = end - 1; index >= 0 && line[index] == '\\'; index--) backslashes++;
        return backslashes % 2 == 0;
    }

    private static string[] NormalizeTableRow(string[] cells, int columns)
    {
        var normalized = new string[columns];
        for (var index = 0; index < columns; index++)
            normalized[index] = index < cells.Length ? cells[index] : "";
        return normalized;
    }

    private static string RenderTable(string[] header, TableAlignment[] alignments, IReadOnlyList<string[]> rows,
        IReadOnlyList<string> sourceLines, int availableWidth)
    {
        var columnCount = header.Length;
        var cellArea = availableWidth - (3 * columnCount + 1);
        var renderedHeader = header.Select(RenderTableCell).ToArray();
        var renderedRows = rows.Select(row => row.Select(RenderTableCell).ToArray()).ToArray();
        var allRows = new[] { renderedHeader }.Concat(renderedRows).ToArray();
        var naturalWidths = Enumerable.Range(0, columnCount)
            .Select(column => Math.Max(1, allRows.Max(row => TableTextWidth(row[column])))).ToArray();
        var minimumWidths = Enumerable.Range(0, columnCount)
            .Select(column => Math.Max(1, allRows.Max(row => MaxTableElementWidth(row[column])))).ToArray();
        if (cellArea < minimumWidths.Sum()) return string.Join('\n', sourceLines);

        var widths = naturalWidths.ToArray();
        if (widths.Sum() > cellArea)
        {
            widths = minimumWidths.ToArray();
            var extraWidth = cellArea - widths.Sum();
            var totalGrowth = naturalWidths.Select((width, index) => Math.Max(0, width - widths[index])).Sum();
            if (totalGrowth > 0)
            {
                for (var index = 0; index < widths.Length; index++)
                    widths[index] += (int)((long)Math.Max(0, naturalWidths[index] - widths[index]) * extraWidth / totalGrowth);
            }
            var remaining = cellArea - widths.Sum();
            while (remaining > 0)
            {
                var grew = false;
                for (var index = 0; index < widths.Length && remaining > 0; index++)
                {
                    if (widths[index] >= naturalWidths[index]) continue;
                    widths[index]++;
                    remaining--;
                    grew = true;
                }
                if (!grew) break;
            }
        }

        var output = new List<string>
        {
            $"┌─{string.Join("─┬─", widths.Select(width => new string('─', width)))}─┐"
        };
        output.AddRange(RenderTableRow(renderedHeader, widths, alignments, isHeader: true));
        var separator = $"├─{string.Join("─┼─", widths.Select(width => new string('─', width)))}─┤";
        output.Add(separator);
        for (var index = 0; index < renderedRows.Length; index++)
        {
            output.AddRange(RenderTableRow(renderedRows[index], widths, alignments, isHeader: false));
            if (index < renderedRows.Length - 1) output.Add(separator);
        }
        output.Add($"└─{string.Join("─┴─", widths.Select(width => new string('─', width)))}─┘");
        return string.Join('\n', output);
    }

    private static string RenderTableCell(string markdown) => Sgr.Replace(RenderInline(markdown), "");

    private static int MaxTableElementWidth(string text)
    {
        var maximum = 0;
        var current = 0;
        var elements = StringInfo.GetTextElementEnumerator(text);
        while (elements.MoveNext())
        {
            var element = (string)elements.Current;
            if (element.Length > 0 && char.IsWhiteSpace(element, 0))
            {
                maximum = Math.Max(maximum, current);
                current = 0;
            }
            else
            {
                current += TerminalCells.Width(element);
            }
        }
        maximum = Math.Max(maximum, current);
        return maximum;
    }

    private static int TableTextWidth(string text)
    {
        var width = 0;
        var elements = StringInfo.GetTextElementEnumerator(text);
        while (elements.MoveNext()) width += TerminalCells.Width((string)elements.Current);
        return width;
    }

    private static IReadOnlyList<string> RenderTableRow(string[] cells, int[] widths,
        TableAlignment[] alignments, bool isHeader)
    {
        var wrapped = cells.Select((cell, index) => WrapTableCell(cell, widths[index])).ToArray();
        var height = wrapped.Max(lines => lines.Count);
        var output = new List<string>(height);
        for (var line = 0; line < height; line++)
        {
            var padded = new string[cells.Length];
            for (var column = 0; column < cells.Length; column++)
            {
                var text = line < wrapped[column].Count ? wrapped[column][line] : "";
                var padding = Math.Max(0, widths[column] - TableTextWidth(text));
                var leftPadding = alignments[column] switch
                {
                    TableAlignment.Right => padding,
                    TableAlignment.Center => padding / 2,
                    _ => 0
                };
                var rightPadding = padding - leftPadding;
                text = new string(' ', leftPadding) + text + new string(' ', rightPadding);
                padded[column] = isHeader ? $"\u001b[1m{text}\u001b[22m" : text;
            }
            output.Add($"│ {string.Join(" │ ", padded)} │");
        }
        return output;
    }

    private static IReadOnlyList<string> WrapTableCell(string text, int width)
    {
        var lines = new List<string>();
        var line = new StringBuilder();
        var used = 0;
        var elements = StringInfo.GetTextElementEnumerator(text);
        while (elements.MoveNext())
        {
            var element = (string)elements.Current;
            var elementWidth = TerminalCells.Width(element);
            if (used > 0 && used + elementWidth > width)
            {
                lines.Add(line.ToString());
                line.Clear();
                used = 0;
            }
            line.Append(element);
            used += elementWidth;
        }
        if (line.Length > 0 || lines.Count == 0) lines.Add(line.ToString());
        return lines;
    }

    private enum TableAlignment { Left, Center, Right }

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
