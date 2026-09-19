using PiSharp.Core;

namespace PiSharp.Core.Tests;

/// <summary>
/// Pinned session-selector-search.test.ts conformance: phrase/regex/token parsing,
/// fuzzy scoring, sort modes, the name filter, and the threaded tree layout.
/// </summary>
public sealed class SessionSearchTests
{
    private static PiSessionInfo Session(
        string id,
        string allMessages,
        string? name = null,
        DateTimeOffset? modified = null,
        string cwd = "/work",
        string? parentSessionPath = null) =>
        new(
            Path.Combine("/sessions", id + ".jsonl"),
            id,
            cwd,
            name,
            parentSessionPath,
            DateTimeOffset.UtcNow.AddHours(-2),
            modified ?? DateTimeOffset.UtcNow.AddHours(-1),
            allMessages.Split(' ').Length,
            allMessages,
            allMessages);

    [Fact]
    public void FiltersByQuotedPhraseWithWhitespaceNormalization()
    {
        var hit = Session("s1", "found the node   cve issue");
        var miss = Session("s2", "found a different issue");
        var query = SessionSearch.ParseQuery("node cve");
        var phraseQuery = SessionSearch.ParseQuery("\"node   cve\"");

        // Unquoted: fuzzy tokens; both tokens must match somewhere.
        Assert.True(SessionSearch.MatchSession(hit, query).Matches);
        Assert.False(SessionSearch.MatchSession(miss, query).Matches);

        // Quoted: whitespace-normalized case-insensitive substring.
        Assert.Single(phraseQuery.Tokens);
        Assert.True(phraseQuery.Tokens[0].IsPhrase);
        Assert.True(SessionSearch.MatchSession(hit, phraseQuery).Matches);
        Assert.False(SessionSearch.MatchSession(miss, phraseQuery).Matches);
    }

    [Fact]
    public void RegexModeIsCaseInsensitiveAndScoresTheFirstMatchIndex()
    {
        var session = Session("myproj", "body text", name: "My Project");
        var query = SessionSearch.ParseQuery("re:MYPROJ");

        Assert.True(query.IsRegex);
        Assert.False(query.HasError);
        var result = SessionSearch.MatchSession(session, query);
        Assert.True(result.Matches);
        Assert.Equal(0, result.Score); // text starts with the id

        var anchored = SessionSearch.ParseQuery("re:^body");
        Assert.False(SessionSearch.MatchSession(session, anchored).Matches);
    }

    [Fact]
    public void InvalidOrEmptyRegexMatchesNothing()
    {
        var session = Session("s1", "anything");

        Assert.True(SessionSearch.ParseQuery("re:(").HasError);
        Assert.Empty(SessionSearch.FilterAndSort([session], "re:(", SessionSortMode.Relevance));

        Assert.True(SessionSearch.ParseQuery("re:").HasError);
        Assert.Empty(SessionSearch.FilterAndSort([session], "re:", SessionSortMode.Relevance));
    }

    [Fact]
    public void UnbalancedQuotesFallBackToPlainWhitespaceTokens()
    {
        var query = SessionSearch.ParseQuery("foo \"bar");

        Assert.Equal(2, query.Tokens.Count);
        Assert.DoesNotContain(query.Tokens, token => token.IsPhrase);
        Assert.Equal("foo", query.Tokens[0].Value);
        Assert.Equal("\"bar", query.Tokens[1].Value);
    }

    [Fact]
    public void RecentSortPreservesInputOrder()
    {
        var a = Session("a", "alpha text");
        var b = Session("b", "alpha beta text");
        var c = Session("c", "gamma text");

        var result = SessionSearch.FilterAndSort([a, b, c], "alpha", SessionSortMode.Recent);

        Assert.Equal([a, b], result);
    }

