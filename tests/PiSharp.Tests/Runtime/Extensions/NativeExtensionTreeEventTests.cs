using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Extensions.AI;
using PiSharp.Extensions.Abstractions;
using PiSharp.Runtime;
using PiSharp.Runtime.Extensions;
using PiSharp.Runtime.Sessions;
using PiSharp.Runtime.Tools;
using ExtensionContext = PiSharp.Extensions.Abstractions.PiSharpExtensionContext;

namespace PiSharp.Tests;

public sealed class NativeExtensionTreeEventTests
{
    [Fact]
    public async Task RequestedBranchSummaryFromExtensionIsAppliedPersistedAndExported()
    {
        var workingDirectory = CreateWorkingDirectory("tree-extension-summary");
        try
        {
            var store = new ConversationStore(workingDirectory, Path.Combine(workingDirectory, "sessions"));
            var (conversation, oldLeaf, targetLeaf, rootEntry) = CreateBranchedConversation(workingDirectory);
            var path = store.NewPath(conversation);
            await store.SaveAsync(conversation, path);

            PiSharpExtensionSessionBeforeTreeEvent? beforeEvent = null;
            PiSharpExtensionSessionTreeEvent? afterEvent = null;
            var registration = new ExtensionRegistration();
            var adapter = new NativeExtensionRegistrationAdapter(registration);
            adapter.AddSessionBeforeTreeHandler((eventData, _, _) =>
            {
                beforeEvent = eventData;
                return ValueTask.FromResult<PiSharpExtensionSessionBeforeTreeResult?>(new(
                    Summary: "extension branch summary",
                    Details: JsonSerializer.SerializeToElement(new { readFiles = new[] { "a.txt" }, modifiedFiles = Array.Empty<string>() }),
                    Usage: new PiSharpExtensionCompactionUsage(2, 3, CachedInputTokens: 4, TotalTokens: 9,
                        CachedWriteTokens: 1)));
            });
            adapter.AddSessionTreeHandler((eventData, _, _) =>
            {
                afterEvent = eventData;
                return ValueTask.CompletedTask;
            });
            adapter.Close();

            var run = await OpenRunAsync(workingDirectory, conversation, registration,
                token => store.SaveAsync(conversation, path, token), path,
                pricing: new ModelPricing(2, 3, CachedInput: 1, CachedWrite: 4));
            await run.NavigateTreeAsync(targetLeaf, new ConversationTreeSelectionOptions(Summarize: true));

            Assert.True(beforeEvent?.UserWantsSummary);
            Assert.NotNull(afterEvent?.BranchSummaryEntryId);
            Assert.True(afterEvent?.FromExtension);
            var summaryEntry = Assert.Single(conversation.Tree.ActivePath(), node => node.Type == "branch_summary");
            Assert.Equal(rootEntry, summaryEntry.ParentId);
            Assert.Equal(oldLeaf, summaryEntry.Payload.GetProperty("fromId").GetString());
            Assert.Equal("extension branch summary", summaryEntry.Payload.GetProperty("summary").GetString());
            Assert.Contains("extension branch summary", conversation.ContextMessages().Last().Text, StringComparison.Ordinal);
            var summaryUsage = Assert.Single(conversation.ActiveUsage());
            Assert.Equal(9, summaryUsage.TotalTokens);
            Assert.Equal(0.000021m, summaryUsage.Cost);
            Assert.Equal(0.000004m, summaryUsage.InputCost);
            Assert.Equal(0.000009m, summaryUsage.OutputCost);
            Assert.Equal(0.000004m, summaryUsage.CachedInputCost);
            Assert.Equal(0.000004m, summaryUsage.CachedWriteCost);

            var loaded = await store.LoadAsync(path);
            Assert.Contains("extension branch summary", loaded.ContextMessages().Last().Text, StringComparison.Ordinal);
            var loadedUsage = Assert.Single(loaded.ActiveUsage());
            Assert.Equal(0.000021m, loadedUsage.Cost);
            Assert.Equal(0.000004m, loadedUsage.InputCost);
            Assert.Equal(0.000009m, loadedUsage.OutputCost);
            Assert.Equal(0.000004m, loadedUsage.CachedInputCost);
            Assert.Equal(0.000004m, loadedUsage.CachedWriteCost);
            var exported = PiJsonlSessionInterchange.Export(loaded);
            var roundTripped = PiJsonlSessionInterchange.Import(exported, workingDirectoryOverride: workingDirectory);
            Assert.Contains("extension branch summary", roundTripped.ContextMessages().Last().Text, StringComparison.Ordinal);
            var roundTrippedUsage = Assert.Single(roundTripped.ActiveUsage());
            Assert.Equal(9, roundTrippedUsage.TotalTokens);
            Assert.Equal(0.000021m, roundTrippedUsage.Cost);
            Assert.Equal(0.000004m, roundTrippedUsage.InputCost);
            Assert.Equal(0.000009m, roundTrippedUsage.OutputCost);
            Assert.Equal(0.000004m, roundTrippedUsage.CachedInputCost);
            Assert.Equal(0.000004m, roundTrippedUsage.CachedWriteCost);
        }
        finally { Directory.Delete(workingDirectory, recursive: true); }
    }

