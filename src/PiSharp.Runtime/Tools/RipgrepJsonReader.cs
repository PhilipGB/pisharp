using System.Globalization;
using System.Text;

namespace PiSharp.Runtime.Tools;

internal sealed record RipgrepMatch(string? Path, double? LineNumber, string? Text, bool LineTruncated);

/// <summary>Validates rg JSON lines while retaining only bounded displayed fields.</summary>
internal sealed class RipgrepJsonReader(Func<RipgrepMatch, bool> onMatch)
{
    private enum Node { Root, Data, Path, Lines, Other }
    private enum Token { Key, Type, Path, Line, Number, Ignore }
    private enum Expected { KeyOrEnd, Key, Colon, Value, ValueOrEnd, CommaOrEnd }
    private struct Frame(Node node, bool isObject)
    {
        public Node Node = node;
        public bool IsObject = isObject;
        public Expected Expected = isObject ? Expected.KeyOrEnd : Expected.ValueOrEnd;
        public string? Property;
    }

    private readonly Frame[] _frames = new Frame[64];
    private readonly Decoder _decoder = new UTF8Encoding(false).GetDecoder();
    private readonly StringBuilder _token = new();
    private readonly StringBuilder _line = new();
    private int _depth;
    private bool _inString;
    private bool _escaped;
    private int _unicodeDigits;
    private int _unicodeValue;
    private bool _inPrimitive;
    private int _numberState;
    private string? _literal;
    private int _literalPosition;
    private Token _kind;
    private string? _type;
    private string? _path;
    private double? _lineNumber;
    private string? _text;
    private long _lineLength;
    private char _lastLineCharacter;
    private bool _truncated;
    private bool _invalid;
    private bool _complete;
    private bool _finished;
    private bool _accepting = true;

    public bool Append(ReadOnlyMemory<byte> bytes)
    {
        if (_finished || !_accepting) return _accepting;
        Span<char> characters = stackalloc char[2048];
        var remaining = bytes.Span;
        do
        {
            _decoder.Convert(remaining, characters, bytes.IsEmpty, out var usedBytes, out var usedCharacters, out _);
            remaining = remaining[usedBytes..];
            foreach (var character in characters[..usedCharacters])
            {
                Read(character);
                if (!_accepting) break;
            }
        } while (remaining.Length > 0 && _accepting);
        if (bytes.IsEmpty)
        {
            FinishDocument();
            _finished = true;
        }
        return _accepting;
    }

    private void Read(char character)
    {
        if (character is '\n' or '\r') { FinishDocument(); return; }
        if (_invalid) return;
        if (_inString)
        {
            if (_unicodeDigits > 0)
            {
                var digit = character is >= '0' and <= '9' ? character - '0' :
                    character is >= 'a' and <= 'f' ? character - 'a' + 10 :
                    character is >= 'A' and <= 'F' ? character - 'A' + 10 : -1;
                if (digit < 0) { _invalid = true; return; }
                _unicodeValue = (_unicodeValue << 4) | digit;
                if (--_unicodeDigits == 0) Character((char)_unicodeValue);
                return;
            }
            if (_escaped)
            {
                _escaped = false;
                if (character == 'u') { _unicodeDigits = 4; _unicodeValue = 0; }
                else if (character is '"' or '\\' or '/' or 'n' or 'r' or 't' or 'b' or 'f')
                    Character(character switch { 'n' => '\n', 'r' => '\r', 't' => '\t', 'b' => '\b', 'f' => '\f', _ => character });
                else _invalid = true;
                return;
            }
            if (character == '\\') { _escaped = true; return; }
            if (character < ' ') { _invalid = true; return; }
            if (character != '"') { Character(character); return; }
            _inString = false;
            if (_kind == Token.Key)
            {
                _frames[_depth - 1].Property = _token.ToString();
                _frames[_depth - 1].Expected = Expected.Colon;
                return;
            }
            switch (_kind)
            {
                case Token.Type: _type = _token.ToString(); break;
                case Token.Path: _path = _token.ToString(); break;
                case Token.Line:
                    var length = _lineLength - (_lastLineCharacter == '\n' ? 1 : 0);
                    _truncated = length > 500;
                    _text = _line.ToString(0, (int)Math.Min(length, 500)) + (_truncated ? "... [truncated]" : "");
                    break;
            }
            CompleteValue();
            return;
        }
        if (_inPrimitive)
        {
            if (character is not (',' or '}' or ']' or ' ' or '\t')) { PrimitiveCharacter(character); return; }
            FinishPrimitive();
            if (_invalid) return;
        }
        if (character is ' ' or '\t') return;
        if (_complete) { _invalid = true; return; }
        if (character is '{' or '[')
        {
            if (!CanStartValue() || _depth == _frames.Length) { _invalid = true; return; }
            ResetSelectedValue();
            var node = _depth == 0 ? character == '{' ? Node.Root : Node.Other :
                (_frames[_depth - 1].Node, _frames[_depth - 1].Property) switch
                {
                    (Node.Root, "data") => Node.Data,
                    (Node.Data, "path") => Node.Path,
                    (Node.Data, "lines") => Node.Lines,
                    _ => Node.Other
                };
            if (_depth > 0) CompleteValue();
            _frames[_depth++] = new(node, character == '{');
        }
        else if (character is '}' or ']')
        {
            if (_depth == 0 || _frames[_depth - 1].IsObject != (character == '}') ||
                _frames[_depth - 1].Expected is not (Expected.KeyOrEnd or Expected.ValueOrEnd or Expected.CommaOrEnd))
            { _invalid = true; return; }
            if (--_depth == 0) _complete = true;
        }
        else if (character == ':')
        {
            if (_depth == 0 || _frames[_depth - 1].Expected != Expected.Colon) _invalid = true;
            else _frames[_depth - 1].Expected = Expected.Value;
        }
        else if (character == ',')
        {
            if (_depth == 0 || _frames[_depth - 1].Expected != Expected.CommaOrEnd) { _invalid = true; return; }
            _frames[_depth - 1].Expected = _frames[_depth - 1].IsObject ? Expected.Key : Expected.Value;
            _frames[_depth - 1].Property = null;
        }
        else if (character == '"')
        {
            var key = _depth > 0 && _frames[_depth - 1].Expected is Expected.Key or Expected.KeyOrEnd;
            if (!key && !CanStartValue()) { _invalid = true; return; }
            if (!key) ResetSelectedValue();
            _kind = key ? Token.Key : SelectedToken();
            _token.Clear();
            if (_kind == Token.Line) { _line.Clear(); _lineLength = 0; _lastLineCharacter = default; }
            _inString = true;
        }
        else
        {
            if (!CanStartValue()) { _invalid = true; return; }
            ResetSelectedValue();
            _kind = _depth > 0 && _frames[_depth - 1] is { Node: Node.Data, Property: "line_number" } ? Token.Number : Token.Ignore;
            _token.Clear();
            _literal = character switch { 't' => "true", 'f' => "false", 'n' => "null", _ => null };
            _literalPosition = 0;
            _numberState = 0;
            _inPrimitive = true;
            PrimitiveCharacter(character);
        }
    }

