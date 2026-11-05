using System.Text.Json;
using Microsoft.Extensions.AI;
using PiSharp.Cli.Tui;
using PiSharp.Core;
using PiSharp.Runtime.Sessions;

namespace PiSharp.Tests;

/// <summary>Deterministic behaviour checks for the session-tree navigator and scoped-models selector
/// (acceptance cases T01-T07/S01-S02 minus terminal-dependent rendering, which the PTY suite covers).</summary>
public sealed class KeysTreeScopedPickerTests
{
    private static readonly DateTimeOffset Baseline = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void TreeSelectionStartsAtCurrentLeafAndWraps()
    {
        var tree = SyntheticTree();
        var leafId = tree.HeadId!;
        var state = new TerminalTreeState(tree.Entries, leafId);

        var leafIndex = IndexOfId(state, leafId);
        Assert.True(leafIndex >= 0);
        Assert.Equal(leafIndex, state.SelectedIndex);

        state.MoveSelection(1);
        Assert.Equal(0, state.SelectedIndex);
        state.MoveSelection(-1);
        Assert.Equal(leafIndex, state.SelectedIndex);
    }

    [Fact]
    public void TreeFoldsBranchAndUnfoldRestoresIt()
    {
        var tree = BranchingTree();
        var parentId = BranchEntryId(tree.Entries, 2);
        var state = new TerminalTreeState(tree.Entries, tree.HeadId!);
        var parentIdIndex = IndexOfId(state, parentId);
        Assert.True(parentIdIndex >= 0);

        state.SelectById(parentId);
        var visibleWithChildren = state.VisibleNodes.Count;
        state.FoldOrUp();
        var visibleAfterFold = state.VisibleNodes.Count;
        Assert.True(visibleAfterFold < visibleWithChildren);
        Assert.True(state.IsFolded(parentId));

        state.AppendSearchText("h");
        Assert.False(state.IsFolded(parentId)); // search edits clear folding

        state.SetFilterMode(TerminalTreeFilterMode.All);
        state.SelectById(parentId);
        state.FoldOrUp();
        Assert.True(state.IsFolded(parentId));
        state.SetFilterMode(TerminalTreeFilterMode.Default);
        Assert.False(state.IsFolded(parentId)); // filter changes clear folding
    }

    [Fact]
    public void TreeHidesBookkeepingAlwaysAndToolCallOnlyAssistantUnlessCurrentLeaf()
    {
        var tree = new ConversationTree();
        var user = tree.Append("chat", CodecChat(ChatRole.User, "question"), at: Stamp(0));
        var result = tree.Append("chat", CodecToolResult(), at: Stamp(1));
        var interest = tree.Append("usage", ToElement(new { tokensIn = 1, tokensOut = 1 }), at: Stamp(2));
        var state = new TerminalTreeState(tree.Entries, user.Id);

        Assert.DoesNotContain(state.VisibleNodes, flatNode => flatNode.Node.Entry.Id == interest.Id);
        var visibleIds = state.VisibleNodes.Select(flatNode => flatNode.Node.Entry.Id).ToArray();
        Assert.Equal(new[] { user.Id, result.Id }, visibleIds.Where(id => id != interest.Id).ToArray());

        var allState = new TerminalTreeState(tree.Entries, user.Id, TerminalTreeFilterMode.All);
        Assert.DoesNotContain(allState.VisibleNodes, flatNode => flatNode.Node.Entry.Id == interest.Id);
        Assert.Contains(allState.VisibleNodes, flatNode => flatNode.Node.Entry.Id == user.Id);
    }

