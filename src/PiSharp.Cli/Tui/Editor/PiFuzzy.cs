using System.Text.RegularExpressions;

namespace PiSharp.Cli.Tui;

/// <summary>Port of Pi's packages/tui/src/fuzzy.ts matching and ordering.</summary>
internal static class PiFuzzy
{
    private static readonly Regex s_alphaNumeric = new("^(?<letters>[a-z]+)(?<digits>[0-9]+)$",
        RegexOptions.CultureInvariant);
    private static readonly Regex s_numericAlpha = new("^(?<digits>[0-9]+)(?<letters>[a-z]+)$",
        RegexOptions.CultureInvariant);
    private static readonly char[] s_delimiters = [' ', '-', '_', '.', '/', ':'];

    private readonly record struct Match(bool Matches, double Score);

    public static IReadOnlyList<T> Filter<T>(IReadOnlyList<T> items, string query, Func<T, string> getText)
    {
        if (string.IsNullOrWhiteSpace(query)) return items;
        var tokens = query.Trim().Split([' ', '\t', '\n', '\r', '/'], StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length == 0) return items;
        var results = new List<(T Item, double Score)>();
        foreach (var item in items)
        {
            var text = getText(item);
            var total = 0d;
            var all = true;
            foreach (var token in tokens)
            {
                var match = MatchToken(token, text);
                if (!match.Matches)
                {
                    all = false;
                    break;
                }
                total += match.Score;
            }
            if (all) results.Add((item, total));
        }
        // Enumerable.OrderBy is stable, matching modern Array.prototype.sort.
        return results.OrderBy(result => result.Score).Select(result => result.Item).ToArray();
    }

    private static Match MatchToken(string query, string text)
    {
        var queryLower = query.ToLowerInvariant();
        var textLower = text.ToLowerInvariant();
        var primary = Score(queryLower, textLower);
        if (primary.Matches) return primary;
        var alphaNumeric = s_alphaNumeric.Match(queryLower);
        var numericAlpha = s_numericAlpha.Match(queryLower);
        var swapped = alphaNumeric.Success
            ? alphaNumeric.Groups["digits"].Value + alphaNumeric.Groups["letters"].Value
            : numericAlpha.Success
                ? numericAlpha.Groups["letters"].Value + numericAlpha.Groups["digits"].Value
                : "";
        if (swapped.Length == 0) return primary;
        var swappedMatch = Score(swapped, textLower);
        return swappedMatch.Matches ? new(true, swappedMatch.Score + 5) : primary;
    }

    private static Match Score(string query, string text)
    {
        if (query.Length == 0) return new(true, 0);
        if (query.Length > text.Length) return new(false, 0);
        var queryIndex = 0;
        var score = 0d;
        var lastMatchIndex = -1;
        var consecutiveMatches = 0;
        while (queryIndex < query.Length)
        {
            var index = text.IndexOf(query[queryIndex], lastMatchIndex + 1);
            if (index < 0) break;
            var isWordBoundary = index == 0 || Array.IndexOf(s_delimiters, text[index - 1]) >= 0;
            if (lastMatchIndex == index - 1)
            {
                consecutiveMatches++;
                score -= consecutiveMatches * 5;
            }
            else
            {
                consecutiveMatches = 0;
                if (lastMatchIndex >= 0) score += (index - lastMatchIndex - 1) * 2;
            }
            if (isWordBoundary) score -= 10;
            score += index * 0.1;
            lastMatchIndex = index;
            queryIndex++;
        }
        if (queryIndex < query.Length) return new(false, 0);
        if (string.Equals(query, text, StringComparison.Ordinal)) score -= 100;
        return new(true, score);
    }
}
