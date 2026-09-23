using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;
using PiSharp.Cli;
using PiSharp.Runtime;
using PiSharp.Runtime.Sessions;

namespace PiSharp.Tests;

public sealed class AgentIntegrationTests
{
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
        }
        finally { Directory.Delete(cwd, recursive: true); }
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
