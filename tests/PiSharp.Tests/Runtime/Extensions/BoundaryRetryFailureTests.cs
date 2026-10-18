using System.Runtime.CompilerServices;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using PiSharp.Extensions.Abstractions;
using PiSharp.Runtime;
using PiSharp.Runtime.Extensions;
using PiSharp.Runtime.Sessions;

namespace PiSharp.Tests;

public sealed class BoundaryRetryFailureTests
{
    [Fact]
    public async Task PartialOutputTransientFailureRetriesBeforeOneSuccessfulBoundary()
    {
        var registration = new ExtensionRegistration();
        var adapter = new NativeExtensionRegistrationAdapter(registration);
        var outcomes = new List<(PiSharpExtensionAgentOutcome Outcome, int TurnIndex)>();
        var settledAborted = new List<bool>();
        adapter.AddTurnEndBoundaryHandler((eventData, _, _) =>
        {
            outcomes.Add((eventData.Outcome, eventData.Turn.TurnIndex));
            return ValueTask.FromResult<PiSharpBoundaryResult?>(new([
                new PiSharpCustomMessageEntryDraft("partial-retry", "persist once", display: false)
            ]));
        });
        adapter.AddAgentSettledHandler((eventData, _, _) =>
        {
            settledAborted.Add(eventData.Aborted);
            return ValueTask.CompletedTask;
        });
        adapter.Close();

        var client = new PartialThenSuccessClient();
        var session = new ConversationSession(Path.GetTempPath(), "boundary-partial-retry", null);
        var run = await OpenRunAsync(client, session, registration, maxRetries: 1);
        var events = await ConsumeAsync(run.RunEventsAsync("retry partial output"));

        Assert.Equal(2, client.RequestCount);
        Assert.Equal([(PiSharpExtensionAgentOutcome.Completed, 0)], outcomes);
        Assert.Single(session.Tree.Entries, entry => entry.Type == "custom_message");
        Assert.Equal("persist once", session.ContextMessages().Single(message => message.Text == "persist once").Text);
        Assert.Contains(events, item => item.Type == "auto_retry_end" && item.RetrySuccess == true);
        Assert.Single(events, item => item.Type == "agent_settled");
        Assert.Equal([false], settledAborted);
    }

    [Fact]
    public async Task AbortAfterSuccessfulRetryDoesNotReplayTheFailedAttemptBoundary()
    {
        var registration = new ExtensionRegistration();
        var adapter = new NativeExtensionRegistrationAdapter(registration);
        using var cancellation = new CancellationTokenSource();
        var outcomes = new List<PiSharpExtensionAgentOutcome>();
        var settled = new List<bool>();
        adapter.AddTurnEndBoundaryHandler((eventData, _, _) =>
        {
            outcomes.Add(eventData.Outcome);
            return ValueTask.FromResult<PiSharpBoundaryResult?>(new([
                new PiSharpCustomEntryDraft("retried-turn")
            ]));
        });
        adapter.AddAgentBeforeSettleHandler((_, _, _) =>
        {
            cancellation.Cancel();
            return ValueTask.FromResult<PiSharpBoundaryResult?>(null);
        });
        adapter.AddAgentSettledHandler((eventData, _, _) =>
        {
            settled.Add(eventData.Aborted);
            return ValueTask.CompletedTask;
        });
        adapter.Close();
        var client = new PartialThenSuccessClient();
        var session = new ConversationSession(Path.GetTempPath(), "boundary-retry-then-abort", null);
        var run = await OpenRunAsync(client, session, registration, maxRetries: 1);

        var events = await ConsumeAsync(run.RunEventsAsync("retry then abort", cancellation.Token))
            .WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(2, client.RequestCount);
        Assert.Equal([PiSharpExtensionAgentOutcome.Completed], outcomes);
        Assert.Single(session.ExtensionStateEntries());
        Assert.Equal([true], settled);
        Assert.Single(events, item => item.Type == "agent_settled");
    }

