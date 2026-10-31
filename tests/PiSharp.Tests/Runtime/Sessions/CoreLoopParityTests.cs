using System.ComponentModel;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;
using PiSharp.Runtime;
using PiSharp.Runtime.Sessions;

namespace PiSharp.Tests;

/// <summary>
/// Pi-parity behavior for the active core loop: failed/aborted turn persistence,
/// live recovery handling, queue drain event ordering, transcript ordering, and
/// parallel tool result ordering. Oracle: pinned Pi (tools/parity/pi-reference.txt).
/// </summary>
public sealed class CoreLoopParityTests
{
    [Fact]
    public async Task ProviderErrorPersistsFailedAssistantMessage()
    {
        var client = new FailingFirstClient();
        var run = await ConversationRun.OpenAsync(new PiAgent(client, new CodingTools(Path.GetTempPath())),
            new ConversationSession(Path.GetTempPath(), "fixture", null));
        var events = new List<AgentLifecycleEvent>();

        await foreach (var item in run.RunEventsAsync("GO")) events.Add(item);

        Assert.Contains(events, item => item.Type == "turn_failed");
        Assert.Equal("agent_settled", events[^1].Type);
        var assistant = Assert.IsType<ChatMessage>(run.Conversation.ActiveMessages().Last());
        Assert.Equal(ChatRole.Assistant, assistant.Role);
        Assert.Equal("error", ChatMessageProperties.String(assistant.AdditionalProperties, "pisharp.stopReason"));
        Assert.Contains("fixture provider failure",
            ChatMessageProperties.String(assistant.AdditionalProperties, "pisharp.errorMessage"));
    }

    [Fact]
    public async Task AbortedTurnPersistsAbortedAssistantAndSettles()
    {
        var client = new HangBeforeContentClient();
        var session = new ConversationSession(Path.GetTempPath(), "fixture", null);
        var run = await ConversationRun.OpenAsync(new PiAgent(client, new CodingTools(Path.GetTempPath())), session);
        using var cancel = new CancellationTokenSource();
        var events = new List<AgentLifecycleEvent>();
        var active = Task.Run(async () =>
        {
            await foreach (var item in run.RunEventsAsync("GO", cancel.Token)) events.Add(item);
        });

        await client.RequestStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancel.Cancel();
        await active.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Contains(events, item => item.Type == "turn_interrupted");
        var assistant = Assert.IsType<ChatMessage>(run.Conversation.ActiveMessages().Last());
        Assert.Equal(ChatRole.Assistant, assistant.Role);
        Assert.Equal("aborted", ChatMessageProperties.String(assistant.AdditionalProperties, "pisharp.stopReason"));
        // A subsequent prompt still works after the aborted turn settles.
        await foreach (var _ in run.RunEventsAsync("NEXT")) { }
        Assert.EndsWith("NEXT-OK", run.Conversation.ActiveMessages().Last().Text ?? string.Empty);
    }

    [Fact]
    public async Task AbortedToolPersistsErrorResultWithCancellationText()
    {
        var fixture = new CancellingToolFixture();
        var tool = AIFunctionFactory.Create(fixture.RunAsync, name: "cancel_probe");
        var run = await ConversationRun.OpenAsync(new PiAgent(new ToolCallThenHangClient(),
            new CodingTools(Path.GetTempPath()), selectedTools: ["cancel_probe"], noTools: true,
            extensionTools: [tool]), new ConversationSession(Path.GetTempPath(), "fixture", null));
        using var cancel = new CancellationTokenSource();
        var active = Task.Run(async () =>
        {
            await foreach (var _ in run.RunEventsAsync("GO", cancel.Token)) { }
        });

        await fixture.ToolStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancel.Cancel();
        await active.WaitAsync(TimeSpan.FromSeconds(5));

        var results = run.Conversation.ActiveMessages()
            .Where(message => message.Role == ChatRole.Tool)
            .SelectMany(message => message.Contents.OfType<FunctionResultContent>())
            .ToList();
        var result = Assert.Single(results);
        var persisted = result.Result?.ToString() ?? string.Empty;
        // The cancelled external tool settles as an error result carrying the cancellation message
        // (so the tool round does not drop the batch), rather than rethrowing the cancellation.
        Assert.Contains("cancel_probe_aborted", persisted);
        Assert.Contains("\"isError\":true", persisted);
        Assert.NotNull(result.Exception);
    }