    [Fact]
    public void TreeFilterCycleFollowsPinnedPiOrderInBothDirections()
    {
        var state = new TerminalTreeState([], null);
        Assert.Equal(TerminalTreeFilterMode.Default, state.FilterMode);
        var forward = new List<TerminalTreeFilterMode>();
        for (var index = 0; index < 5; index++)
        {
            state.CycleFilter(1);
            forward.Add(state.FilterMode);
        }
        Assert.Equal([
            TerminalTreeFilterMode.NoTools, TerminalTreeFilterMode.UserOnly,
            TerminalTreeFilterMode.LabeledOnly, TerminalTreeFilterMode.All,
            TerminalTreeFilterMode.Default
        ], forward);

        state.SetFilterMode(TerminalTreeFilterMode.Default);
        state.CycleFilter(-1);
        Assert.Equal(TerminalTreeFilterMode.All, state.FilterMode);
        state.CycleFilter(-1);
        Assert.Equal(TerminalTreeFilterMode.LabeledOnly, state.FilterMode);
        state.CycleFilter(-1);
        Assert.Equal(TerminalTreeFilterMode.UserOnly, state.FilterMode);
        state.CycleFilter(-1);
        Assert.Equal(TerminalTreeFilterMode.NoTools, state.FilterMode);
    }

    [Fact]
    public void TreeEscapeWithActiveSearchClearsSearchBeforeClosing()
    {
        var tree = SyntheticTree();
        var state = new TerminalTreeState(tree.Entries, tree.HeadId!);
        Assert.Empty(state.SearchQuery);
        Assert.False(state.TryEscape());
        state.AppendSearchText("no-match-at-all");
        Assert.NotEmpty(state.SearchQuery);
        Assert.True(state.TryEscape());
        Assert.Empty(state.SearchQuery);
        Assert.False(state.TryEscape());
    }

    [Fact]
    public void TreeProjectsLatestLabelPerTargetAndCopyTextSkipsSettingsEntries()
    {
        var tree = new ConversationTree();
        var user = tree.Append("chat", CodecChat(ChatRole.User, "copy me"), at: Stamp(0));
        tree.Append("label", ToElement(new { targetId = user.Id, label = "first" }), at: Stamp(1));
        tree.Select(user.Id);
        var labelEntry = tree.Append("label", ToElement(new { targetId = user.Id, label = "latest" }), at: Stamp(2));
        tree.Append("label", ToElement(new { targetId = user.Id, label = (string?)null }), at: Stamp(3));

        var state = new TerminalTreeState(tree.Entries, tree.HeadId!);
        // The latest label entry clears the label, so no label text projects.
        Assert.DoesNotContain(state.VisibleNodes, flatNode => flatNode.Node.Label is not null);

        // Project the "latest" label entry (before the clearing entry) — latest per target wins.
        var userState = new TerminalTreeState(tree.Entries.Take(3).ToList(), user.Id);
        var userNode = userState.VisibleNodes.Single(flatNode => flatNode.Node.Entry.Id == user.Id);
        Assert.Equal("latest", userNode.Node.Label);
        Assert.NotEqual(labelEntry.Id, tree.HeadId);
        Assert.Equal("copy me", userState.SelectedCopyText());

        var labelViewState = new TerminalTreeState(userState.VisibleNodes.Select(flatNode => flatNode.Node.Entry).ToList(),
            user.Id, TerminalTreeFilterMode.LabeledOnly);
        Assert.Empty(labelViewState.VisibleNodes);
    }

    [Fact]
    public void ScopedToggleCollapsesExplicitListToAllEnabled()
    {
        var allIds = new List<string> { "a/m1", "a/m2", "b/m1" };
        var scoped = TerminalScopedModelsSelector.Toggle(null, allIds, "a/m2");
        Assert.Equal(new[] { "a/m1", "b/m1" }, scoped);
        Assert.False(TerminalScopedModelsSelector.IsEnabled(scoped, "a/m2"));

        var restored = TerminalScopedModelsSelector.Toggle(scoped, allIds, "a/m2");
        Assert.Null(restored);
        Assert.All(allIds, id => Assert.True(TerminalScopedModelsSelector.IsEnabled(restored, id)));

        // Toggling the last disabled model collapses the explicit list back to null.
        var missingOne = new List<string> { "a/m2", "b/m1" };
        var collapsed = TerminalScopedModelsSelector.Toggle(missingOne, allIds, "a/m1");
        Assert.Null(collapsed);
    }

