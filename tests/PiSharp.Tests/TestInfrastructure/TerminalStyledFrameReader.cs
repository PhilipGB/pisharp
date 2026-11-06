using System.Globalization;
using PiSharp.Cli.Tui;

namespace PiSharp.Tests;

/// <summary>
/// Decodes synchronized frames into cells that retain their SGR foreground,
/// background and attributes. Terminal parity assertions use the decoded cells
/// rather than only the visible text.
/// </summary>
internal static class TerminalStyledFrameReader
{
    private const string Begin = "\u001b[?2026h";
    private const string End = "\u001b[?2026l";

    internal readonly record struct Rgb(byte R, byte G, byte B);

    internal readonly record struct CellStyle(
        Rgb? Foreground,
        Rgb? Background,
        bool Bold,
        bool Dim,
        bool Italic,
        bool Underline,
        bool Inverse,
        bool Strikethrough)
    {
        public static CellStyle Default => new(null, null, false, false, false, false, false, false);
    }

    internal sealed record Cell(char Char, CellStyle Style);

    internal sealed record Row(IReadOnlyList<Cell> Cells)
    {
        public string Text => new(Cells.Select(cell => cell.Char).ToArray());
    }

    internal sealed record Frame(IReadOnlyList<Row> Rows, int CursorRow, int CursorColumn);

    public static IReadOnlyList<Frame> Read(string output, int rows, int columns)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(rows, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(columns, 1);
        var screen = new Cell[rows, columns];
        for (var row = 0; row < rows; row++)
            for (var column = 0; column < columns; column++)
                screen[row, column] = new Cell(' ', CellStyle.Default);

        var style = new StyleState();
        var cursorRow = 0;
        var cursorColumn = 0;
        var savedRow = 0;
        var savedColumn = 0;
        var frames = new List<Frame>();
        for (var offset = 0; offset < output.Length;)
        {
            if (output.AsSpan(offset).StartsWith(Begin, StringComparison.Ordinal))
            {
                offset += Begin.Length;
                continue;
            }
            if (output.AsSpan(offset).StartsWith(End, StringComparison.Ordinal))
            {
                frames.Add(Capture());
                offset += End.Length;
                continue;
            }
            if (output[offset] == '\u001b')
            {
                if (offset + 1 >= output.Length) break;
                if (output[offset + 1] == '[')
                {
                    var final = offset + 2;
                    while (final < output.Length && output[final] is >= '\u0020' and <= '\u003f') final++;
                    if (final >= output.Length) break;
                    ApplyCsi(output.AsSpan(offset + 2, final - offset - 2), output[final], ref cursorRow,
                        ref cursorColumn, ref savedRow, ref savedColumn, screen, style);
                    offset = final + 1;
                    continue;
                }
                if (output[offset + 1] == ']')
                {
                    var bell = output.IndexOf('\a', offset + 2);
                    var terminator = output.IndexOf("\u001b\\", offset + 2, StringComparison.Ordinal);
                    var end = bell < 0 ? terminator : terminator < 0 ? bell : Math.Min(bell, terminator);
                    if (end < 0) break;
                    offset = end + (end == bell ? 1 : 2);
                    continue;
                }
                offset += 2;
                continue;
            }
            switch (output[offset])
            {
                case '\r':
                    cursorColumn = 0;
                    offset++;
                    continue;
                case '\n':
                    cursorRow = Math.Min(rows - 1, cursorRow + 1);
                    offset++;
                    continue;
            }
            if (output[offset] >= ' ' && cursorRow < rows && cursorColumn < columns)
            {
                screen[cursorRow, cursorColumn] = new Cell(output[offset], style.Snapshot());
                cursorColumn++;
            }
            offset++;
        }
        return frames;

        Frame Capture()
        {
            var captured = new Row[rows];
            for (var row = 0; row < rows; row++)
            {
                var cells = new Cell[columns];
                for (var column = 0; column < columns; column++) cells[column] = screen[row, column];
                captured[row] = new(cells);
            }
            return new(captured, cursorRow, cursorColumn);
        }
    }