    [Fact]
    public async Task LiveProviderFailureDoesNotInjectRecoveryNotice()
    {
        var client = new FailingFirstClient();
        var session = new ConversationSession(Path.GetTempPath(), "fixture", null);
        var run = await ConversationRun.OpenAsync(new PiAgent(client, new CodingTools(Path.GetTempPath())), session);

        await foreach (var _ in run.RunEventsAsync("GO")) { }
        await foreach (var _ in run.RunEventsAsync("NEXT")) { }

        var nextRequest = Assert.Single(client.Requests.Skip(1)).ToList();
        Assert.DoesNotContain(nextRequest.SelectMany(message => message.Contents).OfType<TextContent>(),
            content => content.Text.Contains("Recovery notice"));
        Assert.DoesNotContain(session.Tree.Entries, entry => entry.Type == "run_recovered");
    }

    [Fact]
    public async Task QueuedBatchDrainUpdateFollowsTurnStart()
    {
        var client = new OrderedQueueClient();
        var run = await ConversationRun.OpenAsync(new PiAgent(client, new CodingTools(Path.GetTempPath())),
            new ConversationSession(Path.GetTempPath(), "fixture", null));
        var events = new List<AgentLifecycleEvent>();
        var active = Task.Run(async () =>
        {
            await foreach (var item in run.RunEventsAsync("GO")) events.Add(item);
        });

        await client.FirstRequestStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(run.TrySteer("S1"));
        client.ReleaseFirstRequest.TrySetResult();
        await active.WaitAsync(TimeSpan.FromSeconds(5));

        var types = events.Select(item => item.Type).ToList();
        var firstTurnStart = types.IndexOf("assistant_turn_started");
        var secondTurnStart = types.FindIndex(firstTurnStart + 1, item => item == "assistant_turn_started");
        Assert.True(secondTurnStart > 0, "expected a turn for the queued steering message");
        var drainUpdate = types.LastIndexOf("queue_update");
        var secondRequest = types.FindIndex(secondTurnStart + 1, item => item == "model_request_started");
        // Pi emits the queue update for a drained batch after the batch turn starts and
        // before the batch messages are flushed: turn_start -> queue_update -> message.
        Assert.True(drainUpdate > secondTurnStart,
            $"queue_update[{drainUpdate}] must follow the batch turn_start[{secondTurnStart}]");
        Assert.True(drainUpdate < secondRequest,
            $"queue_update[{drainUpdate}] must precede the batch request[{secondRequest}]");
    }

    [Fact]
    public async Task SystemTranscriptIsPersistedBeforeThePromptMessage()
    {
        var session = new ConversationSession(Path.GetTempPath(), "fixture", null);
        var run = await ConversationRun.OpenAsync(new PiAgent(new TextClient(), new CodingTools(Path.GetTempPath())),
            session);

        await foreach (var _ in run.RunEventsAsync("GO")) { }

        var path = session.Tree.ActivePath().ToList();
        var transcriptIndex = path.FindIndex(entry => entry.Type == "tool_transcript");
        var promptIndex = path.FindIndex(entry => entry.Type == "chat"
            && ConversationMessageCodec.RestoreEntry(entry).Role == ChatRole.User);
        Assert.True(transcriptIndex >= 0, "expected a persisted tool transcript entry");
        Assert.True(transcriptIndex < promptIndex,
            $"tool_transcript[{transcriptIndex}] must precede the prompt chat entry[{promptIndex}]");
    }

    [Fact]
    public async Task ParallelToolResultsPersistInAssistantCallOrder()
    {
        var fixture = new TwoToolFixture();
        var tool = AIFunctionFactory.Create(fixture.RunAsync, name: "fx");
        var run = await ConversationRun.OpenAsync(new PiAgent(new ParallelToolClient(),
            new CodingTools(Path.GetTempPath()), selectedTools: ["fx"], noTools: true,
            extensionTools: [tool]), new ConversationSession(Path.GetTempPath(), "fixture", null));
        var active = Task.Run(async () =>
        {
            await foreach (var _ in run.RunEventsAsync("GO")) { }
        });

        await fixture.BothStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        // Force completion order (fast, slow) to differ from assistant call order (slow, fast).
        fixture.ReleaseSlow.TrySetResult();
        await active.WaitAsync(TimeSpan.FromSeconds(5));

        var callIds = run.Conversation.ActiveMessages()
            .Where(message => message.Role == ChatRole.Tool)
            .SelectMany(message => message.Contents.OfType<FunctionResultContent>())
            .Select(content => content.CallId)
            .ToList();
        Assert.Equal(["call-slow", "call-fast"], callIds);
    }

