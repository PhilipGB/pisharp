namespace PiSharp.Runtime.Resources;

/// <summary>Parses resource YAML while preserving Pi's typed metadata and body envelope.</summary>
internal sealed record ResourceFrontmatter(string? Name, string? Description, bool DisableModelInvocation, string Body,
    string? ArgumentHint = null)
{
    private static readonly char[] BodyWhitespace = "\t\n\v\f\r \u00A0\u1680\u2000\u2001\u2002\u2003\u2004\u2005\u2006\u2007\u2008\u2009\u200A\u2028\u2029\u202F\u205F\u3000\uFEFF".ToCharArray();

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
        var document = ResourceYamlDocument.Parse(yaml);
        return new(ResourceYamlScalar.StringValue(document.Field("name")), ResourceYamlScalar.StringValue(document.Field("description")),
            ResourceYamlScalar.BooleanTrue(document.Field("disable-model-invocation")), body,
            ResourceYamlScalar.StringValue(document.Field("argument-hint")));
    }

    internal static string TrimBody(string text) => text.Trim(BodyWhitespace);
    internal static bool IsWhitespace(char value) => Array.IndexOf(BodyWhitespace, value) >= 0;
}
