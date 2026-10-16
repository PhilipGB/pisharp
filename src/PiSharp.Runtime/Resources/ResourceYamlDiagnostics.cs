using System.Text.RegularExpressions;
using YamlDotNet.Core;

namespace PiSharp.Runtime.Resources;

internal static class ResourceYamlDiagnostics
{
    internal static string FromNative(string yaml, YamlException error)
    {
        var message = error.Message;
        var offset = (int)Math.Clamp(error.Start.Index, 0, yaml.Length);
        if (message.StartsWith("Duplicate key", StringComparison.Ordinal))
            return Format(yaml, "Map keys must be unique", offset);
        if (message.Contains("flow sequence", StringComparison.Ordinal) && message.Contains("expected ',' or ']'", StringComparison.Ordinal))
            return Format(yaml, "Flow sequence in block collection must be sufficiently indented and end with a ]", offset);
        if (message.Contains("flow mapping", StringComparison.Ordinal) && message.Contains("expected ',' or '}'", StringComparison.Ordinal))
        {
            if (offset == yaml.Length)
                return Format(yaml, "Flow map in block collection must be sufficiently indented and end with a }", offset);
            var previousColon = yaml.LastIndexOf(':', Math.Max(0, offset - 1));
            var start = previousColon < 0 ? offset : previousColon + 1;
            while (start < yaml.Length && yaml[start] == ' ') start++;
            var end = yaml.IndexOfAny(['}', ']'], start);
            return Format(yaml, "Block collections are not allowed within flow collections", start,
                Math.Max(1, (end < 0 ? yaml.Length : end) - start));
        }
        if (message.Contains("quoted scalar", StringComparison.Ordinal))
        {
            if (message.Contains("unexpected end of stream", StringComparison.Ordinal))
                return Format(yaml, $"Missing closing {yaml[offset]}quote", Math.Min(yaml.Length, (int)error.End.Index));
            if (message.Contains("unknown escape character", StringComparison.Ordinal) ||
                message.Contains("expected hexadecimal number", StringComparison.Ordinal))
            {
                var escape = (int)error.End.Index - (message.Contains("hexadecimal", StringComparison.Ordinal) ? 2 : 0);
                escape = Math.Clamp(escape, 0, Math.Max(0, yaml.Length - 1));
                var digits = escape + 1 < yaml.Length ? yaml[escape + 1] switch { 'x' => 2, 'u' => 4, 'U' => 8, _ => 0 } : 0;
                var text = yaml.Substring(escape, Math.Min(2 + digits, yaml.Length - escape));
                return Format(yaml, $"Invalid escape sequence {text}", escape);
            }
        }
        if (message.Contains("tab character", StringComparison.Ordinal) && message.Contains("indentation", StringComparison.Ordinal))
            return Format(yaml, "Tabs are not allowed as indentation", Math.Min(yaml.Length, (int)error.End.Index));
        if (message == "Did not find expected <document start>.")
            return Format(yaml, "Missing directives-end indicator line", offset);
        if (message == "Did not find expected <document end>.")
        {
            var end = yaml.IndexOf('\n', offset);
            return Format(yaml, "Unexpected scalar at node end", offset, (end < 0 ? yaml.Length : end) - offset);
        }
        if (message.Contains("block scalar", StringComparison.Ordinal) && message.Contains("expected comment or line break", StringComparison.Ordinal))
        {
            var header = Regex.Match(yaml[offset..], @"^[|>][^\s#]*").Value;
            return Format(yaml, $"Block scalar header includes extra characters: {header}", (int)error.End.Index);
        }
        return message;
    }

    internal static string Format(string yaml, string title, int offset, int underlineLength = 1)
    {
        offset = Math.Clamp(offset, 0, yaml.Length);
        var lineStart = offset == 0 ? 0 : yaml.LastIndexOf('\n', offset - 1) + 1;
        var lineEnd = yaml.IndexOf('\n', offset);
        if (lineEnd < 0) lineEnd = yaml.Length;
        var line = 1 + yaml.AsSpan(0, lineStart).Count('\n');
        var column = offset - lineStart + 1;
        var snippet = yaml[lineStart..lineEnd];
        var cursor = column - 1;
        if (cursor >= 60 && snippet.Length > 80)
        {
            var trim = Math.Min(cursor - 39, snippet.Length - 79);
            snippet = "…" + snippet[trim..];
            cursor -= trim - 1;
        }
        if (snippet.Length > 80) snippet = snippet[..79] + "…";
        if (line > 1 && snippet[..Math.Min(cursor, snippet.Length)].All(character => character == ' '))
        {
            var previousStart = lineStart <= 1 ? 0 : yaml.LastIndexOf('\n', lineStart - 2) + 1;
            var previous = yaml[previousStart..lineStart];
            if (previous.Length > 80) previous = previous[..79] + "…\n";
            snippet = previous + snippet;
        }
        var message = $"{title} at line {line}, column {column}";
        return snippet.Any(character => character != ' ')
            ? $"{message}:\n\n{snippet}\n{new string(' ', cursor)}{new string('^', Math.Max(1, underlineLength))}\n" : message;
    }
}
