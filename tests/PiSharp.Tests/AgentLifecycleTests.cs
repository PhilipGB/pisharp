using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;
using PiSharp.Runtime;
using PiSharp.Runtime.Sessions;

namespace PiSharp.Tests;

public sealed class AgentLifecycleTests
{
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
            Assert.Contains(events, item => item.Type == "model_text_delta" && item.Text == "done");
            Assert.Equal(["turn_completed", "agent_run_completed", "agent_settled"], names.TakeLast(3));
            Assert.Equal("made", await File.ReadAllTextAsync(Path.Combine(cwd, "result.txt")));
        }
        finally { Directory.Delete(cwd, recursive: true); }
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

    private sealed class WaitingClient : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
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