    [Fact]
    public void RelevanceSortOrdersByScoreWithModifiedTieBreak()
    {
        var later = Session("aaa", "alpha beta"); // identical text → identical score
        var older = later with { Modified = later.Modified.AddHours(-5) };

        var result = SessionSearch.FilterAndSort([older, later], "alpha", SessionSortMode.Relevance);

        // Tie on score: most recently modified first.
        Assert.Equal([later, older], result);

        // A stronger (lower) fuzzy score beats a weaker one regardless of order.
        var direct = Session("direct", "alpha");
        var buried = Session("buried", "x y z w alpha");
        var ordered = SessionSearch.FilterAndSort([buried, direct], "alpha", SessionSortMode.Relevance);
        Assert.Equal([direct, buried], ordered);
    }

    [Fact]
    public void NameFilterBehavior()
    {
        var named = Session("n1", "hello", name: "Named Session");
        var blank = Session("n2", "hello", name: "   ");
        var unnamed = Session("n3", "hello");

        Assert.Equal([named], SessionSearch.FilterAndSort([named, blank, unnamed], "", SessionSortMode.Recent, SessionNameFilter.Named));
        Assert.Equal([named, blank, unnamed], SessionSearch.FilterAndSort([named, blank, unnamed], "", SessionSortMode.Recent, SessionNameFilter.All));

        // The name filter applies before the query: the unnamed match is excluded.
        var filtered = SessionSearch.FilterAndSort([named, unnamed], "hello", SessionSortMode.Recent, SessionNameFilter.Named);
        Assert.Equal([named], filtered);
    }

    // --- fuzzy scoring (pinned fuzzy.ts semantics, hand-computed) --------------

    [Fact]
    public void FuzzyMatchRewardsConsecutiveRunsAndExactText()
    {
        // "ab" in "ab": first char at index 0 gets boundary -10 AND the pinned consecutive
        // run quirk (lastMatchIndex -1 == i - 1) -5, second char run -10, position +0.1,
        // exact -100.
        var exact = SessionSearch.FuzzyMatch("ab", "ab");
        Assert.True(exact.Matches);
        Assert.Equal(-124.9, exact.Score, 3);

        // "ac" in "abc": boundary -10, index-0 run quirk -5, gap +2, position +0.2.
        var gap = SessionSearch.FuzzyMatch("ac", "abc");
        Assert.True(gap.Matches);
        Assert.Equal(-12.8, gap.Score, 3);

        var none = SessionSearch.FuzzyMatch("z", "abc");
        Assert.False(none.Matches);
        Assert.Equal(0, none.Score);
    }

    [Fact]
    public void FuzzyMatchRewardsWordBoundaries()
    {
        // 'b' at index 2 after '-': boundary -10, position +0.2.
        var result = SessionSearch.FuzzyMatch("b", "a-b");
        Assert.True(result.Matches);
        Assert.Equal(-9.8, result.Score, 3);
    }

    [Fact]
    public void FuzzyMatchRetriesLetterDigitSwapsWithAPenalty()
    {
        // "abc123" cannot match "123abc" directly; the swapped query "123abc" matches
        // exactly (-213.5 including the index-0 run quirk) and the swap adds +5.
        var result = SessionSearch.FuzzyMatch("abc123", "123abc");
        Assert.True(result.Matches);
        Assert.Equal(-208.5, result.Score, 3);
    }

    // --- threaded tree (pinned buildSessionTree/flattenSessionTree) ------------

    [Fact]
    public void ThreadedTreeGroupsByParentSessionPathAndOrdersBySubtreeActivity()
    {
        var t0 = DateTimeOffset.UtcNow.AddHours(-10);
        var rootOld = Session("root-old", "old root", modified: t0);
        var rootNew = Session("root-new", "new root", modified: t0.AddHours(2));
        var childA = Session("child-a", "child a", modified: t0.AddHours(1), parentSessionPath: rootOld.Path);
        var childB = Session("child-b", "child b", modified: t0.AddHours(4), parentSessionPath: rootOld.Path);

        // rootOld's subtree is active until t0+4h, beating rootNew (t0+2h).
        var tree = SessionSearch.BuildThreadedTree([rootNew, childA, rootOld, childB]);

        Assert.Equal(["root-old", "child-b", "child-a", "root-new"], tree.Select(node => node.Session.Id).ToArray());

        Assert.Equal(0, tree[0].Depth);
        Assert.False(tree[0].IsLast);
        Assert.Equal(1, tree[1].Depth);
        Assert.False(tree[1].IsLast);
        Assert.Single(tree[1].AncestorContinues);
        // The root ancestor (depth 0) never contributes a continuation marker.
        Assert.False(tree[1].AncestorContinues[0]);
        Assert.True(tree[2].IsLast);
        Assert.True(tree[3].IsLast);
    }

