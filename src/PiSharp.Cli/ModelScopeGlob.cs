namespace PiSharp.Cli;

/// <summary>Bounded subset of minimatch globs: stars, globstars, single-character wildcards and bracket ranges.</summary>
public static class ModelScopeGlob
{
    public static bool Matches(string pattern, string value)
    {
        if (pattern.Length > 512 || value.Length > 2048) return false;
        var memo = new Dictionary<(int Pattern, int Value), bool>();
        return Match(0, 0);

        bool Match(int p, int v)
        {
            if (memo.TryGetValue((p, v), out var cached)) return cached;
            bool matched;
            if (p == pattern.Length) matched = v == value.Length;
            else if (pattern[p] == '*')
            {
                var next = p;
                while (next < pattern.Length && pattern[next] == '*') next++;
                var globstar = next - p >= 2 && (p == 0 || pattern[p - 1] == '/') &&
                    (next == pattern.Length || pattern[next] == '/');
                var canConsume = v < value.Length && !(value[v] == '.' && (v == 0 || value[v - 1] == '/'));
                if (globstar && next < pattern.Length && pattern[next] == '/')
                    matched = Match(next + 1, v) || canConsume && Match(p, v + 1);
                else matched = Match(next, v) || canConsume && (globstar || value[v] != '/') && Match(p, v + 1);
            }
            else
            {
                var next = p + 1;
                bool fits = false;
                if (v < value.Length)
                {
                    if (pattern[p] == '[' && TryClass(p, out next, out var chars)) fits = value[v] != '/' && chars(value[v]);
                    else if (pattern[p] == '?') fits = value[v] != '/' && !(value[v] == '.' && (v == 0 || value[v - 1] == '/'));
                    else fits = char.ToUpperInvariant(pattern[p]) == char.ToUpperInvariant(value[v]);
                }
                matched = fits && Match(next, v + 1);
            }
            memo[(p, v)] = matched;
            return matched;
        }

        bool TryClass(int start, out int next, out Func<char, bool> includes)
        {
            next = pattern.IndexOf(']', start + 1);
            includes = _ => false;
            if (next <= start + 1) return false;
            var end = next++;
            var negated = pattern[start + 1] is '!' or '^';
            var first = start + (negated ? 2 : 1);
            if (first == end) return false;
            includes = c =>
            {
                var normalized = char.ToUpperInvariant(c);
                var found = false;
                for (var i = first; i < end; i++)
                {
                    var low = char.ToUpperInvariant(pattern[i]);
                    if (i + 2 < end && pattern[i + 1] == '-')
                    {
                        var high = char.ToUpperInvariant(pattern[i + 2]);
                        if (normalized >= low && normalized <= high) found = true;
                        i += 2;
                    }
                    else if (normalized == low) found = true;
                }
                return negated != found;
            };
            return true;
        }
    }
}
