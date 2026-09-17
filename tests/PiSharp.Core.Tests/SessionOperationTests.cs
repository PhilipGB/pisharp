using System.Text.Json;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using PiSharp.Cli;
using PiSharp.Core;

namespace PiSharp.Core.Tests;

/// <summary>
/// Tests for the shared session-operation coordination: turns, compaction, and navigation are
/// mutually exclusive with deterministic conflict errors, manual compaction drains the active
/// turn first, and stale MAF state caches can never restore pre-compaction history.
/// </summary>
public sealed class SessionOperationTests
{
    [Fact]
    public async Task PromptIsRejectedWhileCompactionIsRunning()
    {
        using var temp = TempDirectory.Create();
        var (controller, lease) = await BeginExclusiveOperationAsync(temp, "compaction");
        try
        {
            Assert.True(controller.IsCompacting);
            Assert.Throws<SessionOperationConflictException>(() => controller.EnterTurn());
        }
        finally
        {
            await lease.DisposeAsync();
        }
    }

    [Fact]
    public async Task PromptIsRejectedWhileNavigationIsRunning()
    {
        using var temp = TempDirectory.Create();
        var (controller, lease) = await BeginExclusiveOperationAsync(temp, "navigation");
        try
        {
            Assert.True(controller.IsNavigating);
            Assert.Throws<SessionOperationConflictException>(() => controller.EnterTurn());
        }
        finally
        {
            await lease.DisposeAsync();
        }
    }

    [Fact]
    public async Task SessionMutationIsRejectedWhileATurnIsRunning()
    {
        using var temp = TempDirectory.Create();
        var controller = (await CreateControllerAsync(temp)).Sessions;
        controller.EnterTurn();

        try
        {
            await Assert.ThrowsAsync<SessionOperationConflictException>(
                () => controller.NavigateAsync("root", false, null, new NoopChatOutput(), CancellationToken.None));
            await Assert.ThrowsAsync<SessionOperationConflictException>(
                () => controller.NewAsync(CancellationToken.None));
            Assert.Throws<SessionOperationConflictException>(() => controller.EnterTurn());
        }
        finally
        {
            controller.ExitTurn();
        }
    }

    [Fact]
    public async Task ManualCompactionAbortsActiveTurnBeforeCompacting()
    {
        using var temp = TempDirectory.Create();
        var controller = (await CreateControllerAsync(temp)).Sessions;
        await SeedCompactableHistoryAsync(controller);
        controller.EnterTurn();
        var aborted = false;
        controller.AbortActiveTurn = () =>
        {
            aborted = true;
            controller.ExitTurn();
        };

        var result = await controller.CompactAsync(
            null,
            CompactionReason.Manual,
            new NoopChatOutput(),
            CancellationToken.None);

        Assert.True(aborted);
        Assert.False(controller.IsTurnActive);
        Assert.Contains("## Goal", result.Summary);
    }

