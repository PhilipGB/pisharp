using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;
using PiSharp.Extensions.Abstractions;
using PiSharp.Runtime;
using PiSharp.Runtime.Extensions;
using PiSharp.Runtime.Sessions;
using PiSharp.Runtime.Tools;

namespace PiSharp.Tests;

public sealed class AgentSettlementCharacterizationTests
{
    [Fact]
    public async Task LegacyTurnEndObservesCanonicalAssistantAndToolResultEntries()
    {
        var workingDirectory = Path.Combine(Path.GetTempPath(), "pisharp-settlement-turn-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workingDirectory);
        try
        {
            var registration = new ExtensionRegistration();
            var adapter = new NativeExtensionRegistrationAdapter(registration);
            var conversation = new ConversationSession(workingDirectory, "fixture", null);
            var turnEnds = new List<PiSharpExtensionTurnEndEvent>();
            adapter.AddTurnEndHandler((eventData, context, _) =>
            {
                Assert.False(context.IsIdle);
                Assert.True(eventData.MessageEntryId is not null);
                Assert.Contains(conversation.Tree.ActivePath(), entry => entry.Id == eventData.MessageEntryId);
                Assert.Equal(eventData.ToolResultEntryIds.Count, eventData.ToolResults.Count);
                foreach (var entryId in eventData.ToolResultEntryIds)
                    Assert.Contains(conversation.Tree.ActivePath(), entry => entry.Id == entryId);
                turnEnds.Add(eventData);
                return ValueTask.CompletedTask;
            });
            var tool = AIFunctionFactory.Create(() => "tool result", name: "probe");
            registration.AddTool(new PiSharpToolRegistration(tool, ToolExposure.Direct));
            adapter.Close();

            var run = await ConversationRun.OpenAsync(new PiAgent(new ToolThenReplyClient(),
                new CodingTools(workingDirectory), noBuiltinTools: true,
                extensionToolRegistrations: registration.ToolDefinitions,
                liveExtensionRegistration: registration), conversation, save: _ => Task.CompletedTask);
            var lifecycle = new List<AgentLifecycleEvent>();
            await foreach (var item in run.RunEventsAsync("inspect turn boundary")) lifecycle.Add(item);

            Assert.Equal(2, turnEnds.Count);
            Assert.Single(turnEnds[0].ToolResults);
            Assert.Single(turnEnds[0].ToolResultEntryIds);
            Assert.Empty(turnEnds[1].ToolResults);
            Assert.Equal("agent_run_completed", lifecycle[^2].Type);
            Assert.Equal("agent_settled", lifecycle[^1].Type);
        }
        finally
        {
            Directory.Delete(workingDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task RetryKeepsPromptLoopOpenForAgentEndQueuedWorkBeforeAgentBecomesIdle()
    {
        var registration = new ExtensionRegistration();
        var adapter = new NativeExtensionRegistrationAdapter(registration);
        ConversationRun? run = null;
        var agentStarts = 0;
        var agentEnds = 0;
        var activeAtEnd = true;
        var idleAtEnd = true;
        var queuedAtEnd = true;
        adapter.AddAgentStartHandler((_, _, _) =>
        {
            agentStarts++;
            return ValueTask.CompletedTask;
        });
        adapter.AddAgentEndHandler((_, context, _) =>
        {
            agentEnds++;
            if (agentEnds > 1) return ValueTask.CompletedTask;
            activeAtEnd = run!.IsActive;
            idleAtEnd = context.IsIdle;
            queuedAtEnd = run.TryQueueExtensionUserMessage(new ChatMessage(ChatRole.User, "late"), "late");
            return ValueTask.CompletedTask;
        });
        adapter.Close();

        var agent = new PiAgent(new CapacityThenReplyClient(), new CodingTools(Path.GetTempPath()), noTools: true,
            retryPolicy: ProviderRetryPolicy.None,
            liveExtensionRegistration: registration);
        run = await ConversationRun.OpenAsync(agent, new ConversationSession(Path.GetTempPath(), "fixture", null),
            retryPolicy: new AgentRunRetryPolicy(enabled: true, maxRetries: 1,
                baseDelay: TimeSpan.Zero, maxDelay: TimeSpan.Zero));
        var events = new List<AgentLifecycleEvent>();
        await foreach (var item in run.RunEventsAsync("retry")) events.Add(item);

        Assert.Equal(1, agentStarts);
        Assert.Equal(2, agentEnds);
        Assert.True(activeAtEnd);
        Assert.False(idleAtEnd);
        Assert.True(queuedAtEnd);
        Assert.Single(events, item => item.Type == "auto_retry_start");
        Assert.Single(events, item => item.Type == "auto_retry_end");
        Assert.Single(events, item => item.Type == "agent_settled");
        Assert.True(events.FindIndex(item => item.Type == "auto_retry_end") <
            events.FindIndex(item => item.Type == "agent_settled"));
    }

    private sealed class ToolThenReplyClient : IChatClient
    {
        private int _requests;

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (Interlocked.Increment(ref _requests) == 1)
                yield return new ChatResponseUpdate(ChatRole.Assistant,
                    [new FunctionCallContent("probe-call", "probe", new Dictionary<string, object?>())]);
            else
                yield return new ChatResponseUpdate(ChatRole.Assistant, "done");
            await Task.CompletedTask;
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    private sealed class CapacityThenReplyClient : IChatClient
    {
        private int _requests;

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (Interlocked.Increment(ref _requests) == 1)
                throw new IOException("Selected model is at capacity");
            yield return new ChatResponseUpdate(ChatRole.Assistant, "done");
            await Task.CompletedTask;
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }
}
