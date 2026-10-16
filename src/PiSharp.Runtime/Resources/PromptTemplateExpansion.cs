using System.Text;
using System.Text.RegularExpressions;

namespace PiSharp.Runtime.Resources;

internal static class PromptTemplateExpansion
{
    internal static string Expand(string template, string arguments)
    {
        var parts = Tokenize(arguments);
        var all = string.Join(' ', parts);
        return Regex.Replace(template, @"\$\{([0-9]+|ARGUMENTS|@):-([^}]*)\}|\$\{@:([0-9]+)(?::([0-9]+))?\}|\$(ARGUMENTS|@|[0-9]+)", match =>
        {
            if (match.Groups[1].Success)
            {
                var value = Argument(match.Groups[1].Value);
                return value.Length == 0 ? match.Groups[2].Value : value;
            }
            if (match.Groups[3].Success)
            {
                var start = int.TryParse(match.Groups[3].Value, out var index) ? Math.Max(0, index - 1) : parts.Count;
                var length = match.Groups[4].Success && int.TryParse(match.Groups[4].Value, out var count)
                    ? count : int.MaxValue;
                return string.Join(' ', parts.Skip(start).Take(length));
            }
            return Argument(match.Groups[5].Value);
        });

        string Argument(string key) => key is "@" or "ARGUMENTS" ? all :
            int.TryParse(key, out var index) && index > 0 && index <= parts.Count ? parts[index - 1] : "";
    }

    private static List<string> Tokenize(string arguments)
    {
        var result = new List<string>();
        var word = new StringBuilder();
        var quote = '\0';
        foreach (var value in arguments)
        {
            if (quote != '\0')
            {
                if (value == quote) quote = '\0';
                else word.Append(value);
            }
            else if (value is '\'' or '"') quote = value;
            else if (ResourceFrontmatter.IsWhitespace(value))
            {
                if (word.Length == 0) continue;
                result.Add(word.ToString());
                word.Clear();
            }
            else word.Append(value);
        }
        if (word.Length > 0) result.Add(word.ToString());
        return result;
    }
}
