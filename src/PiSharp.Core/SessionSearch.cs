using System.Text.RegularExpressions;

namespace PiSharp.Core;

/// <summary>Picker sort mode (pinned session-selector-search.ts <c>SortMode</c>).</summary>
public enum SessionSortMode
{
    /// <summary>Default: session tree by parentSessionPath (threaded), flat with a query.</summary>
    Threaded,

    /// <summary>Input order (most recently modified first) with filtering only.</summary>
    Recent,

    /// <summary>Sorted by match score (lower is better), ties by modified descending.</summary>
    Relevance,
}

/// <summary>Picker name filter (pinned <c>NameFilter</c>).</summary>
public enum SessionNameFilter
{
    All,
    Named,
}

/// <summary>A parsed picker search query (pinned <c>ParsedSearchQuery</c>).</summary>
public sealed record ParsedSessionQuery(bool IsRegex, Regex? Regex, IReadOnlyList<QueryToken> Tokens, string? Error)
{
    /// <summary>True when parsing failed and the query should be treated as non-matching.</summary>
    public bool HasError => Error is not null;
}

/// <summary>One search token: a fuzzy term or a whitespace-normalized exact phrase.</summary>
public readonly record struct QueryToken(bool IsPhrase, string Value);

/// <summary>
/// One flattened tree node for threaded display (pinned <c>FlatSessionNode</c>).
/// </summary>
/// <param name="Session">The session entry.</param>
/// <param name="Depth">Tree depth (roots are 0).</param>
/// <param name="IsLast">Whether the node is the last sibling at its depth.</param>
/// <param name="AncestorContinues">For each ancestor level, whether that ancestor has later siblings.</param>
public sealed record FlatSessionNode(
    PiSessionInfo Session,
    int Depth,
    bool IsLast,
    IReadOnlyList<bool> AncestorContinues);

/// <summary>
/// Port of the pinned session-selector-search.ts: query parsing (tokens, quoted phrases,
/// <c>re:</c> regex), fuzzy scoring, and the three sort modes, plus the pinned
/// session-selector.ts buildSessionTree/flattenSessionTree threading.
/// </summary>
public static class SessionSearch
{
    /// <summary>
    /// Pinned getSessionSearchText: the searchable text is the id, name, all message text,
    /// and the working directory joined by spaces.
    /// </summary>
    public static string GetSearchText(PiSessionInfo session) =>
        $"{session.Id} {session.Name ?? string.Empty} {session.AllMessagesText} {session.Cwd}";

    /// <summary>Pinned hasSessionName: a non-blank name.</summary>
    public static bool HasSessionName(PiSessionInfo session) =>
        !string.IsNullOrWhiteSpace(session.Name);

    /// <summary>
    /// Pinned parseSearchQuery: <c>re:</c> prefix switches to case-insensitive regex mode
    /// (an invalid pattern is an error that makes the query match nothing); otherwise the
    /// query is whitespace-separated fuzzy tokens with double-quoted phrases. Unbalanced
    /// quotes fall back to plain whitespace tokenization.
    /// </summary>
    public static ParsedSessionQuery ParseQuery(string query)
    {
        var trimmed = query.Trim();
        if (trimmed.Length == 0)
        {
            return new ParsedSessionQuery(false, null, [], null);
        }

        const string regexPrefix = "re:";
        if (trimmed.StartsWith(regexPrefix, StringComparison.Ordinal))
        {
            var pattern = trimmed[regexPrefix.Length..].Trim();
            if (pattern.Length == 0)
            {
                return new ParsedSessionQuery(true, null, [], "Empty regex");
            }

            try
            {
                return new ParsedSessionQuery(true, new Regex(pattern, RegexOptions.IgnoreCase), [], null);
            }
            catch (ArgumentException ex)
            {
                return new ParsedSessionQuery(true, null, [], ex.Message);
            }
        }

        var tokens = new List<QueryToken>();
        var buffer = string.Empty;
        var inQuote = false;
        var hadUnclosedQuote = false;

        void Flush(bool isPhrase)
        {
            var value = buffer.Trim();
            buffer = string.Empty;
            if (value.Length > 0)
            {
                tokens.Add(new QueryToken(isPhrase, value));
            }
        }

        for (var i = 0; i < trimmed.Length; i++)
        {
            var ch = trimmed[i];
            if (ch == '"')
            {
                if (inQuote)
                {
                    Flush(isPhrase: true);
                    inQuote = false;
                }
                else
                {
                    Flush(isPhrase: false);
                    inQuote = true;
                }

                continue;
            }

            if (!inQuote && char.IsWhiteSpace(ch))
            {
                Flush(isPhrase: false);
                continue;
            }

            buffer += ch;
        }

        if (inQuote)
        {
            hadUnclosedQuote = true;
        }

        // Pinned: unbalanced quotes fall back to plain whitespace tokenization (quotes included).
        if (hadUnclosedQuote)
        {
            var plain = trimmed
                .Split(WhiteSpaceSeparator, StringSplitOptions.RemoveEmptyEntries)
                .Select(value => new QueryToken(false, value))
                .ToArray();
            return new ParsedSessionQuery(false, null, plain, null);
        }

        Flush(isPhrase: false);
        return new ParsedSessionQuery(false, null, tokens, null);
    }

