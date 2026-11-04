using System.Text.Json;
using PiSharp.Cli;
using PiSharp.Cli.Tui;
using PiSharp.Runtime.Sessions;

namespace PiSharp.Tests;

/// <summary>Regressions for the post-merge session-picker findings F01-F03.</summary>
public sealed class SessionPickerFindingsTests
{
    [Fact]
    public async Task CurrentAndAllScopeUseCanonicalFilePathsAsPickerKeys()
    {
        using var fixture = await TreeFixture.CreateAsync();
        var state = await TerminalSessionPickerState.LoadAsync(fixture.Store, null, null);
        Assert.All(state.BuildEntries(), entry =>
            Assert.Equal(Path.GetFullPath(entry.Listing.Path), entry.Key));

        await state.EnsureAllLoadedAsync();
        state.ToggleScope();
        Assert.All(state.BuildEntries(), entry =>
            Assert.Equal(Path.GetFullPath(entry.Listing.Path), entry.Key));
    }

    [Fact]
    public async Task ThreadedModeBuildsThePinnedPiParentTreeWithSubtreeActivityOrdering()
    {
        using var fixture = await TreeFixture.CreateAsync();
        var state = await TerminalSessionPickerState.LoadAsync(fixture.Store, null, null);

        var entries = state.BuildEntries();
        Assert.Equal(["alpha parent", "alpha child one", "alpha grandchild", "alpha child two",
            "orphan child", "beta parent", "beta child"], entries.Select(entry => entry.Listing.Name));
        Assert.Equal([0, 1, 2, 1, 0, 0, 1], entries.Select(entry => entry.Depth));
        Assert.Equal(["", "   ├─ ", "   │  └─ ", "   └─ ", "", "", "   └─ "],
            entries.Select(entry => entry.TreePrefix));
        Assert.Equal(["alpha child one", "alpha child two", "alpha grandchild"],
            entries.Where(entry => entry.Depth > 0 && entry.Listing.Name!.StartsWith("alpha", StringComparison.Ordinal))
                .Select(entry => entry.Listing.Name).OrderBy(name => name));
        Assert.Equal(SessionCatalog.CanonicalPath(fixture.ParentPath),
            SessionCatalog.CanonicalPath(entries[1].Listing.ParentSessionPath!));
    }

    [Fact]
    public async Task RecentAndFuzzyKeepCatalogOrderWithoutTreeMetadata()
    {
        using var fixture = await TreeFixture.CreateAsync();
        var state = await TerminalSessionPickerState.LoadAsync(fixture.Store, null, null);
        var catalog = await SessionCatalog.ListAsync(fixture.Store);

        state.ToggleSort();
        Assert.Equal("Recent", state.SortLabel);
        var recent = state.BuildEntries();
        Assert.Equal(catalog.Select(item => item.Path), recent.Select(entry => entry.Listing.Path));
        Assert.All(recent, entry => Assert.Equal((0, ""), (entry.Depth, entry.TreePrefix)));

        state.ToggleSort();
        Assert.Equal("Fuzzy", state.SortLabel);
        var fuzzy = state.BuildEntries();
        Assert.Equal(catalog.Select(item => item.Path), fuzzy.Select(entry => entry.Listing.Path));
        Assert.All(fuzzy, entry => Assert.Equal((0, ""), (entry.Depth, entry.TreePrefix)));
    }

    [Fact]
    public async Task SortingPreservesTheSelectedSessionByPathRatherThanIndex()
    {
        using var fixture = await TreeFixture.CreateAsync();
        var state = await TerminalSessionPickerState.LoadAsync(fixture.Store, null, null);
        var options = state.BuildEntries().Select(state.BuildOption).ToArray();
        var selected = options.Single(option => option.Value.Listing.Name == "alpha grandchild");
        var list = new TerminalSelectionList<SessionPickerEntry>("Resume Session (Current Folder)",
            options, options[^1].Key, scopedOptions: options, allLabel: "All",
            scopedLabel: "Current Folder", keymap: new EditorKeymap(agentDirectory: null));
        list.SelectIndex(Array.FindIndex(options, option => option.Key == selected.Key));

        state.ToggleSort();
        list.SortMatchesByScore = state.Sort != SessionPickerSortMode.Recent;
        list.ReplaceOptions([], state.BuildEntries().Select(state.BuildOption).ToArray());

        Assert.Equal(selected.Key, list.Selected?.Option.Key);
        Assert.Equal("alpha grandchild", list.Selected?.Option.Value.Listing.Name);
        Assert.Equal(state.BuildEntries().Select(entry => entry.Listing.Name).ToList()
            .IndexOf("alpha grandchild"), list.SelectedIndex);
    }

