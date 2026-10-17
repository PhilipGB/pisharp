using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Extensions.AI;
using PiSharp.Runtime;
using PiSharp.Runtime.Extensions;
using PiSharp.Runtime.Sessions;
using PiSharp.Runtime.Tools;
using NativeExtensions = PiSharp.Extensions.Abstractions;

namespace PiSharp.Tests;

public sealed class ExtensionSessionStateTests
{
    [Fact]
    public async Task ExtensionEntriesPersistOnTheActiveBranchAndStayOutOfProviderContext()
    {
        var workingDirectory = CreateWorkingDirectory();
        try
        {
            var store = new ConversationStore(workingDirectory, Path.Combine(workingDirectory, "sessions"));
            var conversation = new ConversationSession(workingDirectory, "fixture-model", null);
            conversation.Append(new ChatMessage(ChatRole.User, "ordinary message"));
            var path = store.NewPath(conversation);
            await store.SaveAsync(conversation, path);

            var provider = new CompleteClient();
            var run = await ConversationRun.OpenAsync(CreateAgent(provider), conversation,
                save: token => store.SaveAsync(conversation, path, token));
            using (var session = run.CreateExtensionSessionContext("/extensions/state.dll"))
            using (var payload = JsonDocument.Parse("""{"secret":"extension-only"}"""))
            {
                await session.AppendEntryAsync("counter", payload.RootElement);
                await session.AppendEntryAsync("counter", payload.RootElement);

                Assert.Equal(conversation.Id, session.SessionId);
                Assert.All(session.Entries, entry => Assert.Equal("/extensions/state.dll", entry.Source));
                Assert.Equal(2, session.Entries.Count);
                Assert.NotEqual(session.Entries[0].Id, session.Entries[1].Id);
            }

            await foreach (var _ in run.RunEventsAsync("follow up")) { }
            Assert.DoesNotContain(conversation.ContextMessages(), message =>
                message.Text.Contains("extension-only", StringComparison.Ordinal));
            Assert.DoesNotContain(Assert.Single(provider.Requests), message =>
                message.Text.Contains("extension-only", StringComparison.Ordinal));

            var resumed = await store.LoadAsync(path);
            var resumedRun = await ConversationRun.OpenAsync(CreateAgent(new CompleteClient()), resumed);
            using var resumedContext = resumedRun.CreateExtensionSessionContext();
            Assert.Equal(["counter", "counter"], resumedContext.Entries.Select(entry => entry.CustomType));
            Assert.Equal(["extension-only", "extension-only"], resumedContext.Entries.Select(entry =>
                entry.Data.GetProperty("secret").GetString()));
        }
        finally
        {
            Directory.Delete(workingDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task SaveFailureRollsBackTheAppendAndAllowsRetry()
    {
        var workingDirectory = CreateWorkingDirectory();
        try
        {
            var conversation = new ConversationSession(workingDirectory, "fixture-model", null);
            var saveCount = 0;
            var run = await ConversationRun.OpenAsync(CreateAgent(new CompleteClient()), conversation,
                save: _ => ++saveCount == 1
                    ? Task.FromException(new IOException("simulated write failure"))
                    : Task.CompletedTask);
            using var session = run.CreateExtensionSessionContext();
            using var payload = JsonDocument.Parse("""{"value":1}""");
            var oldHeadId = session.HeadEntryId;

            await Assert.ThrowsAsync<IOException>(() => session.AppendEntryAsync("retryable", payload.RootElement));

            Assert.Equal(oldHeadId, conversation.Tree.HeadId);
            Assert.Empty(session.Entries);

            var entry = await session.AppendEntryAsync("retryable", payload.RootElement);

            Assert.Equal(2, saveCount);
            Assert.Equal(entry.Id, session.HeadEntryId);
            Assert.Equal(entry.Id, Assert.Single(session.Entries).Id);
        }
        finally
        {
            Directory.Delete(workingDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task NewConversationDoesNotInheritExtensionEntriesThroughItsParentReference()
    {
        var workingDirectory = CreateWorkingDirectory();
        try
        {
            var previous = new ConversationSession(workingDirectory, "fixture-model", null);
            var previousRun = await ConversationRun.OpenAsync(CreateAgent(new CompleteClient()), previous);
            using (var previousState = previousRun.CreateExtensionSessionContext())
            using (var payload = JsonDocument.Parse("""{"value":"previous-only"}"""))
                await previousState.AppendEntryAsync("counter", payload.RootElement);

            var next = new ConversationSession(workingDirectory, "fixture-model", null,
                parentSessionPath: Path.Combine(workingDirectory, "previous.session.json"));
            var nextRun = await ConversationRun.OpenAsync(CreateAgent(new CompleteClient()), next);
            using var nextState = nextRun.CreateExtensionSessionContext();

            Assert.NotEqual(previous.Id, next.Id);
            Assert.Empty(nextState.Entries);
        }
        finally
        {
            Directory.Delete(workingDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task ForkAndCompactionKeepOnlyTheStateOnTheirActiveTreePath()
    {
        var workingDirectory = CreateWorkingDirectory();
        try
        {
            var conversation = new ConversationSession(workingDirectory, "fixture-model", null);
            using var state = JsonDocument.Parse("""{"value":"before-fork"}""");
            using var laterState = JsonDocument.Parse("""{"value":"after-fork"}""");
            var run = await ConversationRun.OpenAsync(CreateAgent(new CompleteClient()), conversation);
            using (var session = run.CreateExtensionSessionContext())
                await session.AppendEntryAsync("marker", state.RootElement);

            conversation.Append(new ChatMessage(ChatRole.User, "fork from here"));
            var forkPoint = conversation.Tree.HeadId!;
            using (var session = run.CreateExtensionSessionContext())
                await session.AppendEntryAsync("marker", laterState.RootElement);
            conversation.Append(new ChatMessage(ChatRole.Assistant, "response after fork point"));

            var (forked, prompt) = conversation.ForkAtUser(forkPoint);
            Assert.Equal("fork from here", prompt);
            var forkedRun = await ConversationRun.OpenAsync(CreateAgent(new CompleteClient()), forked);
            using var forkedContext = forkedRun.CreateExtensionSessionContext();
            Assert.Equal("before-fork", Assert.Single(forkedContext.Entries).Data.GetProperty("value").GetString());

            var compaction = Assert.IsType<ConversationSession.CompactionPlan>(conversation.PrepareCompaction(0));
            conversation.AppendCompaction(compaction, "summary without extension state", 0);
            using var compactedContext = run.CreateExtensionSessionContext();
            Assert.Equal(["before-fork", "after-fork"], compactedContext.Entries.Select(entry =>
                entry.Data.GetProperty("value").GetString()));
            Assert.DoesNotContain(conversation.ContextMessages(), message =>
                message.Text.Contains("before-fork", StringComparison.Ordinal) ||
                message.Text.Contains("after-fork", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(workingDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task SessionLifecycleCallbacksAreOrderedFailureIsolatedAndScopedToTheRun()
    {
        var workingDirectory = CreateWorkingDirectory();
        try
        {
            var registration = new ExtensionRegistration();
            var adapter = new NativeExtensionRegistrationAdapter(registration);
            var calls = new List<string>();
            NativeExtensions.PiSharpExtensionContext? retainedContext = null;
            adapter.AddSessionStartHandler((_, _, _) =>
            {
                calls.Add("failing-start");
                throw new InvalidOperationException("start failure");
            });
            adapter.AddSessionStartHandler(async (eventData, context, cancellationToken) =>
            {
                calls.Add("working-start");
                Assert.Equal(NativeExtensions.PiSharpExtensionSessionStartReason.Startup, eventData.Reason);
                retainedContext = context;
                using var payload = JsonDocument.Parse("""{"ready":true}""");
                await context.Session!.AppendEntryAsync("lifecycle", payload.RootElement, cancellationToken);
            });
            NativeExtensions.PiSharpExtensionSessionShutdownReason? shutdownReason = null;
            adapter.AddSessionShutdownHandler((eventData, _, _) =>
            {
                calls.Add("failing-shutdown");
                throw new InvalidOperationException("shutdown failure");
            });
            adapter.AddSessionShutdownHandler((eventData, _, _) =>
            {
                calls.Add("shutdown");
                shutdownReason = eventData.Reason;
                return ValueTask.CompletedTask;
            });
            adapter.Close();

            var run = await ConversationRun.OpenAsync(CreateAgent(new CompleteClient(), registration),
                new ConversationSession(workingDirectory, "fixture-model", null));

            var errors = await run.NotifySessionStartAsync(NativeExtensions.PiSharpExtensionSessionStartReason.Startup);

            Assert.Equal(["failing-start", "working-start"], calls);
            Assert.Equal("start failure", Assert.Single(errors).Error);
            using var observedSession = run.CreateExtensionSessionContext();
            Assert.Equal("lifecycle", Assert.Single(observedSession.Entries).CustomType);
            Assert.Empty(await run.NotifySessionStartAsync(NativeExtensions.PiSharpExtensionSessionStartReason.Startup));
            using var latePayload = JsonDocument.Parse("null");
            await Assert.ThrowsAsync<ObjectDisposedException>(() =>
                retainedContext!.Session!.AppendEntryAsync("late", latePayload.RootElement));

            var shutdownErrors = await run.NotifySessionShutdownAsync(
                NativeExtensions.PiSharpExtensionSessionShutdownReason.Quit);
            Assert.Equal("shutdown failure", Assert.Single(shutdownErrors).Error);
            Assert.Empty(await run.NotifySessionShutdownAsync(
                NativeExtensions.PiSharpExtensionSessionShutdownReason.Quit));
            Assert.Equal(NativeExtensions.PiSharpExtensionSessionShutdownReason.Quit, shutdownReason);
            Assert.Equal(["failing-start", "working-start", "failing-shutdown", "shutdown"], calls);
        }
        finally
        {
            Directory.Delete(workingDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task BeforeAgentCallbackCanPersistStateWhileTheRunOwnsItsExecutionGate()
    {
        var workingDirectory = CreateWorkingDirectory();
        try
        {
            var registration = new ExtensionRegistration();
            var adapter = new NativeExtensionRegistrationAdapter(registration);
            NativeExtensions.PiSharpExtensionContext? retainedContext = null;
            adapter.AddBeforeAgentStartHandler(async (_, context, cancellationToken) =>
            {
                retainedContext = context;
                using var payload = JsonDocument.Parse("""{"marker":"hidden-from-model"}""");
                await context.Session!.AppendEntryAsync("before-agent", payload.RootElement, cancellationToken);
                return null;
            });
            adapter.Close();
            var provider = new CompleteClient();
            var agent = new PiAgent(provider, new CodingTools(workingDirectory), noBuiltinTools: true,
                beforeAgentStartHandlers: registration.BeforeAgentStartHandlers);
            var run = await ConversationRun.OpenAsync(agent,
                new ConversationSession(workingDirectory, "fixture-model", null));
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));

            await foreach (var _ in run.RunEventsAsync("continue", timeout.Token)) { }

            using var state = run.CreateExtensionSessionContext();
            Assert.Equal("hidden-from-model", Assert.Single(state.Entries).Data.GetProperty("marker").GetString());
            Assert.DoesNotContain(Assert.Single(provider.Requests), message =>
                message.Text.Contains("hidden-from-model", StringComparison.Ordinal));
            using var latePayload = JsonDocument.Parse("null");
            await Assert.ThrowsAsync<ObjectDisposedException>(() => retainedContext!.Session!.AppendEntryAsync(
                "late", latePayload.RootElement));
        }
        finally
        {
            Directory.Delete(workingDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task CommandContextSessionBridgeUsesTheExistingConstructorAndCanBeRevoked()
    {
        var workingDirectory = CreateWorkingDirectory();
        try
        {
            var run = await ConversationRun.OpenAsync(CreateAgent(new CompleteClient()),
                new ConversationSession(workingDirectory, "fixture-model", null));
            var context = new NativeExtensions.PiSharpExtensionCommandContext(workingDirectory, true, false, false,
                (_, _) => Task.CompletedTask, (_, _, _, _) => Task.CompletedTask)
            {
                Session = run.CreateExtensionSessionContext("/extensions/command.dll")
            };
            var session = context.Session!;
            using var payload = JsonDocument.Parse("""{"written":true}""");
            await session.AppendEntryAsync("command", payload.RootElement);
            Assert.Equal("command", Assert.Single(session.Entries).CustomType);

            context.Dispose();

            await Assert.ThrowsAsync<ObjectDisposedException>(() => session.AppendEntryAsync("late",
                payload.RootElement));
        }
        finally
        {
            Directory.Delete(workingDirectory, recursive: true);
        }
    }

    private static string CreateWorkingDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "pisharp-extension-session-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static PiAgent CreateAgent(IChatClient client, ExtensionRegistration? registration = null) =>
        new(client, new CodingTools(Path.GetTempPath()), noBuiltinTools: true,
            liveExtensionRegistration: registration);

    private sealed class CompleteClient : IChatClient
    {
        internal List<ChatMessage[]> Requests { get; } = [];

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            Requests.Add(messages.ToArray());
            yield return new ChatResponseUpdate(ChatRole.Assistant, "done");
            await Task.CompletedTask;
        }

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }
}
