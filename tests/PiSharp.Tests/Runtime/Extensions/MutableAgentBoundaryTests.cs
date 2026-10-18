using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Extensions.AI;
using PiSharp.Extensions.Abstractions;
using PiSharp.Runtime;
using PiSharp.Runtime.Extensions;
using PiSharp.Runtime.Sessions;
using PiSharp.Runtime.Tools;
using NativeExtensions = PiSharp.Extensions.Abstractions;

namespace PiSharp.Tests;

public sealed class MutableAgentBoundaryTests
{
    [Fact]
    public async Task TurnEndDraftContinuationIsVisibleToLaterHandlersAndUsesOneAgentBracket()
    {
        var registration = new ExtensionRegistration();
        var adapter = new NativeExtensionRegistrationAdapter(registration);
        var client = new ScriptedClient((_, _) => [Reply("first")], (_, _) => [Reply("second")]);
        var turnEndCalls = 0;
        var starts = 0;
        var ends = 0;
        var settled = new List<bool>();
        var laterHandlerSawDraft = false;
        adapter.AddAgentStartHandler((_, _, _) => { starts++; return ValueTask.CompletedTask; });
        adapter.AddAgentEndHandler((_, _, _) => { ends++; return ValueTask.CompletedTask; });
        adapter.AddAgentSettledHandler((eventData, _, _) =>
        {
            settled.Add(eventData.Aborted);
            return ValueTask.CompletedTask;
        });
        adapter.AddTurnEndBoundaryHandler((_, _, _) =>
        {
            var call = Interlocked.Increment(ref turnEndCalls);
            return ValueTask.FromResult<PiSharpBoundaryResult?>(call == 1
                ? new PiSharpBoundaryResult([
                    new PiSharpCustomMessageEntryDraft("boundary", "turn boundary context", display: false)
                ], @continue: true)
                : new PiSharpBoundaryResult());
        });
        adapter.AddTurnEndBoundaryHandler((eventData, _, _) =>
        {
            if (eventData.Context.ContextMessages.Any(message => message.Text == "turn boundary context"))
                laterHandlerSawDraft = true;
            return ValueTask.FromResult<PiSharpBoundaryResult?>(null);
        });
        adapter.Close();

        var run = await OpenRunAsync(client, registration);
        var events = await ConsumeAsync(run.RunEventsAsync("start"));

        Assert.Equal(2, client.Requests.Count);
        Assert.Contains(client.Requests[1], message => message.Text == "turn boundary context");
        Assert.True(laterHandlerSawDraft);
        Assert.Equal(2, turnEndCalls);
        Assert.Equal(1, starts);
        Assert.Equal(1, ends);
        Assert.Equal([false], settled);
        Assert.Equal("agent_settled", events[^1].Type);
        Assert.Single(run.Conversation.Tree.Entries, entry => entry.Type == "custom_message");
    }

    [Fact]
    public async Task ToolLoopBoundaryUsesFinalizedResultIdsAndFeedsOneNaturalContinuationRequest()
    {
        var registration = new ExtensionRegistration();
        var adapter = new NativeExtensionRegistrationAdapter(registration);
        var tool = AIFunctionFactory.Create(() => "tool-result", name: "probe");
        registration.AddTool(new PiSharpToolRegistration(tool, ToolExposure.Direct));
        var client = new ScriptedClient((_, _) => [ToolCall("call-1", "probe")], (_, _) => [Reply("finished")]);
        var boundaryCalls = 0;
        string? resultEntryId = null;
        string? assistantEntryId = null;
        adapter.AddTurnEndBoundaryHandler((eventData, _, _) =>
        {
            if (Interlocked.Increment(ref boundaryCalls) == 1)
            {
                assistantEntryId = eventData.Turn.MessageEntryId;
                resultEntryId = Assert.Single(eventData.Turn.ToolResultEntryIds);
                Assert.Single(eventData.Turn.ToolResults);
                return ValueTask.FromResult<PiSharpBoundaryResult?>(new PiSharpBoundaryResult([
                    new PiSharpCustomMessageEntryDraft("after-tool", "after tool boundary", display: false)
                ], @continue: true));
            }
            return ValueTask.FromResult<PiSharpBoundaryResult?>(new PiSharpBoundaryResult());
        });
        adapter.Close();

        var run = await OpenRunAsync(client, registration, toolRegistrations: [new PiSharpToolRegistration(tool, ToolExposure.Direct)]);
        await foreach (var _ in run.RunEventsAsync("call tool")) { }

        Assert.Equal(2, client.Requests.Count);
        Assert.Contains(client.Requests[1], message => message.Text == "after tool boundary");
        Assert.Contains(client.Requests[1].SelectMany(message => message.Contents).OfType<FunctionResultContent>(),
            result => result.CallId == "call-1");
        Assert.NotNull(assistantEntryId);
        Assert.NotNull(resultEntryId);
        Assert.Contains(run.Conversation.Tree.ActivePath(), entry => entry.Id == assistantEntryId);
        Assert.Contains(run.Conversation.Tree.ActivePath(), entry => entry.Id == resultEntryId);
        Assert.Equal(2, boundaryCalls);
    }

