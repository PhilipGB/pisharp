using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Extensions.AI;
using PiSharp.Cli.Protocols;
using PiSharp.Runtime;
using PiSharp.Runtime.Sessions;

namespace PiSharp.Tests;

public sealed class RpcModeTests
{
    [Fact]
    public async Task AcceptsPromptThenEmitsEventsAndSupportsSubsequentStateQueries()
    {
        var channel = Channel.CreateUnbounded<string>();
        var reader = new CommandReader(channel.Reader);
        using var output = new LockedWriter();
        var session = new ConversationSession(Path.GetTempPath(), "fixture", null);
        var run = await ConversationRun.OpenAsync(new PiAgent(new StubClient(), new CodingTools(Path.GetTempPath())), session);
        var service = new RpcMode(reader, output, run);
        var serving = service.ServeAsync();
        channel.Writer.TryWrite("{\"id\":\"before\",\"type\":\"get_state\"}");
        channel.Writer.TryWrite("{\"id\":\"prompt-1\",\"type\":\"prompt\",\"message\":\"hello\"}");
        await WaitForAsync(output, "agent_settled");
        channel.Writer.TryWrite("{\"id\":\"after\",\"type\":\"get_messages\"}");
        channel.Writer.TryWrite("{\"id\":\"name\",\"type\":\"set_session_name\",\"name\":\"my feature\"}");
        channel.Writer.TryWrite("{\"id\":\"entries\",\"type\":\"get_entries\"}");
        channel.Writer.TryWrite("{\"id\":\"bad-cursor\",\"type\":\"get_entries\",\"since\":\"missing\"}");
        channel.Writer.TryWrite("{\"id\":\"tree\",\"type\":\"get_tree\"}");
        channel.Writer.TryWrite("{\"id\":\"text\",\"type\":\"get_last_assistant_text\"}");
        channel.Writer.TryWrite("{\"id\":\"unknown\",\"type\":\"unsupported\"}");
        channel.Writer.Complete();
        await serving.WaitAsync(TimeSpan.FromSeconds(5));
        var events = output.Lines().Select(line => JsonDocument.Parse(line)).ToArray();
        try
        {
            Assert.Contains(events, e => e.RootElement.GetProperty("type").GetString() == "response" &&
                e.RootElement.GetProperty("id").GetString() == "prompt-1" && e.RootElement.GetProperty("success").GetBoolean());
            Assert.Contains(events, e => e.RootElement.GetProperty("type").GetString() == "response" &&
                e.RootElement.GetProperty("id").GetString() == "unknown" && !e.RootElement.GetProperty("success").GetBoolean());
            Assert.Contains(events, e => e.RootElement.GetProperty("type").GetString() == "response" &&
                e.RootElement.GetProperty("id").GetString() == "after" &&
                e.RootElement.GetProperty("data").GetProperty("messages").GetArrayLength() == 2);
            Assert.Contains(events, e => e.RootElement.GetProperty("type").GetString() == "response" &&
                e.RootElement.GetProperty("id").GetString() == "bad-cursor" &&
                !e.RootElement.GetProperty("success").GetBoolean());
            Assert.Equal("my feature", session.Name);
            Assert.Contains(events, e => e.RootElement.GetProperty("type").GetString() == "response" &&
                e.RootElement.GetProperty("id").GetString() == "entries" &&
                e.RootElement.GetProperty("data").GetProperty("entries").GetArrayLength() == 2);
            Assert.Contains(events, e => e.RootElement.GetProperty("type").GetString() == "response" &&
                e.RootElement.GetProperty("id").GetString() == "tree" &&
                e.RootElement.GetProperty("data").GetProperty("tree").GetArrayLength() == 1);
            Assert.Contains(events, e => e.RootElement.GetProperty("type").GetString() == "response" &&
                e.RootElement.GetProperty("id").GetString() == "text" &&
                e.RootElement.GetProperty("data").GetProperty("text").GetString() == "reply");
            Assert.DoesNotContain(events, e => e.RootElement.GetProperty("type").GetString() == "session");
        }
        finally { foreach (var e in events) e.Dispose(); }
    }

