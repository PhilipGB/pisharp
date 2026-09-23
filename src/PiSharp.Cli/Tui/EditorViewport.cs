namespace PiSharp.Cli.Tui;

/// <summary>Pure line layout for the normal-screen terminal editor. The terminal transcript remains above the viewport.</summary>
public static class EditorViewport
{
    public sealed record Frame(IReadOnlyList<string> Rows, int CursorRow, int CursorColumn);

    public static Frame Layout(string text, int cursor, int columns, int height)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(cursor);
        if (cursor > text.Length) throw new ArgumentOutOfRangeException(nameof(cursor));
        var capacity = Math.Max(1, columns - 3);
        var rows = new List<string>();
        var line = new System.Text.StringBuilder();
        var cursorRow = 0;
        var cursorColumn = 3;
        for (var index = 0; index <= text.Length; index++)
        {
            if (index == cursor) { cursorRow = rows.Count; cursorColumn = line.Length + 3; }
            if (index == text.Length) break;
            var character = text[index];
            if (character == '\n')
            {
                rows.Add(line.ToString());
                line.Clear();
                continue;
            }
            if (line.Length >= capacity)
            {
                rows.Add(line.ToString());
                line.Clear();
                if (index == cursor) { cursorRow = rows.Count; cursorColumn = 3; }
            }
            line.Append(character == '\t' ? ' ' : char.IsControl(character) ? ' ' : character);
        }
        rows.Add(line.ToString());
        var visibleHeight = Math.Max(1, height);
        var first = Math.Clamp(cursorRow - visibleHeight + 1, 0, Math.Max(0, rows.Count - visibleHeight));
        return new Frame(rows.Skip(first).Take(visibleHeight).Select((row, offset) =>
            (first + offset == 0 ? "❯ " : "│ ") + row).ToArray(), cursorRow - first, cursorColumn);
    }
}