    [Fact]
    public async Task BeforeSettleDraftContinuationRunsOnceAndASecondBoundaryCanDecline()
    {
        var registration = new ExtensionRegistration();
        var adapter = new NativeExtensionRegistrationAdapter(registration);
        var client = new ScriptedClient((_, _) => [Reply("first")], (_, _) => [Reply("after settle")]);
        var calls = 0;
        var previewsSawState = false;
        adapter.AddAgentBeforeSettleHandler((eventData, _, _) =>
        {
            var call = Interlocked.Increment(ref calls);
            if (call == 1)
            {
                return ValueTask.FromResult<PiSharpBoundaryResult?>(new PiSharpBoundaryResult([
                    new PiSharpCustomEntryDraft("settlement.state", JsonSerializer.SerializeToElement(new { once = true })),
                    new PiSharpCustomMessageEntryDraft("settlement.context", "settlement continuation", display: false)
                ], @continue: true));
            }
            previewsSawState = eventData.Context.ContextMessages.Any(message => message.Text == "settlement continuation");
            return ValueTask.FromResult<PiSharpBoundaryResult?>(new PiSharpBoundaryResult(@continue: false));
        });
        adapter.Close();

        var run = await OpenRunAsync(client, registration);
        await foreach (var _ in run.RunEventsAsync("settle")) { }

        Assert.Equal(2, client.Requests.Count);
        Assert.Contains(client.Requests[1], message => message.Text == "settlement continuation");
        Assert.Equal(2, calls);
        Assert.True(previewsSawState);
        Assert.Single(run.Conversation.ExtensionStateEntries());
        Assert.Single(run.Conversation.Tree.Entries, entry => entry.Type == "custom_message");
    }

    [Fact]
    public async Task InvalidContinuationIsReportedAtTurnEndAndBeforeSettle()
    {
        var registration = new ExtensionRegistration();
        var adapter = new NativeExtensionRegistrationAdapter(registration);
        adapter.AddTurnEndBoundaryHandler((_, _, _) =>
            ValueTask.FromResult<PiSharpBoundaryResult?>(new PiSharpBoundaryResult(@continue: true)));
        adapter.AddAgentBeforeSettleHandler((_, _, _) =>
            ValueTask.FromResult<PiSharpBoundaryResult?>(new PiSharpBoundaryResult(@continue: true)));
        adapter.Close();
        var run = await OpenRunAsync(new ScriptedClient((_, _) => [Reply("done")]), registration);

        var events = await ConsumeAsync(run.RunEventsAsync("no continuation context"));

        var errors = events.Where(item => item.Type == "extension_error").ToArray();
        Assert.Equal(2, errors.Length);
        Assert.Equal(new[] { "turn_end", "agent_before_settle" }, errors.Select(item =>
            JsonSerializer.SerializeToElement(item.Details).GetProperty("eventType").GetString()));
        Assert.Single(run.Conversation.Tree.Entries, entry => entry.Type == "chat" &&
            ConversationMessageCodec.RestoreEntry(entry).Role == ChatRole.Assistant);
    }

    [Fact]
    public async Task RetryDoesNotDuplicateDurableBoundaryEntries()
    {
        var registration = new ExtensionRegistration();
        var adapter = new NativeExtensionRegistrationAdapter(registration);
        var turnBoundaries = 0;
        var boundaryTurnIndices = new List<int>();
        adapter.AddTurnEndBoundaryHandler((eventData, _, _) =>
        {
            turnBoundaries++;
            boundaryTurnIndices.Add(eventData.Turn.TurnIndex);
            return ValueTask.FromResult<PiSharpBoundaryResult?>(new PiSharpBoundaryResult([
                new PiSharpCustomMessageEntryDraft("retry.boundary", "one boundary entry", display: false)
            ]));
        });
        adapter.Close();
        var client = new ScriptedClient((_, _) => throw new IOException("Selected model is at capacity"),
            (_, _) => [Reply("retry succeeded")]);
        var run = await OpenRunAsync(client, registration,
            retryPolicy: new AgentRunRetryPolicy(enabled: true, maxRetries: 1,
                baseDelay: TimeSpan.Zero, maxDelay: TimeSpan.Zero));

        var events = await ConsumeAsync(run.RunEventsAsync("retry boundary"));

        Assert.Equal(2, client.Requests.Count);
        Assert.Equal(1, turnBoundaries);
        Assert.Equal([0], boundaryTurnIndices);
        Assert.Single(run.Conversation.Tree.Entries, entry => entry.Type == "custom_message");
        Assert.Single(events, item => item.Type == "auto_retry_start");
        Assert.Single(events, item => item.Type == "auto_retry_end");
    }