    private static readonly string[] WhiteSpaceSeparator = [" ", "\t", "\n", "\r"];

    /// <summary>
    /// Pinned matchSession: regex mode scores the first match index × 0.1; token mode
    /// requires every phrase (whitespace-normalized case-insensitive substring) and every
    /// fuzzy token to match, summing their scores. Lower is better.
    /// </summary>
    public static (bool Matches, double Score) MatchSession(PiSessionInfo session, ParsedSessionQuery query)
    {
        var text = GetSearchText(session);

        if (query.IsRegex)
        {
            if (query.Regex is null)
            {
                return (false, 0);
            }

            var match = query.Regex.Match(text);
            if (!match.Success)
            {
                return (false, 0);
            }

            return (true, match.Index * 0.1);
        }

        if (query.Tokens.Count == 0)
        {
            return (true, 0);
        }

        var totalScore = 0.0;
        string? normalizedText = null;

        foreach (var token in query.Tokens)
        {
            if (token.IsPhrase)
            {
                normalizedText ??= NormalizeWhitespaceLower(text);
                var phrase = NormalizeWhitespaceLower(token.Value);
                if (phrase.Length == 0)
                {
                    continue;
                }

                var index = normalizedText.IndexOf(phrase, StringComparison.Ordinal);
                if (index < 0)
                {
                    return (false, 0);
                }

                totalScore += index * 0.1;
                continue;
            }

            var fuzzy = FuzzyMatch(token.Value, text);
            if (!fuzzy.Matches)
            {
                return (false, 0);
            }

            totalScore += fuzzy.Score;
        }

        return (true, totalScore);
    }

    /// <summary>
    /// Pinned filterAndSortSessions: filters by the name filter and the parsed query.
    /// Recent keeps input order; relevance (and a queried threaded view) sorts by score
    /// with modified-descending tie-break. The query-less threaded tree view is
    /// <see cref="BuildThreadedTree"/> (the pinned selector chooses between the two).
    /// </summary>
    public static IReadOnlyList<PiSessionInfo> FilterAndSort(
        IReadOnlyList<PiSessionInfo> sessions,
        string query,
        SessionSortMode sortMode,
        SessionNameFilter nameFilter = SessionNameFilter.All)
    {
        var nameFiltered = nameFilter == SessionNameFilter.Named
            ? sessions.Where(HasSessionName).ToList()
            : sessions.ToList();

        var trimmed = query.Trim();
        if (trimmed.Length == 0)
        {
            return nameFiltered;
        }

        var parsed = ParseQuery(query);
        if (parsed.HasError)
        {
            return [];
        }

        if (sortMode == SessionSortMode.Recent)
        {
            return nameFiltered
                .Where(session => MatchSession(session, parsed).Matches)
                .ToList();
        }

        var scored = nameFiltered
            .Select(session => (session, Score: MatchSession(session, parsed)))
            .Where(result => result.Score.Matches)
            .ToList();

        return scored
            .OrderBy(result => result.Score.Score)
            .ThenByDescending(result => result.session.Modified)
            .Select(result => result.session)
            .ToList();
    }