    [Fact]
    public async Task ExhaustedTransientRetryProducesOneErrorBoundaryAndNoDuplicateDraft()
    {
        var registration = new ExtensionRegistration();
        var adapter = new NativeExtensionRegistrationAdapter(registration);
        var outcomes = new List<(PiSharpExtensionAgentOutcome Outcome, int TurnIndex)>();
        var settledAborted = new List<bool>();
        adapter.AddTurnEndBoundaryHandler((eventData, _, _) =>
        {
            outcomes.Add((eventData.Outcome, eventData.Turn.TurnIndex));
            return ValueTask.FromResult<PiSharpBoundaryResult?>(new([
                new PiSharpCustomMessageEntryDraft("exhausted-retry", "persist once", display: false)
            ]));
        });
        adapter.AddAgentSettledHandler((eventData, _, _) =>
        {
            settledAborted.Add(eventData.Aborted);
            return ValueTask.CompletedTask;
        });
        adapter.Close();

        var client = new AlwaysUnavailableClient();
        var session = new ConversationSession(Path.GetTempPath(), "boundary-exhausted-retry", null);
        var run = await OpenRunAsync(client, session, registration, maxRetries: 1);
        var events = await ConsumeAsync(run.RunEventsAsync("exhaust retry"));

        Assert.Equal(2, client.RequestCount);
        Assert.Equal([(PiSharpExtensionAgentOutcome.Error, 0)], outcomes);
        Assert.Single(session.Tree.Entries, entry => entry.Type == "custom_message");
        Assert.Contains(events, item => item.Type == "turn_failed" && item.WillRetry == false);
        Assert.Contains(events, item => item.Type == "agent_settled");
        Assert.Equal([false], settledAborted);
    }

    [Fact]
    public async Task RequestPreparationFailureReachesMutableBoundaryWithoutCallingProvider()
    {
        var registration = new ExtensionRegistration();
        var adapter = new NativeExtensionRegistrationAdapter(registration);
        var outcomes = new List<(PiSharpExtensionAgentOutcome Outcome, int TurnIndex)>();
        var settledAborted = new List<bool>();
        adapter.AddContextTransform((_, _) => ValueTask.FromException<IReadOnlyList<ChatMessage>>(
            new InvalidOperationException("selected request preparation failure")));
        adapter.AddTurnEndBoundaryHandler((eventData, _, _) =>
        {
            outcomes.Add((eventData.Outcome, eventData.Turn.TurnIndex));
            return ValueTask.FromResult<PiSharpBoundaryResult?>(new([
                new PiSharpCustomMessageEntryDraft("preparation-failure", "persist once", display: false)
            ]));
        });
        adapter.AddAgentSettledHandler((eventData, _, _) =>
        {
            settledAborted.Add(eventData.Aborted);
            return ValueTask.CompletedTask;
        });
        adapter.Close();

        var client = new AlwaysUnavailableClient();
        var session = new ConversationSession(Path.GetTempPath(), "boundary-preparation-failure", null);
        var run = await OpenRunAsync(client, session, registration, maxRetries: 0);
        var events = await ConsumeAsync(run.RunEventsAsync("fail before request"));

        Assert.Equal(0, client.RequestCount);
        Assert.Equal([(PiSharpExtensionAgentOutcome.Error, 0)], outcomes);
        Assert.Single(session.Tree.Entries, entry => entry.Type == "custom_message");
        Assert.Contains(events, item => item.Type == "turn_failed" &&
            item.Error?.Contains("selected request preparation failure", StringComparison.Ordinal) == true);
        Assert.Equal([false], settledAborted);
    }

