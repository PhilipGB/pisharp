using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;
using PiSharp.Runtime;
using PiSharp.Runtime.Sessions;

namespace PiSharp.Tests;

public sealed class ConversationSessionTests
{
    [Fact]
    public void ExplicitNullHeadSurvivesNativeRoundTripWithoutSelectingLastEntry()
    {
        var session = new ConversationSession(Path.GetTempPath(), "fixture", null);
        session.Append(new ChatMessage(ChatRole.User, "retained branch"));
        session.Append(new ChatMessage(ChatRole.Assistant, "retained reply"));
        session.Tree.Select(null);

        var restored = ConversationSession.Parse(session.ToJson());

        Assert.Null(restored.Tree.HeadId);
        Assert.Equal(2, restored.Tree.Entries.Count);
        Assert.Empty(restored.ActiveMessages());
        Assert.Empty(restored.ContextMessages());
    }

    [Fact]
    public void InvalidUsageOnInactiveBranchFailsClosed()
    {
        var session = new ConversationSession(Path.GetTempPath(), "fixture", null);
        session.Append(new ChatMessage(ChatRole.User, "root"));
        var root = session.Tree.HeadId;
        session.AppendUsage(new UsageRecord("fixture", "model", 10, 2, 0, 0, 12, null, CachedWriteTokens: 0));
        session.Tree.Select(root);
        var document = System.Text.Json.Nodes.JsonNode.Parse(session.ToJson())!;
        document["Entries"]![1]!["Payload"]!["Cost"] = -1;

        Assert.Contains("Invalid usage record", Assert.Throws<InvalidDataException>(() =>
            ConversationSession.Parse(document.ToJsonString())).Message);
    }

    [Fact]
    public void CorruptUnselectedBranchCannotBeSilentlyLoaded()
    {
        var session = new ConversationSession(Path.GetTempPath(), "fixture", null);
        session.Append(new ChatMessage(ChatRole.User, "root"));
        var root = session.Tree.HeadId;
        session.Append(new ChatMessage(ChatRole.User, "inactive"));
        session.Tree.Select(root);
        var document = System.Text.Json.Nodes.JsonNode.Parse(session.ToJson())!;
        document["Entries"]![1]!["Payload"]!["Errors"] = null;
        Assert.Contains("Incomplete chat record", Assert.Throws<InvalidDataException>(() =>
            ConversationSession.Parse(document.ToJsonString())).Message);
    }

    [Fact]
    public void MalformedBashExecutionOnInactiveBranchCannotBeLoaded()
    {
        var session = new ConversationSession(Path.GetTempPath(), "fixture", null);
        session.Append(new ChatMessage(ChatRole.User, "root"));
        var root = session.Tree.HeadId;
        session.AppendBashExecution(new BashExecutionRecord("printf output", "output", 0, false, false, null, true));
        session.Tree.Select(root);
        var document = System.Text.Json.Nodes.JsonNode.Parse(session.ToJson())!;
        document["Entries"]![1]!["Payload"]!["cancelled"] = "false";
        Assert.Contains("Invalid Bash execution", Assert.Throws<InvalidDataException>(() =>
            ConversationSession.Parse(document.ToJsonString())).Message);
    }

    [Fact]
    public void InvalidCheckpointOnInactiveBranchFailsClosed()
    {
        var session = new ConversationSession(Path.GetTempPath(), "fixture", null);
        var head = session.Tree.Append("run_started", System.Text.Json.JsonSerializer.SerializeToElement(new { runId = "test", prompt = "hi" })).Id;
        session.Tree.Append("tool_intent", System.Text.Json.JsonSerializer.SerializeToElement(new { runId = "test", operationId = "op", name = "write", arguments = new { path = "file" } }));
        session.Tree.Select(head);
        var json = System.Text.Json.Nodes.JsonNode.Parse(session.ToJson())!;
        json["Entries"]![1]!["Payload"]!["operationId"] = null;
        Assert.Contains("Invalid checkpoint", Assert.Throws<InvalidDataException>(() =>
            ConversationSession.Parse(json.ToJsonString())).Message);
    }