    [Fact]
    public async Task RequestedBranchSummaryUsesMafAndPersistsItsUsage()
    {
        var workingDirectory = CreateWorkingDirectory("tree-generated-summary");
        try
        {
            var (conversation, oldLeaf, targetLeaf, rootEntry) = CreateBranchedConversation(workingDirectory);
            conversation.Tree.Select(oldLeaf);
            conversation.Append(new ChatMessage(ChatRole.Assistant,
                [new FunctionCallContent("read-branch-file", "read", new Dictionary<string, object?> { ["path"] = "notes.md" })]));
            conversation.Append(new ChatMessage(ChatRole.Tool,
                [new FunctionResultContent("read-branch-file", "branch file contents")]));
            oldLeaf = conversation.Tree.HeadId!;
            var client = new SummaryClient("generated branch summary");
            var run = await OpenRunAsync(workingDirectory, conversation, new ExtensionRegistration(), chatClient: client,
                reasoning: new ReasoningOptions { Effort = ReasoningEffort.High });

            await run.NavigateTreeAsync(targetLeaf, new ConversationTreeSelectionOptions(Summarize: true));

            Assert.Equal(1, client.Calls);
            Assert.Equal(ReasoningEffort.High, client.LastOptions?.Reasoning?.Effort);
            var requestText = string.Join("\n", client.LastRequest!.SelectMany(message => message.Contents)
                .OfType<TextContent>().Select(content => content.Text));
            Assert.Contains("Create a structured summary of this conversation branch", requestText, StringComparison.Ordinal);
            Assert.Contains("old branch", requestText, StringComparison.Ordinal);
            Assert.DoesNotContain("target branch", requestText, StringComparison.Ordinal);
            var summaryEntry = Assert.Single(conversation.Tree.ActivePath(), node => node.Type == "branch_summary");
            Assert.Equal(rootEntry, summaryEntry.ParentId);
            Assert.Equal(oldLeaf, summaryEntry.Payload.GetProperty("fromId").GetString());
            Assert.Contains("Summary of that exploration:", conversation.ContextMessages().Last().Text, StringComparison.Ordinal);
            Assert.Contains("generated branch summary", conversation.ContextMessages().Last().Text, StringComparison.Ordinal);
            Assert.StartsWith("The user explored a different conversation branch before returning here.\n" +
                "Summary of that exploration:\n\n" + "generated branch summary",
                summaryEntry.Payload.GetProperty("summary").GetString(), StringComparison.Ordinal);
            Assert.Contains("<read-files>\nnotes.md\n</read-files>", conversation.ContextMessages().Last().Text, StringComparison.Ordinal);
            Assert.Equal("notes.md", summaryEntry.Payload.GetProperty("details").GetProperty("readFiles")[0].GetString());
            Assert.Equal("branch_summary", Assert.Single(conversation.ActiveUsage()).Source);
            Assert.Equal(20, Assert.Single(conversation.ActiveUsage()).TotalTokens);
        }
        finally { Directory.Delete(workingDirectory, recursive: true); }
    }