    private sealed class FailingFirstClient : IChatClient
    {
        public List<List<ChatMessage>> Requests { get; } = [];

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            Requests.Add(messages.ToList());
            if (Requests.Count == 1)
                throw new InvalidOperationException("fixture provider failure");
            yield return new ChatResponseUpdate(ChatRole.Assistant, "NEXT-OK");
            await Task.CompletedTask;
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    private sealed class HangBeforeContentClient : IChatClient
    {
        public TaskCompletionSource RequestStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Requests { get; private set; }

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            Requests++;
            if (Requests == 1)
            {
                RequestStarted.TrySetResult();
                await Task.Delay(Timeout.Infinite, cancellationToken);
            }
            else
            {
                yield return new ChatResponseUpdate(ChatRole.Assistant, "NEXT-OK");
            }
            await Task.CompletedTask;
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    private sealed class OrderedQueueClient : IChatClient
    {
        public TaskCompletionSource FirstRequestStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseFirstRequest { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            if (messages.Any(message => message.Text == "GO"))
            {
                FirstRequestStarted.TrySetResult();
                await ReleaseFirstRequest.Task.WaitAsync(cancellationToken);
            }
            yield return new ChatResponseUpdate(ChatRole.Assistant, "done");
            await Task.CompletedTask;
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    private sealed class TextClient : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            yield return new ChatResponseUpdate(ChatRole.Assistant, "OK");
            await Task.CompletedTask;
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    private sealed class CancellingToolFixture
    {
        public TaskCompletionSource ToolStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        [Description("Run until the host aborts the operation.")]
        public async Task<string> RunAsync(CancellationToken cancellationToken)
        {
            ToolStarted.TrySetResult();
            try
            {
                await Task.Delay(Timeout.Infinite, cancellationToken);
                return "unreached";
            }
            catch (OperationCanceledException)
            {
                throw new OperationCanceledException("cancel_probe_aborted", cancellationToken);
            }
        }
    }

    private sealed class ToolCallThenHangClient : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            if (messages.SelectMany(message => message.Contents).OfType<FunctionResultContent>().Any())
            {
                yield return new ChatResponseUpdate(ChatRole.Assistant, "done");
            }
            else
            {
                yield return new ChatResponseUpdate(ChatRole.Assistant,
                    [new FunctionCallContent("probe-call", "cancel_probe", new Dictionary<string, object?>())]);
            }
            await Task.CompletedTask;
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    private sealed class TwoToolFixture
    {
        private int _started;
        public TaskCompletionSource BothStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseSlow { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        [Description("Execute a named effect.")]
        public async Task<string> RunAsync(string id, CancellationToken cancellationToken)
        {
            if (id == "slow")
            {
                Interlocked.Increment(ref _started);
                if (_started == 2) BothStarted.TrySetResult();
                await ReleaseSlow.Task.WaitAsync(cancellationToken);
                return "slow done";
            }
            Interlocked.Increment(ref _started);
            if (_started == 2) BothStarted.TrySetResult();
            return "fast done";
        }
    }

    private sealed class ParallelToolClient : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            if (messages.SelectMany(message => message.Contents).OfType<FunctionResultContent>().Any())
            {
                yield return new ChatResponseUpdate(ChatRole.Assistant, "done");
            }
            else
            {
                yield return new ChatResponseUpdate(ChatRole.Assistant,
                [
                    new FunctionCallContent("call-slow", "fx", new Dictionary<string, object?> { ["id"] = "slow" }),
                    new FunctionCallContent("call-fast", "fx", new Dictionary<string, object?> { ["id"] = "fast" })
                ]);
            }
            await Task.CompletedTask;
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }
}
