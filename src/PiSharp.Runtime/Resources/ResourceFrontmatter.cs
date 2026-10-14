using System.Text.RegularExpressions;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

namespace PiSharp.Runtime.Resources;

/// <summary>Parses resource YAML while preserving Pi's typed metadata and body envelope.</summary>
internal sealed record ResourceFrontmatter(string? Name, string? Description, bool DisableModelInvocation, string Body,
    string? ArgumentHint = null)
{
    private static readonly char[] BodyWhitespace = "\t\n\v\f\r \u00A0\u1680\u2000\u2001\u2002\u2003\u2004\u2005\u2006\u2007\u2008\u2009\u200A\u2028\u2029\u202F\u205F\u3000\uFEFF".ToCharArray();
    private const string IntegerScalar = @"^(?:[-+]?[0-9]+|0o[0-7]+|0x[0-9a-fA-F]+)$";
    private const string FloatScalar = @"^(?:[-+]?(?:\.[0-9]+|[0-9]+\.[0-9]*|(?:\.[0-9]+|[0-9]+(?:\.[0-9]*)?)[eE][-+]?[0-9]+)|[-+]?\.(?:inf|Inf|INF)|\.nan|\.NaN|\.NAN)$";

    internal static ResourceFrontmatter Parse(string text)
    {
        var normalized = text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        if (normalized.StartsWith('\uFEFF')) normalized = normalized[1..];
        if (!normalized.StartsWith("---", StringComparison.Ordinal)) return new(null, null, false, normalized);
        var end = normalized.IndexOf("\n---", 3, StringComparison.Ordinal);
        if (end < 0) return new(null, null, false, normalized);
        var body = TrimBody(normalized[(end + 4)..]);
        var yaml = end > 4 ? normalized[4..end] : "";
        if (yaml.Length == 0) return new(null, null, false, body);
        var stream = new YamlStream();
        // Pi's YAML parser treats the closing frontmatter boundary as a final line break for block scalars.
        try { stream.Load(new StringReader(yaml.EndsWith('\n') ? yaml : yaml + "\n")); }
        catch (YamlException error) { throw new YamlException(FormatError(yaml, error), error); }
        if (stream.Documents.Count > 1) throw new YamlException("Expected a single frontmatter document.");
        if (stream.Documents.Count == 0 || stream.Documents[0].RootNode is not YamlMappingNode mapping)
            return new(null, null, false, body);
        YamlNode? Field(string name) => mapping.Children.FirstOrDefault(pair =>
            pair.Key is YamlScalarNode key && key.Value == name).Value;
        return new(StringValue(Field("name")), StringValue(Field("description")),
            BooleanTrue(Field("disable-model-invocation")), body, StringValue(Field("argument-hint")));
    }

    internal static string TrimBody(string text) => text.Trim(BodyWhitespace);
    internal static bool IsWhitespace(char value) => Array.IndexOf(BodyWhitespace, value) >= 0;

    private static string FormatError(string yaml, YamlException error)
    {
        var title = error.Message.StartsWith("Duplicate key", StringComparison.Ordinal) ? "Map keys must be unique"
            : error.Message.Contains("flow sequence", StringComparison.Ordinal) && error.Message.Contains("expected ',' or ']'", StringComparison.Ordinal)
                ? "Flow sequence in block collection must be sufficiently indented and end with a ]" : null;
        if (title is null) return error.Message;
        var offset = (int)Math.Clamp(error.Start.Index, 0, yaml.Length);
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
            ? $"{message}:\n\n{snippet}\n{new string(' ', cursor)}^\n" : message;
    }

    private static string? StringValue(YamlNode? node)
    {
        if (node is not YamlScalarNode scalar) return null;
        var tag = scalar.Tag.ToString();
        var value = scalar.Value ?? "";
        if (!scalar.Tag.IsEmpty)
        {
            var typed = tag switch
            {
                "tag:yaml.org,2002:bool" => IsBoolean(value),
                "tag:yaml.org,2002:null" => IsNull(value),
                "tag:yaml.org,2002:int" => Regex.IsMatch(value, IntegerScalar, RegexOptions.CultureInvariant),
                "tag:yaml.org,2002:float" => Regex.IsMatch(value, FloatScalar, RegexOptions.CultureInvariant),
                _ => false
            };
            return typed ? null : scalar.Value;
        }
        if (scalar.Style != ScalarStyle.Plain) return scalar.Value;
        return IsNull(value) || IsBoolean(value) || Regex.IsMatch(value, IntegerScalar, RegexOptions.CultureInvariant) ||
            Regex.IsMatch(value, FloatScalar, RegexOptions.CultureInvariant) ? null : value;
    }

    private static bool IsNull(string value) => value is "" or "~" or "null" or "Null" or "NULL";
    private static bool IsBoolean(string value) => value is "true" or "True" or "TRUE" or "false" or "False" or "FALSE";

    private static bool BooleanTrue(YamlNode? node) => node is YamlScalarNode scalar &&
        (scalar.Tag.ToString() == "tag:yaml.org,2002:bool" || scalar.Tag.IsEmpty && scalar.Style == ScalarStyle.Plain) &&
        scalar.Value is "true" or "True" or "TRUE";
}
