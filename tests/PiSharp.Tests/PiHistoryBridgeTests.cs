using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Extensions.AI;
using PiSharp.Core;
using PiSharp.Runtime;

namespace PiSharp.Tests;

public sealed class PiHistoryBridgeTests
{
    private static string Fixture() => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixtures", "session-v3-branch.jsonl"));

    [Theory]
    [InlineData("daaaaaaa", "alternate", "branch prompt", "prior summary")]
    [InlineData("f6666666", "corrected", "next", "abandoned path")]
    public async Task SelectedBranchIsVisibleToMafWithoutAbandonedHistory(string leaf, string expected, string next, string absent)
    {
        var journal = PiSessionJournal.Parse(Fixture());
        journal.Tree.Select(leaf);
        var root = Path.GetTempPath();
        var client = new CaptureClient();
        var agent = new PiAgent(client, new CodingTools(root));
        var session = await agent.CreateSessionFromJournalAsync(journal);
        await foreach (var _ in agent.RunStreamingAsync("current turn", session)) { }
        var observed = string.Join("\n", client.Received.Select(m => m.Text));
        Assert.Contains(expected, observed);
        Assert.Contains(next, observed);
        Assert.Contains("current turn", observed);
        Assert.DoesNotContain(absent, observed);
    }

    [Fact]
    public void ToolCallAndResultKeepTheirCorrelationAndUnsupportedImagesFailClosed()
    {
        var items = JsonSerializer.SerializeToElement(new object[]
        {
            new { role = "assistant", content = new object[] { new { type = "toolCall", id = "call_1", name = "read", arguments = new { path = "file" } } } },
            new { role = "toolResult", toolCallId = "call_1", content = new[] { new { type = "text", text = "data" } } }
        });
        var history = PiHistoryBridge.ToChatMessages(new PiSessionContext(items.EnumerateArray().ToArray(), "off", null, null));
        Assert.Equal("call_1", Assert.Single(history[0].Contents.OfType<FunctionCallContent>()).CallId);
        Assert.True(Assert.Single(history[0].Contents.OfType<FunctionCallContent>()).InformationalOnly);
        Assert.Equal("call_1", Assert.Single(history[1].Contents.OfType<FunctionResultContent>()).CallId);
        var image = JsonSerializer.SerializeToElement(new[] { new { role = "user", content = new[] { new { type = "image", data = "abc" } } } });
        Assert.Throws<NotSupportedException>(() => PiHistoryBridge.ToChatMessages(new PiSessionContext(image.EnumerateArray().ToArray(), "off", null, null)));
    }

    [Theory]
    [InlineData("{\"role\":\"assistant\",\"content\":[{\"type\":\"text\",\"text\":\"x\",\"textSignature\":\"opaque\"}]}")]
    [InlineData("{\"role\":\"system\",\"content\":\"\",\"sections\":{\"tools\":\"patched\"}}")]
    [InlineData("{\"role\":\"toolResult\",\"toolCallId\":\"id\",\"isError\":true,\"content\":[{\"type\":\"text\",\"text\":\"fail\"}]}")]
    public void LossyProviderOrToolStateIsNotSilentlyImported(string json)
    {
        using var document = JsonDocument.Parse(json);
        Assert.Throws<NotSupportedException>(() => PiHistoryBridge.ToChatMessages(
            new PiSessionContext([document.RootElement.Clone()], "off", null, null)));
    }

    [Fact]
    public async Task RestoredToolCallAndResultReachModelWithoutExecutingHistoricalToolAgain()
    {
        var directory = Path.Combine(Path.GetTempPath(), "pisharp-history-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var journal = new PiSessionJournal(directory);
            journal.Append("message", JsonSerializer.SerializeToElement(new
            {
                message = new
                {
                    role = "assistant",
                    provider = "fixture",
                    model = "mock",
                    content = new object[]
                {
                    new { type = "toolCall", id = "previous_call", name = "write", arguments = new { path = "should-not-exist", content = "oops" } }
                }
                }
            }));
            journal.Append("message", JsonSerializer.SerializeToElement(new
            {
                message = new { role = "toolResult", toolCallId = "previous_call", toolName = "write", content = new[] { new { type = "text", text = "prior result" } }, isError = false }
            }));
            var file = Path.Combine(directory, "tool-turn.jsonl");
            await PiSessionFiles.SaveAsync(journal, file);
            var reloaded = await PiSessionFiles.LoadAsync(file);
            var client = new CaptureClient();
            var agent = new PiAgent(client, new CodingTools(directory));
            var session = await agent.CreateSessionFromJournalAsync(reloaded);
            await foreach (var _ in agent.RunStreamingAsync("now", session)) { }
            Assert.Contains(client.Received.SelectMany(m => m.Contents).OfType<FunctionCallContent>(), c => c.CallId == "previous_call");
            Assert.Contains(client.Received.SelectMany(m => m.Contents).OfType<FunctionResultContent>(), c => c.CallId == "previous_call");
            Assert.False(File.Exists(Path.Combine(directory, "should-not-exist")));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    private sealed class CaptureClient : IChatClient
    {
        public IReadOnlyList<ChatMessage> Received { get; private set; } = [];
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            Received = messages.ToArray();
            yield return new ChatResponseUpdate(ChatRole.Assistant, "ack");
            await Task.CompletedTask;
        }
        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }
}