    [Fact]
    public async Task SameIdSessionsInDifferentProjectsSelectRenameAndDeleteOnlyTheChosenFile()
    {
        using var fixture = await DuplicateIdFixture.CreateAsync();
        var state = await TerminalSessionPickerState.LoadAsync(fixture.Store, null, null,
            (catalog, listing, token) => SessionFileDeletion.DeleteAsync(catalog, listing,
                (_, _) => Task.FromResult(false), token));
        await state.EnsureAllLoadedAsync();
        state.ToggleScope();

        var entries = state.BuildEntries();
        Assert.Equal(2, entries.Count);
        var local = Assert.Single(entries, entry => entry.Listing.Name == "local same id");
        var foreign = Assert.Single(entries, entry => entry.Listing.Name == "foreign same id");
        Assert.Equal(local.Listing.Id, foreign.Listing.Id);
        Assert.NotEqual(local.Key, foreign.Key);
        Assert.Equal(Path.GetFullPath(local.Listing.Path), local.Key);
        Assert.Equal(Path.GetFullPath(foreign.Listing.Path), foreign.Key);
        Assert.Same(foreign.Listing, state.FindEntry(foreign.Key)?.Listing);

        Assert.True(await state.RenameAsync(foreign.Key, "renamed foreign"));
        Assert.True(File.Exists(local.Listing.Path));
        Assert.True(File.Exists(foreign.Listing.Path));
        var reloaded = state.BuildEntries();
        Assert.Contains(reloaded, entry => entry.Listing.Id == local.Listing.Id &&
            entry.Listing.Name == "local same id" && entry.Key == local.Key);
        var renamed = Assert.Single(reloaded, entry => entry.Key == foreign.Key);
        Assert.Equal("renamed foreign", renamed.Listing.Name);
        Assert.Equal(local.Listing.Id, renamed.Listing.Id);

        Assert.True(state.BeginDelete(foreign.Key));
        Assert.Equal(foreign.Key, state.ConfirmingDeleteKey);
        Assert.True(await state.ConfirmDeleteAsync());
        Assert.True(File.Exists(local.Listing.Path));
        Assert.False(File.Exists(foreign.Listing.Path));
        Assert.Contains(state.BuildEntries(), entry => entry.Key == local.Key);
        Assert.DoesNotContain(state.BuildEntries(), entry => entry.Key == foreign.Key);
    }

    [Fact]
    public async Task AllScopeLinksChildrenToParentsInOtherProjectDirectories()
    {
        using var fixture = await CrossProjectFixture.CreateAsync();
        var state = await TerminalSessionPickerState.LoadAsync(fixture.Store, null, null);
        await state.EnsureAllLoadedAsync();
        state.ToggleScope();

        var entries = state.BuildEntries();
        Assert.Equal(["cross parent", "cross child", "foreign root"],
            entries.Select(entry => entry.Listing.Name));
        Assert.Equal([0, 1, 0], entries.Select(entry => entry.Depth));
        Assert.Equal("", entries[0].TreePrefix);
        Assert.Equal("   └─ ", entries[1].TreePrefix);
        Assert.Equal(SessionCatalog.CanonicalPath(fixture.ParentPath),
            SessionCatalog.CanonicalPath(entries[1].Listing.ParentSessionPath!));
    }

    [Fact]
    public async Task ThreadedSearchFlattensTreeAndBreaksEqualScoresByModifiedTime()
    {
        using var fixture = await SearchFixture.CreateAsync();
        var state = await TerminalSessionPickerState.LoadAsync(fixture.Store, null, null);
        var options = state.BuildEntries().Select(state.BuildOption).ToArray();
        Assert.Equal([fixture.ParentPath, fixture.ChildPath],
            options.Select(option => option.Value.Listing.Path));

        var list = new TerminalSelectionList<SessionPickerEntry>("Resume Session (Current Folder)",
            options, keymap: new EditorKeymap(agentDirectory: null));
        list.HandleInput(new(null, "match"));

        Assert.Equal([fixture.ChildPath, fixture.ParentPath],
            list.VisibleOptions.Select(option => option.Value.Listing.Path));
        Assert.Equal(fixture.ChildPath, list.Selected?.Option.Value.Listing.Path);
        var searched = string.Join('\n', list.Render(140, 40));
        Assert.Contains("same match", searched);
        Assert.DoesNotContain("└─", searched);
        Assert.DoesNotContain("├─", searched);

        list.HandleInput(Event(Key(ConsoleKey.U, ConsoleModifiers.Control)));
        Assert.Equal([fixture.ParentPath, fixture.ChildPath],
            list.VisibleOptions.Select(option => option.Value.Listing.Path));
        Assert.Contains("└─", string.Join('\n', list.Render(140, 40)));
    }

