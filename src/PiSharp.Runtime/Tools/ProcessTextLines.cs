using System.Text;

namespace PiSharp.Runtime.Tools;

/// <summary>Decodes bounded filesystem-path lines using Node readline delimiters.</summary>
internal sealed class ProcessTextLines(Action<string> onLine)
{
    private readonly Decoder _decoder = new UTF8Encoding(false).GetDecoder();
    private readonly StringBuilder _line = new();
    private bool _carriageReturn;
    private bool _finished;

    public void Append(ReadOnlyMemory<byte> bytes)
    {
        if (_finished) return;
        Span<char> characters = stackalloc char[2048];
        var remaining = bytes.Span;
        do
        {
            _decoder.Convert(remaining, characters, bytes.IsEmpty, out var used, out var count, out _);
            remaining = remaining[used..];
            foreach (var character in characters[..count])
            {
                if (character == '\n' && _carriageReturn) { _carriageReturn = false; continue; }
                _carriageReturn = character == '\r';
                if (character is '\r' or '\n') { onLine(_line.ToString()); _line.Clear(); }
                else
                {
                    if (_line.Length == 32768) throw new IOException("Search process emitted a path exceeding the supported platform path length.");
                    _line.Append(character);
                }
            }
        } while (!remaining.IsEmpty);
        if (bytes.IsEmpty)
        {
            if (_line.Length > 0) onLine(_line.ToString());
            _line.Clear();
            _finished = true;
        }
    }

    internal static string Trim(string value)
    {
        const string characters = "\t\n\v\f\r \u00a0\u1680\u2000\u2001\u2002\u2003\u2004\u2005\u2006\u2007\u2008\u2009\u200a\u2028\u2029\u202f\u205f\u3000\ufeff";
        var start = 0;
        var end = value.Length;
        while (start < end && characters.Contains(value[start])) start++;
        while (end > start && characters.Contains(value[end - 1])) end--;
        return value[start..end];
    }
}
