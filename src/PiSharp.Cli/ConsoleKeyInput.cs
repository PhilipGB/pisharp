using System.Text;

namespace PiSharp.Cli;

/// <summary>One logical key event after escape-sequence reassembly (see <see cref="KeySequenceParser"/>).</summary>
internal enum LogicalKeyKind
{
    Character,
    Enter,
    Backspace,
    Escape,
    HistoryPrevious,
    HistoryNext,
    Paste,
    Ignore,
}

/// <summary>A logical key; <see cref="Character"/>/Paste carry payload.</summary>
internal readonly record struct LogicalKey(LogicalKeyKind Kind, char Character = default, string? PasteText = null)
{
    public static LogicalKey KeyChar(char value) => new(LogicalKeyKind.Character, value);
    public static LogicalKey Enter { get; } = new(LogicalKeyKind.Enter);
    public static LogicalKey Backspace { get; } = new(LogicalKeyKind.Backspace);
    public static LogicalKey Escape { get; } = new(LogicalKeyKind.Escape);
    public static LogicalKey HistoryPrevious { get; } = new(LogicalKeyKind.HistoryPrevious);
    public static LogicalKey HistoryNext { get; } = new(LogicalKeyKind.HistoryNext);
    public static LogicalKey Ignore { get; } = new(LogicalKeyKind.Ignore);
    public static LogicalKey Paste(string text) => new(LogicalKeyKind.Paste, default, text);
}

/// <summary>
/// Key-driven line input over the <see cref="IConsoleIO"/> seam (item 4). Every single-line
/// prompt (login/secret, /llama, model and session selectors, the REPL) reads through this
/// so that:
/// <list type="bullet">
/// <item>secret input is masked (entered characters are echoed as '*'; the secret never is),</item>
/// <item>a lone Esc cancels (or, for the REPL, replaces the draft — see <see cref="Options"/>),</item>
/// <item>the <see cref="CancellationToken"/> is observed between keys, so an external cancel
/// (Ctrl+C abort/shutdown, timeouts) is acted on as soon as the loop polls,</item>
/// <item>no thread is orphaned: the read happens on the calling thread and only blocks while
/// a key is in flight.</item>
/// </list>
/// Redirected stdin falls back to a single synchronous line read (masking and Esc are not
/// possible on a pipe).
/// </summary>
internal static class ConsoleKeyInput
{
    /// <summary>Idle poll interval while waiting for a key; keeps a cancel within ~20 ms.</summary>
    private const int KeyPollIntervalMs = 20;

    /// <summary>Mask character echoed for each entered secret character.</summary>
    internal const char MaskCharacter = '*';

    /// <summary>Per-prompt behavior of the key loop.</summary>
    internal sealed class Options
    {
        /// <summary>Echo entered characters as <see cref="MaskCharacter"/> instead of the character.</summary>
        public bool Mask;

        /// <summary>Accept newlines inside bracketed pastes (REPL); single-line prompts take the first line only.</summary>
        public bool AcceptMultilinePaste;

        /// <summary>
        /// Lone-Esc handler: receives the current draft and returns the replacement draft
        /// (null keeps the draft). When null, a lone Esc cancels the prompt
        /// (<see cref="OperationCanceledException"/>).
        /// </summary>
        public Func<string, string?>? OnLoneEscape;

        /// <summary>Up-arrow handler: receives the draft, returns the replacement (null keeps).</summary>
        public Func<string, string?>? HistoryPrevious;

        /// <summary>Down-arrow handler: returns the replacement draft (null keeps).</summary>
        public Func<string?>? HistoryNext;
    }

