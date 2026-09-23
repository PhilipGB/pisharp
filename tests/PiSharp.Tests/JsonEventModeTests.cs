using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Extensions.AI;
using PiSharp.Cli;
using PiSharp.Cli.Protocols;
using PiSharp.Runtime;
using PiSharp.Runtime.Sessions;

namespace PiSharp.Tests;

public sealed class JsonEventModeTests
{
    [Fact]
    public async Task EventStreamIsJsonlWithSingleHeaderAndSettledTerminator()
    {
        using var output = new StringWriter();
        var root = Path.GetTempPath();
        var conversation = new ConversationSession(root, "fixture", null);
        var run = await ConversationRun.OpenAsync(new PiAgent(new ChatFixture(), new CodingTools(root)), conversation);
        var json = new JsonEventMode(output);
        await json.HeaderAsync(conversation);
        Assert.True(await json.RunAsync(run, "hello\u2028world"));
        var records = output.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(line => JsonDocument.Parse(line)).ToArray();
        try
        {
            Assert.Equal("session", records[0].RootElement.GetProperty("type").GetString());
            Assert.Equal("pisharp", records[0].RootElement.GetProperty("format").GetString());
            Assert.Equal("agent_settled", records[^1].RootElement.GetProperty("type").GetString());
            Assert.Single(records, record => record.RootElement.GetProperty("type").GetString() == "message_update");
            Assert.Contains(records, record => record.RootElement.GetProperty("type").GetString() == "turn_end" &&
                record.RootElement.GetProperty("message").GetProperty("content").GetString() == "hello back");
            Assert.Contains(records, record => record.RootElement.GetProperty("type").GetString() == "message_end" &&
                record.RootElement.GetProperty("message").GetProperty("role").GetString() == "user" &&
                record.RootElement.GetProperty("message").GetProperty("content").GetString() == "hello\u2028world");
        }
        finally { foreach (var record in records) record.Dispose(); }
    }

    [Fact]
    public void CliRejectsInvalidModeCombinations()
    {
        Assert.Equal("json", CliArguments.Parse(["--mode", "json", "hello"]).Mode);
        Assert.Equal("rpc", CliArguments.Parse(["--mode", "rpc"]).Mode);
        Assert.Throws<ArgumentException>(() => CliArguments.Parse(["--mode", "rpc", "hello"]));
        Assert.Throws<ArgumentException>(() => CliArguments.Parse(["--mode", "json", "--print"]));
    }

    private sealed class ChatFixture : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            yield return new ChatResponseUpdate(ChatRole.Assistant, "hello back");
            await Task.CompletedTask;
        }
        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }
}
