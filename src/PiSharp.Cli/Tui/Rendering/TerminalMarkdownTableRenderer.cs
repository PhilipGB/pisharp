using System.Globalization;
using Markdig.Extensions.Tables;
using Markdig.Syntax;

namespace PiSharp.Cli.Tui;

/// <summary>Applies terminal-cell sizing and border layout to parsed pipe-table nodes.</summary>
internal static class TerminalMarkdownTableRenderer
{
    public static string Render(Table table, string source, int availableWidth, TerminalTheme? theme = null)
    {
        theme ??= TerminalTheme.Default;
        var rows = table.OfType<TableRow>().ToList();
        if (rows.Count == 0) return "";
        var headerIndex = rows.FindIndex(row => row.IsHeader);
        if (headerIndex < 0) headerIndex = 0;
        var sourceHeader = RowCells(rows[headerIndex]);
        var columnCount = table.ColumnDefinitions?.Count ?? sourceHeader.Length;
        if (columnCount is < 1 or > 16 || rows.Count > 1_001) return TerminalMarkdownSource.Slice(table, source);

        var header = Normalize(sourceHeader, columnCount);
        var dataRows = rows.Where((_, index) => index != headerIndex)
            .Select(row => Normalize(RowCells(row), columnCount)).ToArray();
        var alignments = Enumerable.Range(0, columnCount).Select(index => table.ColumnDefinitions is null
            ? TableAlignment.Left
            : table.ColumnDefinitions[index].Alignment switch
            {
                TableColumnAlign.Center => TableAlignment.Center,
                TableColumnAlign.Right => TableAlignment.Right,
                _ => TableAlignment.Left
            }).ToArray();
        var allRows = new[] { header }.Concat(dataRows).ToArray();
        var naturalWidths = Enumerable.Range(0, columnCount)
            .Select(column => Math.Max(1, allRows.Max(row => TerminalTextLayout.Width(row[column])))).ToArray();
        var minimumWidths = Enumerable.Range(0, columnCount)
            .Select(column => Math.Max(1, allRows.Max(row => MaxTableElementWidth(row[column])))).ToArray();
        var cellArea = availableWidth - (3 * columnCount + 1);
        if (cellArea < minimumWidths.Sum()) return TerminalMarkdownSource.Slice(table, source);

        var widths = naturalWidths.ToArray();
        if (widths.Sum() > cellArea)
        {
            widths = minimumWidths.ToArray();
            var extraWidth = cellArea - widths.Sum();
            var totalGrowth = naturalWidths.Select((value, index) => Math.Max(0, value - widths[index])).Sum();
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
            theme.Style("mdCodeBlockBorder", $"┌─{string.Join("─┬─", widths.Select(value => new string('─', value)))}─┐")
        };
        output.AddRange(RenderRow(header, widths, alignments, isHeader: true, theme: theme));
        var separator = theme.Style("mdCodeBlockBorder", $"├─{string.Join("─┼─", widths.Select(value => new string('─', value)))}─┤");
        output.Add(separator);
        for (var index = 0; index < dataRows.Length; index++)
        {
            output.AddRange(RenderRow(dataRows[index], widths, alignments, isHeader: false, theme: theme));
            if (index < dataRows.Length - 1) output.Add(separator);
        }
        output.Add(theme.Style("mdCodeBlockBorder", $"└─{string.Join("─┴─", widths.Select(value => new string('─', value)))}─┘"));
        return string.Join('\n', output);
    }

    private static string[] RowCells(TableRow row) => row.OfType<TableCell>()
            .Select(cell => string.Join(' ', cell.OfType<ParagraphBlock>()
            .Select(paragraph => TerminalMarkdownInlineRenderer.Render(paragraph.Inline?.FirstChild, styled: false))).Trim()).ToArray();

    private static string[] Normalize(string[] cells, int columns)
    {
        var normalized = new string[columns];
        for (var index = 0; index < columns; index++) normalized[index] = index < cells.Length ? cells[index] : "";
        return normalized;
    }

    private static IReadOnlyList<string> RenderRow(string[] cells, int[] widths,
        TableAlignment[] alignments, bool isHeader, TerminalTheme theme)
    {
        var wrapped = cells.Select((cell, index) => TerminalTextLayout.Wrap(cell, widths[index])).ToArray();
        var height = wrapped.Max(lines => lines.Count);
        var output = new List<string>(height);
        for (var line = 0; line < height; line++)
        {
            var padded = new string[cells.Length];
            for (var column = 0; column < cells.Length; column++)
            {
                var text = line < wrapped[column].Count ? wrapped[column][line] : "";
                var padding = Math.Max(0, widths[column] - TerminalTextLayout.Width(text));
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
            output.Add(theme.Style("mdCodeBlockBorder", $"│ {string.Join(" │ ", padded)} │"));
        }
        return output;
    }

    private static int MaxTableElementWidth(string text)
    {
        var maximum = 0;
        var offset = 0;
        while (offset < text.Length)
        {
            var element = StringInfo.GetNextTextElement(text, offset);
            maximum = Math.Max(maximum, TerminalCells.Width(element));
            offset += element.Length;
        }
        return maximum;
    }

    private enum TableAlignment { Left, Center, Right }
}
