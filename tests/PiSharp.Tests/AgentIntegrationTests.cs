using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;
using PiSharp.Cli;

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
