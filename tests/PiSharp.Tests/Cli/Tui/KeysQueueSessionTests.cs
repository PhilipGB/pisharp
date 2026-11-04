using PiSharp.Cli.Tui;
using PiSharp.Runtime.Sessions;

namespace PiSharp.Tests;

public sealed class KeysQueueSessionTests
{
    [Fact]
    public void DefaultQueueAndSessionPickerBindingsMatchPinnedPi()
    {
        var unix = new EditorKeymap(agentDirectory: null, windowsBindings: false);
        Assert.True(unix.Matches("app.message.followUp", Key(ConsoleKey.Enter, ConsoleModifiers.Alt)));
        Assert.True(unix.Matches("app.message.dequeue", Key(ConsoleKey.UpArrow, ConsoleModifiers.Alt)));
        Assert.True(unix.Matches("app.session.togglePath", Key(ConsoleKey.P, ConsoleModifiers.Control)));
        Assert.True(unix.Matches("app.session.toggleSort", Key(ConsoleKey.S, ConsoleModifiers.Control)));
        Assert.True(unix.Matches("app.session.toggleNamedFilter", Key(ConsoleKey.N, ConsoleModifiers.Control)));
        Assert.True(unix.Matches("app.session.rename", Key(ConsoleKey.R, ConsoleModifiers.Control)));
        Assert.True(unix.Matches("app.session.delete", Key(ConsoleKey.D, ConsoleModifiers.Control)));
        Assert.True(unix.Matches("app.session.deleteNoninvasive",
            Key(ConsoleKey.Backspace, ConsoleModifiers.Control)));
        Assert.True(unix.Matches("tui.input.tab", Key(ConsoleKey.Tab)));

        var windows = new EditorKeymap(agentDirectory: null, windowsBindings: true);
        Assert.True(windows.Matches("app.message.followUp", Key(ConsoleKey.Q, ConsoleModifiers.Control)));
        Assert.True(windows.Matches("app.message.dequeue", Key(ConsoleKey.Q, ConsoleModifiers.Alt)));
        Assert.False(windows.Matches("app.message.followUp", Key(ConsoleKey.Enter, ConsoleModifiers.Alt)));
        Assert.False(windows.Matches("app.message.dequeue", Key(ConsoleKey.UpArrow, ConsoleModifiers.Alt)));
    }