    /// <summary>
    /// Reads one line at <paramref name="prompt"/>. Returns the trimmed line on Enter.
    /// Throws <see cref="OperationCanceledException"/> on Esc (default), EOF, or token
    /// cancellation.
    /// </summary>
    public static string ReadLine(
        IConsoleIO console,
        string prompt,
        CancellationToken cancellationToken,
        Options? options = null)
    {
        options ??= new Options();
        if (!console.IsInteractive)
        {
            return ReadRedirectedLine(console, prompt, cancellationToken);
        }

        console.Write(prompt);
        var buffer = new StringBuilder();
        var parser = new KeySequenceParser(console);

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            while (!console.KeyAvailable)
            {
                cancellationToken.ThrowIfCancellationRequested();
                Thread.Sleep(KeyPollIntervalMs);
            }

            var key = parser.NextKey();
            switch (key.Kind)
            {
                case LogicalKeyKind.Character:
                    AppendCharacter(buffer, key.Character, options, console);
                    break;

                case LogicalKeyKind.Paste:
                    AppendPaste(buffer, key.PasteText!, options, console);
                    break;

                case LogicalKeyKind.Enter:
                    return buffer.ToString().Trim();

                case LogicalKeyKind.Backspace:
                    if (buffer.Length > 0 && buffer[^1] != '\n')
                    {
                        buffer.Remove(buffer.Length - 1, 1);
                        console.Write("\b \b");
                    }
                    break;

                case LogicalKeyKind.Escape:
                    if (options.OnLoneEscape is { } onEscape)
                    {
                        if (onEscape(buffer.ToString()) is { } replacement)
                        {
                            buffer.Clear();
                            buffer.Append(replacement);
                            Repaint(buffer, prompt, options, console);
                        }

                        break;
                    }

                    throw new OperationCanceledException("Prompt cancelled.");

                case LogicalKeyKind.HistoryPrevious:
                    ReplaceDraft(buffer, prompt, options, console, options.HistoryPrevious?.Invoke(buffer.ToString()));
                    break;

                case LogicalKeyKind.HistoryNext:
                    ReplaceDraft(buffer, prompt, options, console, options.HistoryNext?.Invoke());
                    break;

                default:
                    // Unknown keys (left/right arrows, F-keys, Home/End, ...) are consumed and ignored.
                    break;
            }
        }
    }

    private static void AppendCharacter(StringBuilder buffer, char value, Options options, IConsoleIO console)
    {
        buffer.Append(value);
        Emit(value, options.Mask, console);
    }

    private static void AppendPaste(StringBuilder buffer, string text, Options options, IConsoleIO console)
    {
        var content = options.AcceptMultilinePaste
            ? text
            : FirstLineOf(text);
        foreach (var value in content)
        {
            AppendCharacter(buffer, value, options, console);
        }
    }

    private static string FirstLineOf(string text)
    {
        var newline = text.IndexOf('\n');
        return newline < 0 ? text : text[..newline];
    }

    private static void ReplaceDraft(
        StringBuilder buffer,
        string prompt,
        Options options,
        IConsoleIO console,
        string? replacement)
    {
        if (replacement is null)
        {
            return;
        }

        buffer.Clear();
        buffer.Append(replacement);
        Repaint(buffer, prompt, options, console);
    }

    /// <summary>Clears the input line and re-emits prompt plus draft (with masking).</summary>
    private static void Repaint(StringBuilder buffer, string prompt, Options options, IConsoleIO console)
    {
        console.Write("\r\x1b[2K");
        console.Write(prompt);
        foreach (var value in buffer.ToString())
        {
            Emit(value, options.Mask, console);
        }
    }

    private static void Emit(char value, bool mask, IConsoleIO console)
    {
        if (mask)
        {
            if (value != '\n' && value != '\t')
            {
                console.Write(MaskCharacter.ToString());
            }
        }
        else
        {
            console.Write(value.ToString());
        }
    }

    private static string ReadRedirectedLine(IConsoleIO console, string prompt, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        console.Write(prompt);
        var line = console.ReadLineSync();
        return line is null
            ? throw new OperationCanceledException("Prompt closed.")
            : line.Trim();
    }
}

/// <summary>
/// Reassembles the raw <see cref="ConsoleKeyInfo"/> stream into logical keys. .NET's
/// KeyParser pre-parses terminfo-known sequences (arrows, Home/End, F-keys) into single
/// key infos, but unmapped CSI sequences — notably bracketed-paste markers ESC[200~/ESC[201~
/// — are emitted as the individual keys Escape, '[', digits, '~'. This parser restores
/// bracketed-paste boundaries from that stream and reports a lone Esc (no bytes queued
/// behind it) distinctly from an ESC that begins a sequence.
/// </summary>
internal sealed class KeySequenceParser
{
    private enum State
    {
        Normal,
        AfterEscape,
    }