    /// <summary>
    /// Pinned buildSessionTree + flattenSessionTree: groups sessions by canonical
    /// parentSessionPath, orders each level by the latest activity in the subtree
    /// (descending), and flattens pre-order with tree-drawing metadata.
    /// </summary>
    public static IReadOnlyList<FlatSessionNode> BuildThreadedTree(IReadOnlyList<PiSessionInfo> sessions)
    {
        var byPath = new Dictionary<string, (PiSessionInfo Session, List<string> Children, long LatestActivity)>();
        var roots = new List<string>();

        foreach (var session in sessions)
        {
            var sessionPath = Canonicalize(session.Path);
            if (!byPath.ContainsKey(sessionPath))
            {
                byPath[sessionPath] = (session, [], session.Modified.ToUnixTimeMilliseconds());
            }
        }

        foreach (var session in sessions)
        {
            var sessionPath = Canonicalize(session.Path);
            var parentPath = session.ParentSessionPath is null
                ? null
                : Canonicalize(session.ParentSessionPath);

            if (parentPath is not null && byPath.ContainsKey(parentPath))
            {
                var children = byPath[parentPath].Children.ToList();
                children.Add(sessionPath);
                byPath[parentPath] = (byPath[parentPath].Session, children, byPath[parentPath].LatestActivity);
            }
            else
            {
                roots.Add(sessionPath);
            }
        }

        long UpdateLatestActivity(string path)
        {
            var (session, children, _) = byPath[path];
            var latest = session.Modified.ToUnixTimeMilliseconds();
            foreach (var childPath in children)
            {
                latest = Math.Max(latest, UpdateLatestActivity(childPath));
            }

            byPath[path] = (session, children, latest);
            return latest;
        }

        foreach (var root in roots)
        {
            UpdateLatestActivity(root);
        }

        void SortNodes(List<string> paths)
        {
            paths.Sort((a, b) => byPath[b].LatestActivity.CompareTo(byPath[a].LatestActivity));
            foreach (var path in paths.ToList())
            {
                // Sort the stored child list in place so the walk sees the order.
                SortNodes(byPath[path].Children);
            }
        }

        var rootList = roots.ToList();
        SortNodes(rootList);

        var result = new List<FlatSessionNode>();
        void Walk(string path, int depth, List<bool> ancestorContinues, bool isLast)
        {
            result.Add(new FlatSessionNode(byPath[path].Session, depth, isLast, ancestorContinues.ToArray()));
            var children = byPath[path].Children.ToList();
            for (var i = 0; i < children.Count; i++)
            {
                var childIsLast = i == children.Count - 1;
                // Pinned: only non-root ancestors contribute a continuation marker.
                var continues = depth > 0 && !isLast;
                Walk(children[i], depth + 1, ancestorContinues.Append(continues).ToList(), childIsLast);
            }
        }

        for (var i = 0; i < rootList.Count; i++)
        {
            Walk(rootList[i], 0, [], i == rootList.Count - 1);
        }

        return result;
    }

    /// <summary>
    /// Path canonicalization for tree grouping. The pinned code resolves symlinks via
    /// fs.realpath; PiSharp uses full-path normalization, which groups identically for the
    /// common non-symlink case (documented simplification).
    /// </summary>
    private static string Canonicalize(string path) => Path.GetFullPath(path);

    /// <summary>Pinned normalizeWhitespaceLower.</summary>
    private static string NormalizeWhitespaceLower(string text)
    {
        var lower = text.ToLowerInvariant();
        var builder = new System.Text.StringBuilder(lower.Length);
        var inGap = true;
        foreach (var ch in lower)
        {
            if (char.IsWhiteSpace(ch))
            {
                if (!inGap && builder.Length > 0)
                {
                    builder.Append(' ');
                }

                inGap = true;
            }
            else
            {
                builder.Append(ch);
                inGap = false;
            }
        }

        return builder.ToString().Trim();
    }

