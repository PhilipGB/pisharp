using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;
using PiSharp.Extensions.Abstractions;
using NativeContext = PiSharp.Extensions.Abstractions.PiSharpExtensionContext;
using PiSharp.Runtime;
using PiSharp.Runtime.Extensions;
using PiSharp.Runtime.Providers;
using PiSharp.Runtime.Sessions;

namespace PiSharp.Tests;

public sealed class BoundaryCancellationTests
{
    [Fact]
    public async Task CancellationInFirstMutableHandlerKeepsCompletedTurnOutcomeAndSettlesAbortedOnce()
    {
        var registration = new ExtensionRegistration();
        var adapter = new NativeExtensionRegistrationAdapter(registration);
        var started = new TaskCompletionSource<(PiSharpExtensionTurnEndBoundaryEvent Event,
            NativeContext Context, CancellationToken Token)>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<PiSharpBoundaryResult?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var handlerFinished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var outcomes = new List<PiSharpExtensionAgentOutcome>();
        var settled = new List<bool>();
        adapter.AddTurnEndBoundaryHandler(async (eventData, context, token) =>
        {
            started.TrySetResult((eventData, context, token));
            try { return await release.Task; }
            finally { handlerFinished.TrySetResult(); }
        });
        adapter.AddAgentBeforeSettleHandler((eventData, _, _) =>
        {
            outcomes.Add(eventData.Outcome);
            return ValueTask.FromResult<PiSharpBoundaryResult?>(null);
        });
        adapter.AddAgentSettledHandler((eventData, _, _) =>
        {
            settled.Add(eventData.Aborted);
            return ValueTask.CompletedTask;
        });
        adapter.Close();

        var client = new ScriptedClient(Plan.Reply("finished"));
        var fixture = await CreateRunAsync(registration, client);
        using var cancellation = new CancellationTokenSource();
        var run = ReadEventsAsync(fixture.Run, cancellation.Token);
        var callback = await AwaitCallbackAsync(started.Task, run);
        Assert.Equal(PiSharpExtensionAgentOutcome.Completed, callback.Event.Outcome);
        Assert.False(callback.Event.Turn.Aborted);

        cancellation.Cancel();
        await Assert.ThrowsAsync<ObjectDisposedException>(() => callback.Context.Session!.AppendEntryAsync("after-cancel"));
        var events = await run.WaitAsync(TimeSpan.FromSeconds(10));
        release.TrySetResult(null);
        await handlerFinished.Task.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Single(settled);
        Assert.True(settled[0]);
        Assert.Empty(outcomes);
        Assert.Equal(1, client.RequestCount);
        Assert.Single(events, item => item.Type == "agent_settled");
    }

    [Fact]
    public async Task CancellationInLaterMutableHandlerDiscardsUncommittedEarlierProposal()
    {
        var registration = new ExtensionRegistration();
        var adapter = new NativeExtensionRegistrationAdapter(registration);
        var started = new TaskCompletionSource<(PiSharpExtensionTurnEndBoundaryEvent Event,
            NativeContext Context)>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<PiSharpBoundaryResult?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var handlerFinished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var callbacks = 0;
        var settled = new List<bool>();
        adapter.AddTurnEndBoundaryHandler((_, _, _) =>
        {
            Interlocked.Increment(ref callbacks);
            return ValueTask.FromResult<PiSharpBoundaryResult?>(new([
                new PiSharpCustomEntryDraft("pending-first")
            ], @continue: true));
        });
        adapter.AddTurnEndBoundaryHandler(async (eventData, context, _) =>
        {
            Interlocked.Increment(ref callbacks);
            started.TrySetResult((eventData, context));
            try { return await release.Task; }
            finally { handlerFinished.TrySetResult(); }
        });
        adapter.AddAgentSettledHandler((eventData, _, _) =>
        {
            settled.Add(eventData.Aborted);
            return ValueTask.CompletedTask;
        });
        adapter.Close();

        var client = new ScriptedClient(Plan.Reply("finished"));
        var fixture = await CreateRunAsync(registration, client);
        using var cancellation = new CancellationTokenSource();
        var run = ReadEventsAsync(fixture.Run, cancellation.Token);
        var callback = await AwaitCallbackAsync(started.Task, run);
        Assert.Equal(["pending-first"], callback.Event.Entries.OfType<PiSharpCustomEntryDraft>()
            .Select(entry => entry.CustomType));

        cancellation.Cancel();
        await Assert.ThrowsAsync<ObjectDisposedException>(() => callback.Context.Session!.AppendEntryAsync("after-cancel"));
        var events = await run.WaitAsync(TimeSpan.FromSeconds(10));
        release.TrySetResult(new([new PiSharpCustomEntryDraft("late-result")]));
        await handlerFinished.Task.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(2, callbacks);
        Assert.DoesNotContain(fixture.Session.Tree.Entries, entry => entry.Type is "custom_message" or "custom");
        Assert.Equal(1, client.RequestCount);
        Assert.Single(settled);
        Assert.True(settled[0]);
        Assert.Single(events, item => item.Type == "agent_settled");
    }

