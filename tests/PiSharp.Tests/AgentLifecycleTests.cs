using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Extensions.AI;
using PiSharp.Runtime;
using PiSharp.Runtime.Sessions;

namespace PiSharp.Tests;

public sealed class AgentLifecycleTests
{
    [Fact]
    public async Task ReasoningAndProviderUsageStreamAndPersistWithCalculatedCost()
    {
        var session = new ConversationSession(Path.GetTempPath(), "priced-model", null);
        var run = await ConversationRun.OpenAsync(new PiAgent(new ReasoningUsageClient(),
            new CodingTools(Path.GetTempPath())), session,
            pricing: new ModelPricing(Input: 2m, Output: 10m, CachedInput: 1m));
        var events = new List<AgentLifecycleEvent>();

        await foreach (var item in run.RunEventsAsync("think")) events.Add(item);

        Assert.Contains(events, item => item.Type == "reasoning_delta" && item.Text == "checking the context");
        var usage = Assert.Single(events, item => item.Type == "usage");
        Assert.Equal(100, usage.InputTokens);
        Assert.Equal(40, usage.OutputTokens);
        Assert.Equal(20, usage.CachedInputTokens);
        Assert.Equal(12, usage.ReasoningTokens);
        Assert.Equal(140, usage.TotalTokens);
        Assert.Equal(0.00058m, usage.Cost);
        var persisted = Assert.Single(session.ActiveUsage());
        Assert.Equal("priced-model", persisted.Model);
        Assert.Equal("model", persisted.Source);
        Assert.Equal(0.00058m, persisted.Cost);
        var stats = SessionStatistics.Calculate(session);
        Assert.Equal(140, stats.BilledTokens);
        Assert.Equal(0.00058m, stats.Cost);
        Assert.Equal("answer", session.ActiveMessages().Last().Text);
        var reloadedStats = SessionStatistics.Calculate(ConversationSession.Parse(session.ToJson()));
        Assert.Equal(140, reloadedStats.BilledTokens);
        Assert.Equal(0.00058m, reloadedStats.Cost);
    }

