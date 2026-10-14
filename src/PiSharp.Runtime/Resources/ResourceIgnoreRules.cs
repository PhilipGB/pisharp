using System.Text;
using System.Text.RegularExpressions;

namespace PiSharp.Runtime.Resources;

/// <summary>Applies skill-directory ignore files in their discovery order and root-relative scope.</summary>
internal sealed class ResourceIgnoreRules(string root)
{
    private readonly List<(Regex Pattern, bool Negated)> _rules = [];

    internal void AddDirectory(string directory)
    {
        var relative = Path.GetRelativePath(root, directory).Replace(Path.DirectorySeparatorChar, '/');
        var prefix = relative == "." ? "" : relative + "/";
        foreach (var name in new[] { ".gitignore", ".ignore", ".fdignore" })
        {
            string text;
            try
            {
                var path = Path.Combine(directory, name);
                if (!LocalResourceFileType.IsRegularFile(path)) continue;
                text = Encoding.UTF8.GetString(File.ReadAllBytes(path));
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { continue; }
            foreach (var raw in text.Split('\n'))
            {
                var line = raw.EndsWith('\r') ? raw[..^1] : raw;
                var trimmed = ResourceFrontmatter.TrimBody(line);
                if (trimmed.Length == 0 || trimmed.StartsWith('#')) continue;
                var negated = line.StartsWith('!');
                if (negated || line.StartsWith("\\!", StringComparison.Ordinal)) line = line[1..];
                if (line.StartsWith('/')) line = line[1..];
                AddPattern((negated ? "!" : "") + prefix + line);
            }
        }
    }

    internal bool IsIgnored(string relative, bool directory)
    {
        var parts = relative.Replace(Path.DirectorySeparatorChar, '/').Split('/');
        var path = "";
        for (var index = 0; index < parts.Length; index++)
        {
            path += (index == 0 ? "" : "/") + parts[index];
            var candidate = index < parts.Length - 1 || directory ? path + "/" : path;
            var ignored = false;
            foreach (var rule in _rules)
                if (rule.Pattern.IsMatch(candidate)) ignored = !rule.Negated;
            if (ignored) return true;
        }
        return false;
    }

    private void AddPattern(string pattern)
    {
        if (pattern.StartsWith('\uFEFF')) pattern = pattern[1..];
        while (pattern.EndsWith(' '))
        {
            var escapes = 0;
            for (var index = pattern.Length - 2; index >= 0 && pattern[index] == '\\'; index--) escapes++;
            if (escapes % 2 == 1) break;
            pattern = pattern[..^1];
        }
        if (pattern.Length == 0 || pattern == "!") return;
        var negated = pattern.StartsWith('!');
        if (negated) pattern = pattern[1..];
        var directory = pattern.EndsWith('/');
        if (directory) pattern = pattern[..^1];
        var start = pattern.Contains('/') ? "^" : "(?:^|/)";
        try
        {
            var expression = start + GlobExpression(pattern) + (directory ? "/$" : "(?=$|/$)");
            _rules.Add((new Regex(expression, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
                TimeSpan.FromSeconds(1)), negated));
        }
        catch (ArgumentException) { }
    }

    private static string GlobExpression(string pattern)
    {
        var result = new StringBuilder();
        for (var index = 0; index < pattern.Length; index++)
        {
            var value = pattern[index];
            if (value == '\\')
            {
                if (++index == pattern.Length) throw new ArgumentException("Incomplete pattern escape.");
                result.Append(Regex.Escape(pattern[index].ToString()));
            }
            else if (value == '*')
            {
                var end = index;
                while (end + 1 < pattern.Length && pattern[end + 1] == '*') end++;
                var segment = index == 0 || pattern[index - 1] == '/';
                if (end > index && segment && end + 1 < pattern.Length && pattern[end + 1] == '/')
                {
                    result.Append(end + 2 == pattern.Length ? "(?:[^/]+/)+" : "(?:[^/]+/)*");
                    index = end + 1;
                }
                else
                {
                    result.Append(end > index && segment && end + 1 == pattern.Length && index > 0
                        ? ".+" : "[^/]*");
                    index = end;
                }
            }
            else if (value == '?') result.Append("[^/]");
            else if (value == '[' && TryCharacterClass(pattern, index, out var expression, out var end))
            {
                result.Append(expression);
                index = end;
            }
            else result.Append(Regex.Escape(value.ToString()));
        }
        return result.ToString();
    }

    private static bool TryCharacterClass(string pattern, int start, out string expression, out int end)
    {
        expression = "";
        end = -1;
        var content = new StringBuilder("[");
        var index = start + 1;
        if (index < pattern.Length && pattern[index] is '!' or '^')
        {
            content.Append("^/");
            index++;
        }
        if (index < pattern.Length && pattern[index] == ']')
        {
            content.Append(@"\]");
            index++;
        }
        for (; index < pattern.Length; index++)
        {
            if (pattern[index] == ']')
            {
                expression = "(?!/)" + content.Append(']');
                end = index;
                return true;
            }
            if (pattern[index] == '\\' && index + 1 < pattern.Length)
            {
                content.Append(@"\u").Append(((int)pattern[++index]).ToString("x4"));
            }
            else if (pattern[index] == '[' && index + 1 < pattern.Length && pattern[index + 1] == ':' &&
                pattern.IndexOf(":]", index + 2, StringComparison.Ordinal) is var close && close >= 0)
            {
                var name = pattern[(index + 2)..close];
                var range = name switch
                {
                    "alnum" => "a-zA-Z0-9",
                    "alpha" => "a-zA-Z",
                    "blank" => @"\t ",
                    "cntrl" => @"\x00-\x1F\x7F",
                    "digit" => "0-9",
                    "graph" => @"\x21-\x7E",
                    "lower" => "a-z",
                    "print" => @"\x20-\x7E",
                    "punct" => @"\x21-\x2F\x3A-\x40\x5B-\x60\x7B-\x7E",
                    "space" => @"\t\n\v\f\r ",
                    "upper" => "A-Z",
                    "xdigit" => "0-9a-fA-F",
                    _ => null
                };
                if (range is null) return false;
                content.Append(range);
                index = close + 1;
            }
            else if (pattern[index] == '[') content.Append(@"\[");
            else content.Append(pattern[index]);
        }
        return false;
    }
}
