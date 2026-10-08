namespace PiSharp.Tests;

internal static class TerminalOutputFrameReader
{
    internal readonly record struct Frame(int Start, string Screen, string Output);

    public static IReadOnlyList<Frame> Read(string output, int rows, int columns)
    {
        const string begin = "\u001b[?2026h";
        const string end = "\u001b[?2026l";
        var screen = Enumerable.Range(0, rows).Select(_ => Enumerable.Repeat(' ', columns).ToArray()).ToArray();
        var cursorRow = 0;
        var cursorColumn = 0;
        var frameStart = -1;
        var frames = new List<Frame>();
        for (var index = 0; index < output.Length;)
        {
            if (output.AsSpan(index).StartsWith(begin, StringComparison.Ordinal))
            {
                frameStart = index;
                index += begin.Length;
                continue;
            }
            if (output.AsSpan(index).StartsWith(end, StringComparison.Ordinal))
            {
                if (frameStart >= 0)
                    frames.Add(new(frameStart,
                        string.Join(Environment.NewLine, screen.Select(line => new string(line))),
                        output[(frameStart + begin.Length)..index]));
                frameStart = -1;
                index += end.Length;
                continue;
            }
            if (output[index] == '\u001b')
            {
                if (index + 1 >= output.Length) return frames;
                if (output[index + 1] == '[')
                {
                    var final = index + 2;
                    while (final < output.Length && !(output[final] >= '@' && output[final] <= '~')) final++;
                    if (final >= output.Length) return frames;
                    if (frameStart >= 0)
                        ApplyCsi(output.AsSpan(index + 2, final - index - 2), output[final], screen,
                            ref cursorRow, ref cursorColumn);
                    index = final + 1;
                    continue;
                }
                if (output[index + 1] == ']')
                {
                    var bell = output.IndexOf('\a', index + 2);
                    var stringTerminator = output.IndexOf("\u001b\\", index + 2, StringComparison.Ordinal);
                    var terminator = bell < 0 ? stringTerminator : stringTerminator < 0 ? bell : Math.Min(bell, stringTerminator);
                    if (terminator < 0) return frames;
                    index = terminator + (terminator == bell ? 1 : 2);
                    continue;
                }
                index += 2;
                continue;
            }
            if (frameStart >= 0)
            {
                var value = output[index];
                if (value == '\r') cursorColumn = 0;
                else if (value == '\n') cursorRow = Math.Min(rows - 1, cursorRow + 1);
                else if (value >= ' ' && cursorRow < rows && cursorColumn < columns)
                    screen[cursorRow][cursorColumn++] = value;
            }
            index++;
        }
        return frames;
    }

    private static void ApplyCsi(ReadOnlySpan<char> body, char command, char[][] screen,
        ref int cursorRow, ref int cursorColumn)
    {
        if (body.Length > 0 && body[0] is '?' or '>' or '<') body = body[1..];
        var values = body.ToString().Split(';');
        int Parameter(int index, int fallback = 1) => index < values.Length &&
            int.TryParse(values[index], out var value) && value > 0 ? value : fallback;
        switch (command)
        {
            case 'H':
            case 'f':
                cursorRow = Math.Clamp(Parameter(0) - 1, 0, screen.Length - 1);
                cursorColumn = Math.Clamp(Parameter(1) - 1, 0, screen[0].Length - 1);
                break;
            case 'G':
                cursorColumn = Math.Clamp(Parameter(0) - 1, 0, screen[0].Length - 1);
                break;
            case 'd':
                cursorRow = Math.Clamp(Parameter(0) - 1, 0, screen.Length - 1);
                break;
            case 'J':
                var displayMode = Parameter(0, 0);
                if (displayMode is 2 or 3)
                {
                    foreach (var row in screen) Array.Fill(row, ' ');
                    break;
                }
                var firstRow = displayMode == 1 ? 0 : cursorRow;
                var lastRow = displayMode == 0 ? screen.Length - 1 : cursorRow;
                for (var row = firstRow; row <= lastRow; row++)
                {
                    var firstCell = displayMode == 1 && row < cursorRow ? 0 : row == cursorRow ? cursorColumn : 0;
                    var lastCell = displayMode == 0 && row == cursorRow ? screen[row].Length :
                        displayMode == 1 && row == cursorRow ? cursorColumn + 1 : screen[row].Length;
                    var boundedFirst = Math.Clamp(firstCell, 0, screen[row].Length);
                    Array.Fill(screen[row], ' ', boundedFirst,
                        Math.Clamp(lastCell - boundedFirst, 0, screen[row].Length - boundedFirst));
                }
                break;
            case 'K':
                var lineMode = Parameter(0, 0);
                var clearFrom = lineMode == 1 ? 0 : cursorColumn;
                var clearTo = lineMode == 0 ? screen[cursorRow].Length :
                    lineMode == 1 ? cursorColumn + 1 : screen[cursorRow].Length;
                clearFrom = Math.Clamp(clearFrom, 0, screen[cursorRow].Length);
                Array.Fill(screen[cursorRow], ' ', clearFrom,
                    Math.Clamp(clearTo - clearFrom, 0, screen[cursorRow].Length - clearFrom));
                break;
            case 'A': cursorRow = Math.Max(0, cursorRow - Parameter(0)); break;
            case 'B': cursorRow = Math.Min(screen.Length - 1, cursorRow + Parameter(0)); break;
            case 'C': cursorColumn = Math.Min(screen[0].Length - 1, cursorColumn + Parameter(0)); break;
            case 'D': cursorColumn = Math.Max(0, cursorColumn - Parameter(0)); break;
        }
    }
}
