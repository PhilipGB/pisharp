using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Extensions.AI;
using PiSharp.Extensions.Abstractions;
using PiSharp.Runtime;
using PiSharp.Runtime.Extensions;
using PiSharp.Runtime.Sessions;
using PiSharp.Runtime.Tools;

namespace PiSharp.Tests;

public sealed class BoundaryCompactionAndRecoveryTests
{
    [Fact]
    public async Task LargeTurnBoundaryDraftIsSummarizedBeforeContinuationAndBeforeSettle()
    {
        var registration = new ExtensionRegistration();
        var adapter = new NativeExtensionRegistrationAdapter(registration);
        var client = new ScriptedClient([Reply("initial")], [Reply("continued")]);
        var boundaryCalls = 0;
        var beforeSettleSawCompaction = false;
        var agentStarts = 0;
        var lifecycle = new List<string>();
        PiSharpExtensionSessionBeforeCompactEvent? beforeCompact = null;
        PiSharpExtensionSessionCompactEvent? compacted = null;
        adapter.AddTurnEndBoundaryHandler((_, _, _) =>
        {
            if (Interlocked.Increment(ref boundaryCalls) != 1)
                return ValueTask.FromResult<PiSharpBoundaryResult?>(new PiSharpBoundaryResult());
            return ValueTask.FromResult<PiSharpBoundaryResult?>(new PiSharpBoundaryResult([
                new PiSharpCustomMessageEntryDraft("large-boundary", new string('x', 5000), display: false)
            ], @continue: true));
        });
        adapter.AddSessionBeforeCompactHandler((eventData, _, _) =>
        {
            beforeCompact = eventData;
            return ValueTask.FromResult<PiSharpExtensionSessionBeforeCompactResult?>(new(Compaction:
                new PiSharpExtensionCustomCompaction("Boundary checkpoint", eventData.Preparation.FirstKeptEntryId, 12000,
                    JsonSerializer.SerializeToElement(new { source = "boundary" }))));
        });
        adapter.AddSessionCompactHandler((eventData, _, _) =>
        {
            compacted = eventData;
            return ValueTask.CompletedTask;
        });
        adapter.AddAgentBeforeSettleHandler((_, _, _) =>
        {
            beforeSettleSawCompaction = compacted is not null;
            return ValueTask.FromResult<PiSharpBoundaryResult?>(new PiSharpBoundaryResult());
        });
        adapter.AddAgentStartHandler((_, _, _) =>
        {
            agentStarts++;
            return ValueTask.CompletedTask;
        });
        adapter.Close();

        var agent = new PiAgent(client, new CodingTools(Path.GetTempPath()), noBuiltinTools: true,
            liveExtensionRegistration: registration);
        var conversation = new ConversationSession(Path.GetTempPath(), "boundary-compaction", null);
        conversation.Append(new ChatMessage(ChatRole.User, new string('o', 12000)));
        conversation.Append(new ChatMessage(ChatRole.Assistant, "older response"));
        var run = await ConversationRun.OpenAsync(agent,
            conversation, autoCompaction: new AutoCompactionPolicy(10000, ReserveTokens: 6000, KeepRecentTokens: 1));

        var runEvents = new List<AgentLifecycleEvent>();
        await foreach (var item in run.RunEventsAsync("small prompt"))
        {
            runEvents.Add(item);
            if (item.Type is "compaction_start" or "compaction_end") lifecycle.Add(item.Type);
        }

        Assert.DoesNotContain(runEvents, item => item.Type == "extension_error");
        Assert.True(client.Requests.Count == 2, $"Requests={client.Requests.Count}; events={string.Join(",", runEvents.Select(item => item.Type + (item.Error is null ? "" : ":" + item.Error)))}; roles={string.Join(",", run.Conversation.ContextMessages().Select(message => message.Role))}; path={string.Join(",", run.Conversation.Tree.ActivePath().Select(entry => entry.Type))}");
        Assert.Contains(client.Requests[1], message => message.Text.Contains("Boundary checkpoint", StringComparison.Ordinal));
        Assert.Equal(1, agentStarts);
        Assert.Equal(client.Requests[0].Where(message => message.Role == ChatRole.System).Select(message => message.Text),
            client.Requests[1].Where(message => message.Role == ChatRole.System).Select(message => message.Text));
        Assert.NotNull(beforeCompact);
        Assert.Equal(PiSharpExtensionCompactionReason.Threshold, beforeCompact.Reason);
        Assert.NotNull(compacted);
        Assert.True(compacted.FromExtension);
        Assert.Equal("Boundary checkpoint", compacted.Result.Summary);
        Assert.True(beforeSettleSawCompaction);
        Assert.Equal(["compaction_start", "compaction_end"], lifecycle);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AutomaticCompactionAfterFinalAssistantTurnPrecedesSettlementObservers(bool registerBeforeSettle)
    {
        var registration = new ExtensionRegistration();
        var adapter = new NativeExtensionRegistrationAdapter(registration);
        var registrationConversation = new ConversationSession(Path.GetTempPath(), "final-turn-compaction", null);
        var client = new ScriptedClient([Reply(new string('r', 50_000))]);
        var observerOrder = new List<string>();
        var compacted = false;
        var beforeSettleSawCompaction = false;
        var settledSawCompaction = false;
        PiSharpExtensionSessionBeforeCompactEvent? beforeCompact = null;
        PiSharpExtensionSessionCompactEvent? compactEvent = null;
        adapter.AddSessionBeforeCompactHandler((eventData, _, _) =>
        {
            beforeCompact = eventData;
            return ValueTask.FromResult<PiSharpExtensionSessionBeforeCompactResult?>(new(Compaction:
                new PiSharpExtensionCustomCompaction("Pre-settlement checkpoint",
                    eventData.Preparation.FirstKeptEntryId, eventData.Preparation.TokensBefore)));
        });
        adapter.AddSessionCompactHandler((eventData, _, _) =>
        {
            compactEvent = eventData;
            compacted = true;
            observerOrder.Add("compaction");
            return ValueTask.CompletedTask;
        });
        if (registerBeforeSettle)
        {
            adapter.AddAgentBeforeSettleHandler((_, _, _) =>
            {
                beforeSettleSawCompaction = compacted &&
                    registrationConversation.Tree.ActivePath().Any(entry => entry.Type == "compaction");
                observerOrder.Add("agent_before_settle");
                return ValueTask.FromResult<PiSharpBoundaryResult?>(new PiSharpBoundaryResult());
            });
        }
        adapter.AddAgentSettledHandler((_, _, _) =>
        {
            settledSawCompaction = compacted &&
                registrationConversation.Tree.ActivePath().Any(entry => entry.Type == "compaction");
            observerOrder.Add("agent_settled");
            return ValueTask.CompletedTask;
        });
        adapter.Close();

        registrationConversation.Append(new ChatMessage(ChatRole.User, "older user context"));
        registrationConversation.Append(new ChatMessage(ChatRole.Assistant, "older assistant context"));
        var agent = new PiAgent(client, new CodingTools(Path.GetTempPath()), noBuiltinTools: true,
            liveExtensionRegistration: registration);
        var run = await ConversationRun.OpenAsync(agent, registrationConversation,
            autoCompaction: new AutoCompactionPolicy(20_000, ReserveTokens: 0, KeepRecentTokens: 1));

        var events = await ConsumeAsync(run.RunEventsAsync("short final turn"));

        Assert.Single(client.Requests);
        Assert.DoesNotContain(events, item => item.Type == "extension_error");
        Assert.NotNull(beforeCompact);
        Assert.Equal(PiSharpExtensionCompactionReason.Threshold, beforeCompact.Reason);
        Assert.NotNull(compactEvent);
        Assert.True(compactEvent.FromExtension);
        Assert.Equal("Pre-settlement checkpoint", compactEvent.Result.Summary);
        Assert.True(compacted);
        Assert.True(settledSawCompaction);
        Assert.Equal(registerBeforeSettle, beforeSettleSawCompaction);
        Assert.Equal(registerBeforeSettle
            ? new[] { "compaction", "agent_before_settle", "agent_settled" }
            : new[] { "compaction", "agent_settled" }, observerOrder);
        Assert.Single(registrationConversation.Tree.ActivePath(), entry => entry.Type == "compaction");
    }

    [Fact]
    public async Task FollowUpQueuedByCompletedCompactionRunsBeforePreSettlement()
    {
        var registration = new ExtensionRegistration();
        var adapter = new NativeExtensionRegistrationAdapter(registration);
        var conversation = new ConversationSession(Path.GetTempPath(), "compaction-queued-follow-up", null);
        var client = new ScriptedClient([Reply(new string('r', 10_000))], [Reply("follow-up response")]);
        ConversationRun? run = null;
        var compactionCalls = 0;
        var queued = false;
        var beforeSettleCalls = 0;
        var providerRequestsAtBeforeSettle = 0;
        var settledCalls = 0;
        adapter.AddSessionBeforeCompactHandler((eventData, _, _) =>
            ValueTask.FromResult<PiSharpExtensionSessionBeforeCompactResult?>(new(Compaction:
                new PiSharpExtensionCustomCompaction("Queued-work checkpoint",
                    eventData.Preparation.FirstKeptEntryId, eventData.Preparation.TokensBefore))));
        adapter.AddSessionCompactHandler((_, _, _) =>
        {
            compactionCalls++;
            if (compactionCalls == 1) queued = run!.TryFollowUp("queued by compaction callback");
            return ValueTask.CompletedTask;
        });
        adapter.AddAgentBeforeSettleHandler((_, _, _) =>
        {
            beforeSettleCalls++;
            providerRequestsAtBeforeSettle = client.Requests.Count;
            return ValueTask.FromResult<PiSharpBoundaryResult?>(new PiSharpBoundaryResult());
        });
        adapter.AddAgentSettledHandler((_, _, _) =>
        {
            settledCalls++;
            return ValueTask.CompletedTask;
        });
        adapter.Close();

        conversation.Append(new ChatMessage(ChatRole.User, new string('o', 32_000)));
        conversation.Append(new ChatMessage(ChatRole.Assistant, "older assistant context"));
        Assert.InRange(AutoCompactionPolicy.Estimate(conversation.ContextMessages(), "short final turn"), 0, 19_999);
        var agent = new PiAgent(client, new CodingTools(Path.GetTempPath()), noBuiltinTools: true,
            liveExtensionRegistration: registration);
        run = await ConversationRun.OpenAsync(agent, conversation,
            autoCompaction: new AutoCompactionPolicy(20_000, ReserveTokens: 0, KeepRecentTokens: 1));

        var events = await ConsumeAsync(run.RunEventsAsync("short final turn"));

        Assert.DoesNotContain(events, item => item.Type == "extension_error");
        Assert.True(queued);
        Assert.Equal(1, compactionCalls);
        Assert.Equal(2, client.Requests.Count);
        Assert.Single(client.Requests[1], message => message.Text == "queued by compaction callback");
        Assert.Equal(1, beforeSettleCalls);
        Assert.Equal(2, providerRequestsAtBeforeSettle);
        Assert.Equal(1, settledCalls);
        Assert.Single(conversation.Tree.ActivePath(), entry => entry.Type == "compaction");
        Assert.Single(conversation.Tree.ActivePath(), entry => entry.Type == "chat" &&
            ConversationMessageCodec.RestoreEntry(entry).Text == "queued by compaction callback");
    }

    [Fact]
    public async Task SelfRetainingBoundaryCompactionSurvivesNativeAndPiJsonlResume()
    {
        var registration = new ExtensionRegistration();
        var adapter = new NativeExtensionRegistrationAdapter(registration);
        var client = new ScriptedClient([Reply("initial")], [Reply("continued")]);
        var boundaryCalls = 0;
        adapter.AddTurnEndBoundaryHandler((_, _, _) =>
        {
            if (Interlocked.Increment(ref boundaryCalls) != 1)
                return ValueTask.FromResult<PiSharpBoundaryResult?>(new PiSharpBoundaryResult());
            return ValueTask.FromResult<PiSharpBoundaryResult?>(new PiSharpBoundaryResult([
                new PiSharpCompactionEntryDraft("Self-retaining boundary checkpoint", firstKeptEntryId: null,
                    details: JsonSerializer.SerializeToElement(new { boundary = true }))
            ], @continue: true));
        });
        adapter.Close();
        var conversation = new ConversationSession(Path.GetTempPath(), "self-retaining-boundary", null);
        var agent = new PiAgent(client, new CodingTools(Path.GetTempPath()), noBuiltinTools: true,
            liveExtensionRegistration: registration);
        var run = await ConversationRun.OpenAsync(agent, conversation);

        await foreach (var _ in run.RunEventsAsync("self retaining")) { }

        var compaction = Assert.Single(conversation.Tree.ActivePath(), entry => entry.Type == "compaction");
        Assert.Equal(JsonValueKind.Null, compaction.Payload.GetProperty("firstKeptEntryId").ValueKind);
        Assert.Equal(2, client.Requests.Count);
        Assert.Contains(client.Requests[1], message => message.Text.Contains("Self-retaining boundary checkpoint", StringComparison.Ordinal));

        var native = ConversationSession.Parse(conversation.ToJson());
        Assert.Single(native.Tree.ActivePath(), entry => entry.Type == "compaction");
        var imported = PiJsonlSessionInterchange.Import(PiJsonlSessionInterchange.Export(conversation));
        Assert.Single(imported.Tree.ActivePath(), entry => entry.Type == "compaction");
    }

    [Fact]
    public async Task BoundarySaveFailureRollsBackAndLaterPromptCanRun()
    {
        var registration = new ExtensionRegistration();
        var adapter = new NativeExtensionRegistrationAdapter(registration);
        var client = new ScriptedClient([Reply("first")], [Reply("second")]);
        var boundaryCalls = 0;
        adapter.AddTurnEndBoundaryHandler((_, _, _) =>
        {
            if (Interlocked.Increment(ref boundaryCalls) != 1)
                return ValueTask.FromResult<PiSharpBoundaryResult?>(new PiSharpBoundaryResult());
            return ValueTask.FromResult<PiSharpBoundaryResult?>(new PiSharpBoundaryResult([
                new PiSharpCustomMessageEntryDraft("save-failure", "must roll back", display: false)
            ], @continue: true));
        });
        adapter.Close();
        var failBoundarySave = true;
        var conversation = new ConversationSession(Path.GetTempPath(), "boundary-save-failure", null);
        var agent = new PiAgent(client, new CodingTools(Path.GetTempPath()), noBuiltinTools: true,
            liveExtensionRegistration: registration);
        var run = await ConversationRun.OpenAsync(agent, conversation, save: _ =>
        {
            if (failBoundarySave && conversation.Tree.Entries.Any(entry => entry.Type == "custom_message"))
            {
                failBoundarySave = false;
                throw new IOException("selected boundary save failure");
            }
            return Task.CompletedTask;
        });

        var events = new List<AgentLifecycleEvent>();
        await foreach (var item in run.RunEventsAsync("first prompt")) events.Add(item);
        var failedHead = conversation.Tree.HeadId;
        Assert.DoesNotContain(conversation.Tree.Entries, entry => entry.Type == "custom_message");
        Assert.Contains(events, item => item.Type == "extension_error" && item.Error?.Contains("selected boundary save failure") == true);
        Assert.Single(client.Requests);

        await foreach (var _ in run.RunEventsAsync("later prompt")) { }

        Assert.NotEqual(failedHead, conversation.Tree.HeadId);
        Assert.Equal(2, client.Requests.Count);
        Assert.DoesNotContain(conversation.Tree.Entries, entry => entry.Type == "custom_message");
    }

    [Fact]
    public async Task CancellationDuringBoundarySaveRollsBackAndSettlesAborted()
    {
        var registration = new ExtensionRegistration();
        var adapter = new NativeExtensionRegistrationAdapter(registration);
        var client = new ScriptedClient([Reply("first")]);
        var saveEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var settled = new List<bool>();
        adapter.AddTurnEndBoundaryHandler((_, _, _) =>
            ValueTask.FromResult<PiSharpBoundaryResult?>(new PiSharpBoundaryResult([
                new PiSharpCustomMessageEntryDraft("cancel-save", "cancel this commit", display: false)
            ], @continue: true)));
        adapter.AddAgentSettledHandler((eventData, _, _) =>
        {
            settled.Add(eventData.Aborted);
            return ValueTask.CompletedTask;
        });
        adapter.Close();

        var conversation = new ConversationSession(Path.GetTempPath(), "boundary-save-cancel", null);
        var agent = new PiAgent(client, new CodingTools(Path.GetTempPath()), noBuiltinTools: true,
            liveExtensionRegistration: registration);
        var run = await ConversationRun.OpenAsync(agent, conversation, save: async cancellationToken =>
        {
            if (conversation.Tree.Entries.Any(entry => entry.Type == "custom_message"))
            {
                saveEntered.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }
        });
        using var cancellation = new CancellationTokenSource();
        var running = ConsumeAsync(run.RunEventsAsync("cancel during save", cancellation.Token));

        await saveEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();
        var events = await running.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.DoesNotContain(conversation.Tree.Entries, entry => entry.Type == "custom_message");
        Assert.Single(client.Requests);
        Assert.Contains(events, item => item.Type == "agent_settled");
        Assert.Equal([true], settled);
    }

    [Fact]
    public async Task ContextEditsOmittingCompleteToolGraphStayOmittedOnContinuation()
    {
        var registration = new ExtensionRegistration();
        var adapter = new NativeExtensionRegistrationAdapter(registration);
        var tool = AIFunctionFactory.Create(() => "tool result", name: "probe");
        var toolRegistration = new PiSharpToolRegistration(tool, ToolExposure.Direct);
        registration.AddTool(toolRegistration);
        var client = new ScriptedClient([ToolCall("call-1", "probe")], [Reply("continued")]);
        var boundaryCalls = 0;
        adapter.AddTurnEndBoundaryHandler((eventData, _, _) =>
        {
            if (Interlocked.Increment(ref boundaryCalls) != 1)
                return ValueTask.FromResult<PiSharpBoundaryResult?>(new PiSharpBoundaryResult());
            var edits = new List<PiSharpSessionBoundaryDraft>
            {
                new PiSharpContextEditEntryDraft(Assert.IsType<string>(eventData.Turn.MessageEntryId), null)
            };
            edits.AddRange(eventData.Turn.ToolResultEntryIds.Select(id =>
                new PiSharpContextEditEntryDraft(id, null)));
            return ValueTask.FromResult<PiSharpBoundaryResult?>(new PiSharpBoundaryResult(edits, @continue: true));
        });
        adapter.Close();
        var agent = new PiAgent(client, new CodingTools(Path.GetTempPath()), noBuiltinTools: true,
            extensionToolRegistrations: [toolRegistration], liveExtensionRegistration: registration);
        var conversation = new ConversationSession(Path.GetTempPath(), "edited-tool-graph", null);
        var run = await ConversationRun.OpenAsync(agent, conversation);

        await foreach (var _ in run.RunEventsAsync("call tool")) { }

        Assert.Equal(2, client.Requests.Count);
        Assert.DoesNotContain(client.Requests[1].SelectMany(message => message.Contents),
            content => content is FunctionCallContent or FunctionResultContent);
        Assert.Contains(conversation.Tree.ActivePath(), entry => entry.Type == "context_edit");
        Assert.DoesNotContain(conversation.ContextMessages().SelectMany(message => message.Contents),
            content => content is FunctionCallContent or FunctionResultContent);
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

    private sealed class ScriptedClient(params ChatResponseUpdate[][] responses) : IChatClient
    {
        public List<ChatMessage[]> Requests { get; } = [];

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Requests.Add(messages.ToArray());
            var index = Requests.Count - 1;
            if (index >= responses.Length) throw new InvalidOperationException($"Unexpected provider request {index + 1}.");
            foreach (var update in responses[index])
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
