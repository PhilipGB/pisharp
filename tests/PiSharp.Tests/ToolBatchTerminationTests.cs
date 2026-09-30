using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;
using PiSharp.Runtime;
using PiSharp.Runtime.Extensions;
using PiSharp.Runtime.Sessions;

namespace PiSharp.Tests;

public sealed class ToolBatchTerminationTests
{
    [Theory]
    [InlineData(true, true, 1)]
    [InlineData(true, false, 2)]
    [InlineData(false, true, 2)]
    [InlineData(false, false, 2)]
    public async Task OnlyAllTerminatingCallsStopTheBatchAfterBothSiblingsFinish(bool first, bool second, int requests)
    {
        var root = Path.Combine(Path.GetTempPath(), "pisharp-batch-termination-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var started = new[] { Signal(), Signal() };
            var release = new[] { Signal(), Signal() };
            var firstFinished = Signal();
            var flags = new[] { first, second };
            var registrations = Enumerable.Range(0, 2).Select(index => new PiSharpToolRegistration(
                AIFunctionFactory.Create(async (CancellationToken token) =>
                {
                    started[index].TrySetResult();
                    await release[index].Task.WaitAsync(token);
                    return new PiSharpToolResult("result " + index, Terminate: flags[index]);
                }, name: "tool" + index), ToolExposure.ModelOnly)).ToArray();
            var client = new BatchClient();
            var session = new ConversationSession(root, "fixture", null);
            var run = await ConversationRun.OpenAsync(new PiAgent(client, new CodingTools(root), noBuiltinTools: true,
                extensionToolRegistrations: registrations), session, save: _ => Task.CompletedTask);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            var collect = Task.Run(async () =>
            {
                var events = new List<AgentLifecycleEvent>();
                await foreach (var item in run.RunEventsAsync("batch", timeout.Token))
                {
                    events.Add(item);
                    if (item.Type == "tool_execution_finished") firstFinished.TrySetResult();
                }
                return events;
            });
            await Task.WhenAll(started.Select(signal => signal.Task)).WaitAsync(TimeSpan.FromSeconds(10));
            release[1].TrySetResult();
            await firstFinished.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(1, client.RequestCount);
            Assert.False(collect.IsCompleted);
            release[0].TrySetResult();
            var events = await collect.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(requests, client.RequestCount);
            Assert.Equal(2, events.Count(item => item.Type == "tool_execution_finished"));
            Assert.Equal(2, session.ActiveMessages().SelectMany(message => message.Contents).OfType<FunctionResultContent>().Count());
            var restored = ConversationSession.Parse(session.ToJson());
            Assert.Equal(2, restored.ActiveMessages().SelectMany(message => message.Contents).OfType<FunctionResultContent>().Count());
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MixedFirstBatchDoesNotPoisonAllTerminatingSecondBatch(bool error)
    {
        var root = Path.Combine(Path.GetTempPath(), "pisharp-batch-iterations-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var firstCalls = 0;
            var first = AIFunctionFactory.Create(() => new PiSharpToolResult("first", IsError: error,
                Terminate: Interlocked.Increment(ref firstCalls) > 1), name: "tool0");
            var second = AIFunctionFactory.Create(() => new PiSharpToolResult("second", Terminate: true), name: "tool1");
            var client = new BatchClient(2);
            var session = new ConversationSession(root, "fixture", null);
            var run = await ConversationRun.OpenAsync(new PiAgent(client, new CodingTools(root), noBuiltinTools: true,
                extensionToolRegistrations: [new(first, ToolExposure.ModelOnly), new(second, ToolExposure.ModelOnly)]),
                session, save: _ => Task.CompletedTask);
            await foreach (var _ in run.RunEventsAsync("two batches")) { }
            Assert.Equal(2, client.RequestCount);
            Assert.Equal(4, session.ActiveMessages().SelectMany(message => message.Contents).OfType<FunctionResultContent>().Count());
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task UnknownSiblingDoesNotLetKnownTerminatingCallStopContinuation()
    {
        var root = Path.Combine(Path.GetTempPath(), "pisharp-batch-unknown-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var tool = AIFunctionFactory.Create(() => new PiSharpToolResult("stop", Terminate: true), name: "tool0");
            var client = new BatchClient();
            var session = new ConversationSession(root, "fixture", null);
            var run = await ConversationRun.OpenAsync(new PiAgent(client, new CodingTools(root), noBuiltinTools: true,
                extensionToolRegistrations: [new(tool, ToolExposure.ModelOnly)]), session, save: _ => Task.CompletedTask);
            await foreach (var _ in run.RunEventsAsync("unknown sibling")) { }
            Assert.Equal(2, client.RequestCount);
            Assert.Equal(2, session.ActiveMessages().SelectMany(message => message.Contents).OfType<FunctionResultContent>().Count());
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task FailedSiblingContinuesWithBothDurableResults()
    {
        var root = Path.Combine(Path.GetTempPath(), "pisharp-batch-failure-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var stop = AIFunctionFactory.Create(() => new PiSharpToolResult("stop", Terminate: true), name: "tool0");
            var fail = AIFunctionFactory.Create((Func<string>)(() => throw new InvalidOperationException("sibling failed")), name: "tool1");
            var client = new BatchClient();
            var session = new ConversationSession(root, "fixture", null);
            var run = await ConversationRun.OpenAsync(new PiAgent(client, new CodingTools(root), noBuiltinTools: true,
                extensionToolRegistrations: [new(stop, ToolExposure.ModelOnly), new(fail, ToolExposure.ModelOnly)]),
                session, save: _ => Task.CompletedTask);
            await foreach (var _ in run.RunEventsAsync("failed sibling")) { }
            Assert.Equal(2, client.RequestCount);
            var results = session.ActiveMessages().SelectMany(message => message.Contents).OfType<FunctionResultContent>().ToArray();
            Assert.Equal(2, results.Length);
            Assert.Single(results, result => result.Exception is not null);
            Assert.Contains(session.Tree.Entries, entry => entry.Type == "tool_outcome" &&
                entry.Payload.TryGetProperty("error", out var error) && error.GetString() == "sibling failed");
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private sealed class BatchClient(int batches = 1) : IChatClient
    {
        private int _requests;
        public int RequestCount => Volatile.Read(ref _requests);
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            var request = Interlocked.Increment(ref _requests);
            if (request <= batches)
                yield return new ChatResponseUpdate(ChatRole.Assistant,
                    Enumerable.Range(0, 2).Select(index => (AIContent)new FunctionCallContent("call" + request + "-" + index,
                        "tool" + index, new Dictionary<string, object?>())).ToArray());
            else yield return new ChatResponseUpdate(ChatRole.Assistant, "done");
            await Task.CompletedTask;
        }
        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }
}