    private static void ApplyCsi(ReadOnlySpan<char> body, char command, ref int cursorRow, ref int cursorColumn,
        ref int savedRow, ref int savedColumn, Cell[,] screen, StyleState style)
    {
        if (body.Length > 0 && body[0] is '?' or '>' or '<') body = body[1..];
        var parts = body.ToString().Split(';');
        int Parameter(int index, int fallback = 1) => index < parts.Length &&
            int.TryParse(parts[index], NumberStyles.None, CultureInfo.InvariantCulture, out var value) && value > 0
                ? value
                : fallback;
        switch (command)
        {
            case 'H':
            case 'f':
                cursorRow = Math.Clamp(Parameter(0) - 1, 0, screen.GetLength(0) - 1);
                cursorColumn = Math.Clamp(Parameter(1) - 1, 0, screen.GetLength(1) - 1);
                break;
            case 'G':
                cursorColumn = Math.Clamp(Parameter(0) - 1, 0, screen.GetLength(1) - 1);
                break;
            case 'd':
                cursorRow = Math.Clamp(Parameter(0) - 1, 0, screen.GetLength(0) - 1);
                break;
            case 'A':
                cursorRow = Math.Max(0, cursorRow - Parameter(0));
                break;
            case 'B':
                cursorRow = Math.Min(screen.GetLength(0) - 1, cursorRow + Parameter(0));
                break;
            case 'C':
                cursorColumn = Math.Min(screen.GetLength(1) - 1, cursorColumn + Parameter(0));
                break;
            case 'D':
                cursorColumn = Math.Max(0, cursorColumn - Parameter(0));
                break;
            case 's':
                savedRow = cursorRow;
                savedColumn = cursorColumn;
                break;
            case 'u':
                cursorRow = savedRow;
                cursorColumn = savedColumn;
                break;
            case 'K':
                FillRow(cursorRow, Parameter(0, 0) switch
                {
                    1 => 0,
                    2 => 0,
                    _ => cursorColumn
                }, Parameter(0, 0) switch
                {
                    1 => cursorColumn + 1,
                    _ => screen.GetLength(1)
                });
                break;
            case 'J':
                var mode = Parameter(0, 0);
                if (mode is 2 or 3)
                {
                    for (var row = 0; row < screen.GetLength(0); row++)
                        FillRow(row, 0, screen.GetLength(1));
                }
                else if (mode == 1)
                {
                    for (var row = 0; row <= cursorRow; row++)
                        FillRow(row, 0, row == cursorRow ? cursorColumn + 1 : screen.GetLength(1));
                }
                else
                {
                    for (var row = cursorRow; row < screen.GetLength(0); row++)
                        FillRow(row, row == cursorRow ? cursorColumn : 0, screen.GetLength(1));
                }
                break;
            case 'm':
                style.Apply(body.ToString());
                break;
        }

        void FillRow(int row, int start, int end)
        {
            start = Math.Clamp(start, 0, screen.GetLength(1));
            end = Math.Clamp(end, start, screen.GetLength(1));
            for (var column = start; column < end; column++)
                screen[row, column] = new Cell(' ', style.Snapshot());
        }
    }

    private sealed class StyleState
    {
        private Rgb? _foreground;
        private Rgb? _background;
        private bool _bold;
        private bool _dim;
        private bool _italic;
        private bool _underline;
        private bool _inverse;
        private bool _strikethrough;

        public CellStyle Snapshot() => new(_foreground, _background, _bold, _dim, _italic, _underline, _inverse,
            _strikethrough);

        public void Apply(string payload)
        {
            var parts = payload.Split(';');
            for (var index = 0; index < parts.Length;)
            {
                var part = parts[index];
                if (!int.TryParse(part.Length == 0 ? "0" : part, NumberStyles.None, CultureInfo.InvariantCulture,
                        out var code))
                {
                    index++;
                    continue;
                }
                switch (code)
                {
                    case 0: Reset(); index++; continue;
                    case 1: _bold = true; index++; continue;
                    case 2: _dim = true; index++; continue;
                    case 3: _italic = true; index++; continue;
                    case 4: _underline = true; index++; continue;
                    case 7: _inverse = true; index++; continue;
                    case 9: _strikethrough = true; index++; continue;
                    case 22: _bold = false; _dim = false; index++; continue;
                    case 23: _italic = false; index++; continue;
                    case 24: _underline = false; index++; continue;
                    case 27: _inverse = false; index++; continue;
                    case 29: _strikethrough = false; index++; continue;
                    case 39: _foreground = null; index++; continue;
                    case 49: _background = null; index++; continue;
                    case 38:
                    case 48:
                        if (index + 1 < parts.Length && parts[index + 1] == "2" && index + 4 < parts.Length)
                        {
                            var color = new Rgb(Byte(parts[index + 2]), Byte(parts[index + 3]), Byte(parts[index + 4]));
                            if (code == 38) _foreground = color;
                            else _background = color;
                            index += 5;
                            continue;
                        }
                        if (index + 1 < parts.Length && parts[index + 1] == "5" && index + 2 < parts.Length &&
                            int.TryParse(parts[index + 2], NumberStyles.None, CultureInfo.InvariantCulture,
                                out var palette))
                        {
                            var color = Palette(palette);
                            if (code == 38) _foreground = color;
                            else _background = color;
                            index += 3;
                            continue;
                        }
                        index++;
                        continue;
                }

                if (code is >= 30 and <= 37) _foreground = Palette(code - 30);
                else if (code is >= 90 and <= 97) _foreground = Palette(code - 90 + 8);
                else if (code is >= 40 and <= 47) _background = Palette(code - 40);
                else if (code is >= 100 and <= 107) _background = Palette(code - 100 + 8);
                index++;
            }
        }

        private void Reset()
        {
            _foreground = null;
            _background = null;
            _bold = false;
            _dim = false;
            _italic = false;
            _underline = false;
            _inverse = false;
            _strikethrough = false;
        }

        private static byte Byte(string value) =>
            byte.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var result) ? result : (byte)0;

        private static Rgb Palette(int index)
        {
            if (index is >= 0 and < 16) return Standard[index];
            if (index is >= 16 and < 232)
            {
                var value = index - 16;
                return new Rgb(Level(value / 36), Level(value / 6 % 6), Level(value % 6));
            }
            var gray = (byte)(8 + (index - 232) * 10);
            return new Rgb(gray, gray, gray);
        }

        private static byte Level(int value) => value == 0 ? (byte)0 : (byte)(55 + value * 40);

        private static readonly Rgb[] Standard =
        [
            new(0, 0, 0), new(205, 0, 0), new(0, 205, 0), new(205, 205, 0),
            new(0, 0, 238), new(205, 0, 205), new(0, 205, 205), new(229, 229, 229),
            new(127, 127, 127), new(255, 0, 0), new(0, 255, 0), new(255, 255, 0),
            new(92, 92, 255), new(255, 0, 255), new(0, 255, 255), new(255, 255, 255)
        ];
    }
}
