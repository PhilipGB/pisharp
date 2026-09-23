namespace PiSharp.Cli.Tui;

/// <summary>Normal-screen terminal editor. Leaves transcript in the terminal scrollback.</summary>
public sealed class TerminalEditor
{
    private readonly EditorBuffer _buffer = new();
    private const string Prompt = "❯ ";

    public string? ReadLine()
    {
        var previous = Console.TreatControlCAsInput;
        try
        {
            Console.TreatControlCAsInput = true;
            Render();
            while (true)
            {
                var action = _buffer.Handle(Console.ReadKey(intercept: true));
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
