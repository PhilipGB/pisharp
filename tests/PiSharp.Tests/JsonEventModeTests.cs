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
            Assert.Equal("agent_settled", records[^1].RootElement.GetProperty("data").GetProperty("Type").GetString());
            Assert.Single(records, record => record.RootElement.GetProperty("type").GetString() == "event" &&
                record.RootElement.GetProperty("data").GetProperty("Type").GetString() == "model_text_delta");
            Assert.Contains(records.Skip(1), record => record.RootElement.GetProperty("data").GetProperty("Type").GetString() == "model_text_delta" &&
                record.RootElement.GetProperty("data").GetProperty("Text").GetString() == "hello back");
            Assert.Contains(records.Skip(1), record => record.RootElement.GetProperty("data").GetProperty("Type").GetString() == "prompt_accepted" &&
                record.RootElement.GetProperty("data").GetProperty("Text").GetString() == "hello\u2028world");
        }
        finally { foreach (var record in records) record.Dispose(); }
    }

    [Fact]
    public async Task FailedToolIsMarkedErrorOnWireAndInPersistedSession()
    {
        var root = Path.Combine(Path.GetTempPath(), "pisharp-json-tool-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            using var output = new StringWriter();
            var conversation = new ConversationSession(root, "fixture", null);
            var run = await ConversationRun.OpenAsync(new PiAgent(new ToolFixture(), new CodingTools(root)), conversation);
            Assert.True(await new JsonEventMode(output).RunAsync(run, "run failing shell"));
            var records = output.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Select(line => JsonDocument.Parse(line)).ToArray();
            try
            {
                Assert.Contains(records, record => record.RootElement.GetProperty("data").GetProperty("Type").GetString() == "tool_execution_finished" &&
                    record.RootElement.GetProperty("data").GetProperty("IsError").GetBoolean());
                Assert.Contains(conversation.ActiveMessages().SelectMany(message => message.Contents), content =>
                    content is FunctionResultContent { Exception: not null });
            }
            finally { foreach (var record in records) record.Dispose(); }
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task LiveBashOutputIsSerializedAsToolExecutionUpdateEvents()
    {
        var root = Path.Combine(Path.GetTempPath(), "pisharp-json-bash-live-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            using var output = new StringWriter();
            var conversation = new ConversationSession(root, "fixture", null);
            var run = await ConversationRun.OpenAsync(new PiAgent(new LiveToolFixture(), new CodingTools(root)), conversation);
            Assert.True(await new JsonEventMode(output).RunAsync(run, "run a shell command"));
            var records = output.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Select(line => JsonDocument.Parse(line)).ToArray();
            try
            {
                var live = Assert.Single(records, record => record.RootElement.GetProperty("data").GetProperty("Type").GetString() == "tool_execution_update");
                Assert.Equal("bash", live.RootElement.GetProperty("data").GetProperty("Tool").GetString());
                Assert.Contains("json-live-output", live.RootElement.GetProperty("data").GetProperty("Text").GetString());
                var startIndex = Array.FindIndex(records, record => record.RootElement.GetProperty("data").GetProperty("Type").GetString() == "tool_execution_started");
                var updateIndex = Array.IndexOf(records, live);
                var finishIndex = Array.FindIndex(records, record => record.RootElement.GetProperty("data").GetProperty("Type").GetString() == "tool_execution_finished");
                Assert.True(startIndex < updateIndex && updateIndex < finishIndex);
            }
            finally { foreach (var record in records) record.Dispose(); }
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private sealed class ToolFixture : IChatClient
    {
        private int _calls;
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            if (++_calls == 1)
                yield return new ChatResponseUpdate(ChatRole.Assistant,
                    [new FunctionCallContent("failure-1", "bash", new Dictionary<string, object?> { ["command"] = "exit 7" })]);
            else yield return new ChatResponseUpdate(ChatRole.Assistant, "recovered");
            await Task.CompletedTask;
        }
        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    private sealed class LiveToolFixture : IChatClient
    {
        private int _calls;
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            if (++_calls == 1)
                yield return new ChatResponseUpdate(ChatRole.Assistant,
                    [new FunctionCallContent("live-1", "bash", new Dictionary<string, object?> { ["command"] = "printf 'json-live-output\\n'" })]);
            else yield return new ChatResponseUpdate(ChatRole.Assistant, "done");
            await Task.CompletedTask;
        }
        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
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