    [Fact]
    public async Task BranchSummaryFailureLeavesCanonicalSelectionAndPersistenceUnchanged()
    {
        var workingDirectory = CreateWorkingDirectory("tree-summary-failure");
        try
        {
            var store = new ConversationStore(workingDirectory, Path.Combine(workingDirectory, "sessions"));
            var (conversation, oldLeaf, targetLeaf, _) = CreateBranchedConversation(workingDirectory);
            var path = store.NewPath(conversation);
            await store.SaveAsync(conversation, path);
            var run = await OpenRunAsync(workingDirectory, conversation, new ExtensionRegistration(),
                token => store.SaveAsync(conversation, path, token), path,
                new SummaryClient("unused", new IOException("summary failed")));

            var exception = await Assert.ThrowsAsync<IOException>(() =>
                run.NavigateTreeAsync(targetLeaf, new ConversationTreeSelectionOptions(Summarize: true)));

            Assert.Equal("summary failed", exception.Message);
            Assert.Equal(oldLeaf, conversation.Tree.HeadId);
            Assert.Equal(oldLeaf, (await store.LoadAsync(path)).Tree.HeadId);
            Assert.DoesNotContain(conversation.Tree.Entries, node => node.Type == "branch_summary");
        }
        finally { Directory.Delete(workingDirectory, recursive: true); }
    }

    [Fact]
    public async Task TreeNavigationToUserMessageSelectsItsParentAndReturnsDraftText()
    {
        var workingDirectory = CreateWorkingDirectory("tree-user-draft");
        try
        {
            var (conversation, _, targetLeaf, rootEntry) = CreateBranchedConversation(workingDirectory);
            var run = await OpenRunAsync(workingDirectory, conversation, new ExtensionRegistration());

            var result = await run.NavigateTreeAsync(targetLeaf);

            Assert.Equal(rootEntry, conversation.Tree.HeadId);
            Assert.Equal("target branch", result.EditorText);
            Assert.DoesNotContain(conversation.ContextMessages(), message => message.Text == "target branch");
        }
        finally { Directory.Delete(workingDirectory, recursive: true); }
    }

    [Fact]
    public void BranchSummaryFileDetailsStopAtTheSelectedTokenBudget()
    {
        var workingDirectory = CreateWorkingDirectory("tree-summary-budget");
        try
        {
            var conversation = new ConversationSession(workingDirectory, "test-model", null);
            conversation.Append(new ChatMessage(ChatRole.User, "common"));
            conversation.Append(new ChatMessage(ChatRole.Assistant,
                [new FunctionCallContent("read-old-file", "read", new Dictionary<string, object?> { ["path"] = "old.txt" })]));
            conversation.Append(new ChatMessage(ChatRole.Tool,
                [new FunctionResultContent("read-old-file", "old file contents")]));
            conversation.Append(new ChatMessage(ChatRole.Assistant, "middle context passage"));
            conversation.Append(new ChatMessage(ChatRole.Assistant, "recent"));

            var prepared = BranchSummaryPreparation.Prepare(conversation.Tree.ActivePath().Skip(1).ToArray(), tokenBudget: 5);

            Assert.Single(prepared.Messages);
            Assert.Equal("recent", prepared.Messages[0].Text);
            Assert.Empty(prepared.ReadFiles);
        }
        finally { Directory.Delete(workingDirectory, recursive: true); }
    }

