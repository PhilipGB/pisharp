using System.Text;
using PiSharp.Core;

namespace PiSharp.Cli;

/// <summary>
/// Reads the interactive REPL prompt.
///
/// Two modes (item 4):
/// <list type="bullet">
/// <item><b>Key mode</b> (<see cref="IConsoleIO"/> constructor): the prompt is read key-by-key
/// so it is cancellable — Esc clears the draft, up/down arrows walk <see cref="History"/>,
/// bracketed pastes arrive as one submission, and the <see cref="CancellationToken"/> is
/// observed between keys (Ctrl+C abort exits the prompt within ~20 ms). A blocking
/// <c>Console.In.ReadLine()</c> cannot be interrupted on Unix (EINTR retry), which is why
/// the interactive path must be key-driven.</item>
/// <item><b>Line mode</b> (TextReader constructor): the original line-oriented read used for
/// redirected stdin and tests; bracketed paste markers and history arrow lines are handled
/// at the line level.</item>
/// </list>
/// </summary>
public sealed class TerminalPromptReader : IDisposable
{
    private const string EnableBracketedPaste = "\u001b[?2004h";
    private const string DisableBracketedPaste = "\u001b[?2004l";
    private const string BracketedPasteStart = "\u001b[200~";
    private const string BracketedPasteEnd = "\u001b[201~";
    private const string HistoryPrevious = "\u001b[A";
    private const string HistoryNext = "\u001b[B";

    private readonly IConsoleIO? _console;
    private readonly TextReader? _input;
    private readonly TextWriter? _output;
    private readonly bool _bracketedPasteEnabled;
    private bool _disposed;
    private readonly PromptHistory _history = new();

    /// <summary>Line mode: reads from a <see cref="TextReader"/> (redirected stdin or tests).</summary>
    public TerminalPromptReader(TextReader input, TextWriter output, bool enableBracketedPaste)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(output);

        _input = input;
        _output = output;
        _bracketedPasteEnabled = enableBracketedPaste;

        if (_bracketedPasteEnabled)
        {
            _output.Write(EnableBracketedPaste);
            _output.Flush();
        }
    }

    /// <summary>Key mode: reads an interactive terminal through the <see cref="IConsoleIO"/> seam.</summary>
    public TerminalPromptReader(IConsoleIO console, bool enableBracketedPaste)
    {
        ArgumentNullException.ThrowIfNull(console);

        _console = console;
        _bracketedPasteEnabled = enableBracketedPaste;

        if (_bracketedPasteEnabled)
        {
            console.Write(EnableBracketedPaste);
        }
    }

    /// <summary>Gets the prompts submitted during this reader's lifetime.</summary>
    public PromptHistory History => _history;

    /// <summary>
    /// Reads one prompt. Returns the submitted text (possibly multi-line via paste), null
    /// on EOF (line mode). In key mode a lone Esc clears the current draft instead of
    /// exiting; the token cancels the read (<see cref="OperationCanceledException"/>).
    /// </summary>
    public string? ReadPrompt(string promptText = "> ", CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        return _console is null
            ? ReadLinePrompt(promptText)
            : ReadKeyPrompt(promptText, cancellationToken);
    }

    /// <summary>Key-driven interactive read (see type documentation).</summary>
    private string? ReadKeyPrompt(string promptText, CancellationToken cancellationToken)
    {
        var options = new ConsoleKeyInput.Options
        {
            AcceptMultilinePaste = true,
            // Esc clears the current draft (pinned TUI: Esc cancels the in-progress input)
            // without exiting the REPL.
            OnLoneEscape = draft => string.IsNullOrWhiteSpace(draft) ? null : string.Empty,
            HistoryPrevious = draft => _history.Previous(draft),
            HistoryNext = () => _history.Next(),
        };

        var line = ConsoleKeyInput.ReadLine(_console!, promptText, cancellationToken, options);
        _history.Add(line);
        return line;
    }

    /// <summary>Line-oriented read over a <see cref="TextReader"/> (redirected input, tests).</summary>
    private string? ReadLinePrompt(string promptText)
    {
        _output!.Write(promptText);
        _output.Flush();

        var line = _input!.ReadLine();
        if (line is null)
        {
            return null;
        }

        if (line.Equals(HistoryPrevious, StringComparison.Ordinal))
        {
            return _history.Previous(string.Empty) ?? string.Empty;
        }
        if (line.Equals(HistoryNext, StringComparison.Ordinal))
        {
            return _history.Next() ?? string.Empty;
        }

        var pasteStart = line.IndexOf(BracketedPasteStart, StringComparison.Ordinal);
        if (pasteStart < 0)
        {
            _history.Add(line);
            return line;
        }

        line = line.Remove(pasteStart, BracketedPasteStart.Length);
        var result = new StringBuilder();

        while (true)
        {
            var pasteEnd = line.IndexOf(BracketedPasteEnd, StringComparison.Ordinal);
            if (pasteEnd >= 0)
            {
                result.Append(line, 0, pasteEnd);
                result.Append(line[(pasteEnd + BracketedPasteEnd.Length)..]);
                var prompt = result.ToString();
                _history.Add(prompt);
                return prompt;
            }

            result.Append(line);

            var nextLine = _input.ReadLine();
            if (nextLine is null)
            {
                var prompt = result.ToString();
                _history.Add(prompt);
                return prompt;
            }

            result.Append('\n');
            line = nextLine;
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_bracketedPasteEnabled)
        {
            if (_console is not null)
            {
                _console.Write(DisableBracketedPaste);
            }
            else
            {
                _output!.Write(DisableBracketedPaste);
                _output.Flush();
            }
        }
    }
}
