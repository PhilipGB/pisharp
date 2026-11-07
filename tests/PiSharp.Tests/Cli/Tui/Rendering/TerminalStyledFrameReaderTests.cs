using PiSharp.Cli.Tui;

namespace PiSharp.Tests;

public sealed class TerminalStyledFrameReaderTests
{
    [Fact]
    public void WideAndCombiningGraphemesUseTerminalCellWidths()
    {
        const string text = "A界e\u0301🙂B";

        var frame = Assert.Single(TerminalStyledFrameReader.Read(
            $"\u001b[?2026h\u001b[0m{text}\u001b[?2026l", rows: 2, columns: 12));
        var row = frame.Rows[0];

        Assert.Equal(text, row.Text.TrimEnd());
        AssertCell(row, 0, "A", 1);
        AssertCell(row, 1, "界", 2);
        AssertCell(row, 2, "", 0);
        AssertCell(row, 3, "e\u0301", 1);
        AssertCell(row, 4, "🙂", 2);
        AssertCell(row, 5, "", 0);
        AssertCell(row, 6, "B", 1);
    }

    [Fact]
    public void WideCellContinuationRetainsTheActiveStyle()
    {
        var frame = Assert.Single(TerminalStyledFrameReader.Read(
            "\u001b[?2026h\u001b[0m\u001b[1;38;2;1;2;3;48;2;4;5;6m界\u001b[?2026l",
            rows: 2, columns: 8));
        var row = frame.Rows[0];
        var expected = new TerminalStyledFrameReader.CellStyle(
            new TerminalStyledFrameReader.Rgb(1, 2, 3),
            new TerminalStyledFrameReader.Rgb(4, 5, 6),
            true, false, false, false, false, false);

        Assert.Equal("界", row.Text.TrimEnd());
        Assert.Equal(expected, row.Cells[0].Style);
        Assert.Equal(expected, row.Cells[1].Style);
        Assert.Equal(2, row.Cells[0].Width);
        Assert.Equal(0, row.Cells[1].Width);
    }

    private static void AssertCell(TerminalStyledFrameReader.Row row, int column, string text, int width)
    {
        Assert.Equal(text, row.Cells[column].Text);
        Assert.Equal(width, row.Cells[column].Width);
    }
}
