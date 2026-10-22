using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Extensions.AI;
using PiSharp.Runtime;
using PiSharp.Runtime.Sessions;
using PiSharp.Runtime.Tools;

namespace PiSharp.Tests;

public sealed class BuiltinToolResultParityTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "pisharp-builtin-result-" + Guid.NewGuid().ToString("N"));

    public BuiltinToolResultParityTests()
    {
        Directory.CreateDirectory(_root);
        File.WriteAllText(Path.Combine(_root, "file.txt"), "needle\n");
    }

    [Theory]
    [InlineData("grep", "file.txt:1: needle")]
    [InlineData("find", "file.txt")]
    [InlineData("ls", "file.txt")]
    public async Task UntruncatedSearchResultRemainsPlainTextAtTheActualFunctionBoundary(string tool, string expected)
    {
        var function = new CodingTools(_root).CreateAll().OfType<AIFunction>().Single(item => item.Name == tool);
        var value = await function.InvokeAsync(new AIFunctionArguments(Arguments(tool)));

        Assert.True(ToolResultOutput.TryRead(value, out var text, out var details));
        Assert.Equal(expected, text);
        Assert.Null(details);
    }

    [Theory]
    [InlineData("grep", "file.txt:1: needle")]
    [InlineData("find", "file.txt")]
    [InlineData("ls", "file.txt")]
    public async Task PlainSearchResultSurvivesMafContinuationAndCanonicalRestart(string tool, string expected)
    {
        var client = new ToolClient(tool, Arguments(tool));
        var conversation = new ConversationSession(_root, "fixture-model", null);
        var run = await ConversationRun.OpenAsync(new PiAgent(client, new CodingTools(_root), selectedTools: [tool]),
            conversation);
        var events = new List<AgentLifecycleEvent>();
        await foreach (var item in run.RunEventsAsync("inspect the fixture")) events.Add(item);

        var finished = Assert.Single(events, item => item.Type == "tool_execution_finished" && item.Tool == tool);
        Assert.False(finished.IsError);
        Assert.Equal(expected, finished.Text);
        Assert.Equal(expected, client.ProviderResult);
        Assert.Equal("builtin-call", finished.ToolCallId);
        Assert.Null(finished.Details);

        var restored = ConversationSession.Parse(conversation.ToJson());
        var stored = Assert.Single(restored.ActiveMessages().SelectMany(message => message.Contents)
            .OfType<FunctionResultContent>());
        Assert.Equal("builtin-call", stored.CallId);
        Assert.True(ToolResultOutput.TryRead(stored.Result, out var restoredText, out var restoredDetails));
        Assert.Equal(expected, restoredText);
        Assert.Null(restoredDetails);

        var resumedClient = new ToolClient(tool, null);
        var resumed = await ConversationRun.OpenAsync(new PiAgent(resumedClient, new CodingTools(_root), selectedTools: [tool]),
            restored);
        await foreach (var _ in resumed.RunEventsAsync("continue after restart")) { }
        Assert.Equal(expected, resumedClient.ProviderResult);
        Assert.Single(restored.ActiveMessages().SelectMany(message => message.Contents).OfType<FunctionResultContent>());
    }

    [Fact]
    public void TruncationDetailsRetainThePinnedContentField()
    {
        var (text, details) = ToolOutputTruncator.TruncateHead("alpha\nbeta\ngamma", 10);

        Assert.Equal("alpha\nbeta", text);
        Assert.NotNull(details);
        var json = JsonSerializer.SerializeToElement(details, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.True(json.TryGetProperty("content", out var content));
        Assert.Equal(text, content.GetString());
        Assert.Equal(3, json.GetProperty("totalLines").GetInt32());
        Assert.Equal(16, json.GetProperty("totalBytes").GetInt32());
    }

    private static Dictionary<string, object?> Arguments(string tool) => tool switch
    {
        "grep" => new() { ["pattern"] = "needle" },
        "find" => new() { ["pattern"] = "*.txt" },
        _ => []
    };

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private sealed class ToolClient(string tool, Dictionary<string, object?>? arguments) : IChatClient
    {
        private int _requests;
        public string? ProviderResult { get; private set; }

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            if (Interlocked.Increment(ref _requests) == 1 && arguments is not null)
                yield return new ChatResponseUpdate(ChatRole.Assistant, [new FunctionCallContent("builtin-call", tool, arguments)]);
            else
            {
                ProviderResult = Assert.Single(messages.SelectMany(message => message.Contents)
                    .OfType<FunctionResultContent>()).Result as string;
                yield return new ChatResponseUpdate(ChatRole.Assistant, "done");
            }
            await Task.CompletedTask;
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }
}
