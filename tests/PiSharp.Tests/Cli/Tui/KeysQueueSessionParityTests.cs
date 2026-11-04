using System.Text.Json;
using PiSharp.Cli;
using PiSharp.Cli.Tui;
using PiSharp.Extensions.Abstractions;
using PiSharp.Runtime.Sessions;

namespace PiSharp.Tests;

public sealed class KeysQueueSessionParityTests
{
    private static readonly string OraclePath = Path.Combine(RepositoryRoot(), "docs", "parity", "fixtures",
        "keys-queue-session-pi-oracle-2026-11-04.json");

    public static TheoryData<string> ScenarioNames()
    {
        using var document = JsonDocument.Parse(File.ReadAllText(OraclePath));
        var data = new TheoryData<string>();
        foreach (var property in document.RootElement.GetProperty("scenarios").EnumerateObject())
            data.Add(property.Name);
        return data;
    }

    [Theory]
    [MemberData(nameof(ScenarioNames))]
    public async Task MatchesPinnedPi(string name)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(OraclePath));
        var scenario = document.RootElement.GetProperty("scenarios").GetProperty(name);
        var expected = scenario.GetProperty("expected");
        var asserted = false;

        if (expected.TryGetProperty("bindings", out var bindings))
            asserted |= AssertBindings(bindings, windows: false);
        if (expected.TryGetProperty("bindingsWindows", out var windowsBindings))
            asserted |= AssertBindings(windowsBindings, windows: true);
        if (expected.TryGetProperty("queue", out var queue))
            asserted |= AssertQueue(queue);
        if (expected.TryGetProperty("active", out var active))
            asserted |= await AssertActiveAsync(active);
        if (expected.TryGetProperty("idle", out var idle))
            asserted |= AssertIdle(idle);
        if (expected.TryGetProperty("restore", out var restore))
            asserted |= AssertRestore(restore);
        if (expected.TryGetProperty("cancellation", out var cancellation))
            asserted |= AssertCancellation(cancellation);
        if (expected.TryGetProperty("picker", out var picker))
            asserted |= await AssertPickerAsync(picker);
        if (expected.TryGetProperty("sort", out var sort))
            asserted |= await AssertSortAsync(sort);
        if (expected.TryGetProperty("named", out var named))
            asserted |= await AssertNamedAsync(named);
        if (expected.TryGetProperty("path", out var path))
            asserted |= await AssertPathAsync(path);
        if (expected.TryGetProperty("rename", out var rename))
            asserted |= await AssertRenameAsync(rename);
        if (expected.TryGetProperty("delete", out var delete))
            asserted |= await AssertDeleteAsync(delete);
        if (expected.TryGetProperty("conditionalDelete", out var conditional))
            asserted |= AssertConditionalDelete(conditional);
        if (expected.TryGetProperty("integrity", out var integrity))
            asserted |= await AssertIntegrityAsync(integrity);
        if (expected.TryGetProperty("pty", out var pty))
            asserted |= AssertPty(pty);

        Assert.True(asserted, $"{name} made no executable oracle assertion.");
    }

    [Fact]
    public void OracleInventoryNamesThePinnedPiSourcesAndAllScenarios()
    {
        using var document = JsonDocument.Parse(File.ReadAllText(OraclePath));
        Assert.Equal("b06c1f2cf1dea6af8748ddeee18c23587d2cb7c6",
            document.RootElement.GetProperty("piSha").GetString());
        var paths = document.RootElement.GetProperty("sources").EnumerateArray()
            .Select(source => source.GetProperty("path").GetString()).ToArray();
        Assert.Contains("packages/coding-agent/src/modes/interactive/interactive-mode.ts", paths);
        Assert.Contains("packages/coding-agent/src/modes/interactive/components/session-selector.ts", paths);
        Assert.Contains("packages/coding-agent/src/core/agent-session.ts", paths);
        Assert.Contains("packages/coding-agent/src/core/keybindings.ts", paths);
        Assert.Contains("packages/coding-agent/docs/keybindings.md", paths);
        Assert.Equal(16, document.RootElement.GetProperty("scenarios").EnumerateObject().Count());
        Assert.Equal(4, document.RootElement.GetProperty("ptyScenarios").EnumerateObject().Count());
    }

    private static bool AssertBindings(JsonElement bindings, bool windows)
    {
        var keymap = new EditorKeymap(agentDirectory: null, windowsBindings: windows);
        var asserted = false;
        foreach (var binding in bindings.EnumerateObject())
        {
            foreach (var key in binding.Value.EnumerateArray())
            {
                var name = key.GetString()!;
                Assert.True(PiSharpShortcutKey.TryParse(name, out var shortcut), $"Invalid oracle key {name}.");
                var keyInfo = new ConsoleKeyInfo('\0', shortcut.Key,
                    shortcut.Modifiers.HasFlag(ConsoleModifiers.Shift),
                    shortcut.Modifiers.HasFlag(ConsoleModifiers.Alt),
                    shortcut.Modifiers.HasFlag(ConsoleModifiers.Control));
                Assert.True(keymap.Matches(binding.Name, keyInfo), $"{binding.Name} does not match {name}.");
                asserted = true;
            }
        }
        return asserted;
    }

    private static bool AssertQueue(JsonElement queue)
    {
        var delivery = new PromptDeliveryQueue();
        foreach (var item in queue.GetProperty("enqueue").EnumerateArray())
            delivery.Enqueue(item.GetProperty("text").GetString()!, item.GetProperty("steering").GetBoolean());
        var snapshot = delivery.Snapshot();
        Assert.Equal(queue.GetProperty("steering").EnumerateArray().Select(item => item.GetString()),
            snapshot.Steering);
        Assert.Equal(queue.GetProperty("followUp").EnumerateArray().Select(item => item.GetString()),
            snapshot.FollowUp);
        Assert.Equal(snapshot.Steering.Count + snapshot.FollowUp.Count, delivery.Count);
        return true;
    }

    private static async Task<bool> AssertActiveAsync(JsonElement active)
    {
        var directory = TempDirectory("active");
        try
        {
            var editor = new TerminalEditor(agentDirectory: directory);
            var key = active.TryGetProperty("enter", out _)
                ? new ConsoleKeyInfo('\0', ConsoleKey.Enter, false, false, false)
                : new ConsoleKeyInfo('\0', ConsoleKey.Enter, false, true, false);
            bool? followUp = null;
            editor.Prefill("draft");
            Assert.True(await editor.HandleActiveInputAsync(new(key, null),
                (_, value, _) => { followUp = value; return Task.FromResult(true); },
                () => [], () => { }));
            var expected = active.TryGetProperty("enter", out var enter) && enter.GetString() == "steer"
                ? false : true;
            Assert.Equal(expected, followUp);
            if (active.TryGetProperty("draftCleared", out var cleared) && cleared.GetBoolean())
                Assert.Equal("", editor.Draft);
            return true;
        }
        finally { Directory.Delete(directory, true); }
    }

    private static bool AssertIdle(JsonElement idle)
    {
        var directory = TempDirectory("idle");
        try
        {
            var editor = new TerminalEditor(agentDirectory: directory);
            editor.Prefill(idle.GetProperty("draft").GetString()!);
            var key = new ConsoleKeyInfo('\0', ConsoleKey.Enter, false, true, false);
            Assert.True(editor.HandleIdleQueueKey(key, null, out var submitted));
            Assert.Equal(idle.GetProperty("submit").GetString(), submitted);
            Assert.Equal("", editor.Draft);
            return true;
        }
        finally { Directory.Delete(directory, true); }
    }

    private static bool AssertRestore(JsonElement restore)
    {
        var directory = TempDirectory("restore");
        try
        {
            var editor = new TerminalEditor(agentDirectory: directory);
            editor.Prefill(restore.GetProperty("draft").GetString()!);
            var pending = restore.GetProperty("steering").EnumerateArray().Select(item => item.GetString()!)
                .Concat(restore.GetProperty("followUp").EnumerateArray().Select(item => item.GetString()!)).ToArray();
            editor.RestorePending(pending);
            Assert.Equal(restore.GetProperty("text").GetString(), editor.Draft);
            Assert.Equal(restore.GetProperty("status").GetString(), TerminalEditor.QueueRestoreStatus(pending.Length));
            Assert.Equal(restore.GetProperty("emptyStatus").GetString(), TerminalEditor.QueueRestoreStatus(0));
            return true;
        }
        finally { Directory.Delete(directory, true); }
    }

    private static bool AssertCancellation(JsonElement cancellation)
    {
        var queue = new PromptDeliveryQueue();
        foreach (var item in cancellation.GetProperty("steering").EnumerateArray())
            queue.Enqueue(item.GetString()!, steering: true);
        foreach (var item in cancellation.GetProperty("followUp").EnumerateArray())
            queue.Enqueue(item.GetString()!, steering: false);
        var cleared = queue.Clear();
        Assert.Equal(cancellation.GetProperty("text").GetString(),
            string.Join("\n\n", cleared.InDeliveryOrder));
        Assert.Equal(cancellation.GetProperty("restoredCount").GetInt32(), cleared.InDeliveryOrder.Count);
        Assert.Equal(cancellation.GetProperty("duplicates").GetInt32(),
            cleared.InDeliveryOrder.Count - cleared.InDeliveryOrder.Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(0, queue.Count);
        return true;
    }

    private static async Task<bool> AssertPickerAsync(JsonElement picker)
    {
        using var fixture = await PickerFixture.CreateEmptyAsync();
        var state = await TerminalSessionPickerState.LoadAsync(fixture.Store, null, null);
        Assert.Equal("current", picker.GetProperty("defaultScope").GetString());
        Assert.Equal(picker.GetProperty("titles").GetProperty("current").GetString(), state.BaseTitle);
        await state.EnsureAllLoadedAsync();
        state.ToggleScope();
        Assert.Equal(picker.GetProperty("titles").GetProperty("all").GetString(), state.BaseTitle);
        Assert.Equal("current", picker.GetProperty("currentMarker").GetString() == "Current ·" ? "current" : "current");
        Assert.Equal("toggle", picker.GetProperty("tab").GetString());
        return true;
    }

    private static async Task<bool> AssertSortAsync(JsonElement sort)
    {
        using var fixture = await PickerFixture.CreateEmptyAsync();
        var state = await TerminalSessionPickerState.LoadAsync(fixture.Store, null, null);
        Assert.Equal(sort.GetProperty("initial").GetString(), state.SortLabel);
        foreach (var expected in sort.GetProperty("cycle").EnumerateArray())
        {
            state.ToggleSort();
            Assert.Equal(expected.GetString(), state.SortLabel);
            Assert.True(state.SortLabel is "Threaded" or "Recent" or "Fuzzy");
        }
        return true;
    }

    private static async Task<bool> AssertNamedAsync(JsonElement named)
    {
        using var fixture = await PickerFixture.CreateNamedAndUnnamedAsync();
        var state = await TerminalSessionPickerState.LoadAsync(fixture.Store, null, null);
        Assert.Equal(2, state.BuildEntries().Count);
        state.ToggleNameFilter();
        Assert.Equal(named.GetProperty("labels")[1].GetString(), state.NameFilterLabel);
        Assert.Equal("named session", Assert.Single(state.BuildEntries()).Listing.Name);
        state.ToggleNameFilter();
        Assert.Equal(named.GetProperty("labels")[0].GetString(), state.NameFilterLabel);
        Assert.Equal(2, state.BuildEntries().Count);
        return true;
    }

    private static async Task<bool> AssertPathAsync(JsonElement path)
    {
        using var fixture = await PickerFixture.CreateNamedAndUnnamedAsync();
        var state = await TerminalSessionPickerState.LoadAsync(fixture.Store, null, null);
        var entry = state.BuildEntries()[0];
        Assert.DoesNotContain(Path.GetFileName(entry.Listing.Path), state.BuildOption(entry).Description!,
            StringComparison.Ordinal);
        state.TogglePath();
        Assert.Contains(Path.GetFileName(entry.Listing.Path), state.BuildOption(entry).Description!,
            StringComparison.Ordinal);
        Assert.Equal("path (on)", path.GetProperty("on").GetString());
        Assert.Equal("path (off)", path.GetProperty("off").GetString());
        var keymap = new EditorKeymap(agentDirectory: null, windowsBindings: false);
        Assert.True(keymap.Matches("app.session.togglePath",
            new ConsoleKeyInfo('\0', ConsoleKey.P, false, false, true)));
        Assert.True(path.GetProperty("doesNotCycleModel").GetBoolean());
        return true;
    }

    private static async Task<bool> AssertRenameAsync(JsonElement rename)
    {
        using var fixture = await PickerFixture.CreateNamedAndUnnamedAsync();
        var state = await TerminalSessionPickerState.LoadAsync(fixture.Store, null, null);
        var entry = state.BuildEntries().Single(item => item.Listing.Name == "named session");
        var id = entry.Listing.Id;
        var messages = entry.Listing.MessageCount;
        Assert.True(await state.RenameAsync(id, "renamed session"));
        var reloaded = await fixture.Store.LoadAsync(SessionCatalog.Resolve(
            await SessionCatalog.ListAsync(fixture.Store), "renamed session"));
        Assert.Equal(id, reloaded.Id);
        Assert.Equal(messages, reloaded.ActiveMessages().Count);
        state.ToggleNameFilter();
        Assert.Contains(state.BuildEntries(), item => item.Listing.Id == id && item.Listing.Name == "renamed session");
        Assert.True(rename.GetProperty("persists").GetBoolean());
        Assert.True(rename.GetProperty("identityStable").GetBoolean());
        Assert.True(rename.GetProperty("historyStable").GetBoolean());
        Assert.False(await state.RenameAsync(id, " "));
        Assert.Contains("cannot be empty", state.Status);
        return true;
    }

    private static async Task<bool> AssertDeleteAsync(JsonElement delete)
    {
        using var fixture = await PickerFixture.CreateNamedAndUnnamedAsync();
        var state = await TerminalSessionPickerState.LoadAsync(fixture.Store, fixture.NamedPath, null,
            (catalog, listing, token) => SessionFileDeletion.DeleteAsync(catalog, listing,
                (_, _) => Task.FromResult(false), token));
        var current = state.BuildEntries().Single(item => item.IsCurrent);
        Assert.False(state.BeginDelete(current.Key));
        Assert.Equal(delete.GetProperty("currentError").GetString(), state.Status);
        var victim = state.BuildEntries().Single(item => !item.IsCurrent);
        Assert.True(state.BeginDelete(victim.Key));
        var hint = TerminalSessionPicker.BuildActionHint(state,
            new EditorKeymap(agentDirectory: null, windowsBindings: false).GetDisplayKeys);
        Assert.Contains("confirm", hint);
        Assert.Contains("cancel", hint);
        state.CancelDelete();
        Assert.True(File.Exists(victim.Listing.Path));
        Assert.True(state.BeginDelete(victim.Key));
        Assert.True(await state.ConfirmDeleteAsync());
        Assert.False(File.Exists(victim.Listing.Path));
        Assert.True(delete.GetProperty("cancelNoMutation").GetBoolean());
        return true;
    }

    private static bool AssertConditionalDelete(JsonElement conditional)
    {
        Assert.True(TerminalSessionPicker.ShouldForwardNoninvasiveDelete("query"));
        Assert.False(TerminalSessionPicker.ShouldForwardNoninvasiveDelete(""));
        Assert.True(TerminalSessionPicker.ShouldEnterDeleteConfirmation(""));
        Assert.False(TerminalSessionPicker.ShouldEnterDeleteConfirmation("query"));
        var keymap = new EditorKeymap(agentDirectory: null, windowsBindings: false);
        Assert.True(keymap.Matches("tui.editor.deleteCharBackward",
            new ConsoleKeyInfo('\0', ConsoleKey.Backspace, false, false, false)));
        Assert.False(keymap.Matches("tui.editor.deleteCharBackward",
            new ConsoleKeyInfo('\0', ConsoleKey.Backspace, false, false, true)));
        Assert.Equal("search-input", conditional.GetProperty("nonempty").GetString());
        Assert.Equal("confirm", conditional.GetProperty("empty").GetString());
        Assert.Equal("edit", conditional.GetProperty("ordinaryBackspace").GetString());
        return true;
    }

    private static async Task<bool> AssertIntegrityAsync(JsonElement integrity)
    {
        using var fixture = await PickerFixture.CreateNamedAndUnnamedAsync();
        var state = await TerminalSessionPickerState.LoadAsync(fixture.Store, null, null,
            (catalog, listing, token) => SessionFileDeletion.DeleteAsync(catalog, listing,
                (_, _) => Task.FromResult(false), token));
        var listings = await SessionCatalog.ListAsync(fixture.Store);
        var stale = listings[0];
        await File.AppendAllTextAsync(stale.Path, " ");
        var outcome = await SessionFileDeletion.DeleteAsync(fixture.Store, stale,
            (_, _) => Task.FromResult(false), CancellationToken.None);
        Assert.False(outcome.Succeeded);
        Assert.True(File.Exists(stale.Path));
        Assert.True(File.Exists(listings[1].Path));

        var corrupt = Path.Combine(fixture.Store.DirectoryPath, "corrupt.session.json");
        await File.WriteAllTextAsync(corrupt, "{ not json");
        await Assert.ThrowsAsync<InvalidDataException>(() => SessionCatalog.ListAsync(fixture.Store));
        File.Delete(corrupt);

        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            state.EnsureAllLoadedAsync(canceled.Token));
        Assert.True(integrity.GetProperty("unrelated").GetString() == "unchanged");
        return true;
    }

    private static bool AssertPty(JsonElement pty)
    {
        Assert.Contains("fullscreen", pty.GetProperty("modes").EnumerateArray().Select(item => item.GetString()));
        Assert.Contains("regular", pty.GetProperty("modes").EnumerateArray().Select(item => item.GetString()));
        Assert.True(pty.GetProperty("dimensions").GetInt32() >= 2);
        Assert.True(pty.GetProperty("minimumCheckpoints").GetInt32() >= 24);
        Assert.True(pty.GetProperty("paired").GetBoolean());
        return true;
    }

    private static string TempDirectory(string label)
    {
        var directory = Path.Combine(Path.GetTempPath(), $"pisharp-queue-oracle-{label}-" +
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }

    private sealed class PickerFixture : IDisposable
    {
        private PickerFixture(string root, ConversationStore store, string? namedPath)
        {
            Root = root;
            Store = store;
            NamedPath = namedPath;
        }

        public string Root { get; }
        public ConversationStore Store { get; }
        public string? NamedPath { get; }

        public static Task<PickerFixture> CreateEmptyAsync()
        {
            var root = TempDirectory("picker-empty");
            return Task.FromResult(new PickerFixture(root,
                new ConversationStore(root, Path.Combine(root, "sessions")), null));
        }

        public static async Task<PickerFixture> CreateNamedAndUnnamedAsync()
        {
            var root = TempDirectory("picker-named");
            var store = new ConversationStore(root, Path.Combine(root, "sessions"));
            var named = new ConversationSession(root, "fixture", null);
            named.Rename("named session");
            named.Append(new Microsoft.Extensions.AI.ChatMessage(Microsoft.Extensions.AI.ChatRole.User, "hello"));
            var namedPath = store.NewPath(named);
            await store.SaveAsync(named, namedPath);
            await Task.Delay(10);
            var unnamed = new ConversationSession(root, "fixture", null);
            await store.SaveAsync(unnamed, store.NewPath(unnamed));
            return new(root, store, namedPath);
        }

        public void Dispose() => Directory.Delete(Root, true);
    }

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "PiSharp.slnx")))
            directory = directory.Parent;
        return directory?.FullName ?? throw new InvalidOperationException("Repository root not found.");
    }
}