    [Fact]
    public async Task DisabledConfirmationBindingsRenderEmptyKeysAndCannotConfirmOrCancel()
    {
        using var fixture = await TreeFixture.CreateAsync();
        var directory = TempDirectory("disabled-confirm");
        try
        {
            await File.WriteAllTextAsync(Path.Combine(directory, "keybindings.json"), """
                {
                  "app.session.delete": "f7",
                  "tui.select.confirm": [],
                  "tui.select.cancel": []
                }
                """);
            var keymap = new EditorKeymap(directory, windowsBindings: false);
            var state = await TerminalSessionPickerState.LoadAsync(fixture.Store, null, null);
            var victim = state.BuildEntries().First(entry => !entry.IsCurrent);
            Assert.True(state.BeginDelete(victim.Key));

            var hint = TerminalSessionPicker.BuildActionHint(state, keymap.GetDisplayKeys)!;
            Assert.Equal($"Delete {victim.Listing.Name}?  confirm ·  cancel", hint);
            Assert.DoesNotContain("Enter", hint);
            Assert.DoesNotContain("Escape", hint);

            var editor = new TerminalEditor(agentDirectory: directory);
            var picker = new TerminalSessionPicker(fixture.Store, editor);
            var options = state.BuildEntries().Select(state.BuildOption).ToArray();
            var list = new TerminalSelectionList<SessionPickerEntry>("Resume Session (Current Folder)",
                options, victim.Key, scopedOptions: options, allLabel: "All",
                scopedLabel: "Current Folder", keymap: keymap);
            list.SelectIndex(Array.FindIndex(options, option => option.Key == victim.Key));

            Assert.True(await picker.HandleInputAsync(state, list, Event(Key(ConsoleKey.Enter)),
                null, null, CancellationToken.None));
            Assert.Equal(victim.Key, state.ConfirmingDeleteKey);
            Assert.True(File.Exists(victim.Listing.Path));

            Assert.True(await picker.HandleInputAsync(state, list, Event(Key(ConsoleKey.Escape)),
                null, null, CancellationToken.None));
            Assert.Equal(victim.Key, state.ConfirmingDeleteKey);
            Assert.True(File.Exists(victim.Listing.Path));

            Assert.Equal("Delete " + victim.Listing.Name + "? Enter confirm · Escape cancel",
                TerminalSessionPicker.BuildActionHint(state, displayKeys: null));
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task CustomConfirmationBindingsAreAdvertisedAndDispatched()
    {
        using var fixture = await TreeFixture.CreateAsync();
        var directory = TempDirectory("custom-confirm");
        try
        {
            await File.WriteAllTextAsync(Path.Combine(directory, "keybindings.json"), """
                {
                  "tui.select.confirm": "f9",
                  "tui.select.cancel": "f10"
                }
                """);
            var keymap = new EditorKeymap(directory, windowsBindings: false);
            var state = await TerminalSessionPickerState.LoadAsync(fixture.Store, null, null,
                (catalog, listing, token) => SessionFileDeletion.DeleteAsync(catalog, listing,
                    (_, _) => Task.FromResult(false), token));
            var victim = state.BuildEntries().First(entry => !entry.IsCurrent);
            Assert.True(state.BeginDelete(victim.Key));
            var hint = TerminalSessionPicker.BuildActionHint(state, keymap.GetDisplayKeys)!;
            Assert.Contains("F9 confirm", hint);
            Assert.Contains("F10 cancel", hint);

            var editor = new TerminalEditor(agentDirectory: directory);
            var picker = new TerminalSessionPicker(fixture.Store, editor);
            var options = state.BuildEntries().Select(state.BuildOption).ToArray();
            var list = new TerminalSelectionList<SessionPickerEntry>("Resume Session (Current Folder)",
                options, victim.Key, scopedOptions: options, allLabel: "All",
                scopedLabel: "Current Folder", keymap: keymap);
            list.SelectIndex(Array.FindIndex(options, option => option.Key == victim.Key));

            Assert.True(await picker.HandleInputAsync(state, list, Event(Key(ConsoleKey.F10)),
                null, null, CancellationToken.None));
            Assert.Null(state.ConfirmingDeleteKey);
            Assert.True(File.Exists(victim.Listing.Path));

            Assert.True(state.BeginDelete(victim.Key));
            Assert.True(await picker.HandleInputAsync(state, list, Event(Key(ConsoleKey.Enter)),
                null, null, CancellationToken.None));
            Assert.Equal(victim.Key, state.ConfirmingDeleteKey);
            Assert.True(File.Exists(victim.Listing.Path));

            Assert.True(await picker.HandleInputAsync(state, list, Event(Key(ConsoleKey.F9)),
                null, null, CancellationToken.None));
            Assert.Null(state.ConfirmingDeleteKey);
            Assert.False(File.Exists(victim.Listing.Path));
        }
        finally { Directory.Delete(directory, true); }
    }

    private static ConsoleKeyInfo Key(ConsoleKey key, ConsoleModifiers modifiers = 0) => new('\0', key,
        modifiers.HasFlag(ConsoleModifiers.Shift), modifiers.HasFlag(ConsoleModifiers.Alt),
        modifiers.HasFlag(ConsoleModifiers.Control));

    private static TerminalInputEvent Event(ConsoleKeyInfo key) => new(key, null);

    private static string TempDirectory(string label)
    {
        var directory = Path.Combine(Path.GetTempPath(), $"pisharp-picker-findings-{label}-" +
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }

    private static async Task WritePiSessionAsync(string path, string id, string workingDirectory, string name,
        string timestamp, string? parentSessionPath = null)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var parent = parentSessionPath is null ? "" :
            $",\"parentSession\":{JsonSerializer.Serialize(parentSessionPath)}";
        var messageId = id + "-message";
        var infoId = id + "-info";
        await File.WriteAllTextAsync(path,
            $"{{\"type\":\"session\",\"version\":3,\"id\":{JsonSerializer.Serialize(id)}," +
            $"\"timestamp\":{JsonSerializer.Serialize(timestamp)},\"cwd\":{JsonSerializer.Serialize(workingDirectory)}{parent}}}\n" +
            $"{{\"type\":\"message\",\"id\":{JsonSerializer.Serialize(messageId)},\"parentId\":null," +
            $"\"timestamp\":{JsonSerializer.Serialize(timestamp)},\"message\":{{\"role\":\"user\"," +
            $"\"content\":[{{\"type\":\"text\",\"text\":{JsonSerializer.Serialize(name)}}}]," +
            $"\"timestamp\":{DateTimeOffset.Parse(timestamp).ToUnixTimeMilliseconds()}}}}}\n" +
            $"{{\"type\":\"session_info\",\"id\":{JsonSerializer.Serialize(infoId)}," +
            $"\"parentId\":{JsonSerializer.Serialize(messageId)},\"timestamp\":{JsonSerializer.Serialize(timestamp)}," +
            $"\"name\":{JsonSerializer.Serialize(name)}}}\n");
    }

    private sealed class TreeFixture : IDisposable
    {
        private TreeFixture(string root, ConversationStore store, string parentPath)
        {
            Root = root;
            Store = store;
            ParentPath = parentPath;
        }

        public string Root { get; }
        public ConversationStore Store { get; }
        public string ParentPath { get; }

        public static async Task<TreeFixture> CreateAsync()
        {
            var root = TempDirectory("tree");
            var sessions = Path.Combine(root, "sessions");
            Directory.CreateDirectory(sessions);
            var store = new ConversationStore(root, sessions);
            var parent = Path.Combine(sessions, "a-parent.jsonl");
            await WritePiSessionAsync(parent, "tree-a", root, "alpha parent", "2026-01-01T00:00:00Z");
            var childOne = Path.Combine(sessions, "a-child-one.jsonl");
            await WritePiSessionAsync(childOne, "tree-a1", root, "alpha child one", "2026-01-04T00:00:00Z",
                Path.Combine(sessions, ".", "a-parent.jsonl"));
            await WritePiSessionAsync(Path.Combine(sessions, "a-grandchild.jsonl"), "tree-a11", root,
                "alpha grandchild", "2026-01-06T00:00:00Z", childOne);
            await WritePiSessionAsync(Path.Combine(sessions, "a-child-two.jsonl"), "tree-a2", root,
                "alpha child two", "2026-01-05T00:00:00Z", parent);
            await WritePiSessionAsync(Path.Combine(sessions, "b-parent.jsonl"), "tree-b", root,
                "beta parent", "2026-01-03T00:00:00Z");
            await WritePiSessionAsync(Path.Combine(sessions, "b-child.jsonl"), "tree-b1", root,
                "beta child", "2026-01-02T00:00:00Z",
                Path.Combine(sessions, "b-parent.jsonl"));
            await WritePiSessionAsync(Path.Combine(sessions, "orphan.jsonl"), "tree-orphan", root,
                "orphan child", "2026-01-04T00:00:00Z",
                Path.Combine(sessions, "missing-parent.jsonl"));
            return new(root, store, parent);
        }

        public void Dispose() => Directory.Delete(Root, true);
    }

    private sealed class SearchFixture : IDisposable
    {
        private SearchFixture(string root, ConversationStore store, string parentPath, string childPath)
        {
            Root = root;
            Store = store;
            ParentPath = parentPath;
            ChildPath = childPath;
        }

        public string Root { get; }
        public ConversationStore Store { get; }
        public string ParentPath { get; }
        public string ChildPath { get; }

        public static async Task<SearchFixture> CreateAsync()
        {
            var root = TempDirectory("search");
            var sessions = Path.Combine(root, "sessions");
            Directory.CreateDirectory(sessions);
            var store = new ConversationStore(root, sessions);
            var parent = Path.Combine(sessions, "parent.jsonl");
            await WritePiSessionAsync(parent, "same-id", root, "same match",
                "2026-01-01T00:00:00Z");
            var child = Path.Combine(sessions, "child.jsonl");
            await WritePiSessionAsync(child, "same-id", root, "same match",
                "2026-01-02T00:00:00Z", parent);
            return new(root, store, parent, child);
        }

        public void Dispose() => Directory.Delete(Root, true);
    }

    private sealed class DuplicateIdFixture : IDisposable
    {
        private DuplicateIdFixture(string root, ConversationStore store)
        {
            Root = root;
            Store = store;
        }

        public string Root { get; }
        public ConversationStore Store { get; }

        public static async Task<DuplicateIdFixture> CreateAsync()
        {
            var root = TempDirectory("duplicate-id");
            var sessionsRoot = Path.Combine(root, "sessions");
            var localWorking = Path.Combine(root, "project-a");
            var foreignWorking = Path.Combine(root, "project-b");
            var localDirectory = Path.Combine(sessionsRoot, "aaaa");
            var foreignDirectory = Path.Combine(sessionsRoot, "bbbb");
            Directory.CreateDirectory(localWorking);
            Directory.CreateDirectory(foreignWorking);
            Directory.CreateDirectory(localDirectory);
            Directory.CreateDirectory(foreignDirectory);
            var store = new ConversationStore(localWorking, localDirectory, defaultDirectoryPath: localDirectory);
            await WritePiSessionAsync(Path.Combine(localDirectory, "local.jsonl"), "shared-id",
                localWorking, "local same id", "2026-02-01T00:00:00Z");
            await WritePiSessionAsync(Path.Combine(foreignDirectory, "foreign.jsonl"), "shared-id",
                foreignWorking, "foreign same id", "2026-02-02T00:00:00Z");
            return new(root, store);
        }

        public void Dispose() => Directory.Delete(Root, true);
    }

    private sealed class CrossProjectFixture : IDisposable
    {
        private CrossProjectFixture(string root, ConversationStore store, string parentPath)
        {
            Root = root;
            Store = store;
            ParentPath = parentPath;
        }

        public string Root { get; }
        public ConversationStore Store { get; }
        public string ParentPath { get; }

        public static async Task<CrossProjectFixture> CreateAsync()
        {
            var root = TempDirectory("cross-project");
            var sessionsRoot = Path.Combine(root, "sessions");
            var localWorking = Path.Combine(root, "project-a");
            var foreignWorking = Path.Combine(root, "project-b");
            var localDirectory = Path.Combine(sessionsRoot, "aaaa");
            var foreignDirectory = Path.Combine(sessionsRoot, "bbbb");
            Directory.CreateDirectory(localWorking);
            Directory.CreateDirectory(foreignWorking);
            Directory.CreateDirectory(localDirectory);
            Directory.CreateDirectory(foreignDirectory);
            var store = new ConversationStore(localWorking, localDirectory, defaultDirectoryPath: localDirectory);
            var parent = Path.Combine(localDirectory, "parent.jsonl");
            await WritePiSessionAsync(parent, "cross-parent", localWorking, "cross parent",
                "2026-03-01T00:00:00Z");
            await WritePiSessionAsync(Path.Combine(foreignDirectory, "child.jsonl"), "cross-child",
                foreignWorking, "cross child", "2026-03-03T00:00:00Z", parent);
            await WritePiSessionAsync(Path.Combine(foreignDirectory, "root.jsonl"), "cross-root",
                foreignWorking, "foreign root", "2026-03-02T00:00:00Z");
            return new(root, store, parent);
        }

        public void Dispose() => Directory.Delete(Root, true);
    }
}