    [Fact]
    public void ThreadedTreeTreatsMissingParentsAsRoots()
    {
        var orphan = Session("orphan", "orphan text", parentSessionPath: "/sessions/missing.jsonl");
        var root = Session("root", "root text");

        var tree = SessionSearch.BuildThreadedTree([orphan, root]);

        Assert.All(tree, node => Assert.Equal(0, node.Depth));
        Assert.Equal(2, tree.Count);
    }

    // --- delete (pinned deleteSessionFile) --------------------------------------

    [Fact]
    public async Task DeleteSessionFallsBackToUnlinkWhenTrashFails()
    {
        using var temp = TempDirectory.Create();
        var store = new SessionStore(temp.Path);
        var path = Path.Combine(temp.Path, "session.jsonl");
        await File.WriteAllTextAsync(path, "data");
        store.TrashLauncher = (_, _) => Task.FromResult<(int, string?)>((1, "trash: not installed"));
        store.DeleteFileOverride = real => { File.Delete(real); return true; };

        var result = await store.DeleteSessionAsync(path);

        Assert.True(result.Ok);
        Assert.Equal("unlink", result.Method);
        Assert.False(File.Exists(path));
    }

    [Fact]
    public async Task DeleteSessionTrustsATrashingZeroExitCode()
    {
        using var temp = TempDirectory.Create();
        var store = new SessionStore(temp.Path);
        var path = Path.Combine(temp.Path, "session.jsonl");
        await File.WriteAllTextAsync(path, "data");
        store.TrashLauncher = (_, _) => Task.FromResult<(int, string?)>((0, null));

        var result = await store.DeleteSessionAsync(path);

        Assert.True(result.Ok);
        Assert.Equal("trash", result.Method);
        Assert.Null(result.Error);
    }

    [Fact]
    public async Task DeleteSessionSurfacesTheErrorWhenBothMethodsFail()
    {
        using var temp = TempDirectory.Create();
        var store = new SessionStore(temp.Path);
        var path = Path.Combine(temp.Path, "session.jsonl");
        await File.WriteAllTextAsync(path, "data");
        store.TrashLauncher = (_, _) => Task.FromResult<(int, string?)>((1, "boom\nsecond line"));
        store.DeleteFileOverride = _ => false;

        var result = await store.DeleteSessionAsync(path);

        Assert.False(result.Ok);
        Assert.Equal("unlink", result.Method);
        Assert.Contains("boom", result.Error);
        Assert.DoesNotContain("second line", result.Error);
    }

    [Fact]
    public async Task DeleteSessionCancelledDuringTrashPropagatesAndKeepsTheFile()
    {
        using var temp = TempDirectory.Create();
        var store = new SessionStore(temp.Path);
        var path = Path.Combine(temp.Path, "session.jsonl");
        await File.WriteAllTextAsync(path, "data");

        // The trash step is cancelled through the caller's token mid-flight.
        var cts = new CancellationTokenSource();
        cts.Cancel();
        var permanentDeleteCalls = 0;
        store.TrashLauncher = (_, token) =>
        {
            token.ThrowIfCancellationRequested();
            return Task.FromResult<(int, string?)>((0, null));
        };
        store.DeleteFileOverride = _ =>
        {
            permanentDeleteCalls++;
            return true;
        };

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => store.DeleteSessionAsync(path, cts.Token));

        Assert.Equal(0, permanentDeleteCalls);
        Assert.True(File.Exists(path));
    }
}