    [Fact]
    public async Task TreeNavigationPublishesBeforeAndPersistedCompletionEvents()
    {
        var workingDirectory = CreateWorkingDirectory("tree-event");
        try
        {
            var store = new ConversationStore(workingDirectory, Path.Combine(workingDirectory, "sessions"));
            var (conversation, oldLeaf, targetLeaf, rootEntry) = CreateBranchedConversation(workingDirectory);
            var path = store.NewPath(conversation);
            await store.SaveAsync(conversation, path);

            var registration = new ExtensionRegistration();
            var adapter = new NativeExtensionRegistrationAdapter(registration);
            PiSharpExtensionSessionBeforeTreeEvent? beforeEvent = null;
            PiSharpExtensionSessionTreeEvent? afterEvent = null;
            string? persistedHeadDuringAfterEvent = null;
            adapter.AddSessionBeforeTreeHandler((eventData, _, _) =>
            {
                beforeEvent = eventData;
                return ValueTask.FromResult<PiSharpExtensionSessionBeforeTreeResult?>(null);
            });
            adapter.AddSessionTreeHandler(async (eventData, _, cancellationToken) =>
            {
                afterEvent = eventData;
                persistedHeadDuringAfterEvent = (await store.LoadAsync(path, cancellationToken)).Tree.HeadId;
            });
            adapter.Close();

            var run = await OpenRunAsync(workingDirectory, conversation, registration,
                token => store.SaveAsync(conversation, path, token), path);
            await run.SelectAsync(targetLeaf);

            Assert.NotNull(beforeEvent);
            Assert.Equal(targetLeaf, beforeEvent.TargetEntryId);
            Assert.Equal(oldLeaf, beforeEvent.OldLeafEntryId);
            Assert.Equal(rootEntry, beforeEvent.CommonAncestorEntryId);
            Assert.Equal([oldLeaf], beforeEvent.EntriesToSummarize.Select(entry => entry.Id));
            Assert.False(beforeEvent.UserWantsSummary);
            Assert.NotNull(afterEvent);
            Assert.Equal(oldLeaf, afterEvent.OldLeafEntryId);
            Assert.Equal(targetLeaf, afterEvent.NewLeafEntryId);
            Assert.Equal(targetLeaf, persistedHeadDuringAfterEvent);
            Assert.Equal(targetLeaf, conversation.Tree.HeadId);
        }
        finally { Directory.Delete(workingDirectory, recursive: true); }
    }

    [Fact]
    public async Task TreeNavigationVetoLeavesSelectionAndPersistenceUnchanged()
    {
        var workingDirectory = CreateWorkingDirectory("tree-veto");
        try
        {
            var store = new ConversationStore(workingDirectory, Path.Combine(workingDirectory, "sessions"));
            var (conversation, oldLeaf, targetLeaf, _) = CreateBranchedConversation(workingDirectory);
            var path = store.NewPath(conversation);
            await store.SaveAsync(conversation, path);

            var registration = new ExtensionRegistration();
            var adapter = new NativeExtensionRegistrationAdapter(registration);
            var afterEventCount = 0;
            adapter.AddSessionBeforeTreeHandler((_, _, _) =>
                ValueTask.FromResult<PiSharpExtensionSessionBeforeTreeResult?>(new(Cancel: true)));
            adapter.AddSessionTreeHandler((_, _, _) =>
            {
                afterEventCount++;
                return ValueTask.CompletedTask;
            });
            adapter.Close();

            var run = await OpenRunAsync(workingDirectory, conversation, registration,
                token => store.SaveAsync(conversation, path, token), path);
            await Assert.ThrowsAsync<InvalidOperationException>(() => run.SelectAsync(targetLeaf));

            Assert.Equal(oldLeaf, conversation.Tree.HeadId);
            Assert.Equal(oldLeaf, (await store.LoadAsync(path)).Tree.HeadId);
            Assert.Equal(0, afterEventCount);
        }
        finally { Directory.Delete(workingDirectory, recursive: true); }
    }