    [Fact]
    public async Task PromptReceivedDuringStreamingQueuesAnotherTurnBeforeSettlement()
    {
        var channel = Channel.CreateUnbounded<string>();
        using var output = new LockedWriter();
        var client = new QueuedClient();
        var session = new ConversationSession(Path.GetTempPath(), "fixture", null);
        var run = await ConversationRun.OpenAsync(new PiAgent(client, new CodingTools(Path.GetTempPath())), session);
        var serving = new RpcMode(new CommandReader(channel.Reader), output, run).ServeAsync();

        channel.Writer.TryWrite("{\"id\":\"first\",\"type\":\"prompt\",\"message\":\"one\"}");
        await client.FirstRequestStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        channel.Writer.TryWrite("{\"id\":\"second\",\"type\":\"prompt\",\"message\":\"two\"}");
        await WaitForAsync(output, "\"id\":\"second\"");
        client.ReleaseFirstRequest.TrySetResult();
        await WaitForAsync(output, "agent_settled");
        channel.Writer.Complete();
        await serving.WaitAsync(TimeSpan.FromSeconds(5));

        var responses = output.Lines().Where(line => line.Contains("\"type\":\"response\"", StringComparison.Ordinal))
            .Select(line => JsonDocument.Parse(line)).ToArray();
        try
        {
            Assert.Equal(2, responses.Length);
            Assert.All(responses, response => Assert.True(response.RootElement.GetProperty("success").GetBoolean()));
            Assert.Contains(output.Lines(), line => line.Contains("prompt_queued", StringComparison.Ordinal));
            Assert.Equal(2, client.Requests);
            Assert.True(client.SecondRequestSawFirstTurn);
            Assert.Equal(2, session.ActiveMessages().Count(message => message.Role == ChatRole.User));
            Assert.Equal(1, output.Lines().Count(line => line.Contains("agent_settled", StringComparison.Ordinal)));
        }
        finally { foreach (var response in responses) response.Dispose(); }
    }

    [Fact]
    public async Task RpcSteeringFollowUpAndClearQueueExposeDistinctPendingInput()
    {
        var channel = Channel.CreateUnbounded<string>();
        using var output = new LockedWriter();
        var client = new OrderedRpcQueueClient();
        var run = await ConversationRun.OpenAsync(new PiAgent(client, new CodingTools(Path.GetTempPath())),
            new ConversationSession(Path.GetTempPath(), "fixture", null));
        var serving = new RpcMode(new CommandReader(channel.Reader), output, run).ServeAsync();

        channel.Writer.TryWrite("{\"id\":\"start\",\"type\":\"prompt\",\"message\":\"initial\"}");
        await client.FirstRequestStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        channel.Writer.TryWrite("{\"id\":\"f0\",\"type\":\"follow_up\",\"message\":\"discard follow\"}");
        channel.Writer.TryWrite("{\"id\":\"s0\",\"type\":\"steer\",\"message\":\"discard steer\"}");
        channel.Writer.TryWrite("{\"id\":\"clear\",\"type\":\"clear_queue\"}");
        await WaitForAsync(output, "\"command\":\"clear_queue\"");
        channel.Writer.TryWrite("{\"id\":\"follow\",\"type\":\"follow_up\",\"message\":\"later\"}");
        channel.Writer.TryWrite("{\"id\":\"steer\",\"type\":\"steer\",\"message\":\"direction\"}");
        await WaitForAsync(output, "\"id\":\"steer\"");
        client.ReleaseFirstRequest.TrySetResult();
        await WaitForAsync(output, "agent_settled");
        channel.Writer.Complete();
        await serving.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(["initial", "direction", "later"], client.LatestUserByRequest);
        using var clear = JsonDocument.Parse(Assert.Single(output.Lines(), line =>
            line.Contains("\"command\":\"clear_queue\"", StringComparison.Ordinal)));
        Assert.Equal(["discard steer"], clear.RootElement.GetProperty("data").GetProperty("steering")
            .EnumerateArray().Select(item => item.GetString()));
        Assert.Equal(["discard follow"], clear.RootElement.GetProperty("data").GetProperty("followUp")
            .EnumerateArray().Select(item => item.GetString()));
        Assert.Contains(output.Lines(), line => line.Contains("queue_update", StringComparison.Ordinal));
    }

