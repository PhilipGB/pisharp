using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Extensions.AI;
using PiSharp.Cli.Protocols;
using PiSharp.Extensions.Abstractions;
using PiSharp.Runtime;
using PiSharp.Runtime.Extensions;
using PiSharp.Runtime.Resources;
using PiSharp.Runtime.Sessions;
using PiSharp.Runtime.Tools;

using static PiSharp.Tests.RpcTestInputOutput;

namespace PiSharp.Tests;

public sealed class RpcInputRoutingTests
{
    [Fact]
    public async Task HandledIdlePromptSucceedsBeforePreflightAndRun()
    {
        var channel = Channel.CreateUnbounded<string>();
        using var output = new LockedWriter();
        var registration = new ExtensionRegistration();
        var adapter = new NativeExtensionRegistrationAdapter(registration);
        PiSharpInputEvent? received = null;
        adapter.AddInputHandler((eventData, _, _) =>
        {
            received = eventData;
            return ValueTask.FromResult<PiSharpInputResult?>(PiSharpInputResult.Handled);
        });
        adapter.Close();
        var session = new ConversationSession(Path.GetTempPath(), "fixture", null);
        var run = await ConversationRun.OpenAsync(new PiAgent(new RecordingClient(),
            new CodingTools(Path.GetTempPath()), liveExtensionRegistration: registration), session);
        var preflightCalls = 0;
        var serving = new RpcMode(new CommandReader(channel.Reader), output, run, extensions: registration,
            promptPreflight: () => { preflightCalls++; return "provider is not authenticated"; }).ServeAsync();

        channel.Writer.TryWrite("{\"id\":\"handled\",\"type\":\"prompt\",\"message\":\"raw input\"}");
        await WaitForAsync(output, "\"id\":\"handled\"");
        channel.Writer.Complete();
        await serving.WaitAsync(TimeSpan.FromSeconds(5));

        using var response = JsonDocument.Parse(Assert.Single(output.Lines(), line =>
            line.Contains("\"id\":\"handled\"", StringComparison.Ordinal)));
        Assert.True(response.RootElement.GetProperty("success").GetBoolean());
        Assert.Equal("handled", response.RootElement.GetProperty("data").GetProperty("disposition").GetString());
        Assert.Equal(0, preflightCalls);
        Assert.Empty(session.ActiveMessages());
        Assert.DoesNotContain(output.Lines(), line => line.Contains("\"type\":\"agent_start\"", StringComparison.Ordinal));
        Assert.NotNull(received);
        Assert.Equal("raw input", received.Text);
        Assert.Equal(PiSharpInputSource.Rpc, received.Source);
        Assert.Null(received.StreamingBehavior);
    }

