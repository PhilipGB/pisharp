using System.Text;

namespace PiSharp.Cli;

/// <summary>
/// Reads interactive prompts while preserving bracketed multi-line pastes as a single submission.
/// </summary>
public sealed class TerminalPromptReader : IDisposable
{
    private const string EnableBracketedPaste = "\u001b[?2004h";
    private const string DisableBracketedPaste = "\u001b[?2004l";
    private const string BracketedPasteStart = "\u001b[200~";
    private const string BracketedPasteEnd = "\u001b[201~";

    private readonly TextReader _input;
    private readonly TextWriter _output;
    private readonly bool _bracketedPasteEnabled;
    private bool _disposed;

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

    public string? ReadPrompt(string promptText = "> ")
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        _output.Write(promptText);
        _output.Flush();

        var line = _input.ReadLine();
        if (line is null)
        {
            return null;
        }

        var pasteStart = line.IndexOf(BracketedPasteStart, StringComparison.Ordinal);
        if (pasteStart < 0)
        {
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
                return result.ToString();
            }

            result.Append(line);

            var nextLine = _input.ReadLine();
            if (nextLine is null)
            {
                return result.ToString();
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
            _output.Write(DisableBracketedPaste);
            _output.Flush();
        }
    }
}
