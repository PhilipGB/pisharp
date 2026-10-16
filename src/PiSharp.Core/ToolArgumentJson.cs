using System.Text;
using System.Text.Json;

namespace PiSharp.Core;

/// <summary>Parses complete or partial model arguments while preserving malformed string escapes.</summary>
public static class ToolArgumentJson
{
    public static JsonElement ParseComplete(string? source)
    {
        if (string.IsNullOrWhiteSpace(source)) return JsonSerializer.SerializeToElement(new { });
        JsonElement parsed;
        try { parsed = Parse(source); }
        catch (JsonException) { parsed = Parse(Repair(source)); }
        if (parsed.ValueKind != JsonValueKind.Object)
            throw new JsonException("Tool arguments must be a JSON object.");
        return parsed;
    }

    public static JsonElement ParseStreaming(string? source)
    {
        if (string.IsNullOrWhiteSpace(source)) return JsonSerializer.SerializeToElement(new { });
        try { return Parse(source); }
        catch (JsonException) { }
        var repaired = Repair(source);
        if (repaired != source)
        {
            try { return Parse(repaired); }
            catch (JsonException) { }
        }
        foreach (var candidate in new[] { source, repaired })
        {
            try { return JsonSerializer.SerializeToElement(new PartialReader(TrimWhitespace(candidate)).ReadValue()); }
            catch (Exception error) when (error is JsonException or InvalidOperationException) { }
        }
        return JsonSerializer.SerializeToElement(new { });
    }

    private static JsonElement Parse(string source)
    {
        using var document = JsonDocument.Parse(source);
        return document.RootElement.Clone();
    }

    private static bool IsWhitespace(char value) => value == '\uFEFF' || value != '\u0085' && char.IsWhiteSpace(value);

    private static string TrimWhitespace(string value)
    {
        var start = 0;
        var end = value.Length;
        while (start < end && IsWhitespace(value[start])) start++;
        while (end > start && IsWhitespace(value[end - 1])) end--;
        return value[start..end];
    }

    public static string Repair(string source)
    {
        var output = new StringBuilder(source.Length);
        var inString = false;
        for (var index = 0; index < source.Length; index++)
        {
            var value = source[index];
            if (value == '"') inString = !inString;
            if (!inString || value is not ('\\' or < ' '))
            {
                output.Append(value);
                continue;
            }
            if (value < ' ')
            {
                output.Append(value switch
                {
                    '\b' => "\\b",
                    '\f' => "\\f",
                    '\n' => "\\n",
                    '\r' => "\\r",
                    '\t' => "\\t",
                    _ => "\\u" + ((int)value).ToString("x4", System.Globalization.CultureInfo.InvariantCulture)
                });
                continue;
            }
            if (index + 1 < source.Length)
            {
                var next = source[index + 1];
                if (next == 'u' && index + 5 < source.Length &&
                    source.AsSpan(index + 2, 4).IndexOfAnyExcept("0123456789abcdefABCDEF") < 0)
                {
                    output.Append(source.AsSpan(index, 6));
                    index += 5;
                    continue;
                }
                if (next is '"' or '\\' or '/' or 'b' or 'f' or 'n' or 'r' or 't')
                {
                    output.Append(value).Append(next);
                    index++;
                    continue;
                }
            }
            output.Append("\\\\");
        }
        return output.ToString();
    }

    private sealed class PartialReader(string source)
    {
        private int _position;
        private int _depth;

        public object? ReadValue()
        {
            SkipWhitespace();
            if (_position >= source.Length || _depth >= 64) throw new JsonException("Incomplete arguments.");
            if (source[_position] == '"') return ReadString();
            if (source[_position] == '{') return ReadObject();
            if (source[_position] == '[') return ReadArray();
            foreach (var literal in new[] { "true", "false", "null" })
            {
                var remaining = source.AsSpan(_position);
                if (remaining.StartsWith(literal, StringComparison.Ordinal) ||
                    remaining.Length < literal.Length && literal.AsSpan().StartsWith(remaining, StringComparison.Ordinal))
                {
                    _position += literal.Length;
                    return literal switch { "true" => true, "false" => false, _ => null };
                }
            }
            var start = _position;
            while (_position < source.Length && source[_position] is '-' or '+' or '.' or 'e' or 'E' or >= '0' and <= '9')
                _position++;
            if (start == _position) throw new JsonException("Invalid argument value.");
            var token = source[start.._position];
            try { return Parse(token); }
            catch (JsonException) when (token.LastIndexOf('e') is > 0 and var exponent)
            {
                return Parse(token[..exponent]);
            }
        }

        private string ReadString()
        {
            var start = _position++;
            var escaped = false;
            while (_position < source.Length)
            {
                var value = source[_position++];
                if (value == '"' && !escaped)
                    return JsonSerializer.Deserialize<string>(source[start.._position])!;
                escaped = value == '\\' && !escaped;
            }
            var end = _position - (escaped ? 1 : 0);
            try { return JsonSerializer.Deserialize<string>(source[start..end] + '"')!; }
            catch (JsonException)
            {
                var lastEscape = source.LastIndexOf('\\', Math.Max(start, end - 1));
                if (lastEscape < start) throw;
                return JsonSerializer.Deserialize<string>(source[start..lastEscape] + '"')!;
            }
        }

        private Dictionary<string, object?> ReadObject()
        {
            _position++;
            _depth++;
            var result = new Dictionary<string, object?>(StringComparer.Ordinal);
            try
            {
                while (true)
                {
                    SkipWhitespace();
                    if (_position >= source.Length) return result;
                    if (source[_position] == '}') { _position++; return result; }
                    if (source[_position] != '"') return result;
                    var key = ReadString();
                    SkipWhitespace();
                    if (_position >= source.Length || source[_position++] != ':') return result;
                    result[key] = ReadValue();
                    SkipWhitespace();
                    if (_position < source.Length && source[_position] == ',') _position++;
                }
            }
            catch (JsonException) { return result; }
            finally { _depth--; }
        }

        private List<object?> ReadArray()
        {
            _position++;
            _depth++;
            var result = new List<object?>();
            try
            {
                while (true)
                {
                    SkipWhitespace();
                    if (_position >= source.Length) return result;
                    if (source[_position] == ']') { _position++; return result; }
                    result.Add(ReadValue());
                    SkipWhitespace();
                    if (_position < source.Length && source[_position] == ',') _position++;
                }
            }
            catch (JsonException) { return result; }
            finally { _depth--; }
        }

        private void SkipWhitespace()
        {
            while (_position < source.Length && IsWhitespace(source[_position])) _position++;
        }
    }
}
