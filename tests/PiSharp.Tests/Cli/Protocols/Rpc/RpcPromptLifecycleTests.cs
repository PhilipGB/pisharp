using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Extensions.AI;
using PiSharp.Cli;
using PiSharp.Cli.Protocols;
using PiSharp.Runtime;
using PiSharp.Runtime.Extensions;
using PiSharp.Runtime.Providers;
using PiSharp.Runtime.Sessions;
using PiSharp.Runtime.Tools;

using static PiSharp.Tests.RpcTestInputOutput;

namespace PiSharp.Tests;

public sealed class RpcPromptLifecycleTests
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
        channel.Writer.TryWrite("{\"id\":\"fork-messages\",\"type\":\"get_fork_messages\"}");
        channel.Writer.TryWrite("{\"id\":\"blank-name\",\"type\":\"set_session_name\",\"name\":\"  \"}");
        channel.Writer.TryWrite("{\"id\":\"entries\",\"type\":\"get_entries\"}");
        var firstEntryId = session.Tree.Entries[0].Id;
        channel.Writer.TryWrite($"{{\"id\":\"entries-after-cursor\",\"type\":\"get_entries\",\"since\":\"{firstEntryId}\"}}");
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
            var promptResponse = Assert.Single(events, e => e.RootElement.GetProperty("type").GetString() == "response" &&
                e.RootElement.GetProperty("id").GetString() == "prompt-1");
            Assert.Equal("started", promptResponse.RootElement.GetProperty("data").GetProperty("disposition").GetString());
            var responseIndex = Array.FindIndex(output.Lines(), line => line.Contains("\"id\":\"prompt-1\"", StringComparison.Ordinal));
            var start = Assert.Single(events, e => e.RootElement.GetProperty("type").GetString() == "agent_start");
            Assert.Equal(["type"], start.RootElement.EnumerateObject().Select(property => property.Name));
            var startIndex = Array.FindIndex(output.Lines(), line => line.Contains("\"type\":\"agent_start\"", StringComparison.Ordinal));
            Assert.True(responseIndex >= 0 && startIndex > responseIndex);
            var turnStartIndex = Array.FindIndex(output.Lines(), line => line.Contains("\"type\":\"turn_start\"", StringComparison.Ordinal));
            var turnEndIndex = Array.FindIndex(output.Lines(), line => line.Contains("\"type\":\"turn_end\"", StringComparison.Ordinal));
            Assert.True(turnStartIndex > startIndex && turnEndIndex > turnStartIndex);
            var userMessageStart = Assert.Single(events, e => e.RootElement.GetProperty("type").GetString() == "message_start" &&
                e.RootElement.GetProperty("message").GetProperty("role").GetString() == "user");
            var userMessageEnd = Assert.Single(events, e => e.RootElement.GetProperty("type").GetString() == "message_end" &&
                e.RootElement.GetProperty("message").GetProperty("role").GetString() == "user");
            Assert.Equal("hello", userMessageStart.RootElement.GetProperty("message").GetProperty("content").GetString());
            var assistantMessageStartIndex = Array.FindIndex(events, e => e.RootElement.GetProperty("type").GetString() == "message_start" &&
                e.RootElement.GetProperty("message").GetProperty("role").GetString() == "assistant");
            var assistantMessageEndIndex = Array.FindIndex(events, e => e.RootElement.GetProperty("type").GetString() == "message_end" &&
                e.RootElement.GetProperty("message").GetProperty("role").GetString() == "assistant");
            var messageUpdate = Assert.Single(events, e => e.RootElement.GetProperty("type").GetString() == "message_update" &&
                e.RootElement.GetProperty("assistantMessageEvent").GetProperty("type").GetString() == "text_delta");
            Assert.Equal("reply", messageUpdate.RootElement.GetProperty("assistantMessageEvent").GetProperty("delta").GetString());
            Assert.True(startIndex < turnStartIndex && turnStartIndex < Array.IndexOf(events, userMessageStart) &&
                Array.IndexOf(events, userMessageStart) < Array.IndexOf(events, userMessageEnd) &&
                Array.IndexOf(events, userMessageEnd) < assistantMessageStartIndex &&
                assistantMessageStartIndex < Array.IndexOf(events, messageUpdate) &&
                Array.IndexOf(events, messageUpdate) < assistantMessageEndIndex && assistantMessageEndIndex < turnEndIndex);
            Assert.True(JsonElement.DeepEquals(events[assistantMessageEndIndex].RootElement.GetProperty("message"),
                events[turnEndIndex].RootElement.GetProperty("message")));
            Assert.Single(events, e => e.RootElement.GetProperty("type").GetString() == "turn_start");
            Assert.Single(events, e => e.RootElement.GetProperty("type").GetString() == "turn_end");
            var agentEnd = Assert.Single(events, e => e.RootElement.GetProperty("type").GetString() == "agent_end");
            Assert.Equal(["type", "messages", "willRetry"], agentEnd.RootElement.EnumerateObject().Select(property => property.Name));
            Assert.False(agentEnd.RootElement.GetProperty("willRetry").GetBoolean());
            var runMessages = agentEnd.RootElement.GetProperty("messages");
            Assert.Equal(2, runMessages.GetArrayLength());
            Assert.Equal("user", runMessages[0].GetProperty("role").GetString());
            Assert.Equal("hello", runMessages[0].GetProperty("content").GetString());
            Assert.Equal("assistant", runMessages[1].GetProperty("role").GetString());
            Assert.Equal("reply", runMessages[1].GetProperty("content")[0].GetProperty("text").GetString());
            Assert.True(JsonElement.DeepEquals(userMessageEnd.RootElement.GetProperty("message"), runMessages[0]));
            Assert.True(JsonElement.DeepEquals(events[assistantMessageEndIndex].RootElement.GetProperty("message"), runMessages[1]));
            var settled = Assert.Single(events, e => e.RootElement.GetProperty("type").GetString() == "agent_settled");
            Assert.Equal(["type"], settled.RootElement.EnumerateObject().Select(property => property.Name));
            var settledIndex = Array.FindIndex(output.Lines(), line => line.Contains("\"type\":\"agent_settled\"", StringComparison.Ordinal));
            var endIndex = Array.FindIndex(output.Lines(), line => line.Contains("\"type\":\"agent_end\"", StringComparison.Ordinal));
            Assert.True(settledIndex > endIndex && endIndex > turnEndIndex);
            Assert.DoesNotContain(output.Lines(), line => line.Contains("prompt_accepted", StringComparison.Ordinal));
            Assert.Contains(events, e => e.RootElement.GetProperty("type").GetString() == "response" &&
                e.RootElement.GetProperty("id").GetString() == "unknown" && !e.RootElement.GetProperty("success").GetBoolean());
            Assert.Contains(events, e => e.RootElement.GetProperty("type").GetString() == "response" &&
                e.RootElement.GetProperty("id").GetString() == "after" &&
                e.RootElement.GetProperty("data").GetProperty("messages").GetArrayLength() == 2);
            var sessionInfo = Assert.Single(events, e => e.RootElement.GetProperty("type").GetString() == "session_info_changed");
            Assert.Equal(["type", "name"], sessionInfo.RootElement.EnumerateObject().Select(property => property.Name));
            Assert.Equal("my feature", sessionInfo.RootElement.GetProperty("name").GetString());
            var lines = output.Lines();
            var sessionInfoIndex = Array.FindIndex(lines, line => line.Contains("session_info_changed", StringComparison.Ordinal));
            var nameResponseIndex = Array.FindIndex(lines, line => line.Contains("\"id\":\"name\"", StringComparison.Ordinal));
            Assert.True(sessionInfoIndex >= 0 && nameResponseIndex > sessionInfoIndex);
            Assert.Equal("session_info", session.Tree.Entries[^1].Type);
            Assert.Equal("my feature", session.Tree.Entries[^1].Payload.GetProperty("name").GetString());
            Assert.Equal(session.Tree.Entries[^2].Id, session.Tree.Entries[^1].ParentId);
            Assert.Contains(events, e => e.RootElement.GetProperty("type").GetString() == "response" &&
                e.RootElement.GetProperty("id").GetString() == "bad-cursor" &&
                !e.RootElement.GetProperty("success").GetBoolean() &&
                e.RootElement.GetProperty("error").GetString() == "Entry not found: missing");
            Assert.Equal("my feature", session.Name);
            Assert.Contains(events, e => e.RootElement.GetProperty("type").GetString() == "response" &&
                e.RootElement.GetProperty("id").GetString() == "blank-name" &&
                !e.RootElement.GetProperty("success").GetBoolean() &&
                e.RootElement.GetProperty("error").GetString() == "Session name cannot be empty");
            var forkMessages = Assert.Single(events, e => e.RootElement.GetProperty("type").GetString() == "response" &&
                e.RootElement.GetProperty("id").GetString() == "fork-messages").RootElement
                .GetProperty("data").GetProperty("messages");
            Assert.Single(forkMessages.EnumerateArray());
            Assert.Equal("hello", forkMessages[0].GetProperty("text").GetString());
            var entriesResponse = Assert.Single(events, e => e.RootElement.GetProperty("type").GetString() == "response" &&
                e.RootElement.GetProperty("id").GetString() == "entries" &&
                e.RootElement.GetProperty("data").GetProperty("entries").GetArrayLength() == 4);
            var entriesData = entriesResponse.RootElement.GetProperty("data");
            Assert.Equal(["entries", "leafId"], entriesData.EnumerateObject().Select(property => property.Name));
            Assert.False(entriesData.TryGetProperty("format", out _));
            Assert.Equal("message", entriesData.GetProperty("entries")[0].GetProperty("type").GetString());
            Assert.Equal(firstEntryId, entriesData.GetProperty("entries")[0].GetProperty("id").GetString());
            Assert.Equal("session_info", entriesData.GetProperty("entries")[3].GetProperty("type").GetString());
            Assert.Equal("my feature", entriesData.GetProperty("entries")[3].GetProperty("name").GetString());
            Assert.Equal(session.Tree.HeadId, entriesData.GetProperty("leafId").GetString());
            var cursorEntries = Assert.Single(events, e => e.RootElement.GetProperty("type").GetString() == "response" &&
                e.RootElement.GetProperty("id").GetString() == "entries-after-cursor").RootElement.GetProperty("data");
            Assert.Equal(3, cursorEntries.GetProperty("entries").GetArrayLength());
            Assert.Equal(session.Tree.Entries[1].Id, cursorEntries.GetProperty("entries")[0].GetProperty("id").GetString());
            Assert.Equal(session.Tree.Entries[2].Id, cursorEntries.GetProperty("entries")[1].GetProperty("id").GetString());
            Assert.Equal(session.Tree.HeadId, cursorEntries.GetProperty("leafId").GetString());
            var treeResponse = Assert.Single(events, e => e.RootElement.GetProperty("type").GetString() == "response" &&
                e.RootElement.GetProperty("id").GetString() == "tree" &&
                e.RootElement.GetProperty("data").GetProperty("tree").GetArrayLength() == 1);
            var treeData = treeResponse.RootElement.GetProperty("data");
            Assert.Equal(["tree", "leafId"], treeData.EnumerateObject().Select(property => property.Name));
            Assert.False(treeData.TryGetProperty("format", out _));
            var rootNode = Assert.Single(treeData.GetProperty("tree").EnumerateArray());
            Assert.Equal(["entry", "children"], rootNode.EnumerateObject().Select(property => property.Name));
            Assert.Equal(firstEntryId, rootNode.GetProperty("entry").GetProperty("id").GetString());
            Assert.Equal(1, rootNode.GetProperty("children").GetArrayLength());
            var transcriptNode = rootNode.GetProperty("children")[0];
            Assert.Equal("pisharp.tool_transcript", transcriptNode.GetProperty("entry").GetProperty("customType").GetString());
            var assistantNode = transcriptNode.GetProperty("children")[0];
            Assert.Equal("session_info", assistantNode.GetProperty("children")[0]
                .GetProperty("entry").GetProperty("type").GetString());
            Assert.Equal("my feature", assistantNode.GetProperty("children")[0]
                .GetProperty("entry").GetProperty("name").GetString());
            Assert.Equal(session.Tree.HeadId, treeData.GetProperty("leafId").GetString());
            Assert.Contains(events, e => e.RootElement.GetProperty("type").GetString() == "response" &&
                e.RootElement.GetProperty("id").GetString() == "text" &&
                e.RootElement.GetProperty("data").GetProperty("text").GetString() == "reply");
            Assert.DoesNotContain(events, e => e.RootElement.GetProperty("type").GetString() == "session");
        }
        finally { foreach (var e in events) e.Dispose(); }
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

}