    [Fact]
    public async Task FailedTreeSaveRollsBackAndDoesNotPublishCompletionThenCanRetry()
    {
        var workingDirectory = CreateWorkingDirectory("tree-save-failure");
        try
        {
            var store = new ConversationStore(workingDirectory, Path.Combine(workingDirectory, "sessions"));
            var (conversation, oldLeaf, targetLeaf, _) = CreateBranchedConversation(workingDirectory);
            var path = store.NewPath(conversation);
            await store.SaveAsync(conversation, path);
            var entryCountBeforeSelection = conversation.Tree.Entries.Count;

            var failSave = true;
            var registration = new ExtensionRegistration();
            var adapter = new NativeExtensionRegistrationAdapter(registration);
            var afterEvents = new List<PiSharpExtensionSessionTreeEvent>();
            adapter.AddSessionTreeHandler((eventData, _, _) =>
            {
                afterEvents.Add(eventData);
                return ValueTask.CompletedTask;
            });
            adapter.Close();

            async Task SaveAsync(CancellationToken cancellationToken)
            {
                if (failSave) throw new IOException("fixture save failure");
                await store.SaveAsync(conversation, path, cancellationToken);
            }

            var run = await OpenRunAsync(workingDirectory, conversation, registration, SaveAsync, path);
            await Assert.ThrowsAsync<IOException>(() => run.SelectAsync(targetLeaf));
            Assert.Equal(oldLeaf, conversation.Tree.HeadId);
            Assert.Equal(entryCountBeforeSelection, conversation.Tree.Entries.Count);
            Assert.Equal(oldLeaf, (await store.LoadAsync(path)).Tree.HeadId);
            Assert.Empty(afterEvents);

            failSave = false;
            await run.SelectAsync(targetLeaf);
            Assert.Equal(targetLeaf, conversation.Tree.HeadId);
            Assert.Equal(targetLeaf, (await store.LoadAsync(path)).Tree.HeadId);
            Assert.Collection(afterEvents,
                item =>
                {
                    Assert.Equal(oldLeaf, item.OldLeafEntryId);
                    Assert.Equal(targetLeaf, item.NewLeafEntryId);
                });
        }
        finally { Directory.Delete(workingDirectory, recursive: true); }
    }