    private bool CanStartValue() => _depth == 0 ? !_complete : _frames[_depth - 1].Expected is Expected.Value or Expected.ValueOrEnd;

    private void CompleteValue()
    {
        if (_depth == 0) _complete = true;
        else _frames[_depth - 1].Expected = Expected.CommaOrEnd;
    }

    private Token SelectedToken() => _depth == 0 ? Token.Ignore : (_frames[_depth - 1].Node, _frames[_depth - 1].Property) switch
    {
        (Node.Root, "type") => Token.Type,
        (Node.Path, "text") => Token.Path,
        (Node.Lines, "text") => Token.Line,
        _ => Token.Ignore
    };

    private void ResetSelectedValue()
    {
        if (_depth == 0) return;
        switch (_frames[_depth - 1].Node, _frames[_depth - 1].Property)
        {
            case (Node.Root, "data"): _path = null; _text = null; _lineNumber = null; _truncated = false; break;
            case (Node.Root, "type"): _type = null; break;
            case (Node.Data, "path") or (Node.Path, "text"): _path = null; break;
            case (Node.Data, "lines") or (Node.Lines, "text"): _text = null; _truncated = false; break;
            case (Node.Data, "line_number"): _lineNumber = null; break;
        }
    }

    private void PrimitiveCharacter(char character)
    {
        if (_literal is not null)
        {
            if (_literalPosition >= _literal.Length || character != _literal[_literalPosition++]) _invalid = true;
            return;
        }
        var digit = character is >= '0' and <= '9';
        _numberState = _numberState switch
        {
            0 when character == '-' => 1,
            0 or 1 when character == '0' => 2,
            0 or 1 when character is >= '1' and <= '9' => 3,
            3 when digit => 3,
            2 or 3 when character == '.' => 4,
            4 or 5 when digit => 5,
            2 or 3 or 5 when character is 'e' or 'E' => 6,
            6 when character is '+' or '-' => 7,
            6 or 7 or 8 when digit => 8,
            _ => -1
        };
        if (_numberState < 0) _invalid = true;
        Character(character);
    }

    private void FinishPrimitive()
    {
        if (_literal is not null ? _literalPosition != _literal.Length : _numberState is not (2 or 3 or 5 or 8)) _invalid = true;
        if (!_invalid && _kind == Token.Number && _literal is null && double.TryParse(_token.ToString(), CultureInfo.InvariantCulture, out var number)) _lineNumber = number;
        _inPrimitive = false;
        CompleteValue();
    }

    private void FinishDocument()
    {
        if (_inPrimitive) FinishPrimitive();
        if (!_invalid && !_inString && _complete && _type == "match") _accepting = onMatch(new(_path, _lineNumber, _text, _truncated));
        _depth = 0;
        _inString = _escaped = _inPrimitive = _invalid = _complete = _truncated = false;
        _unicodeDigits = 0;
        _type = _path = _text = null;
        _lineNumber = null;
    }

    private void Character(char character)
    {
        if (_kind == Token.Line)
        {
            if (character == '\r') return;
            _lineLength++;
            _lastLineCharacter = character;
            if (_line.Length < 501) _line.Append(character);
        }
        else if (_kind != Token.Ignore && _token.Length < (_kind == Token.Path ? 32768 : 64)) _token.Append(character);
    }
}
