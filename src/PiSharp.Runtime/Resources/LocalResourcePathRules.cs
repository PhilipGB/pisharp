using System.Text;
using System.Text.RegularExpressions;

namespace PiSharp.Runtime.Resources;

/// <summary>Applies Pi's scope-relative local resource path exclusions and exact overrides.</summary>
public static class LocalResourcePathRules
{
    private static readonly StringComparison s_pathComparison = OperatingSystem.IsWindows()
        ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    public static bool IsPattern(string entry) => entry.Length > 0 && entry[0] is '!' or '+' or '-';

    public static IReadOnlyList<string> GetPaths(IReadOnlyList<string>? entries) =>
        (entries ?? []).Where(entry => !IsPattern(entry)).ToArray();

    public static IReadOnlyList<string> GetOverrides(IReadOnlyList<string>? entries) =>
        (entries ?? []).Where(IsPattern).ToArray();

    public static string ResolvePath(string path, string baseDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentException.ThrowIfNullOrWhiteSpace(baseDirectory);
        var expanded = ExpandHome(path);
        return Path.GetFullPath(expanded, Path.GetFullPath(baseDirectory));
    }

    public static IReadOnlyList<string> ApplyOverrides(IEnumerable<string> paths,
        IReadOnlyList<string>? entries, string baseDirectory, bool skillPaths = false)
    {
        var candidates = paths.Select(Path.GetFullPath).Distinct(PathComparer()).ToArray();
        var overrides = GetOverrides(entries);
        var enabled = candidates.Where(path => IsEnabledByOverrides(path, overrides, baseDirectory, skillPaths));
        return enabled.ToArray();
    }

    public static bool IsEnabledByOverrides(string path, IReadOnlyList<string>? entries,
        string baseDirectory, bool skillPath = false)
    {
        var fullPath = Path.GetFullPath(path);
        var enabled = true;
        // Pi applies each pattern class in fixed order: glob exclusions, exact includes,
        // then exact exclusions, regardless of their order in settings.json.
        var excludes = GetOverrides(entries).Where(entry => entry[0] == '!');
        if (excludes.Any(entry => MatchesGlob(fullPath, entry[1..], baseDirectory, skillPath)))
            enabled = false;
        var includes = GetOverrides(entries).Where(entry => entry[0] == '+');
        if (includes.Any(entry => MatchesExact(fullPath, entry[1..], baseDirectory, skillPath)))
            enabled = true;
        var forceExcludes = GetOverrides(entries).Where(entry => entry[0] == '-');
        if (forceExcludes.Any(entry => MatchesExact(fullPath, entry[1..], baseDirectory, skillPath)))
            enabled = false;
        return enabled;
    }

    private static bool MatchesGlob(string path, string pattern, string baseDirectory, bool skillPath)
    {
        if (pattern.Length == 0) return false;
        var fullBase = Path.GetFullPath(baseDirectory);
        var fullPattern = ExpandHome(pattern).Replace('\\', '/');
        var relative = Normalize(Path.GetRelativePath(fullBase, path));
        var absolute = Normalize(path);
        var name = Path.GetFileName(path);
        if (GlobMatch(relative, fullPattern) || GlobMatch(name, fullPattern) || GlobMatch(absolute, fullPattern))
            return true;
        if (!skillPath || !Path.GetFileName(path).Equals("SKILL.md", StringComparison.OrdinalIgnoreCase))
            return false;
        var parent = Path.GetDirectoryName(path)!;
        return GlobMatch(Normalize(Path.GetRelativePath(fullBase, parent)), fullPattern) ||
            GlobMatch(Path.GetFileName(parent), fullPattern) || GlobMatch(Normalize(parent), fullPattern);
    }

    private static bool MatchesExact(string path, string pattern, string baseDirectory, bool skillPath)
    {
        if (pattern.Length == 0) return false;
        var normalized = pattern.StartsWith("./", StringComparison.Ordinal) || pattern.StartsWith(".\\", StringComparison.Ordinal)
            ? pattern[2..] : pattern;
        var expanded = ExpandHome(normalized);
        var fullPath = Path.GetFullPath(path);
        var fullBase = Path.GetFullPath(baseDirectory);
        var target = Path.IsPathRooted(expanded) ? Path.GetFullPath(expanded) : Path.GetFullPath(expanded, fullBase);
        if (string.Equals(fullPath, target, s_pathComparison) ||
            string.Equals(Path.GetRelativePath(fullBase, fullPath), Normalize(expanded), s_pathComparison))
            return true;
        if (!skillPath || !Path.GetFileName(fullPath).Equals("SKILL.md", StringComparison.OrdinalIgnoreCase))
            return false;
        var parent = Path.GetDirectoryName(fullPath)!;
        return string.Equals(parent, target, s_pathComparison) ||
            string.Equals(Path.GetRelativePath(fullBase, parent), Normalize(expanded), s_pathComparison);
    }

    private static bool GlobMatch(string value, string pattern)
    {
        var regex = new StringBuilder("^");
        for (var index = 0; index < pattern.Length; index++)
        {
            var current = pattern[index];
            if (current == '*')
            {
                if (index + 1 < pattern.Length && pattern[index + 1] == '*')
                {
                    index++;
                    if (index + 1 < pattern.Length && pattern[index + 1] == '/')
                    {
                        index++;
                        regex.Append("(?:.*/)?");
                    }
                    else regex.Append(".*");
                }
                else regex.Append("[^/]*");
            }
            else if (current == '?') regex.Append("[^/]");
            else if (current == '[' && pattern.IndexOf(']', index + 1) is var close && close > index + 1)
            {
                var content = pattern[(index + 1)..close];
                var negate = content[0] == '!';
                if (negate) content = content[1..];
                if (content.Length == 0)
                {
                    regex.Append(Regex.Escape(pattern[index..(close + 1)]));
                    index = close;
                    continue;
                }
                regex.Append('[');
                if (negate) regex.Append('^');
                foreach (var character in content)
                {
                    if (character is '\\' or ']' or '^') regex.Append('\\');
                    regex.Append(character);
                }
                regex.Append(']');
                index = close;
            }
            else regex.Append(Regex.Escape(current.ToString()));
        }
        regex.Append('$');
        var options = RegexOptions.CultureInvariant | RegexOptions.Singleline;
        if (OperatingSystem.IsWindows()) options |= RegexOptions.IgnoreCase;
        return Regex.IsMatch(Normalize(value), regex.ToString(), options, TimeSpan.FromMilliseconds(50));
    }

    private static string ExpandHome(string path)
    {
        if (path == "~") return Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (path.StartsWith("~/", StringComparison.Ordinal) || path.StartsWith("~\\", StringComparison.Ordinal))
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), path[2..]);
        return path;
    }

    private static string Normalize(string path) => path.Replace('\\', '/');

    private static StringComparer PathComparer() => OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
}
