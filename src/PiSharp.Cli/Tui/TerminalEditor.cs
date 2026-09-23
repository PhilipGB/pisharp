namespace PiSharp.Cli.Tui;

/// <summary>Normal-screen terminal editor. Leaves transcript in the terminal scrollback.</summary>
public sealed class TerminalEditor
{
    private readonly EditorBuffer _buffer = new();
    private TerminalInput? _input;
    private readonly EditorCompletion _completion;
    public TerminalEditor(Func<IReadOnlyList<string>>? commands = null) => _completion = new(Environment.CurrentDirectory, commands);
    private const string Prompt = "❯ ";

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

    private static void ClearLine() => Console.Write("\r\u001b[2K");

    private void Render()
    {
        // A single-line viewport cannot wrap or erase the already streamed transcript.
        var text = _buffer.Text.Replace("\n", "↵", StringComparison.Ordinal).Replace("\t", "⇥", StringComparison.Ordinal);
        var cursor = _buffer.Text[.._buffer.Cursor].Replace("\n", "↵", StringComparison.Ordinal).Replace("\t", "⇥", StringComparison.Ordinal).Length;
        var width = Math.Max(8, (Console.WindowWidth > 0 ? Console.WindowWidth : 80) - 5);
        var start = Math.Max(0, cursor - width + 1);
        var visible = text.Length > start ? text[start..Math.Min(text.Length, start + width)] : "";
        visible = new string(visible.Select(c => char.IsControl(c) ? ' ' : c).ToArray());
        ClearLine();
        Console.Write($"{Prompt}{visible}");
        Console.Write($"\r\u001b[{Math.Min(cursor - start + 3, width + 3)}G");
    }
}