    [Fact]
    public void ScopedEnableAllClearAllAndMoveMirrorPinnedPiSemantics()
    {
        var allIds = new List<string> { "a/m1", "a/m2", "b/m1" };
        Assert.Null(TerminalScopedModelsSelector.EnableAll(null, allIds));
        var enabledSome = new List<string> { "a/m1" };
        // Enabling the remaining catalog models collapses the explicit list to all-enabled (null).
        Assert.Null(TerminalScopedModelsSelector.EnableAll(enabledSome, allIds));
        var enabledSomeMore = TerminalScopedModelsSelector.EnableAll(enabledSome, allIds, ["b/m1"]);
        Assert.Equal(new[] { "a/m1", "b/m1" }, enabledSomeMore);

        Assert.Empty(TerminalScopedModelsSelector.ClearAll(null, allIds)!);
        var clearedProvider = TerminalScopedModelsSelector.ClearAll(null, allIds, ["a/m1", "a/m2"]);
        Assert.Equal(new[] { "b/m1" }, clearedProvider);
        var clearedExplicit = TerminalScopedModelsSelector.ClearAll(enabledSome, allIds, ["a/m1"]);
        Assert.Empty(clearedExplicit!);
        var clearedUntargeted = TerminalScopedModelsSelector.ClearAll(enabledSome, allIds);
        Assert.Empty(clearedUntargeted!);
        Assert.Null(TerminalScopedModelsSelector.Move(null, "a/m1", -1));

        var moved = TerminalScopedModelsSelector.Move(enabledSome, "a/m1", -1);
        Assert.Equal(enabledSome, moved); // out of bounds keeps the order
    }

    [Fact]
    public void ScopedSortedIdsListEnabledModelsFirstThenRemainingCatalog()
    {
        var allIds = new List<string> { "a/m1", "a/m2", "b/m1", "b/m2" };
        Assert.Equal(allIds, TerminalScopedModelsSelector.GetSortedIds(null, allIds));
        var enabled = new List<string> { "b/m2", "a/m1" };
        Assert.Equal(new[] { "b/m2", "a/m1", "a/m2", "b/m1" },
            TerminalScopedModelsSelector.GetSortedIds(enabled, allIds));
    }

    [Fact]
    public void TreeKeybindingsMatchPinnedPiDefaults()
    {
        var keymap = new EditorKeymap();
        Assert.True(keymap.Matches("app.tree.filter.default", Key(ConsoleKey.D, ConsoleModifiers.Control)));
        Assert.True(keymap.Matches("app.tree.filter.noTools", Key(ConsoleKey.T, ConsoleModifiers.Control)));
        Assert.True(keymap.Matches("app.tree.filter.userOnly", Key(ConsoleKey.U, ConsoleModifiers.Control)));
        Assert.True(keymap.Matches("app.tree.filter.labeledOnly", Key(ConsoleKey.L, ConsoleModifiers.Control)));
        Assert.True(keymap.Matches("app.tree.filter.all", Key(ConsoleKey.A, ConsoleModifiers.Control)));
        Assert.True(keymap.Matches("app.tree.filter.cycleForward", Key(ConsoleKey.O, ConsoleModifiers.Control)));
        Assert.True(keymap.Matches("app.tree.filter.cycleBackward",
            Key(ConsoleKey.O, ConsoleModifiers.Control | ConsoleModifiers.Shift)));
        Assert.True(keymap.Matches("app.tree.editLabel", Key(ConsoleKey.L, ConsoleModifiers.Shift)));
        Assert.True(keymap.Matches("app.tree.toggleLabelTimestamp", Key(ConsoleKey.T, ConsoleModifiers.Shift)));
        var foldOperatingSystemBindings = OperatingSystem.IsMacOS() ? "alt+left" : "ctrl+left";
        var foldDisplayKeys = keymap.GetDisplayKeys("app.tree.foldOrUp")?.Split('/')[0];
        Assert.Equal("left", foldDisplayKeys?[(foldDisplayKeys.IndexOfAny(['+']) + 1)..], StringComparer.OrdinalIgnoreCase);
        Assert.Equal(foldOperatingSystemBindings.Contains("alt"), foldDisplayKeys?.StartsWith("Alt", StringComparison.OrdinalIgnoreCase) == true);
        Assert.True(keymap.Matches("app.message.copy", Key(ConsoleKey.X, ConsoleModifiers.Control)));
        Assert.False(keymap.Matches("app.message.copy", Key(ConsoleKey.C, ConsoleModifiers.Control)));
    }

