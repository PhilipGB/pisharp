using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Extensions.AI;
using PiSharp.Core;
using PiSharp.Runtime;
using PiSharp.Runtime.Sessions;

namespace PiSharp.Tests;

public sealed class EditToolTests
{
    [Fact]
    public async Task EditToolKeepsPreviewDetailsInHistoryAndSendsOnlyTextToTheModel()
    {
        var root = Path.Combine(Path.GetTempPath(), "pisharp-edit-tool-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            await File.WriteAllTextAsync(Path.Combine(root, "fixture.txt"), "Hello, world!");
            var client = new EditClient();
            var conversation = new ConversationSession(root, "fixture-model", null);
            var run = await ConversationRun.OpenAsync(new PiAgent(client, new CodingTools(root)), conversation);
            var events = new List<AgentLifecycleEvent>();
            await foreach (var item in run.RunEventsAsync("replace world")) events.Add(item);

            Assert.Equal("Hello, testing!", await File.ReadAllTextAsync(Path.Combine(root, "fixture.txt")));
            Assert.Equal("Successfully replaced 1 block(s) in fixture.txt.", client.ToolResult);
            var finished = Assert.Single(events, item => item.Type == "tool_execution_finished" && item.Tool == "edit");
            var eventDetails = Assert.IsType<FileEditDetails>(finished.Details);
            Assert.Contains("+1 Hello, testing!", eventDetails.Diff);
            Assert.Contains("--- fixture.txt", eventDetails.Patch);
            Assert.Equal(1, eventDetails.FirstChangedLine);
            using (var eventJson = JsonDocument.Parse(JsonSerializer.Serialize(finished)))
                Assert.Contains("+1 Hello, testing!", eventJson.RootElement.GetProperty("Details").GetProperty("Diff").GetString());

            var result = conversation.ActiveMessages().SelectMany(message => message.Contents)
                .OfType<FunctionResultContent>().Single();
            var resultJson = AsObjectJson(result.Result);
            Assert.Equal("Successfully replaced 1 block(s) in fixture.txt.", resultJson.GetProperty("text").GetString());
            Assert.Contains("+1 Hello, testing!", resultJson.GetProperty("diff").GetString());
            Assert.Equal(1, resultJson.GetProperty("firstChangedLine").GetInt32());

            var resumedClient = new ContinueClient();
            var restored = ConversationSession.Parse(conversation.ToJson());
            var resumed = await ConversationRun.OpenAsync(new PiAgent(resumedClient, new CodingTools(root)), restored);
            await foreach (var _ in resumed.RunEventsAsync("continue after edit")) { }
            Assert.Equal("Successfully replaced 1 block(s) in fixture.txt.", resumedClient.ToolResult);
        }
        finally { Directory.Delete(root, recursive: true); }
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

    private sealed class EditClient : IChatClient
    {
        private int _requests;
        public string? ToolResult { get; private set; }

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            if (Interlocked.Increment(ref _requests) == 1)
            {
                yield return new ChatResponseUpdate(ChatRole.Assistant,
                    [new FunctionCallContent("edit-call", "edit", new Dictionary<string, object?>
                    {
                        ["path"] = "fixture.txt",
                        ["edits"] = new List<TextEdit> { new("world", "testing") }
                    })]);
            }
            else
            {
                var result = messages.SelectMany(message => message.Contents).OfType<FunctionResultContent>().Single();
                ToolResult = result.Result?.ToString();
                yield return new ChatResponseUpdate(ChatRole.Assistant, "done");
            }
            await Task.CompletedTask;
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    private sealed class ContinueClient : IChatClient
    {
        public string? ToolResult { get; private set; }
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            ToolResult = messages.SelectMany(message => message.Contents).OfType<FunctionResultContent>().Single().Result?.ToString();
            yield return new ChatResponseUpdate(ChatRole.Assistant, "continued");
            await Task.CompletedTask;
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }
}