    [Fact]
    public async Task ManualCompactionOnEmptySessionIsADeterministicConflict()
    {
        using var temp = TempDirectory.Create();
        var controller = (await CreateControllerAsync(temp)).Sessions;

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            controller.CompactAsync(null, CompactionReason.Manual, new NoopChatOutput(), CancellationToken.None));
    }

    [Fact]
    public async Task CompactionCreatesFreshRuntimeSessionInsteadOfReusingStaleState()
    {
        using var temp = TempDirectory.Create();
        var result = await CreateControllerAsync(temp);
        var controller = result.Sessions;
        var agent = (RecordingAgent)result.Agent;
        await SeedCompactableHistoryAsync(controller);
        var sessionBefore = controller.Session;

        await controller.CompactAsync(null, CompactionReason.Manual, new NoopChatOutput(), CancellationToken.None);

        // The old in-memory session may describe the discarded pre-compaction context, so it
        // must be replaced with a fresh one; the typed session is the history authority.
        Assert.NotSame(sessionBefore, controller.Session);
        Assert.True(agent.CreateCalls >= 2);
    }

    [Fact]
    public async Task RestartAfterCompactionNeverRestoresTheStaleAgentStateCache()
    {
        using var temp = TempDirectory.Create();
        var (workspace, store) = await CreateWorkspaceAsync(temp);
        var firstAgent = new RecordingAgent();

        // First run: a turn, then the MAF state cache is persisted.
        var first = await CreateControllerAsync(temp, firstAgent);
        await SeedTurnAsync(first.Sessions);
        await first.Sessions.PersistAgentStateCacheAsync(CancellationToken.None);
        Assert.False(first.Sessions.Document!.HasBoundaryAfterStateCache(first.Sessions.ActiveEntryId));

        // Restart A: the cache post-dates no boundary — the cached state is restored.
        var agentA = new RecordingAgent();
        var restartedA = await CreateControllerAsync(temp, agentA, continueSession: true);
        Assert.Equal(1, agentA.DeserializeCalls);

        // Simulate compaction that happened after the cache was written (e.g. by another run):
        // the boundary now post-dates the newest state cache.
        var leaf = restartedA.Sessions.ActiveEntryId;
        await store.AppendEntriesAsync(
            restartedA.Sessions.Document!,
            [new CompactionEntry(
                Guid.NewGuid().ToString("N"),
                leaf,
                DateTimeOffset.UtcNow,
                "## Goal\nlate summary",
                leaf ?? string.Empty,
                100)],
            CancellationToken.None);
        var document = restartedA.Sessions.Document!;
        Assert.True(document.HasBoundaryAfterStateCache(document.LatestEntryId));

        // Restart B: the cache is stale — it must not be deserialized, or the restored agent
        // would resurrect the discarded pre-compaction history.
        var agentB = new RecordingAgent();
        var restartedB = await CreateControllerAsync(temp, agentB, continueSession: true);
        Assert.Equal(0, agentB.DeserializeCalls);
    }

    [Fact]
    public async Task NavigationToAnotherBranchRebuildsContextWithoutLeakingTheAbandonedBranch()
    {
        using var temp = TempDirectory.Create();
        var result = await CreateControllerAsync(temp);
        var controller = result.Sessions;
        var agent = (RecordingAgent)result.Agent;
        var document = controller.Document!;
        var store = new SessionStore(document.Header.WorkingDirectory, Path.Combine(temp.Path, "sessions"));

        // Branch A: U1 -> A1 -> U2 -> A2 (the active path).
        var u1 = (await PersistRawUserAsync(controller, "branch A first")).Id;
        await PersistRawAssistantAsync(controller, "branch A first answer");
        await PersistRawUserAsync(controller, "branch A second");
        var a2 = (await PersistRawAssistantAsync(controller, "branch A second answer")).Id;
        Assert.Equal(a2, controller.ActiveEntryId);

        // Branch B: U3 (child of U1) -> A3. Appended directly to the store, like a session that
        // was forked or resumed from this workspace.
        var u3 = await AppendRawAsync(store, document, u1, new { role = "user", content = "branch B prompt" });
        var a3 = await AppendRawAsync(store, document, u3, new { role = "assistant", content = new object[] { new { type = "text", text = "branch B answer" } } });

        var sessionBefore = controller.Session;
        var navigation = await controller.NavigateAsync(
            a3, summarize: true, null, new NoopChatOutput(), CancellationToken.None);

        Assert.True(navigation.Changed);
        Assert.True(navigation.SummaryAdded);

        // The destination context contains branch B and the branch summary of the abandoned
        // path — never the abandoned branch's own messages.
        var history = string.Join("\n", controller.GetEffectiveHistory()
            .Select(message => string.Concat(message.Contents.OfType<TextContent>().Select(content => content.Text))));
        Assert.Contains("branch B prompt", history);
        Assert.Contains("branch B answer", history);
        Assert.Contains("The following is a summary of a branch", history);
        Assert.DoesNotContain("branch A second", history);

        // The branch summary entry is attached at the destination leaf, and the runtime MAF
        // session is rebuilt so abandoned-branch state cannot leak into the next turn.
        Assert.NotSame(sessionBefore, controller.Session);
        Assert.True(agent.CreateCalls >= 2);
        var branchEntry = document.GetActiveEntryPath(controller.ActiveEntryId)
            .OfType<BranchSummaryEntry>().Single();
        Assert.Equal(a2, branchEntry.FromId);
        Assert.Equal(a3, branchEntry.ParentId);
    }

    [Fact]
    public async Task RestartWithFreshCacheRestoresTheAgentState()
    {
        using var temp = TempDirectory.Create();
        var (workspace, _) = await CreateWorkspaceAsync(temp);
        var firstAgent = new RecordingAgent();

        var first = await CreateControllerAsync(temp, firstAgent);
        await SeedTurnAsync(first.Sessions);
        // No compaction: the cache describes the current effective context.
        await first.Sessions.PersistAgentStateCacheAsync(CancellationToken.None);

        var restoredAgent = new RecordingAgent();
        var restarted = await CreateControllerAsync(temp, restoredAgent, continueSession: true);

        Assert.Equal(1, restoredAgent.DeserializeCalls);
        Assert.False(restarted.Sessions.Document!.HasBoundaryAfterStateCache(restarted.Sessions.ActiveEntryId));
    }

    private static async Task<(string Workspace, SessionStore Store)> CreateWorkspaceAsync(TempDirectory temp)
    {
        var workspace = Directory.CreateDirectory(Path.Combine(temp.Path, "repo")).FullName;
        var store = new SessionStore(workspace, Path.Combine(temp.Path, "sessions"));
        return (workspace, store);
    }

    private static async Task<(SessionController Controller, OperationLease Lease)> BeginExclusiveOperationAsync(
        TempDirectory temp,
        string operation)
    {
        var controller = (await CreateControllerAsync(temp)).Sessions;
        var cts = new CancellationTokenSource();
        var running = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var task = controller.RunExclusiveAsync<bool>(
            operation,
            async token =>
            {
                running.TrySetResult();
                // Holds the operation until the lease cancels it.
                await Task.Delay(Timeout.Infinite, token);
                return true;
            },
            cts.Token);
        await running.Task;
        return (controller, new OperationLease(task, cts));
    }

    private sealed class OperationLease(Task task, CancellationTokenSource cancellation) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            cancellation.Cancel();
            try
            {
                await task;
            }
            catch (OperationCanceledException)
            {
                // Expected: canceling the lease ends the held operation and releases it.
            }
        }
    }

    private static async Task<ControllerResult> CreateControllerAsync(
        TempDirectory temp,
        AIAgent? agentOverride = null,
        bool continueSession = false)
    {
        // The caller owns (and disposes) the temp directory: the session files live under it
        // and outlive this method.
        var (workspace, _) = await CreateWorkspaceAsync(temp);

        var sessionHistory = new PiSessionChatHistoryProvider();
        var compactionTarget = new CompactionTarget();
        var agent = agentOverride ?? new RecordingAgent();

        var summarizer = new CompactionRuntimeTests.ScriptedLeafClient([
            new CompactionRuntimeTests.LeafBehavior
            {
                BuildUpdates = _ =>
                [
                    new ChatResponseUpdate(ChatRole.Assistant, new AIContent[]
                    {
                        new TextContent("## Goal\ntest summary"),
                    }),
                ],
            },
        ]);

        var bootstrap = new AgentBootstrap(
            Agent: agent,
            SummaryClient: summarizer,
            ContextFiles: [],
            Skills: [],
            PromptTemplates: [],
            ExtensionHost: new PiSharpExtensionHost(),
            RetryPolicy: RetryPolicyOptions.Disabled,
            TurnQueue: new TurnMessageQueue(),
            SessionHistory: sessionHistory,
            Compaction: compactionTarget);

        var options = new CliOptions(
            WorkingDirectory: workspace,
            Model: "test-model",
            Endpoint: null,
            ApiKey: "test",
            ContextTokens: 128_000,
            MaxOutputTokens: 1024,
            Prompt: null,
            FilePaths: [],
            ShowHelp: false,
            ContinueSession: continueSession,
            ResumeSession: false,
            SessionSelector: null,
            SessionName: null,
            SessionDirectory: Path.Combine(temp.Path, "sessions"),
            NoSession: false,
            ContextRoot: null,
            ExtensionPaths: [],
            SkillPaths: [],
            PromptTemplatePaths: [],
            NoExtensions: true,
            NoSkills: true,
            NoPromptTemplates: true,
            ProjectTrustOverride: true,
            OutputMode: OutputMode.Text,
            PrintMode: false,
            ReadOnly: false,
            NoTools: false,
            AutoRetry: false);

        var sessions = await SessionController.CreateAsync(bootstrap, options, CancellationToken.None);
        compactionTarget.Current = sessions;
        return new ControllerResult(sessions, agent);
    }

    private static async Task<MessageEntry> PersistRawUserAsync(SessionController controller, string text)
    {
        // Persist through the controller so the active leaf advances with each entry.
        await controller.PersistUserMessageAsync(text, null, CancellationToken.None);
        return (MessageEntry)controller.Document!.GetActiveEntryPath(controller.ActiveEntryId!)[^1];
    }

    private static async Task<MessageEntry> PersistRawAssistantAsync(SessionController controller, string text)
    {
        await controller.PersistAssistantMessagesAsync(
        [
            JsonSerializer.SerializeToElement(new
            {
                role = "assistant",
                content = new object[] { new { type = "text", text } },
            }),
        ], CancellationToken.None);
        return (MessageEntry)controller.Document!.GetActiveEntryPath(controller.ActiveEntryId!)[^1];
    }

    private static async Task<string> AppendRawAsync(
        SessionStore store,
        SessionDocument document,
        string parentId,
        object message)
    {
        var entry = new MessageEntry(
            Guid.NewGuid().ToString("N"),
            parentId,
            DateTimeOffset.UtcNow,
            JsonSerializer.SerializeToElement(message));
        await store.AppendEntriesAsync(document, [entry], CancellationToken.None);
        return entry.Id;
    }

    private static async Task SeedTurnAsync(SessionController controller)
    {
        await controller.PersistUserMessageAsync("seed prompt", null, CancellationToken.None);
        await controller.PersistAssistantMessagesAsync(
        [
            JsonSerializer.SerializeToElement(new
            {
                role = "assistant",
                content = new object[] { new { type = "text", text = "seed answer" } },
            }),
        ], CancellationToken.None);
    }

    /// <summary>
    /// Seeds ~28k tokens of history — enough to exceed the 20k keep-recent budget so a manual
    /// compaction produces a real plan instead of the "too small" conflict.
    /// </summary>
    private static async Task SeedCompactableHistoryAsync(SessionController controller)
    {
        for (var i = 1; i <= 4; i++)
        {
            var filler = new string((char)('a' + i), 28_000);
            await controller.PersistUserMessageAsync($"seed {i} {filler}", null, CancellationToken.None);
            await controller.PersistAssistantMessagesAsync(
            [
                JsonSerializer.SerializeToElement(new
                {
                    role = "assistant",
                    content = new object[] { new { type = "text", text = $"seed answer {i}" } },
                }),
            ], CancellationToken.None);
        }
    }

    private sealed class ControllerResult(SessionController sessions, AIAgent agent)
    {
        public SessionController Sessions { get; } = sessions;

        public AIAgent Agent { get; } = agent;
    }

    /// <summary>Agent fake recording session creation vs. deserialization on restart.</summary>
    internal sealed class RecordingAgent : AIAgent
    {
        private int _createCalls;
        private int _deserializeCalls;

        public int CreateCalls
        {
            get
            {
                lock (this)
                {
                    return _createCalls;
                }
            }
        }

        public int DeserializeCalls
        {
            get
            {
                lock (this)
                {
                    return _deserializeCalls;
                }
            }
        }

        protected override ValueTask<AgentSession> CreateSessionCoreAsync(CancellationToken cancellationToken = default)
        {
            lock (this)
            {
                _createCalls++;
            }
            return ValueTask.FromResult<AgentSession>(new RecordingAgentSession());
        }

        protected override ValueTask<JsonElement> SerializeSessionCoreAsync(
            AgentSession session,
            JsonSerializerOptions? jsonSerializerOptions = null,
            CancellationToken cancellationToken = default)
        {
            return ValueTask.FromResult(JsonSerializer.SerializeToElement(new { cached = true }));
        }

        protected override ValueTask<AgentSession> DeserializeSessionCoreAsync(
            JsonElement serializedState,
            JsonSerializerOptions? jsonSerializerOptions = null,
            CancellationToken cancellationToken = default)
        {
            lock (this)
            {
                _deserializeCalls++;
            }
            return ValueTask.FromResult<AgentSession>(new RecordingAgentSession());
        }

        protected override Task<AgentResponse> RunCoreAsync(
            IEnumerable<ChatMessage> messages,
            AgentSession? session = null,
            AgentRunOptions? options = null,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        protected override IAsyncEnumerable<AgentResponseUpdate> RunCoreStreamingAsync(
            IEnumerable<ChatMessage> messages,
            AgentSession? session = null,
            AgentRunOptions? options = null,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    internal sealed class RecordingAgentSession : AgentSession
    {
    }

    private sealed class NoopChatOutput : IChatOutput
    {
        public void AgentStarted() { }
        public void AgentFinished(string assistantText, bool cancelled) { }
        public void AssistantMessageStarted() { }
        public void AssistantMessageFinished(string assistantText) { }
        public void WriteText(string text) { }
        public void ToolStarted(string callId, string name, string arguments) { }
        public void ToolUpdated(string callId, string name, string arguments) { }
        public void ToolFinished(string callId, string name, string? error, string result) { }
        public void WriteLine() { }

        public void CompactionStarted(string reason) { }

        public void CompactionFinished(string reason, int tokensBefore, int tokensAfter, bool aborted, string? error)
        {
        }

        public void CompactionFailed(string reason, string error, bool aborted) { }
    }
}