    [Fact]
    public async Task FailedTreeHistoryRestoreRollsBackAndDoesNotPublishCompletionThenCanRetry()
    {
        var workingDirectory = CreateWorkingDirectory("tree-restore-failure");
        try
        {
            var store = new ConversationStore(workingDirectory, Path.Combine(workingDirectory, "sessions"));
            var (conversation, oldLeaf, targetLeaf, _) = CreateBranchedConversation(workingDirectory);
            var path = store.NewPath(conversation);
            await store.SaveAsync(conversation, path);

            using var restoreCancellation = new CancellationTokenSource();
            var beforeCalls = 0;
            var registration = new ExtensionRegistration();
            var adapter = new NativeExtensionRegistrationAdapter(registration);
            var afterEvents = new List<PiSharpExtensionSessionTreeEvent>();
            adapter.AddSessionBeforeTreeHandler((_, _, _) =>
            {
                if (Interlocked.Increment(ref beforeCalls) == 1) restoreCancellation.Cancel();
                return ValueTask.FromResult<PiSharpExtensionSessionBeforeTreeResult?>(null);
            });
            adapter.AddSessionTreeHandler((eventData, _, _) =>
            {
                afterEvents.Add(eventData);
                return ValueTask.CompletedTask;
            });
            adapter.Close();

            var saves = 0;
            var run = await OpenRunAsync(workingDirectory, conversation, registration, async cancellationToken =>
            {
                saves++;
                await store.SaveAsync(conversation, path, cancellationToken);
            }, path);

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                run.SelectAsync(targetLeaf, restoreCancellation.Token));

            Assert.Equal(oldLeaf, conversation.Tree.HeadId);
            Assert.Equal(oldLeaf, (await store.LoadAsync(path)).Tree.HeadId);
            Assert.Equal(0, saves);
            Assert.Empty(afterEvents);

            await run.SelectAsync(targetLeaf);
            Assert.Equal(targetLeaf, conversation.Tree.HeadId);
            Assert.Equal(targetLeaf, (await store.LoadAsync(path)).Tree.HeadId);
            Assert.Equal(1, saves);
            Assert.Collection(afterEvents, item =>
            {
                Assert.Equal(oldLeaf, item.OldLeafEntryId);
                Assert.Equal(targetLeaf, item.NewLeafEntryId);
            });
        }
        finally { Directory.Delete(workingDirectory, recursive: true); }
    }

    [Fact]
    public async Task FailedTreeExecutionRestoreRollsBackAndLeavesThePreviousBranchUsable()
    {
        var workingDirectory = CreateWorkingDirectory("tree-execution-restore-failure");
        try
        {
            var store = new ConversationStore(workingDirectory, Path.Combine(workingDirectory, "sessions"));
            var (conversation, oldLeaf, _, rootEntry) = CreateBranchedConversation(workingDirectory);
            conversation.Tree.Select(rootEntry);
            using (var malformedTranscript = JsonDocument.Parse("[{\"message\":{}}]"))
                conversation.Tree.Append("tool_transcript", malformedTranscript.RootElement.Clone());
            var invalidTarget = conversation.Tree.HeadId!;
            conversation.Tree.Select(oldLeaf);
            var path = store.NewPath(conversation);
            await store.SaveAsync(conversation, path);

            var registration = new ExtensionRegistration();
            var adapter = new NativeExtensionRegistrationAdapter(registration);
            var afterEvents = new List<PiSharpExtensionSessionTreeEvent>();
            adapter.AddSessionTreeHandler((eventData, _, _) =>
            {
                afterEvents.Add(eventData);
                return ValueTask.CompletedTask;
            });
            adapter.Close();

            var saves = 0;
            var run = await OpenRunAsync(workingDirectory, conversation, registration, async cancellationToken =>
            {
                saves++;
                await store.SaveAsync(conversation, path, cancellationToken);
            }, path);

            await Assert.ThrowsAsync<KeyNotFoundException>(() => run.SelectAsync(invalidTarget));

            Assert.Equal(oldLeaf, conversation.Tree.HeadId);
            Assert.Equal(oldLeaf, (await store.LoadAsync(path)).Tree.HeadId);
            Assert.Equal(0, saves);
            Assert.Empty(afterEvents);

            await run.SelectAsync(rootEntry);
            Assert.Equal(rootEntry, conversation.Tree.HeadId);
            Assert.Equal(rootEntry, (await store.LoadAsync(path)).Tree.HeadId);
            Assert.Equal(1, saves);
            Assert.Collection(afterEvents, item =>
            {
                Assert.Equal(oldLeaf, item.OldLeafEntryId);
                Assert.Equal(rootEntry, item.NewLeafEntryId);
            });
        }
        finally { Directory.Delete(workingDirectory, recursive: true); }
    }

    [Fact]
    public async Task SelectingCurrentHeadDoesNotDispatchTreeHooksOrSave()
    {
        var workingDirectory = CreateWorkingDirectory("tree-noop");
        try
        {
            var (conversation, oldLeaf, _, _) = CreateBranchedConversation(workingDirectory);
            var registration = new ExtensionRegistration();
            var adapter = new NativeExtensionRegistrationAdapter(registration);
            var callbackCount = 0;
            adapter.AddSessionBeforeTreeHandler((_, _, _) =>
            {
                callbackCount++;
                return ValueTask.FromResult<PiSharpExtensionSessionBeforeTreeResult?>(null);
            });
            adapter.AddSessionTreeHandler((_, _, _) =>
            {
                callbackCount++;
                return ValueTask.CompletedTask;
            });
            adapter.Close();

            var saveCount = 0;
            var run = await OpenRunAsync(workingDirectory, conversation, registration, _ =>
            {
                saveCount++;
                return Task.CompletedTask;
            });
            await run.SelectAsync(oldLeaf);

            Assert.Equal(0, callbackCount);
            Assert.Equal(0, saveCount);
            Assert.Equal(oldLeaf, conversation.Tree.HeadId);
        }
        finally { Directory.Delete(workingDirectory, recursive: true); }
    }

    [Fact]
    public async Task CommittedTreeNavigationNotifiesAfterCallerCancellationAndBoundsObserverLifetime()
    {
        var workingDirectory = CreateWorkingDirectory("tree-observer-timeout");
        var observer = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            var store = new ConversationStore(workingDirectory, Path.Combine(workingDirectory, "sessions"));
            var (conversation, oldLeaf, targetLeaf, _) = CreateBranchedConversation(workingDirectory);
            var path = store.NewPath(conversation);
            await store.SaveAsync(conversation, path);

            var registration = new ExtensionRegistration();
            var adapter = new NativeExtensionRegistrationAdapter(registration);
            var observerStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            ExtensionContext? retainedContext = null;
            var observerToken = CancellationToken.None;
            var eventCount = 0;
            adapter.AddSessionTreeHandler((_, context, cancellationToken) =>
            {
                Interlocked.Increment(ref eventCount);
                retainedContext ??= context;
                if (observerToken == CancellationToken.None) observerToken = cancellationToken;
                observerStarted.TrySetResult();
                return new ValueTask(observer.Task);
            });
            adapter.Close();

            using var callerCancellation = new CancellationTokenSource();
            async Task SaveAndCancelAsync(CancellationToken cancellationToken)
            {
                await store.SaveAsync(conversation, path, cancellationToken);
                callerCancellation.Cancel();
            }

            var run = await OpenRunAsync(workingDirectory, conversation, registration, SaveAndCancelAsync, path);
            await run.SelectAsync(targetLeaf, callerCancellation.Token).WaitAsync(TimeSpan.FromSeconds(5));
            await observerStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await Task.Delay(TimeSpan.FromMilliseconds(350));

            Assert.True(callerCancellation.IsCancellationRequested);
            Assert.Equal(targetLeaf, conversation.Tree.HeadId);
            Assert.Equal(targetLeaf, (await store.LoadAsync(path)).Tree.HeadId);
            Assert.True(observerToken.IsCancellationRequested);
            Assert.NotNull(retainedContext);
            await Assert.ThrowsAsync<ObjectDisposedException>(async () =>
                await retainedContext.Session!.SetSessionNameAsync("late tree observer mutation"));

            // A second selection proves the first observer timeout released the execution gate.
            await run.SelectAsync(oldLeaf).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(oldLeaf, conversation.Tree.HeadId);
            Assert.Equal(2, eventCount);
        }
        finally
        {
            observer.TrySetResult();
            Directory.Delete(workingDirectory, recursive: true);
        }
    }

    private static string CreateWorkingDirectory(string prefix)
    {
        var path = Path.Combine(Path.GetTempPath(), "pisharp-" + prefix + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static (ConversationSession Conversation, string OldLeaf, string TargetLeaf, string RootEntry)
        CreateBranchedConversation(string workingDirectory)
    {
        var conversation = new ConversationSession(workingDirectory, "test-model", null);
        conversation.Append(new ChatMessage(ChatRole.User, "common root"));
        var rootEntry = conversation.Tree.HeadId!;
        conversation.Append(new ChatMessage(ChatRole.Assistant, "old branch"));
        var oldLeaf = conversation.Tree.HeadId!;
        conversation.Tree.Select(rootEntry);
        conversation.Append(new ChatMessage(ChatRole.User, "target branch"));
        var targetLeaf = conversation.Tree.HeadId!;
        conversation.Tree.Select(oldLeaf);
        return (conversation, oldLeaf, targetLeaf, rootEntry);
    }

    private static Task<ConversationRun> OpenRunAsync(string workingDirectory, ConversationSession conversation,
        ExtensionRegistration registration, Func<CancellationToken, Task>? save = null, string? sessionFile = null,
        IChatClient? chatClient = null, ReasoningOptions? reasoning = null, ModelPricing? pricing = null) =>
        ConversationRun.OpenAsync(new PiAgent(chatClient ?? new PassiveClient(), new CodingTools(workingDirectory),
            reasoning: reasoning, noBuiltinTools: true, liveExtensionRegistration: registration), conversation, save: save,
            sessionFile: sessionFile, pricing: pricing);

    private sealed class SummaryClient(string answer, Exception? failure = null) : IChatClient
    {
        public int Calls { get; private set; }
        public IReadOnlyList<ChatMessage>? LastRequest { get; private set; }
        public ChatOptions? LastOptions { get; private set; }

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            Calls++;
            LastRequest = messages.ToArray();
            LastOptions = options;
            if (failure is not null) throw failure;
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, answer)
            {
                AdditionalProperties = new()
            })
            {
                Usage = new UsageDetails { InputTokenCount = 17, OutputTokenCount = 3, TotalTokenCount = 20 }
            });
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask;
            yield break;
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    private sealed class PassiveClient : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask;
            yield break;
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }
}
