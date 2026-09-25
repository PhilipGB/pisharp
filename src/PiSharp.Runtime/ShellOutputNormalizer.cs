using System.Text;

namespace PiSharp.Runtime;

/// <summary>Removes terminal control sequences and unsafe control characters from direct Bash output.</summary>
internal sealed class ShellOutputNormalizer
{
    private enum State { Text, Escape, Csi, Osc, OscEscape }

    private State _state;

    public string Append(ReadOnlySpan<char> input)
    {
        var output = new StringBuilder(input.Length);
        foreach (var value in input)
        {
            switch (_state)
            {
                case State.Text:
                    ProcessText(value, output);
                    break;
                case State.Escape:
                    ProcessEscape(value, output);
                    break;
                case State.Csi:
                    ProcessCsi(value, output);
                    break;
                case State.Osc:
                    if (value is '\a' or '\u009c') _state = State.Text;
                    else if (value == '\u001b') _state = State.OscEscape;
                    break;
                case State.OscEscape:
                    if (value == '\\') _state = State.Text;
                    else _state = value == '\u001b' ? State.OscEscape : State.Osc;
                    break;
            }
        }
        return output.ToString();
    }

    /// <summary>Discards an incomplete terminal control sequence at end of output.</summary>
    public string Finish()
    {
        _state = State.Text;
        return "";
    }

    public static string NormalizeComplete(string input)
    {
        ArgumentNullException.ThrowIfNull(input);
        var normalizer = new ShellOutputNormalizer();
        return normalizer.Append(input.AsSpan()) + normalizer.Finish();
    }

    private void ProcessText(char value, StringBuilder output)
    {
        if (value == '\u001b') _state = State.Escape;
        else if (value == '\u009b') _state = State.Csi;
        else AppendSafe(value, output);
    }

    private void ProcessEscape(char value, StringBuilder output)
    {
        if (value == ']') _state = State.Osc;
        else if (value == '\u009b' || IsCsiIntermediate(value)) _state = State.Csi;
        else if (IsCsiFinal(value)) _state = State.Text;
        else
        {
            _state = State.Text;
            ProcessText(value, output);
        }
    }

    private void ProcessCsi(char value, StringBuilder output)
    {
        if (IsCsiParameter(value) || IsCsiIntermediate(value)) { }
        else if (IsCsiFinal(value)) _state = State.Text;
        else
        {
            _state = State.Text;
            ProcessText(value, output);
        }
    }

    private static bool IsCsiParameter(char value) => value is >= '0' and <= '9' or ';' or ':';

    private static bool IsCsiIntermediate(char value) => value is '[' or ']' or '(' or ')' or '#' or ';' or '?';

    // Matches the final-byte set used by Pi's stripAnsi expression.
    private static bool IsCsiFinal(char value) => value is >= '0' and <= '9' or
        >= 'A' and <= 'P' or >= 'R' and <= 'T' or 'Z' or 'c' or >= 'f' and <= 'n' or
        >= 'q' and <= 'u' or 'y' or '=' or '>' or '<' or '~';

    private static void AppendSafe(char value, StringBuilder output)
    {
        if (value == '\r' || value <= '\u001f' && value is not ('\t' or '\n') || value is >= '\ufff9' and <= '\ufffb')
            return;
        output.Append(value);
    }
}