    [Fact]
    public void ReplacementAndDisabledQueueAndPickerBindingsFollowTheKeymap()
    {
        var directory = Path.Combine(Path.GetTempPath(), "pisharp-queue-keys-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            File.WriteAllText(Path.Combine(directory, "keybindings.json"), """
                {
                  "app.message.followUp": "ctrl+u",
                  "app.message.dequeue": ["alt+r", "f9"],
                  "app.session.togglePath": [],
                  "app.session.toggleSort": "f6",
                  "app.session.toggleNamedFilter": [],
                  "app.session.rename": "f5",
                  "app.session.delete": [],
                  "app.session.deleteNoninvasive": "ctrl+shift+backspace",
                  "tui.input.tab": "f4"
                }
                """);
            var keymap = new EditorKeymap(directory, windowsBindings: false);
            Assert.True(keymap.Matches("app.message.followUp", Key(ConsoleKey.U, ConsoleModifiers.Control)));
            Assert.False(keymap.Matches("app.message.followUp", Key(ConsoleKey.Enter, ConsoleModifiers.Alt)));
            Assert.True(keymap.Matches("app.message.dequeue", Key(ConsoleKey.R, ConsoleModifiers.Alt)));
            Assert.True(keymap.Matches("app.message.dequeue", Key(ConsoleKey.F9)));
            Assert.Null(keymap.GetDisplayKeys("app.session.togglePath"));
            Assert.True(keymap.Matches("app.session.toggleSort", Key(ConsoleKey.F6)));
            Assert.False(keymap.Matches("app.session.toggleSort", Key(ConsoleKey.S, ConsoleModifiers.Control)));
            Assert.Null(keymap.GetDisplayKeys("app.session.toggleNamedFilter"));
            Assert.True(keymap.Matches("app.session.rename", Key(ConsoleKey.F5)));
            Assert.Null(keymap.GetDisplayKeys("app.session.delete"));
            Assert.True(keymap.Matches("app.session.deleteNoninvasive",
                Key(ConsoleKey.Backspace, ConsoleModifiers.Control | ConsoleModifiers.Shift)));
            Assert.True(keymap.Matches("tui.input.tab", Key(ConsoleKey.F4)));
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public void IdleFollowUpSubmitsOnceAndIdleDequeueRestoresQueuedTextBeforeTheDraft()
    {
        var directory = Path.Combine(Path.GetTempPath(), "pisharp-idle-queue-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var editor = new TerminalEditor(agentDirectory: directory);
            editor.Prefill("idle prompt");
            Assert.True(editor.HandleIdleQueueKey(Key(ConsoleKey.Enter, ConsoleModifiers.Alt), null,
                out var submitted));
            Assert.Equal("idle prompt", submitted);
            Assert.Equal("", editor.Draft);

            Assert.True(editor.HandleIdleQueueKey(Key(ConsoleKey.Enter, ConsoleModifiers.Alt), null,
                out submitted));
            Assert.Null(submitted);
            Assert.Equal("", editor.Draft);

            editor.Prefill("existing draft");
            Assert.True(editor.HandleIdleQueueKey(Key(ConsoleKey.UpArrow, ConsoleModifiers.Alt),
                () => ["steering text", "follow-up text"], out submitted));
            Assert.Null(submitted);
            Assert.Equal("steering text\n\nfollow-up text\n\nexisting draft", editor.Draft);
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task ActiveRunRoutesEnterToSteeringAndAltEnterToFollowUpWithoutTouchingTheDraftEarly()
    {
        var directory = Path.Combine(Path.GetTempPath(), "pisharp-active-queue-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var editor = new TerminalEditor(agentDirectory: directory);
            var submissions = new List<(string Text, bool FollowUp)>();
            Task<bool> Queue(string text, bool followUp, CancellationToken _)
            {
                submissions.Add((text, followUp));
                return Task.FromResult(true);
            }
            editor.Prefill("steer me");
            Assert.True(await editor.HandleActiveInputAsync(Event(Key(ConsoleKey.Enter)), Queue, () => [], () => { }));
            Assert.Equal([("steer me", false)], submissions);
            Assert.Equal("", editor.Draft);

            editor.Prefill("follow me");
            Assert.True(await editor.HandleActiveInputAsync(Event(Key(ConsoleKey.Enter, ConsoleModifiers.Alt)),
                Queue, () => [], () => { }));
            Assert.Equal([("steer me", false), ("follow me", true)], submissions);
            Assert.Equal("", editor.Draft);
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task ActiveRunDequeueRestoresSteeringBeforeFollowUpBeforeTheDraft()
    {
        var directory = Path.Combine(Path.GetTempPath(), "pisharp-dequeue-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var editor = new TerminalEditor(agentDirectory: directory);
            editor.Prefill("draft text");
            var cleared = 0;
            Assert.True(await editor.HandleActiveInputAsync(Event(Key(ConsoleKey.UpArrow, ConsoleModifiers.Alt)),
                (_, _, _) => Task.FromResult(true),
                () => { cleared++; return ["steer", "follow"]; }, () => { }));
            Assert.Equal(1, cleared);
            Assert.Equal("steer\n\nfollow\n\ndraft text", editor.Draft);
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task QueueFailureRestoresTheSubmittedText()
    {
        var directory = Path.Combine(Path.GetTempPath(), "pisharp-queue-failure-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var editor = new TerminalEditor(agentDirectory: directory);
            editor.Prefill("do not lose me");
            Assert.True(await editor.HandleActiveInputAsync(Event(Key(ConsoleKey.Enter)),
                (_, _, _) => Task.FromResult(false), () => [], () => { }));
            Assert.Equal("do not lose me", editor.Draft);
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task NamedFilterComposesWithSearchAndTogglingRestoresEntries()
    {
        using var fixture = await PickerFixture.CreateAsync();
        var state = await TerminalSessionPickerState.LoadAsync(fixture.Store, fixture.CurrentPath, null);
        Assert.Equal(4, state.BuildEntries().Count);

        state.ToggleNameFilter();
        var named = state.BuildEntries();
        Assert.Equal(["current session", "named one", "named two"], named.Select(entry => entry.Listing.Name).OrderBy(name => name));
        var list = new TerminalSelectionList<SessionPickerEntry>("Resume Session (Current Folder)",
            state.BuildEntriesForScope(SessionPickerScope.All).Select(state.BuildOption).ToArray(),
            scopedOptions: state.BuildEntries().Select(state.BuildOption).ToArray(),
            allLabel: "All", scopedLabel: "Current Folder", keymap: new EditorKeymap());
        list.HandleInput(new(null, "two"));
        Assert.Equal("named two", list.Selected?.Option.Value.Listing.Name);
        Assert.DoesNotContain("(unnamed)", string.Join('\n', list.Render(80, 24)));

        state.ToggleNameFilter();
        Assert.Equal(4, state.BuildEntries().Count);
        Assert.Equal("All", state.NameFilterLabel);
    }

    [Fact]
    public async Task SortCyclesThreadedRecentAndFuzzyAndPreservesTheSelectedIndex()
    {
        using var fixture = await PickerFixture.CreateAsync();
        var state = await TerminalSessionPickerState.LoadAsync(fixture.Store, fixture.CurrentPath, null);
        var list = new TerminalSelectionList<SessionPickerEntry>("Resume Session (Current Folder)",
            Array.Empty<TerminalSelectionOption<SessionPickerEntry>>(),
            scopedOptions: state.BuildEntries().Select(state.BuildOption).ToArray(),
            allLabel: "All", scopedLabel: "Current Folder", keymap: new EditorKeymap());
        list.HandleInput(Event(Key(ConsoleKey.DownArrow)));
        var selectedIndex = list.SelectedIndex;

        Assert.Equal("Threaded", state.SortLabel);
        state.ToggleSort();
        Assert.Equal("Recent", state.SortLabel);
        list.SortMatchesByScore = state.Sort != SessionPickerSortMode.Recent;
        list.ReplaceOptions([], state.BuildEntries().Select(state.BuildOption).ToArray(), preserveIndex: true);
        Assert.Equal(selectedIndex, list.SelectedIndex);
        state.ToggleSort();
        Assert.Equal("Fuzzy", state.SortLabel);
        state.ToggleSort();
        Assert.Equal("Threaded", state.SortLabel);
    }

    [Fact]
    public async Task PathDisplayTogglesDescriptionsWithoutChangingSelectionOrSearchText()
    {
        using var fixture = await PickerFixture.CreateAsync();
        var state = await TerminalSessionPickerState.LoadAsync(fixture.Store, fixture.CurrentPath, null);
        var entry = state.BuildEntries().First();
        var withoutPath = state.BuildOption(entry);
        Assert.DoesNotContain(entry.Listing.Path, withoutPath.Description, StringComparison.Ordinal);

        var list = new TerminalSelectionList<SessionPickerEntry>("Resume Session (Current Folder)",
            state.BuildEntries().Select(state.BuildOption).ToArray());
        list.HandleInput(Event(Key(ConsoleKey.DownArrow)));
        var selected = list.Selected?.Option.Key;
        state.TogglePath();
        list.ReplaceOptions(state.BuildEntries().Select(state.BuildOption).ToArray(), scopedOptions: null);
        Assert.True(state.ShowPath);
        Assert.Equal(selected, list.Selected?.Option.Key);
        Assert.Contains(Path.GetFileName(entry.Listing.Path), state.BuildOption(entry).Description!, StringComparison.Ordinal);
        list.HandleInput(new(null, entry.Listing.Id[..6]));
        Assert.Equal(entry.Listing.Id, list.Selected?.Option.Key);
    }

    [Fact]
    public async Task DeleteRequiresConfirmationProtectsTheCurrentSessionAndRemovesOnlyTheSelection()
    {
        using var fixture = await PickerFixture.CreateAsync();
        var state = await TerminalSessionPickerState.LoadAsync(fixture.Store, fixture.CurrentPath, null,
            (catalog, listing, token) => SessionFileDeletion.DeleteAsync(catalog, listing,
                (_, _) => Task.FromResult(false), token));
        var current = state.BuildEntries().Single(entry => entry.IsCurrent);
        Assert.False(state.BeginDelete(current.Key));
        Assert.Contains("Cannot delete the currently active session", state.Status);
        Assert.NotNull(fixture.CurrentPath);

        var victim = state.BuildEntries().First(entry => !entry.IsCurrent);
        Assert.True(state.BeginDelete(victim.Key));
        Assert.Equal(victim.Key, state.ConfirmingDeleteId);
        Assert.True(File.Exists(victim.Listing.Path));
        var unrelated = state.BuildEntries().Where(entry => entry.Key != victim.Key)
            .Select(entry => entry.Listing.Path).ToArray();

        Assert.True(await state.ConfirmDeleteAsync());
        Assert.False(File.Exists(victim.Listing.Path));
        Assert.DoesNotContain(state.BuildEntries(), entry => entry.Key == victim.Key);
        Assert.All(unrelated, path => Assert.True(File.Exists(path)));
        Assert.Contains("Session deleted", state.Status);
    }

    [Fact]
    public async Task DeleteConfirmationCanBeCancelledWithoutFilesystemMutation()
    {
        using var fixture = await PickerFixture.CreateAsync();
        var state = await TerminalSessionPickerState.LoadAsync(fixture.Store, fixture.CurrentPath, null);
        var victim = state.BuildEntries().First(entry => !entry.IsCurrent);
        Assert.True(state.BeginDelete(victim.Key));
        state.CancelDelete();
        Assert.Null(state.ConfirmingDeleteId);
        Assert.True(File.Exists(victim.Listing.Path));
        Assert.Equal(4, state.BuildEntries().Count);
    }

    [Fact]
    public async Task TrashFirstDeletionUsesTrashAndFallsBackToUnlinkWithFingerprintValidation()
    {
        using var fixture = await PickerFixture.CreateAsync();
        var state = await TerminalSessionPickerState.LoadAsync(fixture.Store, fixture.CurrentPath, null);
        var victim = state.BuildEntries().First(entry => !entry.IsCurrent);
        var trashed = new List<string>();
        var result = await SessionFileDeletion.DeleteAsync(fixture.Store, victim.Listing, (path, _) =>
        {
            trashed.Add(path);
            File.Delete(path);
            return Task.FromResult(true);
        }, CancellationToken.None);
        Assert.True(result.Succeeded);
        Assert.Equal("trash", result.Method);
        Assert.Equal([victim.Listing.Path], trashed.ToArray());
        Assert.False(File.Exists(victim.Listing.Path));
        await state.ReloadAsync();

        var fallback = state.BuildEntries().First(entry => !entry.IsCurrent);
        var fallbackResult = await SessionFileDeletion.DeleteAsync(fixture.Store, fallback.Listing,
            (_, _) => Task.FromResult(false), CancellationToken.None);
        Assert.True(fallbackResult.Succeeded);
        Assert.Equal("unlink", fallbackResult.Method);
        Assert.False(File.Exists(fallback.Listing.Path));

        var stale = state.BuildEntries().First();
        await File.AppendAllTextAsync(stale.Listing.Path, " ");
        var staleResult = await SessionFileDeletion.DeleteAsync(fixture.Store, stale.Listing,
            (_, _) => Task.FromResult(false), CancellationToken.None);
        Assert.False(staleResult.Succeeded);
        Assert.Contains("changed on disk", staleResult.Error);
        Assert.True(File.Exists(stale.Listing.Path));
    }

    [Fact]
    public async Task RenamePrefillsExistingNamePersistsAndKeepsTheSessionIdentityAndHistory()
    {
        using var fixture = await PickerFixture.CreateAsync();
        var state = await TerminalSessionPickerState.LoadAsync(fixture.Store, fixture.CurrentPath, null);
        var target = state.BuildEntries().First(entry => !entry.IsCurrent);
        var id = target.Listing.Id;
        var messageCount = target.Listing.MessageCount;

        Assert.True(await state.RenameAsync(id, "renamed session"));
        var saved = await fixture.Store.LoadAsync(SessionCatalog.Resolve(
            await SessionCatalog.ListAsync(fixture.Store), "renamed session"));
        Assert.Equal(id, saved.Id);
        Assert.Equal(messageCount, saved.ActiveMessages().Count);
        Assert.Contains(state.BuildEntries(), entry => entry.Listing.Id == id &&
            entry.Listing.Name == "renamed session");
    }

    [Fact]
    public async Task RenameFailureLeavesTheSessionFileAndCatalogIntact()
    {
        using var fixture = await PickerFixture.CreateAsync();
        var state = await TerminalSessionPickerState.LoadAsync(fixture.Store, fixture.CurrentPath, null);
        var target = state.BuildEntries().First(entry => !entry.IsCurrent);
        var before = await File.ReadAllBytesAsync(target.Listing.Path);

        Assert.False(await state.RenameAsync(target.Key, "   "));
        Assert.Contains("cannot be empty", state.Status);
        Assert.Equal(before, await File.ReadAllBytesAsync(target.Listing.Path));

        await File.AppendAllTextAsync(target.Listing.Path, " ");
        Assert.False(await state.RenameAsync(target.Key, "stale write"));
        Assert.Contains("Failed to rename", state.Status);
    }

    [Fact]
    public async Task PickerHintsFollowReplacementAndDisabledBindingsWithoutLeakingModelActions()
    {
        using var fixture = await PickerFixture.CreateAsync();
        var state = await TerminalSessionPickerState.LoadAsync(fixture.Store, fixture.CurrentPath, null);
        var directory = Path.Combine(Path.GetTempPath(), "pisharp-picker-hints-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            File.WriteAllText(Path.Combine(directory, "keybindings.json"), """
                {
                  "app.session.toggleSort": "f6",
                  "app.session.toggleNamedFilter": [],
                  "app.session.delete": "f7",
                  "app.session.rename": [],
                  "app.session.togglePath": "f8",
                  "tui.select.confirm": "f9",
                  "tui.select.cancel": "f10"
                }
                """);
            var keymap = new EditorKeymap(directory, windowsBindings: false);
            var hint = TerminalSessionPicker.BuildActionHint(state, keymap.GetDisplayKeys)!;
            Assert.Contains("F6 sort", hint);
            Assert.Contains("F7 delete", hint);
            Assert.Contains("F8 path", hint);
            Assert.DoesNotContain("named", hint);
            Assert.DoesNotContain("rename", hint);
            Assert.DoesNotContain("Ctrl+", hint);

            state.BeginDelete(state.BuildEntries().First(entry => !entry.IsCurrent).Key);
            var confirmation = TerminalSessionPicker.BuildActionHint(state, keymap.GetDisplayKeys)!;
            Assert.Contains("F9 confirm", confirmation);
            Assert.Contains("F10 cancel", confirmation);

            // Ctrl+P/S/D are not editor app actions while the picker owns the same keymap.
            Assert.False(keymap.Matches("app.session.togglePath", Key(ConsoleKey.P, ConsoleModifiers.Control)));
            Assert.False(keymap.Matches("app.session.toggleSort", Key(ConsoleKey.S, ConsoleModifiers.Control)));
            Assert.False(keymap.Matches("app.session.delete", Key(ConsoleKey.D, ConsoleModifiers.Control)));
            Assert.True(keymap.Matches("app.model.cycleForward", Key(ConsoleKey.P, ConsoleModifiers.Control)));
            Assert.True(keymap.Matches("app.models.save", Key(ConsoleKey.S, ConsoleModifiers.Control)));
            Assert.True(keymap.Matches("app.exit", Key(ConsoleKey.D, ConsoleModifiers.Control)));
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task AllScopeDiscoversSessionsFromOtherProjectDirectoriesAndKeepsTheirStores()
    {
        using var fixture = await PickerFixture.CreateAllScopeAsync();
        var state = await TerminalSessionPickerState.LoadAsync(fixture.Store, fixture.CurrentPath, null);
        await state.EnsureAllLoadedAsync();
        state.ToggleScope();
        Assert.Equal(SessionPickerScope.All, state.Scope);
        Assert.Equal("Resume Session (All)", state.BaseTitle);
        Assert.Contains(state.BuildEntries(), entry =>
            entry.Listing.Name == "foreign session" && entry.Listing.WorkingDirectory != fixture.Store.WorkingDirectory);
    }

    [Fact]
    public async Task UnreadableSessionFailsVisiblyWithoutModifyingUnrelatedSessions()
    {
        if (!OperatingSystem.IsLinux()) return;
        using var fixture = await PickerFixture.CreateAsync();
        var listings = await SessionCatalog.ListAsync(fixture.Store);
        var unreadable = listings.First(item => item.Path == fixture.CurrentPath);
        var unrelated = listings.First(item => item.Path != fixture.CurrentPath);
        File.SetUnixFileMode(unreadable.Path, UnixFileMode.None);
        try
        {
            await Assert.ThrowsAnyAsync<Exception>(() => SessionCatalog.ListAsync(fixture.Store));
            Assert.True(File.Exists(unrelated.Path));
        }
        finally { File.SetUnixFileMode(unreadable.Path, UnixFileMode.UserRead | UnixFileMode.UserWrite); }
        Assert.Contains(await SessionCatalog.ListAsync(fixture.Store), item => item.Path == unrelated.Path);
    }

    [Fact]
    public async Task ConcurrentCatalogChangeRejectsStaleMutationAndPreservesUnrelatedSessions()
    {
        using var fixture = await PickerFixture.CreateAsync();
        var first = await TerminalSessionPickerState.LoadAsync(fixture.Store, fixture.CurrentPath, null,
            (catalog, listing, token) => SessionFileDeletion.DeleteAsync(catalog, listing,
                (_, _) => Task.FromResult(false), token));
        var second = await TerminalSessionPickerState.LoadAsync(fixture.Store, fixture.CurrentPath, null,
            (catalog, listing, token) => SessionFileDeletion.DeleteAsync(catalog, listing,
                (_, _) => Task.FromResult(false), token));
        var victim = first.BuildEntries().First(entry => !entry.IsCurrent);
        var other = first.BuildEntries().First(entry => !entry.IsCurrent && entry.Key != victim.Key);
        Assert.True(first.BeginDelete(victim.Key));
        Assert.True(await first.ConfirmDeleteAsync());
        Assert.False(File.Exists(victim.Listing.Path));
        Assert.True(File.Exists(other.Listing.Path));

        Assert.True(second.BeginDelete(victim.Key));
        Assert.False(await second.ConfirmDeleteAsync());
        Assert.Contains("Failed to delete", second.Status);
        Assert.True(File.Exists(other.Listing.Path));
    }

    [Fact]
    public async Task LongSessionPathsAreClippedSafelyAcrossTerminalWidths()
    {
        using var fixture = await PickerFixture.CreateAsync();
        var state = await TerminalSessionPickerState.LoadAsync(fixture.Store, fixture.CurrentPath, null);
        state.TogglePath();
        var options = state.BuildEntries().Select(state.BuildOption).ToArray();
        foreach (var width in new[] { 24, 40, 80, 140 })
        {
            var list = new TerminalSelectionList<SessionPickerEntry>("Resume Session (Current Folder)",
                options, scopedOptions: options, allLabel: "All", scopedLabel: "Current Folder",
                keymap: new EditorKeymap());
            var rendered = list.Render(width, 20);
            Assert.All(rendered, line => Assert.True(TerminalTextLayout.Width(line) <= Math.Max(12, width - 8)));
            Assert.NotEmpty(rendered);
        }
    }

    [Fact]
    public async Task CtrlBackspaceUsesTheQueryUnlessTheQueryIsEmpty()
    {
        using var fixture = await PickerFixture.CreateAsync();
        var state = await TerminalSessionPickerState.LoadAsync(fixture.Store, fixture.CurrentPath, null);
        var list = new TerminalSelectionList<SessionPickerEntry>("Resume Session (Current Folder)",
            state.BuildEntries().Select(state.BuildOption).ToArray());
        list.HandleInput(new(null, "abc"));
        Assert.Equal("abc", list.SearchQuery);

        // Non-empty query forwards the key to the search input, which ignores modified
        // Backspace under Kitty, so the query is unchanged and no confirmation starts.
        Assert.True(TerminalSessionPicker.ShouldForwardNoninvasiveDelete(list.SearchQuery));
        list.HandleInput(Event(Key(ConsoleKey.Backspace)));
        list.HandleInput(Event(Key(ConsoleKey.Backspace)));
        list.HandleInput(Event(Key(ConsoleKey.Backspace)));
        Assert.Equal("", list.SearchQuery);
        Assert.Null(state.ConfirmingDeleteId);
        var selected = list.Selected!.Option.Value;
        Assert.True(TerminalSessionPicker.ShouldEnterDeleteConfirmation(""));
        Assert.False(TerminalSessionPicker.ShouldEnterDeleteConfirmation("abc"));
        Assert.True(TerminalSessionPicker.ShouldForwardNoninvasiveDelete("abc"));
        Assert.False(TerminalSessionPicker.ShouldForwardNoninvasiveDelete(""));
        Assert.True(state.BeginDelete(selected.Key) || state.Status?.Contains("currently active") == true);
    }

    private static ConsoleKeyInfo Key(ConsoleKey key, ConsoleModifiers modifiers = 0) => new('\0', key,
        modifiers.HasFlag(ConsoleModifiers.Shift), modifiers.HasFlag(ConsoleModifiers.Alt),
        modifiers.HasFlag(ConsoleModifiers.Control));

    private static TerminalInputEvent Event(ConsoleKeyInfo key) => new(key, null);

    private sealed class PickerFixture : IDisposable
    {
        private PickerFixture(string root, ConversationStore store, string currentPath)
        {
            Root = root;
            Store = store;
            CurrentPath = currentPath;
        }

        public string Root { get; }
        public ConversationStore Store { get; }
        public string CurrentPath { get; }

        public static async Task<PickerFixture> CreateAsync()
        {
            var root = Path.Combine(Path.GetTempPath(), "pisharp-picker-state-" + Guid.NewGuid().ToString("N"));
            var sessions = Path.Combine(root, "sessions");
            var store = new ConversationStore(root, sessions);
            var current = new ConversationSession(root, "fixture", null);
            current.Rename("current session");
            current.Append(new Microsoft.Extensions.AI.ChatMessage(Microsoft.Extensions.AI.ChatRole.User, "hello"));
            var currentPath = store.NewPath(current);
            await store.SaveAsync(current, currentPath);

            var unnamed = new ConversationSession(root, "fixture", null);
            await store.SaveAsync(unnamed, store.NewPath(unnamed));
            await Task.Delay(10);

            var namedOne = new ConversationSession(root, "fixture", null);
            namedOne.Rename("named one");
            namedOne.Append(new Microsoft.Extensions.AI.ChatMessage(Microsoft.Extensions.AI.ChatRole.User, "one"));
            namedOne.Append(new Microsoft.Extensions.AI.ChatMessage(Microsoft.Extensions.AI.ChatRole.Assistant, "two"));
            await store.SaveAsync(namedOne, store.NewPath(namedOne));
            await Task.Delay(10);

            var namedTwo = new ConversationSession(root, "fixture", null);
            namedTwo.Rename("named two");
            namedTwo.Append(new Microsoft.Extensions.AI.ChatMessage(Microsoft.Extensions.AI.ChatRole.User, "two"));
            await store.SaveAsync(namedTwo, store.NewPath(namedTwo));
            return new(root, store, currentPath);
        }

        public static async Task<PickerFixture> CreateAllScopeAsync()
        {
            var root = Path.Combine(Path.GetTempPath(), "pisharp-picker-all-" + Guid.NewGuid().ToString("N"));
            var sessionsRoot = Path.Combine(root, "sessions");
            var localWorking = Path.Combine(root, "project-a");
            var foreignWorking = Path.Combine(root, "project-b");
            Directory.CreateDirectory(localWorking);
            Directory.CreateDirectory(foreignWorking);
            var localDirectory = Path.Combine(sessionsRoot, "aaaa");
            var foreignDirectory = Path.Combine(sessionsRoot, "bbbb");
            Directory.CreateDirectory(localDirectory);
            Directory.CreateDirectory(foreignDirectory);
            var store = new ConversationStore(localWorking, localDirectory, defaultDirectoryPath: localDirectory);
            var local = new ConversationSession(localWorking, "fixture", null);
            local.Rename("local session");
            var currentPath = store.NewPath(local);
            await store.SaveAsync(local, currentPath);

            var foreignStore = new ConversationStore(foreignWorking, foreignDirectory, defaultDirectoryPath: foreignDirectory);
            var foreign = new ConversationSession(foreignWorking, "fixture", null);
            foreign.Rename("foreign session");
            await foreignStore.SaveAsync(foreign, foreignStore.NewPath(foreign));
            return new(root, store, currentPath);
        }

        public void Dispose() => Directory.Delete(Root, true);
    }
}