    [Fact]
    public async Task RpcHtmlExportIsPrivateIncludesBranchesAndNeverOverwrites()
    {
        var dir = Path.Combine(Path.GetTempPath(), "pisharp-rpc-export-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var session = new ConversationSession(dir, "fixture", null);
            session.Append(new ChatMessage(ChatRole.User, "first"));
            var root = session.Tree.HeadId;
            session.Append(new ChatMessage(ChatRole.Assistant, "inactive <script>"));
            session.Tree.Select(root);
            session.Append(new ChatMessage(ChatRole.Assistant, "active"));
            var run = await ConversationRun.OpenAsync(new PiAgent(new StubClient(), new CodingTools(dir)), session);
            var channel = Channel.CreateUnbounded<string>();
            using var output = new LockedWriter();
            var serving = new RpcMode(new CommandReader(channel.Reader), output, run).ServeAsync();
            channel.Writer.TryWrite("{\"id\":1,\"type\":\"export_html\"}");
            channel.Writer.TryWrite("{\"id\":2,\"type\":\"export_html\"}");
            channel.Writer.TryWrite("{\"id\":3,\"type\":\"export_html\",\"outputPath\":null}");
            var customPath = Path.Combine(dir, "custom.html");
            channel.Writer.TryWrite(JsonSerializer.Serialize(new { id = 4, type = "export_html", outputPath = customPath }));
            channel.Writer.Complete();
            await serving.WaitAsync(TimeSpan.FromSeconds(5));
            var path = Path.Combine(dir, $"pisharp-{session.Id[..12]}.html");
            var html = await File.ReadAllTextAsync(path);
            Assert.Contains("inactive", html);
            Assert.DoesNotContain("<script>", html);
            Assert.Contains("active", html);
            using var first = JsonDocument.Parse(output.Lines()[0]);
            Assert.True(first.RootElement.GetProperty("success").GetBoolean());
            Assert.Equal(path, first.RootElement.GetProperty("data").GetProperty("path").GetString());
            Assert.Equal(4, output.Lines().Length);
            foreach (var line in output.Lines().Skip(1).Take(2))
            {
                using var response = JsonDocument.Parse(line);
                Assert.False(response.RootElement.GetProperty("success").GetBoolean());
            }
            using var custom = JsonDocument.Parse(output.Lines()[3]);
            Assert.True(custom.RootElement.GetProperty("success").GetBoolean());
            Assert.Equal(customPath, custom.RootElement.GetProperty("data").GetProperty("path").GetString());
            Assert.True(File.Exists(customPath));
            if (OperatingSystem.IsLinux()) Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite,
                File.GetUnixFileMode(path) & (UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public async Task SessionStatisticsCountActiveBranchAndExposeUnknownBillingThroughRpc()
    {
        var session = new ConversationSession(Path.GetTempPath(), "fixture", null);
        session.Append(new ChatMessage(ChatRole.User, "question"));
        var root = session.Tree.HeadId;
        session.Append(new ChatMessage(ChatRole.Assistant, [new FunctionCallContent("a", "read",
            new Dictionary<string, object?> { ["path"] = "x" })]));
        session.Append(new ChatMessage(ChatRole.Tool, [new FunctionResultContent("a", "contents")]));
        session.Tree.Select(root);
        session.Append(new ChatMessage(ChatRole.Assistant, "alternate"));
        var stats = SessionStatistics.Calculate(session);
        Assert.Equal(4, stats.Entries);
        Assert.Equal(2, stats.Leaves);
        Assert.Equal(2, stats.ActiveMessages);
        Assert.Equal(1, stats.UserTurns);
        Assert.Equal(0, stats.ToolCalls);
        Assert.Null(stats.BilledTokens);
        Assert.Null(stats.Cost);
        var channel = Channel.CreateUnbounded<string>();
        using var output = new LockedWriter();
        var run = await ConversationRun.OpenAsync(new PiAgent(new StubClient(), new CodingTools(Path.GetTempPath())), session);
        var serving = new RpcMode(new CommandReader(channel.Reader), output, run).ServeAsync();
        channel.Writer.TryWrite("{\"id\":\"stats\",\"type\":\"get_session_stats\"}");
        channel.Writer.Complete();
        await serving.WaitAsync(TimeSpan.FromSeconds(5));
        using var response = JsonDocument.Parse(Assert.Single(output.Lines()));
        var data = response.RootElement.GetProperty("data");
        Assert.Equal(2, data.GetProperty("Leaves").GetInt32());
        Assert.Equal(JsonValueKind.Null, data.GetProperty("BilledTokens").ValueKind);
    }

    [Fact]
    public async Task CommandsAndTemplateExpansionUseSharedResourceCatalog()
    {
        var root = Path.Combine(Path.GetTempPath(), "pisharp-rpc-resources-" + Guid.NewGuid().ToString("N"));
        var prompts = Path.Combine(root, "agent", "prompts");
        Directory.CreateDirectory(prompts);
        try
        {
            await File.WriteAllTextAsync(Path.Combine(prompts, "review.md"), "Check $1");
            var resources = await PiSharp.Runtime.Resources.ResourceCatalog.LoadAsync(root, Path.Combine(root, "agent"), false);
            var channel = Channel.CreateUnbounded<string>();
            using var output = new LockedWriter();
            var session = new ConversationSession(root, "fixture", null);
            var run = await ConversationRun.OpenAsync(new PiAgent(new StubClient(), new CodingTools(root)), session);
            var service = new RpcMode(new CommandReader(channel.Reader), output, run, resources: resources);
            var serving = service.ServeAsync();
            channel.Writer.TryWrite("{\"id\":1,\"type\":\"get_commands\"}");
            channel.Writer.TryWrite("{\"id\":2,\"type\":\"prompt\",\"message\":\"/review concurrency\"}");
            await WaitForAsync(output, "agent_settled");
            channel.Writer.Complete();
            await serving.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal("Check concurrency", session.ActiveMessages().First().Text);
            Assert.Contains(output.Lines(), line => line.Contains("\"name\":\"review\"", StringComparison.Ordinal));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task ModelDiscoveryIsAvailableThroughRpcWithoutModelCall()
    {
        var channel = Channel.CreateUnbounded<string>();
        using var output = new LockedWriter();
        var run = await ConversationRun.OpenAsync(new PiAgent(new StubClient(), new CodingTools(Path.GetTempPath())),
            new ConversationSession(Path.GetTempPath(), "fixture", null));
        var serving = new RpcMode(new CommandReader(channel.Reader), output, run,
            discoverModels: _ => Task.FromResult<IReadOnlyList<PiSharp.Runtime.Providers.ModelDescriptor>>(
                [new("model-one", "fixture", 4096, "loaded")])).ServeAsync();
        channel.Writer.TryWrite("{\"id\":5,\"type\":\"get_available_models\"}");
        channel.Writer.Complete();
        await serving.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Contains(output.Lines(), line => line.Contains("\"command\":\"get_available_models\"", StringComparison.Ordinal) &&
            line.Contains("\"Id\":\"model-one\"", StringComparison.Ordinal));
    }

    [Fact]
    public async Task CompactCommandRebuildsContextButNotRawMessages()
    {
        var channel = Channel.CreateUnbounded<string>();
        using var output = new LockedWriter();
        var session = new ConversationSession(Path.GetTempPath(), "fixture", null);
        session.Append(new ChatMessage(ChatRole.User, "one"));
        session.Append(new ChatMessage(ChatRole.Assistant, "answer"));
        session.Append(new ChatMessage(ChatRole.User, "two"));
        var run = await ConversationRun.OpenAsync(new PiAgent(new StubClient(), new CodingTools(Path.GetTempPath())), session);
        var serving = new RpcMode(new CommandReader(channel.Reader), output, run).ServeAsync();
        channel.Writer.TryWrite("{\"id\":9,\"type\":\"compact\",\"instructions\":\"retain decisions\"}");
        await WaitForAsync(output, "\"compacted\":true");
        channel.Writer.Complete();
        await serving.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(3, session.ActiveMessages().Count);
        Assert.Equal(2, session.ContextMessages().Count);
        Assert.Contains(output.Lines(), line => line.Contains("\"command\":\"compact\"", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AbortSettlesAndPreservesInterruptionMarker()
    {
        var channel = Channel.CreateUnbounded<string>();
        using var output = new LockedWriter();
        var session = new ConversationSession(Path.GetTempPath(), "fixture", null);
        var run = await ConversationRun.OpenAsync(new PiAgent(new BlockingClient(), new CodingTools(Path.GetTempPath())), session);
        var serving = new RpcMode(new CommandReader(channel.Reader), output, run).ServeAsync();
        channel.Writer.TryWrite("{\"id\":1,\"type\":\"prompt\",\"message\":\"wait\"}");
        await WaitForAsync(output, "model_request_started");
        channel.Writer.TryWrite("{\"id\":2,\"type\":\"abort\"}");
        await WaitForAsync(output, "agent_settled");
        channel.Writer.Complete();
        await serving.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Contains(session.Tree.Entries, entry => entry.Type == "interrupted");
        Assert.Contains(output.Lines(), line => line.Contains("\"command\":\"abort\",\"success\":true", StringComparison.Ordinal));
    }

    private static async Task WaitForAsync(LockedWriter output, string fragment)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!output.Lines().Any(line => line.Contains(fragment, StringComparison.Ordinal)))
            await Task.Delay(10, timeout.Token);
    }

    private sealed class CommandReader(ChannelReader<string> channel) : TextReader
    {
        public override async ValueTask<string?> ReadLineAsync(CancellationToken cancellationToken)
        {
            try { return await channel.ReadAsync(cancellationToken); }
            catch (ChannelClosedException) { return null; }
        }
    }

    private sealed class LockedWriter : StringWriter
    {
        private readonly object _gate = new();
        public override Task WriteLineAsync(string? value)
        {
            lock (_gate) WriteLine(value);
            return Task.CompletedTask;
        }
        public string[] Lines() { lock (_gate) return ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries); }
    }

    private sealed class OrderedRpcQueueClient : IChatClient
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
            yield return new ChatResponseUpdate(ChatRole.Assistant, "reply");
        }
        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    private sealed class QueuedClient : IChatClient
    {
        public int Requests { get; private set; }
        public bool SecondRequestSawFirstTurn { get; private set; }
        public TaskCompletionSource FirstRequestStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseFirstRequest { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            Requests++;
            if (Requests == 1)
            {
                FirstRequestStarted.TrySetResult();
                await ReleaseFirstRequest.Task.WaitAsync(cancellationToken);
                yield return new ChatResponseUpdate(ChatRole.Assistant, "first reply");
                yield break;
            }
            var snapshot = messages.ToArray();
            SecondRequestSawFirstTurn = snapshot.Any(message => message.Role == ChatRole.User && message.Text == "one") &&
                snapshot.Any(message => message.Role == ChatRole.Assistant && message.Text == "first reply") &&
                snapshot.Any(message => message.Role == ChatRole.User && message.Text == "two");
            yield return new ChatResponseUpdate(ChatRole.Assistant, "second reply");
        }
        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    private sealed class StubClient : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default) => Task.FromResult(new ChatResponse([new ChatMessage(ChatRole.Assistant, "summary")]));
        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        { yield return new ChatResponseUpdate(ChatRole.Assistant, "reply"); await Task.CompletedTask; }
        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    private sealed class BlockingClient : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        { await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken); yield break; }
        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }
}
