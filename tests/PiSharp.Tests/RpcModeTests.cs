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
    public async Task AbortSettlesAndPreservesInterruptionMarker()
    {
        var channel = Channel.CreateUnbounded<string>();
        using var output = new LockedWriter();
        var session = new ConversationSession(Path.GetTempPath(), "fixture", null);
        var run = await ConversationRun.OpenAsync(new PiAgent(new BlockingClient(), new CodingTools(Path.GetTempPath())), session);
        var serving = new RpcMode(new CommandReader(channel.Reader), output, run).ServeAsync();
        channel.Writer.TryWrite("{\"id\":1,\"type\":\"prompt\",\"message\":\"wait\"}");
        await WaitForAsync(output, "agent_start");
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

    private sealed class StubClient : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
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