    /// <summary>
    /// Pinned fuzzyMatch (pi-tui fuzzy.ts): every query character must appear in order in
    /// the text; consecutive runs are rewarded (−5 each, growing), gaps penalized (+2 per
    /// skipped char), word-boundary starts rewarded (−10), later positions penalized
    /// (×0.1), an exact whole-text match gets −100, and letter/digit order swaps retry
    /// with +5. Lower score is better.
    /// </summary>
    public static (bool Matches, double Score) FuzzyMatch(string query, string text)
    {
        var queryLower = query.ToLowerInvariant();
        var textLower = text.ToLowerInvariant();

        (bool Matches, double Score) MatchQuery(string normalizedQuery)
        {
            if (normalizedQuery.Length == 0)
            {
                return (true, 0);
            }

            if (normalizedQuery.Length > textLower.Length)
            {
                return (false, 0);
            }

            var queryIndex = 0;
            var score = 0.0;
            var lastMatchIndex = -1;
            var consecutiveMatches = 0;

            for (var i = 0; i < textLower.Length && queryIndex < normalizedQuery.Length; i++)
            {
                if (textLower[i] != normalizedQuery[queryIndex])
                {
                    continue;
                }

                // Pinned /[/\s\-_./:]/: whitespace plus - _ . / :.
                var isWordBoundary = i == 0 || char.IsWhiteSpace(textLower[i - 1]) ||
                                     textLower[i - 1] is '-' or '_' or '.' or '/' or ':';

                // Reward consecutive matches.
                if (lastMatchIndex == i - 1)
                {
                    consecutiveMatches++;
                    score -= consecutiveMatches * 5;
                }
                else
                {
                    consecutiveMatches = 0;
                    // Penalize gaps.
                    if (lastMatchIndex >= 0)
                    {
                        score += (i - lastMatchIndex - 1) * 2;
                    }
                }

                // Reward word-boundary matches.
                if (isWordBoundary)
                {
                    score -= 10;
                }

                // Slight penalty for later matches.
                score += i * 0.1;
                lastMatchIndex = i;
                queryIndex++;
            }

            if (queryIndex < normalizedQuery.Length)
            {
                return (false, 0);
            }

            if (normalizedQuery == textLower)
            {
                score -= 100;
            }

            return (true, score);
        }

        var primary = MatchQuery(queryLower);
        if (primary.Matches)
        {
            return primary;
        }

        // Retry with letters/digits swapped when the query is a pure letter+digit (or
        // digit+letter) run, penalized by +5.
        var swappedQuery = SwapLettersAndDigits(queryLower);
        if (swappedQuery.Length == 0)
        {
            return primary;
        }

        var swapped = MatchQuery(swappedQuery);
        if (!swapped.Matches)
        {
            return primary;
        }

        return (true, swapped.Score + 5);
    }

    /// <summary>
    /// Pinned swap derivation: <c>abc123</c> → <c>123abc</c> and <c>123abc</c> → <c>abc123</c>;
    /// anything else yields no retry.
    /// </summary>
    private static string SwapLettersAndDigits(string queryLower)
    {
        if (queryLower.Length < 2)
        {
            return string.Empty;
        }

        // Pinned ^([a-z]+)([0-9]+)$: letters then digits → digits then letters.
        var splitAt = 0;
        while (splitAt < queryLower.Length && IsAsciiLower(queryLower[splitAt]))
        {
            splitAt++;
        }

        if (splitAt > 0 && splitAt < queryLower.Length && queryLower[splitAt..].All(IsAsciiDigit))
        {
            return queryLower[splitAt..] + queryLower[..splitAt];
        }

        // Pinned ^([0-9]+)([a-z]+)$: digits then letters → letters then digits.
        splitAt = 0;
        while (splitAt < queryLower.Length && IsAsciiDigit(queryLower[splitAt]))
        {
            splitAt++;
        }

        if (splitAt > 0 && splitAt < queryLower.Length && queryLower[splitAt..].All(IsAsciiLower))
        {
            return queryLower[splitAt..] + queryLower[..splitAt];
        }

        return string.Empty;
    }

    private static bool IsAsciiLower(char c) => c is >= 'a' and <= 'z';

    private static bool IsAsciiDigit(char c) => c is >= '0' and <= '9';
}