    [Fact]
    public void ScopedRefreshKeepsTheRowIndexWhenSortOrderChanges()
    {
        var list = new TerminalSelectionList<string>("fixture",
        [
            new("alpha", "alpha", "alpha"), new("beta", "beta", "beta"), new("gamma", "gamma", "gamma")
        ], selectedKey: "gamma");
        Assert.Equal(2, list.SelectedIndex);

        // Pi keeps selectedIndex across refresh even when the enabled order changes.
        list.ReplaceOptions([
            new("gamma", "gamma", "gamma"), new("alpha", "alpha", "alpha"), new("beta", "beta", "beta")
        ], null, preserveIndex: true);
        Assert.Equal(2, list.SelectedIndex);
        Assert.Equal("beta", list.Selected?.Option.Key);
    }

    [Fact]
    public void TreeSelectionListClearSearchRestoresFullList()
    {
        var tree = SyntheticTree();
        var options = BuildTreeOptions(tree);
        var list = new TerminalSelectionList<string>("fixture", options, null, keymap: new EditorKeymap());
        Assert.Empty(list.SearchQuery);
        list.ClearSearch();
        Assert.Equal(options.Count, list.VisibleOptions.Count);
    }

    private static IReadOnlyList<TerminalSelectionOption<string>> BuildTreeOptions(ConversationTree tree) =>
        tree.Entries.Select(entry => new TerminalSelectionOption<string>(entry.Id, entry.Id, entry.Id)).ToArray();

    private static int IndexOfId(TerminalTreeState state, string id)
    {
        for (var index = 0; index < state.VisibleNodes.Count; index++)
            if (state.VisibleNodes[index].Node.Entry.Id == id) return index;
        return -1;
    }

    private static string BranchEntryId(IReadOnlyList<ConversationNode> entries, int index) => entries[index].Id;

    private static JsonElement CodecChat(ChatRole role, string text) =>
        ConversationMessageCodec.EncodeMessage(new ChatMessage(role, text));

    private static JsonElement CodecToolResult() => ConversationMessageCodec.EncodeMessage(new ChatMessage(ChatRole.Tool,
        [new FunctionResultContent("call-1", "result-1")]));

    private static ConversationTree SyntheticTree()
    {
        var tree = new ConversationTree();
        tree.Append("chat", CodecChat(ChatRole.User, "first question"), at: Stamp(0));
        tree.Append("chat", CodecChat(ChatRole.Assistant, "first answer"), at: Stamp(1));
        tree.Append("chat", CodecChat(ChatRole.User, "second question"), at: Stamp(2));
        return tree;
    }

    private static ConversationTree BranchingTree()
    {
        var tree = new ConversationTree();
        var root = tree.Append("chat", CodecChat(ChatRole.User, "root"), at: Stamp(0));
        tree.Append("chat", CodecChat(ChatRole.Assistant, "first branch answer"), at: Stamp(1));
        tree.Select(root.Id);
        tree.Append("chat", CodecChat(ChatRole.Assistant, "second branch answer"), at: Stamp(2));
        tree.Append("chat", CodecChat(ChatRole.Assistant, "third branch answer"), at: Stamp(3));
        return tree;
    }

    private static DateTimeOffset Stamp(int minutes) => Baseline.AddMinutes(minutes);

    private static ConsoleKeyInfo Key(ConsoleKey key, ConsoleModifiers modifiers = ConsoleModifiers.None) =>
        new('\0', key, modifiers.HasFlag(ConsoleModifiers.Shift), modifiers.HasFlag(ConsoleModifiers.Alt),
            modifiers.HasFlag(ConsoleModifiers.Control));

    private static JsonElement ToElement(object value) => JsonSerializer.SerializeToElement(value);
}