    [Fact]
    public async Task RealProviderCallsAndToolInvocationAreOrderedAcrossTwoModelRequests()
    {
        var cwd = Path.Combine(Path.GetTempPath(), "pisharp-events-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(cwd);
        try
        {
            var session = new ConversationSession(cwd, "fixture", null);
            var run = await ConversationRun.OpenAsync(new PiAgent(new ToolClient(), new CodingTools(cwd)), session);
            var events = new List<AgentLifecycleEvent>();
            await foreach (var item in run.RunEventsAsync("write file")) events.Add(item);
            var names = events.Select(item => item.Type).ToList();
            var firstModel = names.IndexOf("model_request_started");
            var call = names.IndexOf("tool_execution_started");
            var outcome = names.IndexOf("tool_execution_finished");
            var secondModel = names.FindIndex(firstModel + 1, item => item == "model_request_started");
            Assert.Equal("prompt_accepted", names[0]);
            Assert.True(firstModel > 0 && call > firstModel && outcome > call && secondModel > outcome);
            var started = Assert.Single(events, item => item.Type == "tool_execution_started");
            Assert.Equal("result.txt", started.ToolArguments?["path"]);
            Assert.DoesNotContain("ToolArguments", JsonSerializer.Serialize(started));
            Assert.Contains(events, item => item.Type == "model_text_delta" && item.Text == "done");
            Assert.Equal(["turn_completed", "agent_run_completed", "agent_settled"], names.TakeLast(3));
            Assert.Equal("made", await File.ReadAllTextAsync(Path.Combine(cwd, "result.txt")));
        }
        finally { Directory.Delete(cwd, recursive: true); }
    }

    [Fact]
    public async Task ActiveThinkingChangeAppliesToTheNextProviderRequestAndPersists()
    {
        var fixture = new ThinkingToolFixture();
        var client = new ThinkingChangeClient();
        var tool = AIFunctionFactory.Create(fixture.WaitAsync, name: "wait_for_thinking");
        var session = new ConversationSession(Path.GetTempPath(), "fixture", null);
        var agent = new PiAgent(client, new CodingTools(Path.GetTempPath()), selectedTools: ["wait_for_thinking"],
            noTools: true, extensionTools: [tool],
            reasoning: new ReasoningOptions { Effort = ReasoningEffort.Low, Output = ReasoningOutput.Full });
        var run = await ConversationRun.OpenAsync(agent, session, reasoningLevel: "low");
        var events = new List<AgentLifecycleEvent>();
        var active = Task.Run(async () =>
        {
            await foreach (var item in run.RunEventsAsync("continue after tool")) events.Add(item);
        });

        await fixture.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(run.SetThinkingLevelDuringRun("high",
            new ReasoningOptions { Effort = ReasoningEffort.High, Output = ReasoningOutput.Full }));
        fixture.Release.TrySetResult();
        await active.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal([ReasoningEffort.Low, ReasoningEffort.High], client.RequestEfforts);
        Assert.True(client.ContinuationSawToolResult);
        var thinkingEntry = Assert.Single(session.Tree.Entries, entry => entry.Type == "thinking_level_change");
        Assert.Equal("high", thinkingEntry.Payload.GetProperty("thinkingLevel").GetString());
        Assert.Equal("done", session.ActiveMessages().Last().Text);
        Assert.Equal("agent_settled", events[^1].Type);
    }

    [Fact]
    public async Task RetryToolContinuationAndQueuedPromptShareOneSettledMultiTurnRun()
    {
        var cwd = Path.Combine(Path.GetTempPath(), "pisharp-retry-queue-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(cwd);
        try
        {
            var client = new RetryQueueClient();
            var session = new ConversationSession(cwd, "fixture", null);
            var agent = new PiAgent(client, new CodingTools(cwd), retryPolicy: new ProviderRetryPolicy(maxRetries: 1));
            var run = await ConversationRun.OpenAsync(agent, session);
            var events = new List<AgentLifecycleEvent>();
            var collecting = Task.Run(async () =>
            {
                await foreach (var item in run.RunEventsAsync("write the first file")) events.Add(item);
            });

            await client.FinalResponseStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(run.TryQueuePrompt("now remember the completed first turn"));
            Assert.Equal(1, run.PendingPromptCount);
            client.ReleaseFinalResponse.TrySetResult();
            await collecting.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.Equal(4, client.Requests);
            Assert.True(client.QueuedRequestSawFirstTurn);
            Assert.Equal("made", await File.ReadAllTextAsync(Path.Combine(cwd, "result.txt")));
            Assert.Equal(2, session.ActiveMessages().Count(message => message.Role == ChatRole.User));
            Assert.Contains(session.ActiveMessages().SelectMany(message => message.Contents),
                content => content is FunctionCallContent { CallId: "call-1" });
            Assert.Contains(session.ActiveMessages().SelectMany(message => message.Contents),
                content => content is FunctionResultContent { CallId: "call-1" });
            Assert.Equal(2, events.Count(item => item.Type == "turn_completed"));
            Assert.Single(events, item => item.Type == "model_retry_scheduled");
            Assert.Contains(events, item => item.Type == "prompt_queued" && item.Text == "now remember the completed first turn");
            Assert.Contains(events, item => item.Type == "model_text_delta" && item.Text == "first ");
            Assert.Equal("agent_settled", events[^1].Type);
            Assert.Equal(0, run.PendingPromptCount);
        }
        finally { Directory.Delete(cwd, recursive: true); }
    }

    [Fact]
    public async Task SteeringRunsBeforeFollowUpAndAbortReturnsUnstartedInput()
    {
        var client = new OrderedQueueClient();
        var run = await ConversationRun.OpenAsync(new PiAgent(client, new CodingTools(Path.GetTempPath())),
            new ConversationSession(Path.GetTempPath(), "fixture", null));
        var events = new List<AgentLifecycleEvent>();
        var active = Task.Run(async () =>
        {
            await foreach (var item in run.RunEventsAsync("initial")) events.Add(item);
        });
        await client.FirstRequestStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(run.TryFollowUp("follow up"));
        Assert.True(run.TrySteer("steer one"));
        Assert.True(run.TrySteer("steer two"));
        client.ReleaseFirstRequest.TrySetResult();
        await active.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(["initial", "steer one", "steer two", "follow up"], client.LatestUserByRequest);
        Assert.Equal(4, events.Count(item => item.Type == "turn_completed"));
        Assert.Contains(events, item => item.Type == "prompt_queued" && item.Tool == "steering");
        Assert.Contains(events, item => item.Type == "prompt_queued" && item.Tool == "follow_up");

        var blocked = new WaitingClient();
        var abortRun = await ConversationRun.OpenAsync(new PiAgent(blocked, new CodingTools(Path.GetTempPath())),
            new ConversationSession(Path.GetTempPath(), "fixture", null));
        using var cancel = new CancellationTokenSource();
        var interrupted = Task.Run(async () =>
        {
            await foreach (var _ in abortRun.RunEventsAsync("wait", cancel.Token)) { }
        });
        await blocked.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(abortRun.TrySteer("restore steer"));
        Assert.True(abortRun.TryFollowUp("restore follow up"));
        cancel.Cancel();
        await interrupted.WaitAsync(TimeSpan.FromSeconds(5));
        var returned = abortRun.ClearPendingPrompts();
        Assert.Equal(["restore steer"], returned.Steering);
        Assert.Equal(["restore follow up"], returned.FollowUp);
        Assert.Equal(0, abortRun.PendingPromptCount);
    }

    [Fact]
    public async Task SteeringQueuedDuringToolExecutionReachesImmediateContinuationRequest()
    {
        var fixture = new SteeringToolFixture();
        var client = new SteeringToolClient();
        var tool = AIFunctionFactory.Create(fixture.WaitAsync, name: "wait_for_steering");
        var session = new ConversationSession(Path.GetTempPath(), "fixture", null);
        var run = await ConversationRun.OpenAsync(new PiAgent(client, new CodingTools(Path.GetTempPath()),
            selectedTools: ["wait_for_steering"], noTools: true, extensionTools: [tool]), session);
        var events = new List<AgentLifecycleEvent>();
        var active = Task.Run(async () =>
        {
            await foreach (var item in run.RunEventsAsync("start")) events.Add(item);
        });
        await fixture.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(run.TrySteer("change direction"));
        fixture.Release.TrySetResult();
        await active.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(client.ContinuationSawSteeringAndResult);
        Assert.Equal(2, client.Requests);
        Assert.Equal(0, run.PendingPromptCount);
        Assert.Equal([ChatRole.User, ChatRole.Assistant, ChatRole.Tool, ChatRole.User, ChatRole.Assistant],
            session.ActiveMessages().Select(message => message.Role));
        Assert.Equal("change direction", session.ActiveMessages()[3].Text);
        Assert.Single(events, item => item.Type == "turn_completed");
    }

    [Fact]
    public async Task MultipleModelToolCallsExecuteConcurrentlyAndPersistInSourceOrder()
    {
        var fixture = new ConcurrentToolFixture();
        var tool = AIFunctionFactory.Create(fixture.BarrierAsync, name: "barrier");
        var client = new ParallelToolClient();
        var session = new ConversationSession(Path.GetTempPath(), "fixture", null);
        var run = await ConversationRun.OpenAsync(new PiAgent(client, new CodingTools(Path.GetTempPath()),
            selectedTools: ["barrier"], noTools: true, extensionTools: [tool]), session);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var events = new List<AgentLifecycleEvent>();
        await foreach (var item in run.RunEventsAsync("run both", timeout.Token)) events.Add(item);

        Assert.True(fixture.OverlapObserved);
        Assert.Equal(["first", "second"], client.ResultValues);
        var starts = events.Select((item, index) => (item, index))
            .Where(pair => pair.item.Type == "tool_execution_started").Select(pair => pair.index).ToArray();
        var finishes = events.Select((item, index) => (item, index))
            .Where(pair => pair.item.Type == "tool_execution_finished").Select(pair => pair.index).ToArray();
        Assert.Equal(2, starts.Length);
        Assert.Equal(2, finishes.Length);
        Assert.True(starts.Max() < finishes.Min());
        Assert.Equal(["a", "b"], session.ActiveMessages().SelectMany(message => message.Contents)
            .OfType<FunctionResultContent>().Select(result => result.CallId));
    }

    [Fact]
    public async Task ProviderFailureAfterPartialOutputIsNotRetriedAndPreservesPartialResponse()
    {
        var session = new ConversationSession(Path.GetTempPath(), "fixture", null);
        var client = new PartialFailureClient();
        var run = await ConversationRun.OpenAsync(new PiAgent(client, new CodingTools(Path.GetTempPath()),
            retryPolicy: new ProviderRetryPolicy(maxRetries: 3)), session);
        var events = new List<AgentLifecycleEvent>();
        await foreach (var item in run.RunEventsAsync("start")) events.Add(item);

        Assert.Equal(1, client.Requests);
        Assert.Contains(events, item => item.Type == "model_text_delta" && item.Text == "partial");
        Assert.DoesNotContain(events, item => item.Type == "model_retry_scheduled");
        Assert.Contains(events, item => item.Type == "turn_failed");
        Assert.DoesNotContain(events, item => item.Type is "turn_completed" or "agent_run_completed");
        var interrupted = Assert.Single(session.Tree.Entries, entry => entry.Type == "interrupted");
        Assert.Equal("partial", interrupted.Payload.GetProperty("partialText").GetString());
        Assert.Equal("partial", interrupted.Payload.GetProperty("partialAssistantText").GetString());
    }

    [Fact]
    public async Task SessionRetryContinuesExistingContextAndReportsAttemptBoundaries()
    {
        var client = new RetryAfterPartialClient();
        var session = new ConversationSession(Path.GetTempPath(), "fixture", null);
        var retryPolicy = new AgentRunRetryPolicy(enabled: true, maxRetries: 1,
            baseDelay: TimeSpan.Zero, maxDelay: TimeSpan.Zero);
        var run = await ConversationRun.OpenAsync(new PiAgent(client, new CodingTools(Path.GetTempPath()),
            retryPolicy: ProviderRetryPolicy.None), session, retryPolicy: retryPolicy);
        var events = new List<AgentLifecycleEvent>();

        await foreach (var item in run.RunEventsAsync("hello")) events.Add(item);

        Assert.Equal(2, client.Requests);
        Assert.Equal(1, client.RetryMessages.Count(message => message.Role == ChatRole.User && message.Text == "hello"));
        Assert.DoesNotContain(client.RetryMessages, message => message.Text?.Contains("partial", StringComparison.Ordinal) == true);
        Assert.Single(session.ActiveMessages(), message => message.Role == ChatRole.User && message.Text == "hello");
        Assert.Equal("recovered", session.ActiveMessages().Last(message => message.Role == ChatRole.Assistant).Text);
        var failed = Assert.Single(events, item => item.Type == "turn_failed");
        Assert.True(failed.WillRetry);
        Assert.Single(events, item => item.Type == "auto_retry_start");
        Assert.Single(events, item => item.Type == "agent_attempt_started");
        Assert.Single(events, item => item.Type == "auto_retry_end" && item.RetrySuccess == true);
        Assert.Single(events, item => item.Type == "turn_completed");
        Assert.Equal("agent_settled", events[^1].Type);
        Assert.True(events.FindIndex(item => item.Type == "turn_failed") <
            events.FindIndex(item => item.Type == "auto_retry_start"));
        Assert.True(events.FindIndex(item => item.Type == "auto_retry_start") <
            events.FindIndex(item => item.Type == "agent_attempt_started"));
    }

    [Fact]
    public async Task AbortRetryCancelsOnlyTheRetryDelayAndReportsFinalization()
    {
        var delayStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Func<TimeSpan, CancellationToken, Task> retryDelay = async (_, cancellationToken) =>
        {
            delayStarted.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        };
        var client = new RetryAfterPartialClient();
        var retryPolicy = new AgentRunRetryPolicy(enabled: true, maxRetries: 2,
            baseDelay: TimeSpan.FromSeconds(2), maxDelay: TimeSpan.FromSeconds(4));
        var run = await ConversationRun.OpenAsync(new PiAgent(client, new CodingTools(Path.GetTempPath()),
            retryPolicy: ProviderRetryPolicy.None), new ConversationSession(Path.GetTempPath(), "fixture", null),
            retryPolicy: retryPolicy, retryDelay: retryDelay);
        var events = new List<AgentLifecycleEvent>();
        var running = Task.Run(async () =>
        {
            await foreach (var item in run.RunEventsAsync("hello")) events.Add(item);
        });

        await delayStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(run.IsRetrying);
        run.AbortRetry();
        await running.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(1, client.Requests);
        var retryEnd = Assert.Single(events, item => item.Type == "auto_retry_end");
        Assert.False(retryEnd.RetrySuccess);
        Assert.Equal("Retry cancelled", retryEnd.RetryFinalError);
        Assert.DoesNotContain(events, item => item.Type == "agent_attempt_started");
        Assert.False(run.IsRetrying);
        Assert.Equal("agent_settled", events[^1].Type);
    }

    [Theory]
    [InlineData(400, false)]
    [InlineData(401, false)]
    [InlineData(403, false)]
    [InlineData(408, true)]
    [InlineData(409, true)]
    [InlineData(429, true)]
    [InlineData(503, true)]
    public async Task HttpStatusRetryIsLimitedToTransientFailures(int status, bool retry)
    {
        var client = new HttpStatusClient((System.Net.HttpStatusCode)status);
        var session = new ConversationSession(Path.GetTempPath(), "fixture", null);
        var run = await ConversationRun.OpenAsync(new PiAgent(client, new CodingTools(Path.GetTempPath()),
            retryPolicy: new ProviderRetryPolicy(maxRetries: 1)), session);
        var events = new List<AgentLifecycleEvent>();
        await foreach (var item in run.RunEventsAsync("hello")) events.Add(item);
        Assert.Equal(retry ? 2 : 1, client.Requests);
        Assert.Equal(retry, events.Any(item => item.Type == "model_retry_scheduled"));
        Assert.Equal(retry, events.Any(item => item.Type == "turn_completed"));
    }

    [Fact]
    public async Task MetadataOnlyProviderUpdateDoesNotBlockSafeRetry()
    {
        var client = new MetadataFailureClient();
        var session = new ConversationSession(Path.GetTempPath(), "fixture", null);
        var run = await ConversationRun.OpenAsync(new PiAgent(client, new CodingTools(Path.GetTempPath()),
            retryPolicy: new ProviderRetryPolicy(maxRetries: 1)), session);
        var events = new List<AgentLifecycleEvent>();
        await foreach (var item in run.RunEventsAsync("hello")) events.Add(item);
        Assert.Equal(2, client.Requests);
        Assert.Single(events, item => item.Type == "model_retry_scheduled");
        Assert.Single(events, item => item.Type == "turn_completed");
        Assert.Single(session.ActiveMessages(), item => item.Role == ChatRole.Assistant);
        Assert.Equal("ok", session.ActiveMessages().Last(item => item.Role == ChatRole.Assistant).Text);
    }

    [Fact]
    public async Task ProviderFailureNeverProducesSuccessfulCompletion()
    {
        var cwd = Path.GetTempPath();
        var session = new ConversationSession(cwd, "fixture", null);
        var run = await ConversationRun.OpenAsync(new PiAgent(new FailClient(), new CodingTools(cwd)), session);
        var events = new List<AgentLifecycleEvent>();
        await foreach (var item in run.RunEventsAsync("fail")) events.Add(item);
        Assert.Contains(events, item => item.Type == "model_request_failed" && item.Error == "provider broke");
        Assert.Contains(events, item => item.Type == "turn_failed");
        Assert.DoesNotContain(events, item => item.Type is "turn_completed" or "agent_run_completed");
        Assert.Equal("agent_settled", events[^1].Type);
    }

    [Fact]
    public async Task SlowConsumerCanDisposeBoundedStreamWithoutDeadlock()
    {
        var run = await ConversationRun.OpenAsync(new PiAgent(new ManyUpdatesClient(), new CodingTools(Path.GetTempPath())),
            new ConversationSession(Path.GetTempPath(), "fixture", null));
        var consuming = Task.Run(async () =>
        {
            await using var enumerator = run.RunEventsAsync("lots").GetAsyncEnumerator();
            Assert.True(await enumerator.MoveNextAsync());
            Assert.Equal("prompt_accepted", enumerator.Current.Type);
            await Task.Delay(50);
        });
        await consuming.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task CancelledProviderReportsInterruptionAndSettlement()
    {
        var run = await ConversationRun.OpenAsync(new PiAgent(new WaitingClient(), new CodingTools(Path.GetTempPath())),
            new ConversationSession(Path.GetTempPath(), "fixture", null));
        using var cancel = new CancellationTokenSource();
        var events = new List<AgentLifecycleEvent>();
        await foreach (var item in run.RunEventsAsync("wait", cancel.Token))
        {
            events.Add(item);
            if (item.Type == "model_request_started") cancel.Cancel();
        }
        Assert.Contains(events, item => item.Type == "turn_interrupted");
        Assert.Equal("agent_settled", events[^1].Type);
        Assert.DoesNotContain(events, item => item.Type is "turn_completed" or "agent_run_completed");
    }

    private sealed class ReasoningUsageClient : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            yield return new ChatResponseUpdate(ChatRole.Assistant,
                [new TextReasoningContent("checking the context")]);
            yield return new ChatResponseUpdate(ChatRole.Assistant, "answer");
            yield return new ChatResponseUpdate(ChatRole.Assistant,
                [new UsageContent(new UsageDetails
                {
                    InputTokenCount = 100,
                    OutputTokenCount = 40,
                    CachedInputTokenCount = 20,
                    ReasoningTokenCount = 12,
                    TotalTokenCount = 140
                })]);
            await Task.CompletedTask;
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    private sealed class OrderedQueueClient : IChatClient
    {
        public List<string> LatestUserByRequest { get; } = [];
        public TaskCompletionSource FirstRequestStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseFirstRequest { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            LatestUserByRequest.Add(messages.Last(message => message.Role == ChatRole.User).Text!);
            if (LatestUserByRequest.Count == 1)
            {
                FirstRequestStarted.TrySetResult();
                await ReleaseFirstRequest.Task.WaitAsync(cancellationToken);
            }
            yield return new ChatResponseUpdate(ChatRole.Assistant, "done");
        }
        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    private sealed class SteeringToolFixture
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        [Description("Wait until steering has been queued.")]
        public async Task<string> WaitAsync(CancellationToken cancellationToken)
        {
            Started.TrySetResult();
            await Release.Task.WaitAsync(cancellationToken);
            return "tool done";
        }
    }

    private sealed class ThinkingToolFixture
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        [Description("Wait for an active thinking setting change before returning.")]
        public async Task<string> WaitAsync(CancellationToken cancellationToken)
        {
            Started.TrySetResult();
            await Release.Task.WaitAsync(cancellationToken);
            return "tool done";
        }
    }

    private sealed class ThinkingChangeClient : IChatClient
    {
        private int _requests;
        public List<ReasoningEffort?> RequestEfforts { get; } = [];
        public bool ContinuationSawToolResult { get; private set; }

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            RequestEfforts.Add(options?.Reasoning?.Effort);
            if (++_requests == 1)
                yield return new ChatResponseUpdate(ChatRole.Assistant,
                    [new FunctionCallContent("thinking-call", "wait_for_thinking", new Dictionary<string, object?>())]);
            else
            {
                ContinuationSawToolResult = messages.SelectMany(message => message.Contents)
                    .OfType<FunctionResultContent>().Any(result => result.CallId == "thinking-call");
                yield return new ChatResponseUpdate(ChatRole.Assistant, "done");
            }
            await Task.CompletedTask;
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    private sealed class SteeringToolClient : IChatClient
    {
        public int Requests { get; private set; }
        public bool ContinuationSawSteeringAndResult { get; private set; }
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            if (++Requests == 1)
                yield return new ChatResponseUpdate(ChatRole.Assistant,
                    [new FunctionCallContent("wait", "wait_for_steering", new Dictionary<string, object?>())]);
            else
            {
                var snapshot = messages.ToArray();
                ContinuationSawSteeringAndResult = snapshot.Any(message => message.Role == ChatRole.User &&
                        message.Text == "change direction") &&
                    snapshot.SelectMany(message => message.Contents).OfType<FunctionResultContent>()
                        .Any(result => result.CallId == "wait");
                yield return new ChatResponseUpdate(ChatRole.Assistant, "steered");
            }
            await Task.CompletedTask;
        }
        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    private sealed class ConcurrentToolFixture
    {
        private readonly TaskCompletionSource _firstStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _secondStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool OverlapObserved { get; private set; }

        [Description("Wait for both calls so the fixture can prove concurrent invocation.")]
        public async Task<string> BarrierAsync(string value, CancellationToken cancellationToken)
        {
            if (value == "first")
            {
                _firstStarted.TrySetResult();
                await _secondStarted.Task.WaitAsync(cancellationToken);
                OverlapObserved = true;
            }
            else
            {
                await _firstStarted.Task.WaitAsync(cancellationToken);
                _secondStarted.TrySetResult();
            }
            return value;
        }
    }

    private sealed class ParallelToolClient : IChatClient
    {
        private int _requests;
        public string[] ResultValues { get; private set; } = [];
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            if (++_requests == 1)
                yield return new ChatResponseUpdate(ChatRole.Assistant,
                [
                    new FunctionCallContent("a", "barrier", new Dictionary<string, object?> { ["value"] = "first" }),
                    new FunctionCallContent("b", "barrier", new Dictionary<string, object?> { ["value"] = "second" })
                ]);
            else
            {
                ResultValues = messages.SelectMany(message => message.Contents).OfType<FunctionResultContent>()
                    .Select(result => result.Result?.ToString()!).ToArray();
                yield return new ChatResponseUpdate(ChatRole.Assistant, "done");
            }
            await Task.CompletedTask;
        }
        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    private sealed class MetadataFailureClient : IChatClient
    {
        public int Requests { get; private set; }
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            if (++Requests == 1)
            {
                yield return new ChatResponseUpdate(ChatRole.Assistant, []);
                await Task.Yield();
                throw new IOException("failed before content");
            }
            yield return new ChatResponseUpdate(ChatRole.Assistant, "ok");
        }
        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }
    private sealed class HttpStatusClient(System.Net.HttpStatusCode status) : IChatClient
    {
        public int Requests { get; private set; }
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            if (++Requests == 1)
            {
                await Task.Yield();
                throw new HttpRequestException("provider status", null, status);
            }
            yield return new ChatResponseUpdate(ChatRole.Assistant, "ok");
        }
        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }
    private sealed class RetryQueueClient : IChatClient
    {
        public int Requests { get; private set; }
        public bool QueuedRequestSawFirstTurn { get; private set; }
        public TaskCompletionSource FinalResponseStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseFinalResponse { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            var request = ++Requests;
            if (request == 1)
            {
                await Task.Yield();
                throw new IOException("transient before output");
            }
            if (request == 2)
            {
                yield return new ChatResponseUpdate(ChatRole.Assistant,
                    [new FunctionCallContent("call-1", "write", new Dictionary<string, object?>
                    {
                        ["path"] = "result.txt",
                        ["content"] = "made"
                    })]);
                yield break;
            }
            if (request == 3)
            {
                FinalResponseStarted.TrySetResult();
                await ReleaseFinalResponse.Task.WaitAsync(cancellationToken);
                yield return new ChatResponseUpdate(ChatRole.Assistant, "first ");
                yield return new ChatResponseUpdate(ChatRole.Assistant, "done");
                yield break;
            }

            var snapshot = messages.ToArray();
            QueuedRequestSawFirstTurn = snapshot.Any(message => message.Role == ChatRole.User &&
                    message.Text == "write the first file") &&
                snapshot.Any(message => message.Role == ChatRole.User &&
                    message.Text == "now remember the completed first turn") &&
                snapshot.SelectMany(message => message.Contents).OfType<FunctionResultContent>()
                    .Any(result => result.CallId == "call-1");
            yield return new ChatResponseUpdate(ChatRole.Assistant, "second done");
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    private sealed class PartialFailureClient : IChatClient
    {
        public int Requests { get; private set; }
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            Requests++;
            yield return new ChatResponseUpdate(ChatRole.Assistant, "partial");
            await Task.Yield();
            throw new IOException("failed after output");
        }
        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    private sealed class RetryAfterPartialClient : IChatClient
    {
        public int Requests { get; private set; }
        public IReadOnlyList<ChatMessage> RetryMessages { get; private set; } = [];
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            if (++Requests == 1)
            {
                yield return new ChatResponseUpdate(ChatRole.Assistant, "partial");
                await Task.Yield();
                throw new HttpRequestException("503 service unavailable", null, System.Net.HttpStatusCode.ServiceUnavailable);
            }
            RetryMessages = messages.ToArray();
            yield return new ChatResponseUpdate(ChatRole.Assistant, "recovered");
        }
        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    private sealed class WaitingClient : IChatClient
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            Started.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            yield break;
        }
        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    private sealed class ManyUpdatesClient : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            for (var i = 0; i < 10000; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return new ChatResponseUpdate(ChatRole.Assistant, "x");
            }
            await Task.CompletedTask;
        }
        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    private sealed class ToolClient : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            if (!messages.SelectMany(m => m.Contents).OfType<FunctionResultContent>().Any())
                yield return new ChatResponseUpdate(ChatRole.Assistant,
                    [new FunctionCallContent("call1", "write", new Dictionary<string, object?> { ["path"] = "result.txt", ["content"] = "made" })]);
            else yield return new ChatResponseUpdate(ChatRole.Assistant, "done");
            await Task.CompletedTask;
        }
        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    private sealed class FailClient : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            throw new IOException("provider broke");
#pragma warning disable CS0162
            yield break;
#pragma warning restore CS0162
        }
        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }
}
