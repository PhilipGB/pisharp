using System.Text;
using PiSharp.Cli;

namespace PiSharp.Core.Tests;

/// <summary>
/// Scripted <see cref="IConsoleIO"/> for deterministic prompt tests: a queue of keys (as the
/// terminal would buffer them, including raw escape-sequence bytes), a queue of lines for the
/// redirected path, and a full record of everything written.
/// </summary>
internal sealed class FakeConsoleIO : IConsoleIO
{
    private readonly Queue<ConsoleKeyInfo> _keys = new();
    private readonly Queue<string?> _lines = new();
    private readonly StringBuilder _output = new();

    public bool IsInteractive { get; init; } = true;
    public bool KeyAvailable => _keys.Count > 0;
    public void Write(string text) => _output.Append(text);
    public void WriteLine(string text) => _output.Append(text).Append('\n');
    public ConsoleKeyInfo ReadKey() => _keys.Count > 0
        ? _keys.Dequeue()
        : throw new InvalidOperationException("ReadKey called with no queued key.");
    public string? ReadLineSync() => _lines.Count > 0 ? _lines.Dequeue() : null;

    /// <summary>Everything written to the console, in order.</summary>
    public string Output => _output.ToString();

    public void EnqueueText(string text)
    {
        foreach (var value in text)
        {
            _keys.Enqueue(CharacterKey(value));
        }
    }

    public void EnqueueKey(ConsoleKeyInfo key) => _keys.Enqueue(key);
    public void EnqueueLine(string? line) => _lines.Enqueue(line);

    /// <summary>Queues a bracketed-paste start marker as the raw key stream (.NET emits unmapped CSI as individual keys).</summary>
    public void EnqueuePasteStart() => EnqueueCsi("200~");

    /// <summary>Queues a bracketed-paste end marker.</summary>
    public void EnqueuePasteEnd() => EnqueueCsi("201~");

    private void EnqueueCsi(string body)
    {
        _keys.Enqueue(EscapeKey);
        foreach (var value in "[" + body)
        {
            _keys.Enqueue(CharacterKey(value));
        }
    }

    public static ConsoleKeyInfo EscapeKey => new('\u001b', ConsoleKey.Escape, false, false, false);

    public static ConsoleKeyInfo CharacterKey(char value) => value switch
    {
        '\r' or '\n' => new('\r', ConsoleKey.Enter, false, false, false),
        '\b' => new('\b', ConsoleKey.Backspace, true, false, false),
        '\t' => new('\t', ConsoleKey.Tab, false, false, false),
        '\u001b' => new('\u001b', ConsoleKey.Escape, false, false, false),
        _ when char.IsAsciiLetterLower(value) => new(value, ConsoleKey.A + value - 'a', false, false, false),
        _ when char.IsAsciiLetterUpper(value) => new(value, ConsoleKey.A + value - 'A', true, false, false),
        _ when char.IsAsciiDigit(value) => new(value, ConsoleKey.D0 + value - '0', false, false, false),
        _ => new(value, ConsoleKey.None, false, false, false),
    };
}