    [Fact]
    public void StartupModelAndNameFlagsValidateArguments()
    {
        var cli = PiSharp.Cli.CliArguments.Parse(["--model", "local/test", "--name", "Research notes", "--no-session"]);
        Assert.Equal("local/test", cli.ModelOverride);
        Assert.Equal("Research notes", cli.SessionName);
        Assert.Equal("draft", PiSharp.Cli.CliArguments.Parse(["-n", "draft"]).SessionName);
        Assert.Throws<ArgumentException>(() => PiSharp.Cli.CliArguments.Parse(["--model"]));
        Assert.Throws<ArgumentException>(() => PiSharp.Cli.CliArguments.Parse(["--name", ""]));
        Assert.Throws<ArgumentException>(() => PiSharp.Cli.CliArguments.Parse(["--list-models", "--name", "x"]));
    }

    [Fact]
    public void ForkAtUserExcludesSelectedTurnKeepsSourceAndRestoresDraft()
    {
        var original = new ConversationSession(Path.GetTempPath(), "fixture", null);
        original.Append(new ChatMessage(ChatRole.User, "first"));
        original.Append(new ChatMessage(ChatRole.Assistant, "answer"));
        var oldHead = original.Tree.HeadId;
        original.Append(new ChatMessage(ChatRole.User, "second\nline"));
        var selectedId = original.Tree.HeadId!;
        original.Append(new ChatMessage(ChatRole.Assistant, "discarded"));
        var (fork, draft) = original.ForkAtUser(selectedId);
        Assert.Equal("second\nline", draft);
        Assert.Equal(oldHead, fork.Tree.HeadId);
        Assert.Equal(["first", "answer"], fork.ActiveMessages().Select(message => message.Text));
        Assert.Equal(["first", "answer", "second\nline", "discarded"], original.ActiveMessages().Select(message => message.Text));
        Assert.NotEqual(original.Id, fork.Id);
        var parsed = ConversationSession.Parse(fork.ToJson());
        Assert.Equal(oldHead, parsed.Tree.HeadId);
        Assert.Equal(["first", "answer"], parsed.ActiveMessages().Select(message => message.Text));
        Assert.Throws<ArgumentException>(() => original.ForkAtUser(oldHead!));
        Assert.Throws<ArgumentException>(() => original.ForkAtUser("missing"));
        original.SelectModel("other", null);
        Assert.Empty(original.ForkableUserMessages());
        Assert.Throws<InvalidOperationException>(() => original.ForkAtUser(selectedId));
    }

    [Fact]
    public void UserMessagesForForkingIncludesMessagesFromEveryBranch()
    {
        var session = new ConversationSession(Path.GetTempPath(), "fixture", null);
        session.Append(new ChatMessage(ChatRole.User, "first"));
        var firstId = session.Tree.HeadId!;
        session.Append(new ChatMessage(ChatRole.Assistant, "answer"));
        session.Append(new ChatMessage(ChatRole.User, "other branch"));
        var otherBranchId = session.Tree.HeadId!;
        session.Tree.Select(firstId);
        session.Append(new ChatMessage(ChatRole.User, "alternate"));

        var alternateId = session.Tree.HeadId!;
        Assert.Equal([(firstId, "first"), (otherBranchId, "other branch"), (alternateId, "alternate")],
            session.UserMessagesForForking());
        Assert.Equal([(firstId, "first"), (alternateId, "alternate")], session.ForkableUserMessages());

        var (fork, prompt) = session.ForkAtUser(otherBranchId);
        Assert.Equal("other branch", prompt);
        Assert.Equal(["first", "answer"], fork.ActiveMessages().Select(message => message.Text));
    }

    [Fact]
    public void ForkTextIncludesTextPartsAndIgnoresImages()
    {
        var session = new ConversationSession(Path.GetTempPath(), "fixture", null);
        session.Append(new ChatMessage(ChatRole.User,
            [new TextContent("before "), new DataContent(new byte[] { 1, 2, 3 }, "image/png"), new TextContent("after")]));
        var entryId = session.Tree.HeadId!;

        Assert.Equal([(entryId, "before after")], session.UserMessagesForForking());
        Assert.Equal("before after", session.ForkAtUser(entryId).Prompt);
    }

