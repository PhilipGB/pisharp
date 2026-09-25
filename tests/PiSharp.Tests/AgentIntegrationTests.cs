using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;
using PiSharp.Runtime;
using PiSharp.Runtime.Sessions;

namespace PiSharp.Tests;

public sealed class AgentIntegrationTests
{
    [Fact]
    public async Task BashToolReceivesCurrentConversationAndRunMetadata()
    {
        var cwd = Path.Combine(Path.GetTempPath(), "pisharp-bash-session-env-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(cwd);
        try
        {
            var conversation = new ConversationSession(cwd, "fixture-model", null, "fixture-provider");
            var sessionFile = Path.Combine(cwd, "fixture.session.json");
            var command = "printf '%s|%s|%s|%s|%s' \"$PI_SESSION_ID\" \"$PI_SESSION_FILE\" \"$PI_PROVIDER\" \"$PI_MODEL\" \"$PI_REASONING_LEVEL\"";
            var client = new BashCommandClient(command);
            var run = await ConversationRun.OpenAsync(new PiAgent(client, new CodingTools(cwd)), conversation,
                sessionFile: sessionFile, provider: "fixture-provider", reasoningLevel: "high");
            await foreach (var _ in run.RunEventsAsync("inspect session environment")) { }

            Assert.Equal($"{conversation.Id}|{sessionFile}|fixture-provider|fixture-model|high", client.ToolResult);
        }
        finally { Directory.Delete(cwd, recursive: true); }
    }

    [Fact]
    public async Task ModelToolCallWritesFileAndContinuesStreaming()
    {
        var dir = Path.Combine(Path.GetTempPath(), "pisharp-agent-" + Guid.NewGuid());
        Directory.CreateDirectory(dir);
        try
        {
            var provider = new ScriptedClient();
            var agent = new PiAgent(provider, new CodingTools(dir));
            var session = await agent.CreateSessionAsync();
            var text = "";
            var contents = new List<AIContent>();
            await foreach (var update in agent.RunStreamingAsync("Create hello.txt", session))
            {
                text += update.Text;
                contents.AddRange(update.Contents ?? []);
            }
            Assert.Equal("done", text);
            Assert.Contains(contents, c => c is FunctionCallContent);
            Assert.Contains(contents, c => c is FunctionResultContent);
            Assert.Equal("hello", await File.ReadAllTextAsync(Path.Combine(dir, "hello.txt")));
            Assert.Equal(2, provider.Requests);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public async Task TwoToolCallsRetainResultsAndContinueInOneModelTurn()
    {
        var cwd = Path.Combine(Path.GetTempPath(), "pisharp-multiple-tools-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(cwd);
        try
        {
            var client = new TwoToolsClient();
            var conversation = new ConversationSession(cwd, "fixture", null);
            var run = await ConversationRun.OpenAsync(new PiAgent(client, new CodingTools(cwd)), conversation);
            var events = new List<AgentLifecycleEvent>();
            await foreach (var item in run.RunEventsAsync("write both")) events.Add(item);
            Assert.Equal("one", await File.ReadAllTextAsync(Path.Combine(cwd, "one.txt")));
            Assert.Equal("two", await File.ReadAllTextAsync(Path.Combine(cwd, "two.txt")));
            Assert.Equal(["a", "b"], client.SeenResults!.Order(StringComparer.Ordinal));
            Assert.Equal(2, events.Count(item => item.Type == "tool_execution_started"));
            Assert.Equal(2, events.Count(item => item.Type == "tool_execution_finished"));
            Assert.Equal("agent_settled", events[^1].Type);
            Assert.Equal(2, conversation.ActiveMessages().SelectMany(message => message.Contents).OfType<FunctionResultContent>().Count());
        }
        finally { Directory.Delete(cwd, recursive: true); }
    }

    private sealed class TwoToolsClient : IChatClient
    {
        public string[]? SeenResults { get; private set; }
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            var results = messages.SelectMany(message => message.Contents).OfType<FunctionResultContent>().ToArray();
            if (results.Length == 0)
                yield return new ChatResponseUpdate(ChatRole.Assistant,
                [
                    new FunctionCallContent("a", "write", new Dictionary<string, object?> { ["path"] = "one.txt", ["content"] = "one" }),
                    new FunctionCallContent("b", "write", new Dictionary<string, object?> { ["path"] = "two.txt", ["content"] = "two" })
                ]);
            else
            {
                SeenResults = results.Select(result => result.CallId).ToArray();
                yield return new ChatResponseUpdate(ChatRole.Assistant, "done");
            }
            await Task.CompletedTask;
        }
        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    [Fact]
    public async Task FailedShellIsReportedAsToolFailureAndModelCanRecover()
    {
        var cwd = Path.Combine(Path.GetTempPath(), "pisharp-tool-failure-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(cwd);
        try
        {
            var client = new FailureClient();
            var agent = new PiAgent(client, new CodingTools(cwd));
            var conversation = new ConversationSession(cwd, "fixture", null);
            var run = await ConversationRun.OpenAsync(agent, conversation);
            var text = "";
            await foreach (var update in run.RunStreamingAsync("fail shell")) text += update.Text;
            Assert.Equal("recovered", text);
            Assert.Equal(2, client.Requests);
            Assert.NotNull(client.Result);
            Assert.NotNull(client.Result.Exception);
            Assert.Contains("code 7", client.Result.Exception.Message);
            Assert.Contains("output-before-failure", client.Result.Exception.Message);
            var store = new ConversationStore(cwd, Path.Combine(cwd, "sessions"));
            var path = store.NewPath(conversation);
            await store.SaveAsync(conversation, path);
            var reloaded = await store.LoadAsync(path);
            Assert.Contains(reloaded.ActiveMessages().SelectMany(m => m.Contents), c =>
                c is FunctionResultContent { CallId: "fail-1", Exception: not null });
            var resumed = new FailureContinuationClient();
            var continuation = await ConversationRun.OpenAsync(new PiAgent(resumed, new CodingTools(cwd)), reloaded);
            await foreach (var _ in continuation.RunStreamingAsync("continue after failure")) { }
            Assert.True(resumed.SawFailure);
        }
        finally { Directory.Delete(cwd, recursive: true); }
    }

    [Fact]
    public async Task BashEmitsOutputUpdatesBeforeTheShellCompletes()
    {
        if (!OperatingSystem.IsLinux()) return;
        var cwd = Path.Combine(Path.GetTempPath(), "pisharp-bash-live-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(cwd);
        var gate = Path.Combine(cwd, "release");
        var command = $"printf 'before-release\\n'; while [ ! -e {ProcessTestHelpers.ShellQuote(gate)} ]; do :; done; printf 'after-release\\n'";
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        Task? collecting = null;
        try
        {
            var client = new BashCommandClient(command);
            var run = await ConversationRun.OpenAsync(new PiAgent(client, new CodingTools(cwd)),
                new ConversationSession(cwd, "fixture", null));
            var events = new List<AgentLifecycleEvent>();
            var firstOutput = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            collecting = Task.Run(async () =>
            {
                await foreach (var item in run.RunEventsAsync("run a shell command", cancellation.Token))
                {
                    events.Add(item);
                    if (item.Type == "tool_execution_update" && item.Text?.Contains("before-release", StringComparison.Ordinal) == true)
                        firstOutput.TrySetResult();
                }
            });

            await firstOutput.Task.WaitAsync(TimeSpan.FromSeconds(5), cancellation.Token);
            Assert.False(collecting.IsCompleted);
            File.WriteAllText(gate, "release");
            await collecting.WaitAsync(TimeSpan.FromSeconds(5), cancellation.Token);

            var startIndex = events.FindIndex(item => item.Type == "tool_execution_started" && item.Tool == "bash");
            var liveIndex = events.FindIndex(item => item.Type == "tool_execution_update" && item.Text?.Contains("before-release", StringComparison.Ordinal) == true);
            var finishIndex = events.FindIndex(item => item.Type == "tool_execution_finished" && item.Tool == "bash");
            Assert.True(startIndex >= 0 && liveIndex > startIndex && finishIndex > liveIndex);
            Assert.Contains("before-release", events[finishIndex].Text);
            Assert.Contains("after-release", events[finishIndex].Text);
        }
        finally
        {
            File.WriteAllText(gate, "release");
            cancellation.Cancel();
            if (collecting is not null)
            {
                try { await collecting.WaitAsync(TimeSpan.FromSeconds(5)); }
                catch (Exception error) when (error is OperationCanceledException or TimeoutException) { }
            }
            Directory.Delete(cwd, recursive: true);
        }
    }

    private sealed class FailureContinuationClient : IChatClient
    {
        public bool SawFailure { get; private set; }
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            SawFailure = messages.SelectMany(m => m.Contents).OfType<FunctionResultContent>()
                .Any(result => result.CallId == "fail-1" && result.Exception is not null);
            yield return new ChatResponseUpdate(ChatRole.Assistant, "resumed");
            await Task.CompletedTask;
        }
        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    private sealed class BashCommandClient(string command) : IChatClient
    {
        private int _requests;
        public string? ToolResult { get; private set; }
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            if (Interlocked.Increment(ref _requests) == 1)
                yield return new ChatResponseUpdate(ChatRole.Assistant,
                    [new FunctionCallContent("bash-live", "bash", new Dictionary<string, object?> { ["command"] = command })]);
            else
            {
                ToolResult = messages.SelectMany(message => message.Contents).OfType<FunctionResultContent>().Single().Result?.ToString();
                yield return new ChatResponseUpdate(ChatRole.Assistant, "done");
            }
            await Task.CompletedTask;
        }
        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    private sealed class FailureClient : IChatClient
    {
        public int Requests { get; private set; }
        public FunctionResultContent? Result { get; private set; }
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            if (++Requests == 1)
                yield return new ChatResponseUpdate(ChatRole.Assistant,
                    [new FunctionCallContent("fail-1", "bash", new Dictionary<string, object?> { ["command"] = "printf output-before-failure; exit 7" })]);
            else
            {
                Result = messages.SelectMany(m => m.Contents).OfType<FunctionResultContent>().Single();
                yield return new ChatResponseUpdate(ChatRole.Assistant, "recovered");
            }
            await Task.CompletedTask;
        }
        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    private sealed class ScriptedClient : IChatClient
    {
        public int Requests { get; private set; }
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("The integration test requires streaming.");

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            if (++Requests == 1)
            {
                Assert.Contains(options?.Tools ?? [], t => t is AIFunction f && f.Name == "write");
                yield return new ChatResponseUpdate(ChatRole.Assistant,
                    [new FunctionCallContent("call-1", "write", new Dictionary<string, object?> { ["path"] = "hello.txt", ["content"] = "hello" })]);
            }
            else
            {
                Assert.Contains(messages.SelectMany(m => m.Contents), c => c is FunctionResultContent);
                yield return new ChatResponseUpdate(ChatRole.Assistant, "done");
            }
            await Task.CompletedTask;
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }
}