    [Fact]
    public async Task CancellationDuringBoundarySaveStopsContinuationAndKeepsSaveOutcomeConservative()
    {
        var registration = new ExtensionRegistration();
        var adapter = new NativeExtensionRegistrationAdapter(registration);
        var saveStarted = new TaskCompletionSource<(bool HasProposal, CancellationToken Token)>(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseSave = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var settled = new List<bool>();
        adapter.AddTurnEndBoundaryHandler((_, _, _) => ValueTask.FromResult<PiSharpBoundaryResult?>(new([
            new PiSharpCustomMessageEntryDraft("save-boundary", "proposal", display: false)
        ], @continue: true)));
        adapter.AddAgentSettledHandler((eventData, _, _) =>
        {
            settled.Add(eventData.Aborted);
            return ValueTask.CompletedTask;
        });
        adapter.Close();

        var client = new ScriptedClient(Plan.Reply("finished"));
        var session = new ConversationSession(Path.GetTempPath(), "boundary-cancel-save", null);
        var agent = new PiAgent(client, new CodingTools(Path.GetTempPath()), noBuiltinTools: true,
            liveExtensionRegistration: registration);
        var runFixture = new RunFixture(await ConversationRun.OpenAsync(agent, session, save: async token =>
        {
            var hasProposal = session.Tree.Entries.Any(entry => entry.Type == "custom_message");
            if (!hasProposal) return;
            saveStarted.TrySetResult((true, token));
            await releaseSave.Task;
        }), session);
        using var cancellation = new CancellationTokenSource();
        var run = ReadEventsAsync(runFixture.Run, cancellation.Token);
        var save = await saveStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(save.HasProposal);

        cancellation.Cancel();
        Assert.True(save.Token.IsCancellationRequested);
        releaseSave.TrySetResult();
        var events = await run.WaitAsync(TimeSpan.FromSeconds(10));

        // Save began after the proposal entered the in-memory tree. A host save can persist it before
        // cancellation is observed, so only the absence of a later provider continuation is asserted.
        Assert.Equal(1, client.RequestCount);
        Assert.Single(settled);
        Assert.True(settled[0]);
        Assert.Single(events, item => item.Type == "agent_settled");
    }

    [Fact]
    public async Task CancellationDuringContinuationProviderDoesNotUndoCommittedBoundaryEntry()
    {
        var registration = new ExtensionRegistration();
        var adapter = new NativeExtensionRegistrationAdapter(registration);
        var continuationStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var settled = new List<bool>();
        var boundaryCalls = 0;
        var boundaryOutcomes = new List<PiSharpExtensionAgentOutcome>();
        adapter.AddTurnEndBoundaryHandler((eventData, _, _) =>
        {
            boundaryOutcomes.Add(eventData.Outcome);
            if (Interlocked.Increment(ref boundaryCalls) != 1)
                return ValueTask.FromResult<PiSharpBoundaryResult?>(null);
            return ValueTask.FromResult<PiSharpBoundaryResult?>(new([
                new PiSharpCustomMessageEntryDraft("committed-boundary", "continue", display: false)
            ], @continue: true));
        });
        adapter.AddAgentSettledHandler((eventData, _, _) =>
        {
            settled.Add(eventData.Aborted);
            return ValueTask.CompletedTask;
        });
        adapter.Close();

        var client = new ScriptedClient(Plan.Reply("first answer"), Plan.Block(continuationStarted));
        var fixture = await CreateRunAsync(registration, client);
        using var cancellation = new CancellationTokenSource();
        var run = ReadEventsAsync(fixture.Run, cancellation.Token);
        await continuationStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
        cancellation.Cancel();
        var events = await run.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(2, client.RequestCount);
        Assert.Equal(2, boundaryCalls);
        Assert.Equal([PiSharpExtensionAgentOutcome.Completed, PiSharpExtensionAgentOutcome.Aborted], boundaryOutcomes);
        Assert.Contains(fixture.Session.Tree.Entries, entry => entry.Type == "custom_message" &&
            entry.Payload.GetRawText().Contains("committed-boundary", StringComparison.Ordinal));
        Assert.Single(settled);
        Assert.True(settled[0]);
        Assert.Single(events, item => item.Type == "agent_settled");
    }

    [Fact]
    public async Task NormalSettledObserverRemainsActiveBeyondTwoSecondsUntilReleased()
    {
        var registration = new ExtensionRegistration();
        var adapter = new NativeExtensionRegistrationAdapter(registration);
        var started = new TaskCompletionSource<(NativeContext Context, CancellationToken Token,
            PiSharpExtensionAgentSettledEvent Event)>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        adapter.AddAgentSettledHandler(async (eventData, context, token) =>
        {
            started.TrySetResult((context, token, eventData));
            await release.Task;
        });
        adapter.Close();

        var fixture = await CreateRunAsync(registration, new ScriptedClient(Plan.Reply("finished")));
        var run = ReadEventsAsync(fixture.Run, CancellationToken.None);
        var observer = await AwaitCallbackAsync(started.Task, run);
        try
        {
            // Cross the former production deadline while the handler deliberately remains pending.
            await Task.Delay(TimeSpan.FromMilliseconds(2500));
            Assert.False(observer.Token.IsCancellationRequested);
            Assert.False(run.IsCompleted);
            Assert.False(observer.Event.Aborted);
            Assert.NotNull(observer.Context.Session!.Snapshot);
            Assert.NotNull(observer.Context.Capabilities!.GetSessionSnapshot());
        }
        finally { release.TrySetResult(); }

        var events = await run.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Single(events, item => item.Type == "agent_settled");
        Assert.DoesNotContain(events, item => item.Error is not null);
    }

    [Fact]
    public async Task CancellationDuringSettledObserverRevokesContextAndDetachesWithoutRewritingCompletedOutcome()
    {
        var registration = new ExtensionRegistration();
        var adapter = new NativeExtensionRegistrationAdapter(registration);
        var started = new TaskCompletionSource<(NativeContext Context, CancellationToken Token,
            PiSharpExtensionAgentSettledEvent Event)>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handlerFinished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var settledCalls = 0;
        var beforeSettle = new List<PiSharpExtensionAgentOutcome>();
        adapter.AddAgentBeforeSettleHandler((eventData, _, _) =>
        {
            beforeSettle.Add(eventData.Outcome);
            return ValueTask.FromResult<PiSharpBoundaryResult?>(null);
        });
        adapter.AddAgentSettledHandler(async (eventData, context, token) =>
        {
            Interlocked.Increment(ref settledCalls);
            started.TrySetResult((context, token, eventData));
            try { await release.Task; }
            finally { handlerFinished.TrySetResult(); }
        });
        adapter.Close();

        var fixture = await CreateRunAsync(registration, new ScriptedClient(Plan.Reply("finished")));
        using var cancellation = new CancellationTokenSource();
        var run = ReadEventsAsync(fixture.Run, cancellation.Token);
        var observer = await AwaitCallbackAsync(started.Task, run);
        var session = observer.Context.Session!;
        var capabilities = observer.Context.Capabilities!;
        try
        {
            Assert.False(observer.Event.Aborted);
            Assert.Equal(PiSharpExtensionAgentOutcome.Completed, Assert.Single(beforeSettle));

            cancellation.Cancel();
            Assert.True(observer.Token.IsCancellationRequested);
            await Assert.ThrowsAsync<ObjectDisposedException>(() => session.AppendEntryAsync("after-cancel"));
            Assert.Throws<ObjectDisposedException>(() => capabilities.GetSessionSnapshot());
            var events = await run.WaitAsync(TimeSpan.FromSeconds(10));

            Assert.False(handlerFinished.Task.IsCompleted);
            Assert.False(observer.Event.Aborted);
            Assert.Equal(1, settledCalls);
            Assert.Single(events, item => item.Type == "agent_settled");
        }
        finally { release.TrySetResult(); }
        await handlerFinished.Task.WaitAsync(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task BeforeSettleCancellationAfterSynchronousProposalCommitsOnceWithoutContinuation()
    {
        var registration = new ExtensionRegistration();
        var adapter = new NativeExtensionRegistrationAdapter(registration);
        using var cancellation = new CancellationTokenSource();
        var beforeSettleCalls = 0;
        var settled = new List<bool>();
        adapter.AddAgentBeforeSettleHandler((_, _, _) =>
        {
            Interlocked.Increment(ref beforeSettleCalls);
            cancellation.Cancel();
            return ValueTask.FromResult<PiSharpBoundaryResult?>(new([
                new PiSharpCustomEntryDraft("cancelled-before-settle")
            ], @continue: true));
        });
        adapter.AddAgentSettledHandler((eventData, _, _) =>
        {
            settled.Add(eventData.Aborted);
            return ValueTask.CompletedTask;
        });
        adapter.Close();

        var client = new ScriptedClient(Plan.Reply("completed"), Plan.Reply("must-not-run"));
        var session = new ConversationSession(Path.GetTempPath(), "boundary-cancel-before-settle", null);
        var agent = new PiAgent(client, new CodingTools(Path.GetTempPath()), noBuiltinTools: true,
            liveExtensionRegistration: registration);
        var persistedProposalCount = 0;
        var runFixture = new RunFixture(await ConversationRun.OpenAsync(agent, session, save: _ =>
        {
            if (session.Tree.Entries.Any(entry => entry.Type == "extension_state" &&
                    entry.Payload.GetRawText().Contains("cancelled-before-settle", StringComparison.Ordinal)))
                Interlocked.Increment(ref persistedProposalCount);
            return Task.CompletedTask;
        }), session);

        var events = await ReadEventsAsync(runFixture.Run, cancellation.Token).WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(1, beforeSettleCalls);
        Assert.Equal(1, persistedProposalCount);
        Assert.Equal(1, client.RequestCount);
        Assert.Single(session.Tree.Entries, entry => entry.Type == "extension_state" &&
            entry.Payload.GetRawText().Contains("cancelled-before-settle", StringComparison.Ordinal));
        Assert.True(Assert.Single(settled));
        Assert.Single(events, item => item.Type == "agent_settled");
    }

    [Fact]
    public async Task CancellationDuringActiveProviderTurnDispatchesAbortedBoundaryOutcome()
    {
        var registration = new ExtensionRegistration();
        var adapter = new NativeExtensionRegistrationAdapter(registration);
        var requestStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var outcomes = new List<PiSharpExtensionAgentOutcome>();
        var settled = new List<bool>();
        adapter.AddTurnEndBoundaryHandler((eventData, _, _) =>
        {
            outcomes.Add(eventData.Outcome);
            Assert.True(eventData.Turn.Aborted);
            return ValueTask.FromResult<PiSharpBoundaryResult?>(null);
        });
        adapter.AddAgentSettledHandler((eventData, _, _) =>
        {
            settled.Add(eventData.Aborted);
            return ValueTask.CompletedTask;
        });
        adapter.Close();

        var fixture = await CreateRunAsync(registration, new ScriptedClient(Plan.Block(requestStarted, yieldPartial: true)));
        using var cancellation = new CancellationTokenSource();
        var run = ReadEventsAsync(fixture.Run, cancellation.Token);
        await requestStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
        cancellation.Cancel();
        var events = await run.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal([PiSharpExtensionAgentOutcome.Aborted], outcomes);
        Assert.True(Assert.Single(settled));
        Assert.Single(events, item => item.Type == "agent_settled");
    }

    [Fact]
    public async Task FinalProviderErrorReachesBeforeSettleAsErrorAndSettlesWithoutAbort()
    {
        var registration = new ExtensionRegistration();
        var adapter = new NativeExtensionRegistrationAdapter(registration);
        var outcomes = new List<PiSharpExtensionAgentOutcome>();
        var settled = new List<bool>();
        adapter.AddAgentBeforeSettleHandler((eventData, _, _) =>
        {
            outcomes.Add(eventData.Outcome);
            return ValueTask.FromResult<PiSharpBoundaryResult?>(null);
        });
        adapter.AddAgentSettledHandler((eventData, _, _) =>
        {
            settled.Add(eventData.Aborted);
            return ValueTask.CompletedTask;
        });
        adapter.Close();

        var client = new ScriptedClient(Plan.Fail(new IOException("final provider failure")));
        var fixture = await CreateRunAsync(registration, client);
        var events = await ReadEventsAsync(fixture.Run, CancellationToken.None);

        Assert.Equal(PiSharpExtensionAgentOutcome.Error, Assert.Single(outcomes));
        Assert.False(Assert.Single(settled));
        Assert.Contains(events, item => item.Type == "turn_failed");
        Assert.Single(events, item => item.Type == "agent_settled");
    }

    private static async Task<T> AwaitCallbackAsync<T>(Task<T> callback, Task<IReadOnlyList<AgentLifecycleEvent>> run)
    {
        if (await Task.WhenAny(callback, run).WaitAsync(TimeSpan.FromSeconds(10)) == run)
            throw new InvalidOperationException("Run settled before callback: " +
                string.Join(" | ", (await run).Select(item => item.Type + ":" + item.Error)));
        return await callback;
    }

    private static async Task<RunFixture> CreateRunAsync(ExtensionRegistration registration, ScriptedClient client,
        Func<CancellationToken, Task>? save = null)
    {
        var session = new ConversationSession(Path.GetTempPath(), "boundary-cancellation", null);
        var agent = new PiAgent(client, new CodingTools(Path.GetTempPath()), noBuiltinTools: true,
            liveExtensionRegistration: registration);
        var run = await ConversationRun.OpenAsync(agent, session, save: save);
        run.SetExtensionCapabilitiesFactory((_, contextSession) =>
            new PiSharpExtensionCapabilities(null, contextSession, null));
        return new(run, session);
    }

    private static async Task<IReadOnlyList<AgentLifecycleEvent>> ReadEventsAsync(ConversationRun run,
        CancellationToken cancellationToken)
    {
        var events = new List<AgentLifecycleEvent>();
        await foreach (var item in run.RunEventsAsync("prompt", cancellationToken)) events.Add(item);
        return events;
    }

    private sealed record RunFixture(ConversationRun Run, ConversationSession Session);

    private sealed record Plan(string? ReplyText = null, Exception? Error = null,
        TaskCompletionSource? Gate = null, TaskCompletionSource? Started = null, bool YieldPartial = false)
    {
        internal static Plan Reply(string text) => new(ReplyText: text);
        internal static Plan Fail(Exception error) => new(Error: error);
        internal static Plan Block(TaskCompletionSource started, bool yieldPartial = false) =>
            new(Gate: new(), Started: started, YieldPartial: yieldPartial);
    }

    private sealed class ScriptedClient(params Plan[] plans) : IChatClient
    {
        private int _requestCount;
        internal int RequestCount => Volatile.Read(ref _requestCount);

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var index = Interlocked.Increment(ref _requestCount) - 1;
            if (index >= plans.Length) throw new InvalidOperationException($"Unexpected provider request {index + 1}.");
            var plan = plans[index];
            if (plan.YieldPartial)
            {
                yield return new(ChatRole.Assistant, "partial");
                plan.Started?.TrySetResult();
            }
            else plan.Started?.TrySetResult();
            if (plan.Gate is not null) await plan.Gate.Task.WaitAsync(cancellationToken);
            if (plan.Error is not null) throw plan.Error;
            cancellationToken.ThrowIfCancellationRequested();
            yield return new(ChatRole.Assistant, plan.ReplyText ?? "reply");
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }
}