    private readonly IConsoleIO _console;
    private readonly StringBuilder _csi = new();
    private State _state = State.Normal;
    private ConsoleKeyInfo? _pending;

    public KeySequenceParser(IConsoleIO console) => _console = console;

    /// <summary>Reads the next logical key. Call only when <see cref="IConsoleIO.KeyAvailable"/>.</summary>
    public LogicalKey NextKey()
    {
        var key = TakeRawKey();
        while (true)
        {
            switch (_state)
            {
                case State.Normal:
                    if (key.Key == ConsoleKey.Escape && (key.Modifiers & ConsoleModifiers.Alt) == 0)
                    {
                        if (_console.KeyAvailable)
                        {
                            // Bytes queued behind the Esc: it begins a sequence (CSI/SS3).
                            _state = State.AfterEscape;
                            key = TakeRawKey();
                            continue;
                        }

                        return LogicalKey.Escape;
                    }

                    return Translate(key);

                case State.AfterEscape:
                    if (key.KeyChar == '[')
                    {
                        var sequence = ReadCsiBody();
                        _state = State.Normal;
                        return TranslateCsi(sequence);
                    }

                    // A real key arrived right after a lone Esc: report the Esc first,
                    // then the pending key on the next call.
                    _state = State.Normal;
                    _pending = key;
                    return LogicalKey.Escape;
            }
        }
    }

    /// <summary>Reads a CSI body (after ESC '[') through its final byte (0x40–0x7E) and returns it, e.g. "200~".</summary>
    private string ReadCsiBody()
    {
        _csi.Clear();
        while (true)
        {
            var value = TakeRawKey().KeyChar;
            _csi.Append(value);
            if (value is >= '\x40' and <= '\x7e')
            {
                break;
            }
        }

        return _csi.ToString();
    }

    private LogicalKey TranslateCsi(string sequence) => sequence switch
    {
        "200~" => ReadBracketedPaste(),
        // A stray terminator or any other (unknown) CSI is consumed and ignored.
        _ => LogicalKey.Ignore,
    };

    private LogicalKey ReadBracketedPaste()
    {
        var content = new StringBuilder();
        while (true)
        {
            var key = TakeRawKey();
            if (key.Key == ConsoleKey.Escape && (key.Modifiers & ConsoleModifiers.Alt) == 0 && _console.KeyAvailable)
            {
                var next = TakeRawKey();
                if (next.KeyChar == '[')
                {
                    if (ReadCsiBody() == "201~")
                    {
                        return LogicalKey.Paste(content.ToString());
                    }

                    // An unknown CSI inside the paste content: consumed, ignored.
                    continue;
                }

                // Esc in paste content that does not start a sequence: the next key is
                // paste content, not a sequence prefix.
                _pending = next;
                continue;
            }

            var logical = Translate(key);
            switch (logical.Kind)
            {
                case LogicalKeyKind.Character:
                    content.Append(logical.Character);
                    break;

                case LogicalKeyKind.Enter:
                    content.Append('\n');
                    break;

                case LogicalKeyKind.Backspace:
                    if (content.Length > 0)
                    {
                        content.Remove(content.Length - 1, 1);
                    }
                    break;
            }
        }
    }

    private ConsoleKeyInfo TakeRawKey() =>
        _pending is { } pending ? (_pending = null, pending).Item2 : _console.ReadKey();

    private static LogicalKey Translate(ConsoleKeyInfo key) => key.Key switch
    {
        ConsoleKey.Enter => LogicalKey.Enter,
        ConsoleKey.Backspace => LogicalKey.Backspace,
        ConsoleKey.Tab => LogicalKey.KeyChar('\t'),
        ConsoleKey.UpArrow => LogicalKey.HistoryPrevious,
        ConsoleKey.DownArrow => LogicalKey.HistoryNext,
        // Printable characters (letters, digits, symbols, space, unicode). Control
        // characters (Ctrl+letter) and unmapped keys are ignored.
        _ when key.KeyChar != '\0' && key.KeyChar >= ' ' => LogicalKey.KeyChar(key.KeyChar),
        _ => LogicalKey.Ignore,
    };
}
