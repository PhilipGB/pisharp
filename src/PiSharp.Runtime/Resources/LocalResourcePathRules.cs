using System.Text;
using System.Text.RegularExpressions;

namespace PiSharp.Runtime.Resources;

/// <summary>Applies Pi's scope-relative local resource path exclusions and exact overrides.</summary>
public static class LocalResourcePathRules
{
    private static readonly StringComparison s_pathComparison = OperatingSystem.IsWindows()
        ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
    private static readonly IReadOnlyDictionary<string, (string Pattern, bool Negated)> s_posixClasses =
        new Dictionary<string, (string, bool)>(StringComparer.Ordinal)
        {
            ["[:alnum:]"] = ("\\p{L}\\p{Nl}\\p{Nd}", false),
            ["[:alpha:]"] = ("\\p{L}\\p{Nl}", false),
            ["[:ascii:]"] = ("\\x00-\\x7f", false),
            ["[:blank:]"] = ("\\p{Zs}\\t", false),
            ["[:cntrl:]"] = ("\\p{Cc}", false),
            ["[:digit:]"] = ("\\p{Nd}", false),
            ["[:graph:]"] = ("\\p{Z}\\p{C}", true),
            ["[:lower:]"] = ("\\p{Ll}", false),
            ["[:print:]"] = ("\\p{C}", false),
            ["[:punct:]"] = ("\\p{P}", false),
            ["[:space:]"] = ("\\p{Z}\\t\\r\\n\\v\\f", false),
            ["[:upper:]"] = ("\\p{Lu}", false),
            ["[:word:]"] = ("\\p{L}\\p{Nl}\\p{Nd}\\p{Pc}", false),
            ["[:xdigit:]"] = ("A-Fa-f0-9", false),
        };

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
        var fullPattern = Normalize(ExpandHome(pattern));
        var relative = Normalize(Path.GetRelativePath(fullBase, path));
        var absolute = Normalize(path);
        var name = Path.GetFileName(path);
        if (GlobMatchAny([relative, name, absolute], fullPattern))
            return true;
        if (!skillPath || !Path.GetFileName(path).Equals("SKILL.md", StringComparison.Ordinal))
            return false;
        var parent = Path.GetDirectoryName(path)!;
        return GlobMatchAny([
            Normalize(Path.GetRelativePath(fullBase, parent)),
            Path.GetFileName(parent),
            Normalize(parent)
        ], fullPattern);
    }

    private static bool GlobMatchAny(IReadOnlyList<string> values, string pattern)
    {
        if (pattern.Length == 0 || pattern.Length > 4096) return false;

        var patternStart = 0;
        while (patternStart < pattern.Length && pattern[patternStart] == '!') patternStart++;
        if (patternStart == 0)
            return values.Any(value => GlobMatch(value, pattern));

        var matches = values.Any(value => GlobMatchExpanded(value, pattern[patternStart..]));
        return (patternStart % 2 != 0) != matches;
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
        if (!skillPath || !Path.GetFileName(fullPath).Equals("SKILL.md", StringComparison.Ordinal))
            return false;
        var parent = Path.GetDirectoryName(fullPath)!;
        return string.Equals(parent, target, s_pathComparison) ||
            string.Equals(Path.GetRelativePath(fullBase, parent), Normalize(expanded), s_pathComparison);
    }

    internal static bool GlobMatch(string value, string pattern)
    {
        if (pattern.Length == 0 || pattern.Length > 4096) return false;

        // Match minimatch's leading comment and negation handling. Resource settings already use
        // one leading `!` as the exclusion marker, so a second one reaches this matcher as a
        // negated minimatch pattern.
        if (pattern[0] == '#') return false;
        var negated = false;
        var patternStart = 0;
        while (patternStart < pattern.Length && pattern[patternStart] == '!')
        {
            negated = !negated;
            patternStart++;
        }
        if (patternStart > 0) pattern = pattern[patternStart..];

        var matches = ExpandBracePatterns(pattern).Any(expanded => GlobMatchExpanded(value, expanded));
        return negated != matches;
    }

    private static bool GlobMatchExpanded(string value, string pattern)
    {
        var valueSegments = Normalize(value).Split('/');
        var patternSegments = Normalize(pattern).Split('/');
        if (valueSegments.Length > 512 || patternSegments.Length > 512) return false;

        var matched = new Dictionary<(int Value, int Pattern), bool>();
        return MatchSegments(0, 0);

        bool MatchSegments(int valueIndex, int patternIndex)
        {
            if (matched.TryGetValue((valueIndex, patternIndex), out var cached)) return cached;

            bool result;
            if (patternIndex == patternSegments.Length)
            {
                result = valueIndex == valueSegments.Length;
            }
            else if (patternSegments[patternIndex] == "**")
            {
                if (patternIndex == patternSegments.Length - 1 && patternIndex > 0)
                {
                    result = valueIndex < valueSegments.Length &&
                        valueSegments.Skip(valueIndex).All(IsGlobStarMatchableSegment);
                }
                else
                {
                    result = MatchSegments(valueIndex, patternIndex + 1) ||
                        valueIndex < valueSegments.Length &&
                        IsGlobStarMatchableSegment(valueSegments[valueIndex]) &&
                        MatchSegments(valueIndex + 1, patternIndex);
                }
            }
            else if (valueIndex >= valueSegments.Length ||
                     IsHidden(valueSegments[valueIndex]) && !PatternExplicitlyMatchesDot(patternSegments[patternIndex]))
            {
                result = false;
            }
            else
            {
                result = SegmentMatch(valueSegments[valueIndex], patternSegments[patternIndex]) &&
                    MatchSegments(valueIndex + 1, patternIndex + 1);
            }

            matched[(valueIndex, patternIndex)] = result;
            return result;
        }
    }

    private static IReadOnlyList<string> ExpandBracePatterns(string pattern)
    {
        const int maximumExpansions = 256;
        var pending = new Stack<string>();
        var expandedPatterns = new HashSet<string>(StringComparer.Ordinal);
        pending.Push(pattern);
        while (pending.Count > 0)
        {
            if (pending.Count + expandedPatterns.Count > maximumExpansions) return [pattern];
            var current = pending.Pop();
            if (!TryExpandFirstBrace(current, out var alternatives))
            {
                expandedPatterns.Add(current);
                continue;
            }
            foreach (var alternative in alternatives) pending.Push(alternative);
        }
        return expandedPatterns.ToArray();
    }

    private static bool TryExpandFirstBrace(string pattern, out IReadOnlyList<string> alternatives)
    {
        for (var open = 0; open < pattern.Length; open++)
        {
            if (IsPatternEscape(pattern[open]))
            {
                open++;
                continue;
            }
            if (pattern[open] != '{') continue;
            var depth = 0;
            for (var close = open + 1; close < pattern.Length; close++)
            {
                if (IsPatternEscape(pattern[close]))
                {
                    close++;
                    continue;
                }
                if (pattern[close] == '{') depth++;
                else if (pattern[close] == '}')
                {
                    if (depth > 0)
                    {
                        depth--;
                        continue;
                    }

                    var body = pattern[(open + 1)..close];
                    var choices = SplitBraceAlternatives(body);
                    if (choices.Count < 2) choices = ExpandBraceRange(body);
                    if (choices.Count == 0) break;

                    alternatives = choices.Select(choice => pattern[..open] + choice + pattern[(close + 1)..]).ToArray();
                    return true;
                }
            }
        }

        alternatives = [];
        return false;
    }

    private static IReadOnlyList<string> SplitBraceAlternatives(string body)
    {
        var choices = new List<string>();
        var depth = 0;
        var start = 0;
        for (var index = 0; index < body.Length; index++)
        {
            if (IsPatternEscape(body[index]))
            {
                index++;
                continue;
            }
            if (body[index] == '{') depth++;
            else if (body[index] == '}') depth--;
            else if (body[index] == ',' && depth == 0)
            {
                choices.Add(body[start..index]);
                start = index + 1;
            }
        }
        if (choices.Count == 0) return [];
        choices.Add(body[start..]);
        return choices;
    }

    private static IReadOnlyList<string> ExpandBraceRange(string body)
    {
        var range = body.Split("..", StringSplitOptions.None);
        if (range.Length is < 2 or > 3) return [];
        var step = 1L;
        if (range.Length == 3)
        {
            if (!long.TryParse(range[2], System.Globalization.NumberStyles.AllowLeadingSign,
                    System.Globalization.CultureInfo.InvariantCulture, out var parsedStep)) return [];
            if (parsedStep == long.MinValue) step = long.MaxValue;
            else step = Math.Abs(parsedStep);
            if (step == 0) step = 1;
        }

        if (range[0].Length == 1 && range[1].Length == 1 && char.IsAsciiLetter(range[0][0]) &&
            char.IsAsciiLetter(range[1][0]))
        {
            var first = range[0][0];
            var last = range[1][0];
            var direction = first <= last ? 1 : -1;
            var count = Math.Abs(last - first) / step + 1;
            if (count > 256) return [];
            return Enumerable.Range(0, (int)count).Select(index => ((char)(first + direction * step * index)).ToString())
                .ToArray();
        }

        if (!int.TryParse(range[0], System.Globalization.NumberStyles.AllowLeadingSign,
                System.Globalization.CultureInfo.InvariantCulture, out var start) ||
            !int.TryParse(range[1], System.Globalization.NumberStyles.AllowLeadingSign,
                System.Globalization.CultureInfo.InvariantCulture, out var end)) return [];
        var numericDirection = start <= end ? 1 : -1;
        var distance = Math.Abs((long)end - start);
        var numericCount = distance / step + 1;
        if (numericCount > 256) return [];
        var padding = Math.Max(range[0].TrimStart('-').Length, range[1].TrimStart('-').Length);
        var padded = range[0].TrimStart('-').Length > 1 && range[0].TrimStart('-').StartsWith('0') ||
            range[1].TrimStart('-').Length > 1 && range[1].TrimStart('-').StartsWith('0');
        return Enumerable.Range(0, (int)numericCount)
            .Select(index => ((long)start + (long)numericDirection * step * index).ToString(
                padded ? $"D{padding}" : "0", System.Globalization.CultureInfo.InvariantCulture))
            .ToArray();
    }

    private static bool SegmentMatch(string value, string pattern)
    {
        var regex = new StringBuilder("^").Append(CompileSegmentPattern(pattern));
        regex.Append('$');
        var options = RegexOptions.CultureInvariant | RegexOptions.Singleline;
        if (OperatingSystem.IsWindows()) options |= RegexOptions.IgnoreCase;
        try
        {
            return Regex.IsMatch(value, regex.ToString(), options, TimeSpan.FromMilliseconds(50));
        }
        catch (RegexMatchTimeoutException)
        {
            return false;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static string CompileSegmentPattern(string pattern, int depth = 0)
    {
        const int maximumRegexLength = 16_384;
        const int maximumExtGlobDepth = 64;
        if (depth >= maximumExtGlobDepth) return Regex.Escape(pattern);

        var regex = new StringBuilder();
        for (var index = 0; index < pattern.Length; index++)
        {
            var current = pattern[index];
            if (IsPatternEscape(current))
            {
                if (index + 1 < pattern.Length) regex.Append(Regex.Escape(pattern[++index].ToString()));
                else regex.Append(Regex.Escape(current.ToString()));
                continue;
            }
            if (IsExtGlobOperator(current) && index + 1 < pattern.Length && pattern[index + 1] == '(' &&
                TryGetExtGlob(pattern, index, out var extGlobClose))
            {
                var alternatives = SplitExtGlobAlternatives(pattern[(index + 2)..extGlobClose]);
                if (alternatives.Count > 256)
                {
                    regex.Append(Regex.Escape(pattern[index..(extGlobClose + 1)]));
                    index = extGlobClose;
                    continue;
                }

                var alternativesRegex = string.Join("|", alternatives.Select(alternative =>
                    CompileSegmentPattern(alternative, depth + 1)));
                if (alternativesRegex.Length > maximumRegexLength) return Regex.Escape(pattern);
                switch (current)
                {
                    case '@': regex.Append("(?:").Append(alternativesRegex).Append(')'); break;
                    case '?': regex.Append("(?:").Append(alternativesRegex).Append(")?"); break;
                    case '*': regex.Append("(?:").Append(alternativesRegex).Append(")*"); break;
                    case '+': regex.Append("(?:").Append(alternativesRegex).Append(")+"); break;
                    case '!':
                        var suffix = CompileSegmentPattern(pattern[(extGlobClose + 1)..], depth + 1);
                        if (alternativesRegex.Length + suffix.Length * 2 > maximumRegexLength)
                            return Regex.Escape(pattern);
                        regex.Append("(?!(?:").Append(alternativesRegex).Append(")(?:")
                            .Append(suffix).Append(")$).*").Append(suffix);
                        return regex.ToString();
                }
                if (regex.Length > maximumRegexLength) return Regex.Escape(pattern);
                index = extGlobClose;
                continue;
            }

            if (current == '*')
            {
                regex.Append(".*");
            }
            else if (current == '?') regex.Append('.');
            else if (current == '[' && FindCharacterClassClose(pattern, index) is var close && close > index + 1)
            {
                regex.Append(CompileCharacterClass(pattern[(index + 1)..close]));
                index = close;
            }
            else regex.Append(Regex.Escape(current.ToString()));
        }
        return regex.Length > maximumRegexLength ? Regex.Escape(pattern) : regex.ToString();
    }

    private static bool IsExtGlobOperator(char character) => character is '@' or '+' or '?' or '*' or '!';

    private static int FindCharacterClassClose(string pattern, int open)
    {
        for (var index = open + 1; index < pattern.Length; index++)
        {
            if (IsPatternEscape(pattern[index]))
            {
                index++;
                continue;
            }
            if (pattern[index] == '[' && TryGetPosixClass(pattern, index, out _, out var consumed))
            {
                index += consumed - 1;
                continue;
            }
            if (pattern[index] == ']') return index;
        }
        return -1;
    }

    private static string CompileCharacterClass(string content)
    {
        var negate = content.Length > 0 && content[0] is '!' or '^';
        if (negate) content = content[1..];

        var ranges = new StringBuilder();
        var negatedRanges = new StringBuilder();
        for (var index = 0; index < content.Length; index++)
        {
            if (content[index] == '[' && TryGetPosixClass(content, index, out var posixClass, out var consumed))
            {
                (posixClass.Negated ? negatedRanges : ranges).Append(posixClass.Pattern);
                index += consumed - 1;
                continue;
            }

            var character = content[index];
            if (IsPatternEscape(character))
            {
                if (index + 1 < content.Length)
                {
                    character = content[++index];
                    if (character is '\\' or ']' or '^' or '-') ranges.Append('\\');
                    ranges.Append(character);
                }
                else ranges.Append("\\\\");
            }
            else
            {
                if (character is ']' or '^') ranges.Append('\\');
                ranges.Append(character);
            }
        }

        if (ranges.Length == 0 && negatedRanges.Length == 0) return "(?!)";
        if (negatedRanges.Length == 0)
            return "[" + (negate ? "^" : "") + ranges + "]";
        if (ranges.Length == 0)
            return "[" + (negate ? "" : "^") + negatedRanges + "]";
        return "(?:[" + (negate ? "^" : "") + ranges + "]|[" + (negate ? "" : "^") + negatedRanges + "])";
    }

    private static bool TryGetPosixClass(string value, int start,
        out (string Pattern, bool Negated) posixClass, out int consumed)
    {
        posixClass = default;
        consumed = 0;
        if (start + 1 >= value.Length || value[start] != '[' || value[start + 1] != ':') return false;
        var close = value.IndexOf(":]", start + 2, StringComparison.Ordinal);
        if (close < 0 || !s_posixClasses.TryGetValue(value[start..(close + 2)], out posixClass)) return false;
        consumed = close + 2 - start;
        return true;
    }

    private static bool TryGetExtGlob(string pattern, int operatorIndex, out int close)
    {
        var depth = 0;
        var inCharacterClass = false;
        for (var index = operatorIndex + 2; index < pattern.Length; index++)
        {
            if (IsPatternEscape(pattern[index]))
            {
                index++;
                continue;
            }
            if (pattern[index] == '[') inCharacterClass = true;
            else if (pattern[index] == ']' && inCharacterClass) inCharacterClass = false;
            else if (!inCharacterClass && IsExtGlobOperator(pattern[index]) &&
                index + 1 < pattern.Length && pattern[index + 1] == '(')
            {
                depth++;
                index++;
            }
            else if (!inCharacterClass && pattern[index] == ')')
            {
                if (depth == 0)
                {
                    close = index;
                    return true;
                }
                depth--;
            }
        }

        close = -1;
        return false;
    }

    private static IReadOnlyList<string> SplitExtGlobAlternatives(string pattern)
    {
        var alternatives = new List<string>();
        var start = 0;
        var depth = 0;
        var inCharacterClass = false;
        for (var index = 0; index < pattern.Length; index++)
        {
            if (IsPatternEscape(pattern[index]))
            {
                index++;
                continue;
            }
            if (pattern[index] == '[') inCharacterClass = true;
            else if (pattern[index] == ']' && inCharacterClass) inCharacterClass = false;
            else if (!inCharacterClass && IsExtGlobOperator(pattern[index]) &&
                index + 1 < pattern.Length && pattern[index + 1] == '(')
            {
                depth++;
                index++;
            }
            else if (!inCharacterClass && pattern[index] == ')') depth--;
            else if (!inCharacterClass && depth == 0 && pattern[index] == '|')
            {
                alternatives.Add(pattern[start..index]);
                start = index + 1;
            }
        }

        alternatives.Add(pattern[start..]);
        return alternatives;
    }

    private static bool IsHidden(string segment) => segment.Length > 0 && segment[0] == '.';

    private static bool IsGlobStarMatchableSegment(string segment) =>
        segment is not ("." or "..") && !IsHidden(segment);

    private static bool PatternExplicitlyMatchesDot(string pattern)
    {
        if (pattern.Length > 0 && pattern[0] == '.') return true;
        if (pattern.Length > 1 && IsPatternEscape(pattern[0]) && pattern[1] == '.') return true;
        if (pattern.Length > 2 && IsExtGlobOperator(pattern[0]) && pattern[1] == '(' &&
            TryGetExtGlob(pattern, 0, out var extGlobClose) && pattern[0] is ('@' or '+' or '?'))
        {
            return SplitExtGlobAlternatives(pattern[2..extGlobClose])
                .Any(PatternExplicitlyMatchesDot);
        }
        if (pattern.Length < 3 || pattern[0] != '[') return false;
        var close = FindCharacterClassClose(pattern, 0);
        if (close < 0) return false;

        var content = pattern[1..close];
        return content == "." || content.Length == 2 && IsPatternEscape(content[0]) && content[1] == '.';
    }

    private static string ExpandHome(string path)
    {
        if (path == "~") return Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (path.StartsWith("~/", StringComparison.Ordinal) || path.StartsWith("~\\", StringComparison.Ordinal))
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), path[2..]);
        return path;
    }

    private static bool IsPatternEscape(char character) => !OperatingSystem.IsWindows() && character == '\\';

    private static string Normalize(string path) => OperatingSystem.IsWindows() ? path.Replace('\\', '/') : path;

    private static StringComparer PathComparer() => OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
}