    [Fact]
    public async Task AbortDuringIgnoredBeforeSettleRevokesContextAndSettlesAsAborted()
    {
        var registration = new ExtensionRegistration();
        var adapter = new NativeExtensionRegistrationAdapter(registration);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        NativeExtensions.PiSharpExtensionContext? heldContext = null;
        var settledOutcomes = new List<bool>();
        adapter.AddAgentBeforeSettleHandler(async (_, context, _) =>
        {
            heldContext = context;
            entered.TrySetResult();
            await release.Task.ConfigureAwait(false); // Deliberately ignores host cancellation.
            return null;
        });
        adapter.AddAgentSettledHandler((eventData, _, _) =>
        {
            settledOutcomes.Add(eventData.Aborted);
            return ValueTask.CompletedTask;
        });
        adapter.Close();
        var run = await OpenRunAsync(new ScriptedClient((_, _) => [Reply("done")]), registration);
        using var cancellation = new CancellationTokenSource();
        var running = ConsumeAsync(run.RunEventsAsync("abort while settling", cancellation.Token));

        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();
        var events = await running.WaitAsync(TimeSpan.FromSeconds(5));
        release.TrySetResult();

        Assert.NotNull(heldContext);
        Assert.Throws<ObjectDisposedException>(() => _ = heldContext!.Session!.Snapshot);
        Assert.Contains(events, item => item.Type == "agent_settled");
        Assert.Equal([true], settledOutcomes);
    }

    [Fact]
    public async Task AgentEndFollowUpIsConsumedBeforePreSettleRuns()
    {
        var registration = new ExtensionRegistration();
        var adapter = new NativeExtensionRegistrationAdapter(registration);
        var client = new ScriptedClient((_, _) => [Reply("first")], (_, _) => [Reply("follow-up answer")]);
        ConversationRun? run = null;
        var queued = false;
        var preSettleCalls = 0;
        var providerRequestsAtPreSettle = 0;
        adapter.AddAgentEndHandler((_, _, _) =>
        {
            if (!queued) queued = run!.TryFollowUp("queued from agent_end");
            return ValueTask.CompletedTask;
        });
        adapter.AddAgentBeforeSettleHandler((_, _, _) =>
        {
            preSettleCalls++;
            providerRequestsAtPreSettle = client.Requests.Count;
            return ValueTask.FromResult<PiSharpBoundaryResult?>(new PiSharpBoundaryResult());
        });
        adapter.Close();
        run = await OpenRunAsync(client, registration);

        await foreach (var _ in run.RunEventsAsync("first prompt")) { }

        Assert.True(queued);
        Assert.Equal(2, client.Requests.Count);
        Assert.Contains(client.Requests[1], message => message.Text == "queued from agent_end");
        Assert.Equal(1, preSettleCalls);
        Assert.Equal(2, providerRequestsAtPreSettle);
    }

    private static async Task<ConversationRun> OpenRunAsync(ScriptedClient client,
        ExtensionRegistration registration, AgentRunRetryPolicy? retryPolicy = null,
        IReadOnlyCollection<PiSharpToolRegistration>? toolRegistrations = null)
    {
        var agent = new PiAgent(client, new CodingTools(Path.GetTempPath()), noBuiltinTools: true,
            extensionToolRegistrations: toolRegistrations, liveExtensionRegistration: registration,
            retryPolicy: ProviderRetryPolicy.None);
        return await ConversationRun.OpenAsync(agent,
            new ConversationSession(Path.GetTempPath(), "mutable-boundary", null),
            retryPolicy: retryPolicy ?? AgentRunRetryPolicy.None,
            retryDelay: (_, token) => { token.ThrowIfCancellationRequested(); return Task.CompletedTask; });
    }

    private static async Task<List<AgentLifecycleEvent>> ConsumeAsync(IAsyncEnumerable<AgentLifecycleEvent> stream)
    {
        var events = new List<AgentLifecycleEvent>();
        await foreach (var item in stream) events.Add(item);
        return events;
    }

    private static ChatResponseUpdate Reply(string text) => new(ChatRole.Assistant, text);

    private static ChatResponseUpdate ToolCall(string id, string name) => new(ChatRole.Assistant,
        [new FunctionCallContent(id, name, new Dictionary<string, object?>())]);

    private sealed class ScriptedClient(params Func<int, IReadOnlyList<ChatMessage>, IReadOnlyList<ChatResponseUpdate>>[] steps)
        : IChatClient
    {
        public List<ChatMessage[]> Requests { get; } = [];

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var request = messages.ToArray();
            Requests.Add(request);
            var index = Requests.Count - 1;
            if (index >= steps.Length) throw new InvalidOperationException($"Unexpected provider request {index + 1}.");
            foreach (var update in steps[index](index, request))
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return update;
            }
            await Task.CompletedTask;
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }
}
