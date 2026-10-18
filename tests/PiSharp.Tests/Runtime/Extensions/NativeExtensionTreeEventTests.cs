using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;
using PiSharp.Extensions.Abstractions;
using PiSharp.Runtime;
using PiSharp.Runtime.Extensions;
using PiSharp.Runtime.Sessions;
using PiSharp.Runtime.Tools;

namespace PiSharp.Tests;

public sealed class NativeExtensionTreeEventTests
{
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
        ExtensionRegistration registration, Func<CancellationToken, Task>? save = null, string? sessionFile = null) =>
        ConversationRun.OpenAsync(new PiAgent(new PassiveClient(), new CodingTools(workingDirectory),
            noBuiltinTools: true, liveExtensionRegistration: registration), conversation, save: save,
            sessionFile: sessionFile);

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