    [Fact]
    public async Task RawInputTransformsBeforeResourceExpansionAndTransformedCommandIsNotRedispatched()
    {
        var root = Path.Combine(Path.GetTempPath(), "pisharp-rpc-input-order-" + Guid.NewGuid().ToString("N"));
        var agentDirectory = Path.Combine(root, "agent");
        var prompts = Path.Combine(agentDirectory, "prompts");
        Directory.CreateDirectory(prompts);
        try
        {
            await File.WriteAllTextAsync(Path.Combine(prompts, "review.md"), "Review $1");
            var resources = await ResourceCatalog.LoadAsync(root, agentDirectory, false);
            var channel = Channel.CreateUnbounded<string>();
            using var output = new LockedWriter();
            var registration = new ExtensionRegistration();
            var adapter = new NativeExtensionRegistrationAdapter(registration);
            var commandRuns = 0;
            var seen = new ConcurrentQueue<PiSharpInputEvent>();
            adapter.AddCommand("target", (_, _) =>
            {
                Interlocked.Increment(ref commandRuns);
                return Task.FromResult("command ran");
            });
            adapter.AddInputHandler((eventData, _, _) =>
            {
                seen.Enqueue(eventData);
                var transformed = eventData.Text switch
                {
                    "expand" => "/review concurrency",
                    "slash" => "/target transformed",
                    _ => null
                };
                return ValueTask.FromResult<PiSharpInputResult?>(transformed is null
                    ? PiSharpInputResult.Continue : PiSharpInputResult.Transform(transformed,
                        [new DataContent(new byte[] { 1, 2, 3 }, "image/png")]));
            });
            adapter.Close();
            var client = new RecordingClient();
            var session = new ConversationSession(root, "fixture", null);
            var run = await ConversationRun.OpenAsync(new PiAgent(client, new CodingTools(root),
                liveExtensionRegistration: registration), session);
            var serving = new RpcMode(new CommandReader(channel.Reader), output, run,
                resources: resources, extensions: registration).ServeAsync();

            channel.Writer.TryWrite("{\"id\":\"command\",\"type\":\"prompt\",\"message\":\"/target direct\"}");
            await WaitForAsync(output, "\"id\":\"command\"");
            channel.Writer.TryWrite("{\"id\":\"expand\",\"type\":\"prompt\",\"message\":\"expand\"}");
            await WaitForAsync(output, "agent_settled");
            channel.Writer.TryWrite("{\"id\":\"slash\",\"type\":\"prompt\",\"message\":\"slash\"}");
            await WaitForSettlementCountAsync(output, 2);
            channel.Writer.Complete();
            await serving.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.Equal(1, commandRuns);
            Assert.Equal(["Review concurrency", "/target transformed"], client.UserTexts);
            Assert.All(client.UserContentsByRequest, contents =>
                Assert.Contains(contents, content => content is DataContent { MediaType: "image/png" }));
            Assert.Equal(["expand", "slash"], seen.Select(item => item.Text));
            Assert.All(seen, item =>
            {
                Assert.Equal(PiSharpInputSource.Rpc, item.Source);
                Assert.Null(item.StreamingBehavior);
            });
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task ActiveRpcPromptSteerFollowUpAndExtensionMessagesUseTheInputDispatcherAndExistingQueues()
    {
        var channel = Channel.CreateUnbounded<string>();
        using var output = new LockedWriter();
        var registration = new ExtensionRegistration();
        var adapter = new NativeExtensionRegistrationAdapter(registration);
        var seen = new ConcurrentQueue<PiSharpInputEvent>();
        adapter.AddCommand("send", async (_, context, cancellationToken) =>
        {
            await context.SendUserMessageAsync("extension raw", cancellationToken);
            return "sent";
        });
        adapter.AddInputHandler((eventData, _, _) =>
        {
            seen.Enqueue(eventData);
            if (eventData.Text == "consume")
                return ValueTask.FromResult<PiSharpInputResult?>(PiSharpInputResult.Handled);
            var transformed = eventData.Text switch
            {
                "prompt steer" => "prompt steered",
                "explicit steer" => "explicit steered",
                "explicit follow" => "explicit followed",
                "extension raw" => "extension steered",
                _ => null
            };
            return ValueTask.FromResult<PiSharpInputResult?>(transformed is null
                ? PiSharpInputResult.Continue : PiSharpInputResult.Transform(transformed,
                    [new DataContent(new byte[] { 4, 5, 6 }, "image/png")]));
        });
        adapter.Close();
        var client = new BlockingRecordingClient();
        var session = new ConversationSession(Path.GetTempPath(), "fixture", null);
        var run = await ConversationRun.OpenAsync(new PiAgent(client, new CodingTools(Path.GetTempPath()),
            liveExtensionRegistration: registration), session);
        var serving = new RpcMode(new CommandReader(channel.Reader), output, run, extensions: registration).ServeAsync();

        channel.Writer.TryWrite("{\"id\":\"first\",\"type\":\"prompt\",\"message\":\"first\"}");
        await client.FirstRequestStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        channel.Writer.TryWrite("{\"id\":\"consumed\",\"type\":\"prompt\",\"message\":\"consume\",\"streamingBehavior\":\"steer\"}");
        await WaitForAsync(output, "\"id\":\"consumed\"");
        channel.Writer.TryWrite("{\"id\":\"prompt-steer\",\"type\":\"prompt\",\"message\":\"prompt steer\",\"streamingBehavior\":\"steer\"}");
        await WaitForAsync(output, "\"id\":\"prompt-steer\"");
        channel.Writer.TryWrite("{\"id\":\"steer\",\"type\":\"steer\",\"message\":\"explicit steer\"}");
        await WaitForAsync(output, "\"id\":\"steer\"");
        channel.Writer.TryWrite("{\"id\":\"follow\",\"type\":\"follow_up\",\"message\":\"explicit follow\"}");
        await WaitForAsync(output, "\"id\":\"follow\"");
        channel.Writer.TryWrite("{\"id\":\"extension\",\"type\":\"prompt\",\"message\":\"/send\"}");
        await WaitForAsync(output, "\"id\":\"extension\"");

        Assert.Equal(["prompt steered", "explicit steered", "extension steered"], run.GetPendingPrompts().Steering);
        Assert.Equal(["explicit followed"], run.GetPendingPrompts().FollowUp);
        Assert.Equal(6, seen.Count);
        Assert.Contains(seen, item => item.Text == "consume" && item.Source == PiSharpInputSource.Rpc &&
            item.StreamingBehavior == PiSharpInputStreamingBehavior.Steer);
        Assert.Contains(seen, item => item.Text == "explicit follow" && item.Source == PiSharpInputSource.Rpc &&
            item.StreamingBehavior == PiSharpInputStreamingBehavior.FollowUp);
        Assert.Contains(seen, item => item.Text == "extension raw" && item.Source == PiSharpInputSource.Extension &&
            item.StreamingBehavior == PiSharpInputStreamingBehavior.Steer);
        using (var handled = JsonDocument.Parse(Assert.Single(output.Lines(), line =>
            line.Contains("\"id\":\"consumed\"", StringComparison.Ordinal))))
            Assert.Equal("handled", handled.RootElement.GetProperty("data").GetProperty("disposition").GetString());

        client.ReleaseFirstRequest.TrySetResult();
        await WaitForAsync(output, "agent_settled");
        channel.Writer.Complete();
        await serving.WaitAsync(TimeSpan.FromSeconds(5));

        var queuedMessages = client.RequestMessages.SelectMany(messages => messages)
            .Where(message => message.Role == ChatRole.User).ToArray();
        foreach (var text in new[] { "prompt steered", "explicit steered", "explicit followed", "extension steered" })
        {
            var messages = queuedMessages.Where(candidate =>
                candidate.Contents.OfType<TextContent>().Any(content => content.Text == text));
            Assert.NotEmpty(messages);
            Assert.All(messages, message =>
                Assert.Contains(message.Contents, content => content is DataContent { MediaType: "image/png" }));
        }
    }

    private sealed class RecordingClient : IChatClient
    {
        public List<string> UserTexts { get; } = [];
        public List<AIContent[]> UserContentsByRequest { get; } = [];

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            var user = messages.Last(message => message.Role == ChatRole.User);
            UserTexts.Add(user.Text ?? "");
            UserContentsByRequest.Add(user.Contents.ToArray());
            yield return new ChatResponseUpdate(ChatRole.Assistant, "reply");
            await Task.CompletedTask;
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    private static async Task WaitForSettlementCountAsync(LockedWriter output, int expected)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (output.Lines().Count(line => line.Contains("agent_settled", StringComparison.Ordinal)) < expected)
            await Task.Delay(10, timeout.Token);
    }

    private sealed class BlockingRecordingClient : IChatClient
    {
        private int _requests;
        public ConcurrentQueue<ChatMessage[]> RequestMessages { get; } = new();
        public TaskCompletionSource FirstRequestStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseFirstRequest { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            RequestMessages.Enqueue(messages.ToArray());
            if (Interlocked.Increment(ref _requests) == 1)
            {
                FirstRequestStarted.TrySetResult();
                await ReleaseFirstRequest.Task.WaitAsync(cancellationToken);
            }
            yield return new ChatResponseUpdate(ChatRole.Assistant, "reply");
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }
}