    [Fact]
    public async Task ModelChangePersistsAndCrossModelBranchSelectionFailsClosed()
    {
        var cwd = Path.Combine(Path.GetTempPath(), "pisharp-model-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(cwd);
        try
        {
            var session = new ConversationSession(cwd, "first", null);
            var run = await ConversationRun.OpenAsync(new PiAgent(new ScriptedClient(), new CodingTools(cwd)), session);
            await foreach (var _ in run.RunStreamingAsync("first turn")) { }
            var oldHead = session.Tree.HeadId;
            session.SelectModel("second", null);
            var changedRun = await ConversationRun.OpenAsync(new PiAgent(new ScriptedClient(), new CodingTools(cwd)), session);
            await foreach (var _ in changedRun.RunStreamingAsync("second turn")) { }
            var store = new ConversationStore(cwd, Path.Combine(cwd, "sessions"));
            var path = store.NewPath(session);
            await store.SaveAsync(session, path);
            var reloaded = await store.LoadAsync(path);
            Assert.Equal("second", reloaded.Model);
            var resumed = await ConversationRun.OpenAsync(new PiAgent(new ScriptedClient(), new CodingTools(cwd)), reloaded);
            await Assert.ThrowsAsync<InvalidOperationException>(() => resumed.SelectAsync(oldHead));
            Assert.Equal(session.Tree.HeadId, reloaded.Tree.HeadId);
        }
        finally { Directory.Delete(cwd, recursive: true); }
    }

    [Fact]
    public async Task ConcurrentStoresRejectStaleWrites()
    {
        var cwd = Path.Combine(Path.GetTempPath(), "pisharp-store-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(cwd);
        try
        {
            var directory = Path.Combine(cwd, "sessions");
            var first = new ConversationStore(cwd, directory);
            var second = new ConversationStore(cwd, directory);
            var original = new ConversationSession(cwd, "fixture", null);
            var path = first.NewPath(original);
            await first.SaveAsync(original, path);
            var stale = await second.LoadAsync(path);
            original.Append(new ChatMessage(ChatRole.User, "newer"));
            await first.SaveAsync(original, path);
            stale.Append(new ChatMessage(ChatRole.User, "stale"));
            await Assert.ThrowsAsync<InvalidDataException>(() => second.SaveAsync(stale, path));
            Assert.Equal("newer", (await first.LoadAsync(path)).ActiveMessages().Single().Text);
            if (OperatingSystem.IsLinux())
            {
                Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, File.GetUnixFileMode(directory));
                Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path + ".lock"));
            }
        }
        finally { Directory.Delete(cwd, recursive: true); }
    }

    [Fact]
    public async Task FailedAtomicPublicationKeepsTargetAndCleansTemporarySnapshot()
    {
        var cwd = Path.Combine(Path.GetTempPath(), "pisharp-atomic-failure-" + Guid.NewGuid().ToString("N"));
        var directory = Path.Combine(cwd, "sessions");
        Directory.CreateDirectory(directory);
        try
        {
            var target = Path.Combine(directory, "occupied.session.json");
            Directory.CreateDirectory(target);
            var sentinel = Path.Combine(target, "keep.txt");
            await File.WriteAllTextAsync(sentinel, "unchanged");
            var store = new ConversationStore(cwd, directory);

            var failure = await Record.ExceptionAsync(() => store.SaveAsync(
                new ConversationSession(cwd, "fixture", null), target));

            Assert.NotNull(failure);
            Assert.True(failure is IOException or UnauthorizedAccessException, failure.ToString());
            Assert.True(Directory.Exists(target));
            Assert.Equal("unchanged", await File.ReadAllTextAsync(sentinel));
            Assert.Empty(Directory.EnumerateFiles(directory, ".pisharp-*.tmp"));
        }
        finally { Directory.Delete(cwd, recursive: true); }
    }

    [Fact]
    public async Task ConcurrentSessionLeasesInOneProcessWaitForTheCurrentWriter()
    {
        var cwd = Path.Combine(Path.GetTempPath(), "pisharp-local-lease-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(cwd);
        SessionFileLease? first = null;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        try
        {
            var sessionPath = Path.Combine(cwd, "session.json");
            first = await SessionFileLease.AcquireAsync(sessionPath, timeout.Token);
            var secondPending = SessionFileLease.AcquireAsync(sessionPath, timeout.Token);
            await Task.Delay(50, timeout.Token);
            var acquiredBeforeRelease = secondPending.IsCompleted;
            if (acquiredBeforeRelease) await (await secondPending).DisposeAsync();
            Assert.False(acquiredBeforeRelease, "A second writer in this process acquired the held session lease.");

            var releasing = first;
            first = null;
            await releasing.DisposeAsync();
            var second = await secondPending.WaitAsync(timeout.Token);
            await second.DisposeAsync();
        }
        finally
        {
            if (first is not null) await first.DisposeAsync();
            Directory.Delete(cwd, recursive: true);
        }
    }

    [Fact]
    public async Task CancelledStoreSaveWaitingForLeaseKeepsPersistedSnapshotAndCleansNoTempFiles()
    {
        var cwd = Path.Combine(Path.GetTempPath(), "pisharp-cancelled-session-save-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(cwd);
        SessionFileLease? heldLease = null;
        try
        {
            var directory = Path.Combine(cwd, "sessions");
            var store = new ConversationStore(cwd, directory);
            var conversation = new ConversationSession(cwd, "fixture-model", null);
            var path = store.NewPath(conversation);
            await store.SaveAsync(conversation, path);
            var original = await File.ReadAllBytesAsync(path);
            conversation.Append(new ChatMessage(ChatRole.User, "must not publish"));

            heldLease = await SessionFileLease.AcquireAsync(path, CancellationToken.None);
            using var cancellation = new CancellationTokenSource();
            var pendingSave = store.SaveAsync(conversation, path, cancellation.Token);
            Assert.False(pendingSave.IsCompleted);
            cancellation.Cancel();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pendingSave);
            Assert.Equal(original, await File.ReadAllBytesAsync(path));
            Assert.Empty(Directory.EnumerateFiles(directory, ".pisharp-*.tmp"));
        }
        finally
        {
            if (heldLease is not null) await heldLease.DisposeAsync();
            Directory.Delete(cwd, recursive: true);
        }
    }

    [Fact]
    public async Task ConcurrentSessionLeaseAliasesWaitForTheCurrentWriter()
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS()) return;
        var cwd = Path.Combine(Path.GetTempPath(), "pisharp-lease-alias-" + Guid.NewGuid().ToString("N"));
        var alias = Path.Combine(Path.GetTempPath(), "pisharp-lease-link-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(cwd);
        SessionFileLease? first = null;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        try
        {
            Directory.CreateSymbolicLink(alias, cwd);
            var directPath = Path.Combine(cwd, "session.json");
            var aliasedPath = Path.Combine(alias, "session.json");
            first = await SessionFileLease.AcquireAsync(directPath, timeout.Token);
            var secondPending = SessionFileLease.AcquireAsync(aliasedPath, timeout.Token);
            await Task.Delay(50, timeout.Token);
            var acquiredBeforeRelease = secondPending.IsCompleted;
            if (acquiredBeforeRelease) await (await secondPending).DisposeAsync();
            Assert.False(acquiredBeforeRelease, "A symlink alias acquired the held session lease in this process.");

            var releasing = first;
            first = null;
            await releasing.DisposeAsync();
            var second = await secondPending.WaitAsync(timeout.Token);
            await second.DisposeAsync();
        }
        finally
        {
            if (first is not null) await first.DisposeAsync();
            if (Directory.Exists(alias)) Directory.Delete(alias);
            Directory.Delete(cwd, recursive: true);
        }
    }

    [Fact]
    public void VersionOneTextIsMigratedButAmbiguousToolTurnsFailClosed()
    {
        var original = new ConversationSession(Path.GetTempPath(), "fixture", null);
        var user = new ChatMessage(ChatRole.User, "old text");
        original.Append(user);
        var document = System.Text.Json.Nodes.JsonNode.Parse(original.ToJson())!;
        document["Version"] = 1;
        document["Entries"]![0]!["Payload"] = System.Text.Json.JsonSerializer.SerializeToNode(user, AIJsonUtilities.DefaultOptions);
        var migrated = ConversationSession.Parse(document.ToJsonString());
        Assert.Equal("old text", migrated.ActiveMessages().Single().Text);
        Assert.Equal(2, System.Text.Json.JsonDocument.Parse(migrated.ToJson()).RootElement.GetProperty("Version").GetInt32());
        var tool = new ChatMessage(ChatRole.Tool, [new FunctionResultContent("call", "failure")]);
        document["Entries"]![0]!["Payload"] = System.Text.Json.JsonSerializer.SerializeToNode(tool, AIJsonUtilities.DefaultOptions);
        Assert.Contains("failure state", Assert.Throws<InvalidDataException>(() => ConversationSession.Parse(document.ToJsonString())).Message);
    }

    [Fact]
    public async Task CompletedToolTurnSurvivesRestartWithoutRepeatingSideEffect()
    {
        var cwd = Path.Combine(Path.GetTempPath(), "pisharp-canonical-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(cwd);
        try
        {
            var store = new ConversationStore(cwd, Path.Combine(cwd, "sessions"));
            var canonical = new ConversationSession(cwd, "fixture", null);
            var initial = await ConversationRun.OpenAsync(new PiAgent(new ScriptedClient(), new CodingTools(cwd)), canonical);
            await foreach (var _ in initial.RunStreamingAsync("Make file")) { }
            var file = Path.Combine(cwd, "generated.txt");
            Assert.Equal("made", await File.ReadAllTextAsync(file));
            var path = store.NewPath(canonical);
            await store.SaveAsync(canonical, path);
            Assert.Equal(path, store.MostRecentPath());
            if (OperatingSystem.IsLinux())
                Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path));
            var afterRestart = await store.LoadAsync(path);
            var client = new ScriptedClient();
            var resumed = await ConversationRun.OpenAsync(new PiAgent(client, new CodingTools(cwd)), afterRestart);
            await foreach (var _ in resumed.RunStreamingAsync("What happened?")) { }
            Assert.Single(client.Requests);
            Assert.Contains(client.Requests[0].SelectMany(m => m.Contents), c => c is FunctionCallContent { CallId: "call-1" });
            Assert.Contains(client.Requests[0].SelectMany(m => m.Contents), c => c is FunctionResultContent { CallId: "call-1" });
            Assert.Equal("made", await File.ReadAllTextAsync(file));
            Assert.Contains(afterRestart.ActiveMessages(), m => m.Text == "What happened?");
        }
        finally { Directory.Delete(cwd, recursive: true); }
    }

    [Fact]
    public async Task BranchSelectionSurvivesSaveAndRestartEvenIfLastEntryIsElsewhere()
    {
        var cwd = Path.Combine(Path.GetTempPath(), "pisharp-tree-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(cwd);
        try
        {
            var canonical = new ConversationSession(cwd, "fixture", null);
            var store = new ConversationStore(cwd, Path.Combine(cwd, "sessions"));
            var run = await ConversationRun.OpenAsync(new PiAgent(new ScriptedClient(), new CodingTools(cwd)), canonical);
            await foreach (var _ in run.RunStreamingAsync("root")) { }
            var root = canonical.Tree.HeadId!;
            await foreach (var _ in run.RunStreamingAsync("abandoned")) { }
            await run.SelectAsync(root);
            await foreach (var _ in run.RunStreamingAsync("alternate")) { }
            var alternate = canonical.Tree.HeadId!;
            await run.SelectAsync(root); // Intentional selection differs from last appended entry.
            var path = store.NewPath(canonical);
            await store.SaveAsync(canonical, path);
            var loaded = await store.LoadAsync(path);
            Assert.Equal(root, loaded.Tree.HeadId);
            Assert.NotEqual(alternate, loaded.Tree.HeadId);
            var client = new ScriptedClient();
            var restored = await ConversationRun.OpenAsync(new PiAgent(client, new CodingTools(cwd)), loaded);
            var countBefore = loaded.Tree.Entries.Count;
            await foreach (var _ in restored.RunStreamingAsync("continue root")) { }
            var history = string.Join(" ", client.Requests.Single().Select(m => m.Text));
            Assert.Contains("root", history);
            Assert.Contains("continue root", history);
            Assert.DoesNotContain("abandoned", history);
            Assert.DoesNotContain("alternate", history);
            Assert.Equal(root, loaded.Tree.Entries[countBefore].ParentId);
        }
        finally { Directory.Delete(cwd, recursive: true); }
    }

    [Fact]
    public async Task RepeatedLargeSessionSaveReloadKeepsSelectedBranchAndContextStable()
    {
        var cwd = Path.Combine(Path.GetTempPath(), "pisharp-repeated-session-reload-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(cwd);
        try
        {
            var store = new ConversationStore(cwd, Path.Combine(cwd, "sessions"));
            var session = new ConversationSession(cwd, "fixture-model", null);
            for (var index = 0; index < 2048; index++)
            {
                var role = index % 2 == 0 ? ChatRole.User : ChatRole.Assistant;
                session.Append(new ChatMessage(role, $"history-{index:D4}-" + new string('x', 96)));
            }
            var originalEntries = session.Tree.Entries.Count;
            var branchPoint = session.Tree.Entries[1023].Id;
            session.Tree.Select(branchPoint);
            session.Append(new ChatMessage(ChatRole.User, "alternate branch prompt"));
            session.Append(new ChatMessage(ChatRole.Assistant, "alternate branch response"));
            var expectedHead = session.Tree.HeadId;
            var expectedContext = session.ContextMessages().Select(message => message.Text).ToArray();
            var piRoundTrip = PiJsonlSessionInterchange.Import(PiJsonlSessionInterchange.Export(session));
            Assert.Equal(expectedContext, piRoundTrip.ContextMessages().Select(message => message.Text));
            var path = store.NewPath(session);
            await store.SaveAsync(session, path);

            for (var restart = 0; restart < 8; restart++)
            {
                session = await store.LoadAsync(path);
                Assert.Equal(expectedHead, session.Tree.HeadId);
                Assert.Equal(originalEntries + 2, session.Tree.Entries.Count);
                Assert.Equal(expectedContext, session.ContextMessages().Select(message => message.Text));
                await store.SaveAsync(session, path);
            }
        }
        finally { Directory.Delete(cwd, recursive: true); }
    }

    [Fact]
    public async Task ConcurrentReadersObserveOnlyCompleteSessionSnapshotsDuringWrites()
    {
        var cwd = Path.Combine(Path.GetTempPath(), "pisharp-session-reader-writer-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(cwd);
        try
        {
            var directory = Path.Combine(cwd, "sessions");
            var writerStore = new ConversationStore(cwd, directory);
            var readerStore = new ConversationStore(cwd, directory);
            var session = new ConversationSession(cwd, "fixture-model", null);
            var path = writerStore.NewPath(session);
            await writerStore.SaveAsync(session, path);

            for (var index = 0; index < 32; index++)
            {
                session.Append(new ChatMessage(index % 2 == 0 ? ChatRole.User : ChatRole.Assistant,
                    $"snapshot-{index:D2}-" + new string('x', 16 * 1024)));
                var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var save = Task.Run(async () =>
                {
                    await start.Task;
                    await writerStore.SaveAsync(session, path);
                });
                var read = Task.Run(async () =>
                {
                    await start.Task;
                    return await readerStore.LoadAsync(path);
                });
                start.SetResult();
                await Task.WhenAll(save, read);
                Assert.InRange((await read).ActiveMessages().Count, index, index + 1);
            }

            Assert.Equal(32, (await readerStore.LoadAsync(path)).ActiveMessages().Count);
        }
        finally { Directory.Delete(cwd, recursive: true); }
    }

    [Fact]
    public async Task CancelledTurnRecordsInterruptionAndCanResume()
    {
        var cwd = Path.Combine(Path.GetTempPath(), "pisharp-interrupt-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(cwd);
        try
        {
            var store = new ConversationStore(cwd, Path.Combine(cwd, "sessions"));
            var canonical = new ConversationSession(cwd, "fixture", null);
            var run = await ConversationRun.OpenAsync(new PiAgent(new BlockingClient(), new CodingTools(cwd)), canonical);
            using var cancel = new CancellationTokenSource();
            var partialSeen = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var running = Task.Run(async () =>
            {
                await foreach (var update in run.RunStreamingAsync("interrupt me", cancel.Token))
                    if (update.Text == "partial") partialSeen.TrySetResult();
            });
            await partialSeen.Task.WaitAsync(TimeSpan.FromSeconds(5));
            cancel.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running);
            Assert.Equal("interrupted", canonical.Tree.Entries.Last().Type);
            Assert.Equal("partial", canonical.Tree.Entries.Last().Payload.GetProperty("partialText").GetString());
            Assert.Equal("interrupt me", canonical.Tree.Entries.Last().Payload.GetProperty("prompt").GetString());
            var path = store.NewPath(canonical);
            await store.SaveAsync(canonical, path);
            var loaded = await store.LoadAsync(path);
            var restored = await ConversationRun.OpenAsync(new PiAgent(new ScriptedClient(), new CodingTools(cwd)), loaded);
            await foreach (var _ in restored.RunStreamingAsync("next")) { }
            Assert.Contains(loaded.ActiveMessages(), m => m.Text == "next");
        }
        finally { Directory.Delete(cwd, recursive: true); }
    }

    private sealed class BlockingClient : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            yield return new ChatResponseUpdate(ChatRole.Assistant, "partial");
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            yield break;
        }
        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    private sealed class ScriptedClient : IChatClient
    {
        public List<ChatMessage[]> Requests { get; } = [];
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            var captured = messages.ToArray();
            Requests.Add(captured);
            if (captured.LastOrDefault(m => m.Role == ChatRole.User)?.Text == "Make file" &&
                !captured.SelectMany(m => m.Contents).OfType<FunctionResultContent>().Any())
                yield return new ChatResponseUpdate(ChatRole.Assistant,
                    [new FunctionCallContent("call-1", "write", new Dictionary<string, object?> { ["path"] = "generated.txt", ["content"] = "made" })]);
            else yield return new ChatResponseUpdate(ChatRole.Assistant, "ack");
            await Task.CompletedTask;
        }
        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }
}
