namespace PiSharp.Cli.Tui;

/// <summary>Normal-screen terminal editor. Leaves transcript in the terminal scrollback.</summary>
public sealed class TerminalEditor
{
    private readonly EditorBuffer _buffer = new();
    private TerminalInput? _input;
    private readonly EditorCompletion _completion;
    public TerminalEditor(Func<IReadOnlyList<string>>? commands = null) => _completion = new(Environment.CurrentDirectory, commands);
    private const string Prompt = "❯ ";
    private int _renderedRows;
    private int _cursorRow;

    public string? ReadLine()
    {
        var previous = Console.TreatControlCAsInput;
        try
        {
            Console.TreatControlCAsInput = true;
            using var mode = TerminalMode.Enter();
            _input ??= TerminalInput.OpenConsole();
            Render();
            while (true)
            {
                var next = _input.Read();
                if (next.Key is null && next.Text is null)
                {
                    ClearLine();
                    Console.WriteLine();
                    return null;
                }
                if (next.Key is { Key: ConsoleKey.Tab, Modifiers: ConsoleModifiers.None })
                {
                    try
                    {
                        var matches = _completion.Complete(_buffer);
                        if (matches.Count == 0) _buffer.Handle(next.Key.Value);
                        else if (matches.Count > 1)
                        {
                            ClearLine();
                            Console.WriteLine();
                            Console.WriteLine(string.Join("  ", matches.Take(10).Select(match =>
                                new string(match.Select(c => char.IsControl(c) ? ' ' : c).ToArray()))) +
                                (matches.Count > 10 ? "  …" : ""));
                        }
                    }
                    catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException)
                    {
                        ClearLine();
                        Console.WriteLine($"\nCompletion unavailable: {error.Message}");
                    }
                    Render();
                    continue;
                }
                var action = next.Text is not null ? _buffer.InsertText(next.Text) : _buffer.Handle(next.Key!.Value);
                switch (action)
                {
                    case EditorAction.Exit:
                        ClearLine();
                        Console.WriteLine();
                        return null;
                    case EditorAction.Cancel:
                        Render();
                        break;
                    case EditorAction.Submit:
                        var submitted = _buffer.Text;
                        ClearLine();
                        Console.WriteLine($"{Prompt}{submitted.Replace('\n', '↵')}");
                        _buffer.Clear();
                        return submitted;
                    case EditorAction.Render: Render(); break;
                }
            }
        }
        finally { Console.TreatControlCAsInput = previous; }
    }

    private void ClearLine()
    {
        if (_renderedRows == 0) { Console.Write("\r\u001b[2K"); return; }
        if (_cursorRow > 0) Console.Write($"\u001b[{_cursorRow}A");
        for (var row = 0; row < _renderedRows; row++)
        {
            Console.Write("\r\u001b[2K");
            if (row < _renderedRows - 1) Console.Write("\u001b[1B");
        }
        if (_renderedRows > 1) Console.Write($"\u001b[{_renderedRows - 1}A");
        Console.Write("\r");
        _renderedRows = 0;
        _cursorRow = 0;
    }

    private void Render()
    {
        var width = Console.WindowWidth > 0 ? Console.WindowWidth : 80;
        var height = Console.WindowHeight > 0 ? Console.WindowHeight : 24;
        var frame = EditorViewport.Layout(_buffer.Text, _buffer.Cursor, width, Math.Max(1, height / 3));
        ClearLine();
        for (var row = 0; row < frame.Rows.Count; row++)
        {
            if (row > 0) Console.Write("\n");
            Console.Write(frame.Rows[row]);
        }
        _renderedRows = frame.Rows.Count;
        _cursorRow = frame.CursorRow;
        var up = frame.Rows.Count - 1 - frame.CursorRow;
        if (up > 0) Console.Write($"\u001b[{up}A");
        Console.Write($"\r\u001b[{frame.CursorColumn}G");
    }
}
