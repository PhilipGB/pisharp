using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Extensions.AI;
using PiSharp.Runtime;
using PiSharp.Runtime.Sessions;
using PiSharp.Runtime.Tools;

namespace PiSharp.Tests;

public sealed class StructuredSearchToolTests
{
    [Fact]
    public async Task GrepFindAndLsExposeTheirLimitDetails()
    {
        var root = CreateRoot("pisharp-search-details-");
        try
        {
            await File.WriteAllTextAsync(Path.Combine(root, "alpha.txt"), "needle\n");
            await File.WriteAllTextAsync(Path.Combine(root, "beta.txt"), "needle\n");

            var grep = await new SearchTools(root).GrepForTool("needle", limit: 1);
            var grepDetails = Assert.IsType<GrepToolDetails>(grep.Details);
            Assert.Equal(1, grepDetails.MatchLimitReached);
            Assert.Contains("1 matches limit reached", grep.Text);

            var find = await new SearchTools(root).FindForTool("*.txt", limit: 1);
            var findDetails = Assert.IsType<FindToolDetails>(find.Details);
            Assert.Equal(1, findDetails.ResultLimitReached);
            Assert.Contains("1 results limit reached", find.Text);

            var ls = await new DirectoryListingTool(root).ListForTool(limit: 1);
            var lsDetails = Assert.IsType<LsToolDetails>(ls.Details);
            Assert.Equal(1, lsDetails.EntryLimitReached);
            Assert.Contains("1 entries limit reached", ls.Text);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task GrepReportsByteAndLongLineTruncationDetails()
    {
        var root = CreateRoot("pisharp-search-truncation-");
        try
        {
            for (var index = 0; index < 110; index++)
                await File.WriteAllTextAsync(Path.Combine(root, $"match-{index:000}.txt"), "needle " + new string('x', 600));

            var output = await new SearchTools(root).GrepForTool("needle", limit: 200);
            var details = Assert.IsType<GrepToolDetails>(output.Details);
            var truncation = Assert.IsType<ToolTruncationDetails>(details.Truncation);
            Assert.True(truncation.Truncated);
            Assert.Equal("bytes", truncation.TruncatedBy);
            Assert.Equal(110, truncation.TotalLines);
            Assert.InRange(truncation.OutputBytes, 1, 50 * 1024);
            Assert.Null(details.MatchLimitReached);
            Assert.True(details.LinesTruncated);
            Assert.Contains("50.0KB limit reached", output.Text);
            Assert.Contains("Some lines truncated to 500 chars", output.Text);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void OutputParserKeepsOrdinaryJsonTextUntouchedAndFlattensSearchRecords()
    {
        Assert.False(ToolResultOutput.TryRead("{\"Text\":\"ordinary JSON output\",\"Value\":1}", out _, out _));
        Assert.True(ToolResultOutput.TryRead("{\"Text\":\"search output\",\"Details\":null}", out var text, out var details));
        Assert.Equal("search output", text);
        Assert.Null(details);
    }

    [Fact]
    public async Task StructuredGrepDetailsPersistAndProviderReceivesOnlyText()
    {
        var root = CreateRoot("pisharp-search-history-");
        try
        {
            await File.WriteAllTextAsync(Path.Combine(root, "alpha.txt"), "needle\n");
            var client = new GrepClient();
            var conversation = new ConversationSession(root, "fixture-model", null);
            var run = await ConversationRun.OpenAsync(new PiAgent(client, new CodingTools(root), selectedTools: ["grep"]), conversation);
            var events = new List<AgentLifecycleEvent>();
            await foreach (var item in run.RunEventsAsync("search for needle")) events.Add(item);

            var finished = Assert.Single(events, item => item.Type == "tool_execution_finished" && item.Tool == "grep");
            var eventDetails = AsObjectJson(finished.Details);
            Assert.Equal(1, GetProperty(eventDetails, "MatchLimitReached").GetInt32());
            Assert.Equal("alpha.txt:1: needle\n\n[1 matches limit reached. Use limit=2 for more, or refine pattern]", finished.Text);
            Assert.Equal(finished.Text, client.ProviderToolResult);

            var result = conversation.ActiveMessages().SelectMany(message => message.Contents)
                .OfType<FunctionResultContent>().Single();
            var resultJson = AsObjectJson(result.Result);
            Assert.Equal(finished.Text, GetProperty(resultJson, "Text").GetString());
            Assert.Equal(1, GetProperty(GetProperty(resultJson, "Details"), "MatchLimitReached").GetInt32());

            var restored = ConversationSession.Parse(conversation.ToJson());
            var restoredResult = restored.ActiveMessages().SelectMany(message => message.Contents)
                .OfType<FunctionResultContent>().Single();
            var restoredJson = AsObjectJson(restoredResult.Result);
            Assert.Equal(1, GetProperty(GetProperty(restoredJson, "Details"), "MatchLimitReached").GetInt32());

            var continueClient = new ContinueGrepClient();
            var resumed = await ConversationRun.OpenAsync(new PiAgent(continueClient, new CodingTools(root), selectedTools: ["grep"]), restored);
            await foreach (var _ in resumed.RunEventsAsync("continue")) { }
            Assert.Equal(finished.Text, continueClient.ProviderToolResult);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static string CreateRoot(string prefix)
    {
        var root = Path.Combine(Path.GetTempPath(), prefix + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static JsonElement AsObjectJson(object? value)
    {
        Assert.NotNull(value);
        if (value is JsonElement json && json.ValueKind == JsonValueKind.Object) return json;
        var serialized = value is string text ? text : JsonSerializer.Serialize(value, value.GetType());
        using var document = JsonDocument.Parse(serialized);
        Assert.Equal(JsonValueKind.Object, document.RootElement.ValueKind);
        return document.RootElement.Clone();
    }

    private static JsonElement GetProperty(JsonElement value, string name) =>
        value.EnumerateObject().Single(item => item.Name.Equals(name, StringComparison.OrdinalIgnoreCase)).Value;

    private sealed class GrepClient : IChatClient
    {
        private int _requests;
        public string? ProviderToolResult { get; private set; }

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            if (Interlocked.Increment(ref _requests) == 1)
            {
                yield return new ChatResponseUpdate(ChatRole.Assistant,
                    [new FunctionCallContent("grep-call", "grep", new Dictionary<string, object?>
                    {
                        ["pattern"] = "needle",
                        ["limit"] = 1
                    })]);
            }
            else
            {
                ProviderToolResult = messages.SelectMany(message => message.Contents)
                    .OfType<FunctionResultContent>().Single().Result as string;
                yield return new ChatResponseUpdate(ChatRole.Assistant, "done");
            }
            await Task.CompletedTask;
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    private sealed class ContinueGrepClient : IChatClient
    {
        public string? ProviderToolResult { get; private set; }

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            ProviderToolResult = messages.SelectMany(message => message.Contents)
                .OfType<FunctionResultContent>().Single().Result as string;
            yield return new ChatResponseUpdate(ChatRole.Assistant, "continued");
            await Task.CompletedTask;
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }
}