    [Fact]
    public async Task RetryRestoreFailureRollsBackBoundaryProposalAndFailsExistingRetryController()
    {
        var session = new ConversationSession(Path.GetTempPath(), "boundary-retry-restore-failure", null);
        session.Append(new ChatMessage(ChatRole.User, "canonical request"));
        var originalEntries = session.Tree.Entries.ToArray();
        var restoreCalls = 0;
        var adopted = false;
        var coordinator = new ExtensionBoundaryCoordinator(session, null,
            (_, _) =>
            {
                restoreCalls++;
                return Task.FromException<AgentSession>(new IOException("execution restore failed"));
            },
            (_, _) => adopted = true,
            () => ([], []));
        var controller = new AgentRunRetryController(new AgentRunRetryPolicy(enabled: true, maxRetries: 1,
            baseDelay: TimeSpan.Zero, maxDelay: TimeSpan.Zero));
        var events = new List<AgentLifecycleEvent>();

        async IAsyncEnumerable<AgentResponseUpdate> FailAttempt(bool continuation,
            Action<AgentLifecycleEvent> observeAttempt,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            if (continuation) yield break;
            observeAttempt(new("prompt_accepted") { RunStartHead = session.Tree.HeadId });
            observeAttempt(new("model_request_failed", Error: "503 service unavailable"));
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Yield();
            throw new HttpRequestException("503 service unavailable", null, System.Net.HttpStatusCode.ServiceUnavailable);
        }

        var outcome = await controller.RunTurnAsync(
            (continuation, observe) => FailAttempt(continuation, observe),
            events.Add,
            _ => { },
            (_, _, _) => Task.CompletedTask,
            async cancellationToken =>
            {
                await coordinator.CommitAsync([
                    new PiSharpCustomMessageEntryDraft("retry-restore", "must roll back", display: false)
                ], cancellationToken);
            },
            () => { },
            () => session.Tree.HeadId,
            CancellationToken.None);

        Assert.Equal(AgentTurnOutcome.Failed, outcome);
        Assert.Equal(1, restoreCalls);
        Assert.False(adopted);
        Assert.Equal(originalEntries, session.Tree.Entries);
        Assert.DoesNotContain(session.Tree.Entries, entry => entry.Type == "custom_message");
        Assert.Single(events, item => item.Type == "auto_retry_end" &&
            item.RetryFinalError == "execution restore failed");
        Assert.Contains(events, item => item.Type == "turn_failed");
    }

    private static async Task<ConversationRun> OpenRunAsync(IChatClient client, ConversationSession session,
        ExtensionRegistration registration, int maxRetries)
    {
        var agent = new PiAgent(client, new CodingTools(Path.GetTempPath()), noBuiltinTools: true,
            retryPolicy: ProviderRetryPolicy.None,
            liveExtensionRegistration: registration,
            extensionContextTransforms: registration.ContextTransforms);
        return await ConversationRun.OpenAsync(agent, session,
            retryPolicy: new AgentRunRetryPolicy(enabled: maxRetries > 0, maxRetries,
                baseDelay: TimeSpan.Zero, maxDelay: TimeSpan.Zero),
            retryDelay: (_, token) =>
            {
                token.ThrowIfCancellationRequested();
                return Task.CompletedTask;
            });
    }

    private static async Task<List<AgentLifecycleEvent>> ConsumeAsync(IAsyncEnumerable<AgentLifecycleEvent> stream)
    {
        var events = new List<AgentLifecycleEvent>();
        await foreach (var item in stream) events.Add(item);
        return events;
    }

    private sealed class PartialThenSuccessClient : IChatClient
    {
        private int _requestCount;
        public int RequestCount => Volatile.Read(ref _requestCount);

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (Interlocked.Increment(ref _requestCount) == 1)
            {
                yield return new ChatResponseUpdate(ChatRole.Assistant, "partial output");
                await Task.Yield();
                throw new HttpRequestException("503 service unavailable", null,
                    System.Net.HttpStatusCode.ServiceUnavailable);
            }

            yield return new ChatResponseUpdate(ChatRole.Assistant, "recovered response");
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    private sealed class AlwaysUnavailableClient : IChatClient
    {
        private int _requestCount;
        public int RequestCount => Volatile.Read(ref _requestCount);

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Interlocked.Increment(ref _requestCount);
            await Task.Yield();
            throw new HttpRequestException("503 service unavailable", null,
                System.Net.HttpStatusCode.ServiceUnavailable);
#pragma warning disable CS0162
            yield break;
#pragma warning restore CS0162
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }
}
