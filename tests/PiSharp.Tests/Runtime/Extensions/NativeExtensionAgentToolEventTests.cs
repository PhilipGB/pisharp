using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;
using PiSharp.Extensions.Abstractions;
using PiSharp.Runtime;
using PiSharp.Runtime.Extensions;
using PiSharp.Runtime.Sessions;
using PiSharp.Runtime.Tools;

namespace PiSharp.Tests;

public sealed class NativeExtensionAgentToolEventTests
{
    [Fact]
    public async Task RunAndTurnEventsBracketThePromptPumpAndToolEventsKeepEffectiveNestedState()
    {
        var workingDirectory = Path.Combine(Path.GetTempPath(), "pisharp-native-agent-events-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workingDirectory);
        try
        {
            var registration = new ExtensionRegistration();
            var adapter = new NativeExtensionRegistrationAdapter(registration);
            var order = new List<string>();
            var starts = new List<PiSharpExtensionToolExecutionStartEvent>();
            var updates = new List<PiSharpExtensionToolExecutionUpdateEvent>();
            var ends = new List<PiSharpExtensionToolExecutionEndEvent>();
            var turnStarts = new List<PiSharpExtensionTurnStartEvent>();
            var turnEnds = new List<PiSharpExtensionTurnEndEvent>();
            PiSharpExtensionAgentEndEvent? agentEnd = null;

            adapter.AddAgentStartHandler((_, context, _) =>
            {
                Assert.False(context.IsIdle);
                order.Add("agent_start");
                return ValueTask.CompletedTask;
            });
            adapter.AddAgentEndHandler((eventData, context, _) =>
            {
                Assert.False(context.IsIdle);
                agentEnd = eventData;
                order.Add("agent_end");
                return ValueTask.CompletedTask;
            });
            adapter.AddTurnStartHandler((eventData, _, _) =>
            {
                turnStarts.Add(eventData);
                order.Add($"turn_start:{eventData.TurnIndex}");
                return ValueTask.CompletedTask;
            });
            adapter.AddTurnEndHandler((eventData, _, _) =>
            {
                turnEnds.Add(eventData);
                order.Add($"turn_end:{eventData.TurnIndex}");
                return ValueTask.CompletedTask;
            });
            adapter.AddToolExecutionStartHandler((eventData, _, _) =>
            {
                starts.Add(eventData);
                order.Add($"tool_start:{eventData.ToolName}");
                return ValueTask.CompletedTask;
            });
            adapter.AddToolExecutionUpdateHandler((eventData, _, _) =>
            {
                updates.Add(eventData);
                order.Add($"tool_update:{eventData.ToolName}");
                return ValueTask.CompletedTask;
            });
            adapter.AddToolExecutionEndHandler((eventData, _, _) =>
            {
                ends.Add(eventData);
                order.Add($"tool_end:{eventData.ToolName}");
                return ValueTask.CompletedTask;
            });
            adapter.AddToolCallHook((context, _) =>
            {
                if (context.ToolName == "child") context.Arguments["value"] = "effective";
                return ValueTask.FromResult(PiSharp.Extensions.Abstractions.PiSharpToolCallDecision.Allow);
            });
            adapter.AddToolResultHook((context, _) =>
            {
                if (context.ToolName == "child") context.Result = new PiSharpToolResult("finalized");
                return ValueTask.CompletedTask;
            });

            var child = AIFunctionFactory.Create(async (string value, AIFunctionArguments arguments,
                CancellationToken cancellationToken) =>
            {
                var toolContext = PiSharpToolExecutionContext.Get(arguments)!;
                Assert.Equal("effective", value);
                toolContext.ReportProgress("halfway");
                await Task.Yield();
                cancellationToken.ThrowIfCancellationRequested();
                return new PiSharpToolResult("raw");
            }, name: "child");
            var parent = AIFunctionFactory.Create(async (AIFunctionArguments arguments,
                CancellationToken cancellationToken) =>
            {
                var nested = await PiSharpToolExecutionContext.Get(arguments)!.ExecuteToolAsync("child",
                    new Dictionary<string, object?> { ["value"] = "original" }, cancellationToken);
                Assert.False(nested.IsError);
                Assert.Equal("finalized", nested.Text);
                return "parent complete";
            }, name: "parent");
            registration.AddTool(new PiSharpToolRegistration(parent, ToolExposure.ModelOnly));
            registration.AddTool(new PiSharpToolRegistration(child, ToolExposure.Deferred));
            adapter.Close();

            var agent = new PiAgent(new EventClient(), new CodingTools(workingDirectory), noBuiltinTools: true,
                extensionToolCallHooks: registration.ToolCallHooks,
                extensionToolResultHooks: registration.ToolResultHooks,
                extensionToolRegistrations: registration.ToolDefinitions,
                liveExtensionRegistration: registration);
            var conversation = new ConversationSession(workingDirectory, "fixture", null);
            var run = await ConversationRun.OpenAsync(agent, conversation, save: _ => Task.CompletedTask);
            var runEvents = new List<AgentLifecycleEvent>();
            await foreach (var item in run.RunEventsAsync("run events")) runEvents.Add(item);

            Assert.Equal(["agent_start", "turn_start:0", "tool_start:parent", "tool_start:child",
                "tool_update:child", "tool_end:child", "tool_end:parent", "turn_end:0",
                "turn_start:1", "turn_end:1", "agent_end"], order);
            Assert.Equal(2, turnStarts.Count);
            Assert.Equal([0, 1], turnStarts.Select(item => item.TurnIndex));
            Assert.Equal([0, 1], turnEnds.Select(item => item.TurnIndex));
            Assert.All(turnStarts, item => Assert.NotEqual(default, item.Timestamp));

            var nestedStart = Assert.Single(starts, item => item.ToolName == "child");
            Assert.Equal("root-call/1", nestedStart.ToolCallId);
            Assert.Equal("root-call", nestedStart.ParentToolCallId);
            Assert.Equal("effective", nestedStart.Arguments.GetProperty("value").GetString());
            var nestedUpdate = Assert.Single(updates);
            Assert.Equal(nestedStart.ToolCallId, nestedUpdate.ToolCallId);
            Assert.Equal(nestedStart.ParentToolCallId, nestedUpdate.ParentToolCallId);
            Assert.Equal("effective", nestedUpdate.Arguments.GetProperty("value").GetString());
            Assert.Equal("halfway", nestedUpdate.PartialResult.GetString());
            var nestedEnd = Assert.Single(ends, item => item.ToolName == "child");
            Assert.Equal(nestedStart.ToolCallId, nestedEnd.ToolCallId);
            Assert.Equal(nestedStart.ParentToolCallId, nestedEnd.ParentToolCallId);
            Assert.Equal("finalized", nestedEnd.Result.GetProperty("content").GetString());
            Assert.False(nestedEnd.IsError);
            Assert.NotNull(nestedEnd.DurationMs);

            var firstTurn = turnEnds[0];
            Assert.NotNull(firstTurn.Message);
            Assert.Single(firstTurn.ToolResults);
            Assert.True(firstTurn.MessageEntryId is not null,
                $"turn message={firstTurn.Message}; active entries={string.Join(" | ", conversation.Tree.ActivePath().Select(entry => $"{entry.Id}:{entry.Type}:{entry.Payload}"))}");
            Assert.Single(firstTurn.ToolResultEntryIds);
            Assert.Contains(conversation.Tree.ActivePath(), entry => entry.Id == firstTurn.MessageEntryId);
            Assert.Contains(conversation.Tree.ActivePath(), entry => entry.Id == firstTurn.ToolResultEntryIds[0]);
            Assert.NotNull(agentEnd);
            Assert.Equal(PiSharpExtensionAgentOutcome.Completed, agentEnd.Outcome);
            Assert.Contains(agentEnd.Messages, message => message.Role == ChatRole.User && message.Text == "run events");
            Assert.Contains(runEvents, item => item.Type == "agent_run_completed");
        }
        finally
        {
            Directory.Delete(workingDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task OneAgentBracketCoversQueuedPromptBatches()
    {
        var workingDirectory = Path.Combine(Path.GetTempPath(), "pisharp-native-agent-queue-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workingDirectory);
        try
        {
            var registration = new ExtensionRegistration();
            var adapter = new NativeExtensionRegistrationAdapter(registration);
            var starts = 0;
            var ends = 0;
            adapter.AddAgentStartHandler((_, _, _) => { starts++; return ValueTask.CompletedTask; });
            adapter.AddAgentEndHandler((_, _, _) => { ends++; return ValueTask.CompletedTask; });
            adapter.Close();
            var agent = new PiAgent(new EventClient(), new CodingTools(workingDirectory), noBuiltinTools: true,
                liveExtensionRegistration: registration);
            var run = await ConversationRun.OpenAsync(agent,
                new ConversationSession(workingDirectory, "fixture", null), save: _ => Task.CompletedTask);
            run.QueueRpcInput("queued follow up", steering: false, _ => { });

            await foreach (var _ in run.RunEventsAsync("first prompt")) { }

            Assert.Equal(1, starts);
            Assert.Equal(1, ends);
        }
        finally
        {
            Directory.Delete(workingDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task AgentObserverFailureIsReportedAndLaterObserversStillRun()
    {
        var workingDirectory = Path.Combine(Path.GetTempPath(), "pisharp-native-agent-observer-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workingDirectory);
        try
        {
            var registration = new ExtensionRegistration();
            var adapter = new NativeExtensionRegistrationAdapter(registration);
            var laterObserverCalls = 0;
            adapter.AddAgentStartHandler((_, _, _) =>
                ValueTask.FromException(new InvalidOperationException("observer failed")));
            adapter.AddAgentStartHandler((_, _, _) =>
            {
                laterObserverCalls++;
                return ValueTask.CompletedTask;
            });
            adapter.Close();
            var agent = new PiAgent(new EventClient(), new CodingTools(workingDirectory), noBuiltinTools: true,
                liveExtensionRegistration: registration);
            var run = await ConversationRun.OpenAsync(agent,
                new ConversationSession(workingDirectory, "fixture", null), save: _ => Task.CompletedTask);
            var events = new List<AgentLifecycleEvent>();

            await foreach (var item in run.RunEventsAsync("observer failure")) events.Add(item);

            Assert.Equal(1, laterObserverCalls);
            Assert.True(events.Any(item => item.Type == "extension_error" && item.Error == "observer failed"),
                $"events={string.Join(", ", events.Select(item => $"{item.Type}:{item.Error}"))}");
            Assert.Contains(events, item => item.Type == "agent_run_completed");
        }
        finally
        {
            Directory.Delete(workingDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task AgentEndReportsErrorAfterProviderFailure()
    {
        var workingDirectory = Path.Combine(Path.GetTempPath(), "pisharp-native-agent-error-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workingDirectory);
        try
        {
            var registration = new ExtensionRegistration();
            var adapter = new NativeExtensionRegistrationAdapter(registration);
            PiSharpExtensionAgentEndEvent? agentEnd = null;
            adapter.AddAgentEndHandler((eventData, _, _) =>
            {
                agentEnd = eventData;
                return ValueTask.CompletedTask;
            });
            adapter.Close();
            var agent = new PiAgent(new FailingClient(), new CodingTools(workingDirectory), noBuiltinTools: true,
                liveExtensionRegistration: registration);
            var run = await ConversationRun.OpenAsync(agent,
                new ConversationSession(workingDirectory, "fixture", null), save: _ => Task.CompletedTask,
                retryPolicy: AgentRunRetryPolicy.None);
            var events = new List<AgentLifecycleEvent>();

            await foreach (var item in run.RunEventsAsync("provider failure")) events.Add(item);

            Assert.NotNull(agentEnd);
            Assert.Equal(PiSharpExtensionAgentOutcome.Error, agentEnd.Outcome);
            Assert.Contains("provider failure", agentEnd.ErrorMessage);
            Assert.Contains(events, item => item.Type == "turn_failed");
        }
        finally
        {
            Directory.Delete(workingDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task HostCancellationDuringProviderStreamSettlesAndSuppressesAgentEndObservers()
    {
        var workingDirectory = Path.Combine(Path.GetTempPath(), "pisharp-native-agent-abort-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workingDirectory);
        var providerStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            var registration = new ExtensionRegistration();
            var adapter = new NativeExtensionRegistrationAdapter(registration);
            PiSharpExtensionAgentEndEvent? agentEnd = null;
            adapter.AddAgentEndHandler((eventData, _, _) =>
            {
                agentEnd = eventData;
                return ValueTask.CompletedTask;
            });
            adapter.Close();
            var agent = new PiAgent(new WaitingClient(providerStarted), new CodingTools(workingDirectory),
                noBuiltinTools: true, liveExtensionRegistration: registration);
            var run = await ConversationRun.OpenAsync(agent,
                new ConversationSession(workingDirectory, "fixture", null), save: _ => Task.CompletedTask,
                retryPolicy: AgentRunRetryPolicy.None);
            using var cancellation = new CancellationTokenSource();
            var events = new List<AgentLifecycleEvent>();
            var runTask = Task.Run(async () =>
            {
                await foreach (var item in run.RunEventsAsync("abort provider request", cancellation.Token))
                    events.Add(item);
            });

            await providerStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            cancellation.Cancel();
            await runTask.WaitAsync(TimeSpan.FromSeconds(5));

            // Pi emits agent_end for an aborted stream, but PiSharp host cancellation owns the
            // callback token and can suppress terminal observers to keep cancellation prompt.
            Assert.Null(agentEnd);
            Assert.Contains(events, item => item.Type == "turn_interrupted");
            Assert.Contains(events, item => item.Type == "agent_settled");
        }
        finally
        {
            Directory.Delete(workingDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task HostCancellationReleasesRunWaitingOnAgentEndObserver()
    {
        var workingDirectory = Path.Combine(Path.GetTempPath(), "pisharp-native-agent-cancel-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workingDirectory);
        var handlerStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseHandler = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            var registration = new ExtensionRegistration();
            var adapter = new NativeExtensionRegistrationAdapter(registration);
            adapter.AddAgentEndHandler((_, _, _) =>
            {
                handlerStarted.TrySetResult();
                return new ValueTask(releaseHandler.Task);
            });
            adapter.Close();
            var agent = new PiAgent(new EventClient(), new CodingTools(workingDirectory), noBuiltinTools: true,
                liveExtensionRegistration: registration);
            var run = await ConversationRun.OpenAsync(agent,
                new ConversationSession(workingDirectory, "fixture", null), save: _ => Task.CompletedTask);
            using var cancellation = new CancellationTokenSource();
            var events = new List<AgentLifecycleEvent>();
            var runTask = Task.Run(async () =>
            {
                await foreach (var item in run.RunEventsAsync("cancel terminal observer", cancellation.Token))
                    events.Add(item);
            });

            await handlerStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            cancellation.Cancel();
            await runTask.WaitAsync(TimeSpan.FromSeconds(5));
            releaseHandler.TrySetResult();

            Assert.Contains(events, item => item.Type == "agent_settled");
        }
        finally
        {
            releaseHandler.TrySetResult();
            Directory.Delete(workingDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task ToolBlockedByCallHookDoesNotEmitExecutionStartOrEnd()
    {
        var workingDirectory = Path.Combine(Path.GetTempPath(), "pisharp-native-tool-block-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workingDirectory);
        try
        {
            var registration = new ExtensionRegistration();
            var adapter = new NativeExtensionRegistrationAdapter(registration);
            var starts = 0;
            var ends = 0;
            adapter.AddToolCallHook((_, _) =>
                ValueTask.FromException<PiSharp.Extensions.Abstractions.PiSharpToolCallDecision>(
                    new InvalidOperationException("blocked")));
            adapter.AddToolExecutionStartHandler((_, _, _) =>
            {
                starts++;
                return ValueTask.CompletedTask;
            });
            adapter.AddToolExecutionEndHandler((_, _, _) =>
            {
                ends++;
                return ValueTask.CompletedTask;
            });
            registration.AddTool(new PiSharpToolRegistration(
                AIFunctionFactory.Create(() => "should not execute", name: "blocked"), ToolExposure.ModelOnly));
            adapter.Close();
            var agent = new PiAgent(new EventClient("blocked"), new CodingTools(workingDirectory), noBuiltinTools: true,
                extensionToolCallHooks: registration.ToolCallHooks,
                extensionToolRegistrations: registration.ToolDefinitions,
                liveExtensionRegistration: registration);
            var run = await ConversationRun.OpenAsync(agent,
                new ConversationSession(workingDirectory, "fixture", null), save: _ => Task.CompletedTask);

            await foreach (var _ in run.RunEventsAsync("blocked tool")) { }

            Assert.Equal(0, starts);
            Assert.Equal(0, ends);
        }
        finally
        {
            Directory.Delete(workingDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task ToolFailureAfterExecutionStartsEmitsErrorEndWithDuration()
    {
        var workingDirectory = Path.Combine(Path.GetTempPath(), "pisharp-native-tool-failure-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workingDirectory);
        try
        {
            var registration = new ExtensionRegistration();
            var adapter = new NativeExtensionRegistrationAdapter(registration);
            var starts = new List<PiSharpExtensionToolExecutionStartEvent>();
            var ends = new List<PiSharpExtensionToolExecutionEndEvent>();
            adapter.AddToolExecutionStartHandler((eventData, _, _) =>
            {
                starts.Add(eventData);
                return ValueTask.CompletedTask;
            });
            adapter.AddToolExecutionEndHandler((eventData, _, _) =>
            {
                ends.Add(eventData);
                return ValueTask.CompletedTask;
            });
            Func<string> failingTool = () => throw new InvalidOperationException("execution failed");
            registration.AddTool(new PiSharpToolRegistration(
                AIFunctionFactory.Create(failingTool, name: "failure"), ToolExposure.ModelOnly));
            adapter.Close();
            var agent = new PiAgent(new EventClient("failure"), new CodingTools(workingDirectory), noBuiltinTools: true,
                extensionToolRegistrations: registration.ToolDefinitions,
                liveExtensionRegistration: registration);
            var run = await ConversationRun.OpenAsync(agent,
                new ConversationSession(workingDirectory, "fixture", null), save: _ => Task.CompletedTask);

            await foreach (var _ in run.RunEventsAsync("failing tool")) { }

            var start = Assert.Single(starts);
            var end = Assert.Single(ends);
            Assert.Equal(start.ToolCallId, end.ToolCallId);
            Assert.True(end.IsError);
            Assert.NotNull(end.DurationMs);
            Assert.Contains("execution failed", end.Result.GetProperty("error").GetString());
        }
        finally
        {
            Directory.Delete(workingDirectory, recursive: true);
        }
    }

    private sealed class EventClient : IChatClient
    {
        private readonly string _toolName;
        private int _requests;

        public EventClient(string toolName = "parent") => _toolName = toolName;

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (Interlocked.Increment(ref _requests) == 1 && options?.Tools?.Any(tool => tool.Name == _toolName) == true)
                yield return new ChatResponseUpdate(ChatRole.Assistant,
                    [new FunctionCallContent("root-call", _toolName, new Dictionary<string, object?>())]);
            else
                yield return new ChatResponseUpdate(ChatRole.Assistant, "done");
            await Task.CompletedTask;
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    private sealed class FailingClient : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return new ChatResponseUpdate(ChatRole.Assistant, "partial");
            await Task.Yield();
            throw new InvalidOperationException("provider failure");
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    private sealed class WaitingClient(TaskCompletionSource started) : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            started.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            yield break;
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }
}
