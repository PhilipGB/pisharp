using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.AI;
using PiSharp.Runtime;
using PiSharp.Runtime.Sessions;
using PiSharp.Runtime.Tools;

namespace PiSharp.Tests;

public sealed class BuiltinToolWorkflowTests
{
    [Fact]
    public async Task AllSevenToolsRetainPinnedOutcomesThroughMafAndCanonicalFileRestart()
    {
        if (!OperatingSystem.IsLinux()) return;
        using var fixture = SearchProcessProtocolTests.SourceFixture("agent", "workflow");
        var source = fixture.RootElement;
        var root = Path.Combine(Path.GetTempPath(), "pisharp-agent-tools-" + Guid.NewGuid().ToString("N"));
        var workspace = Path.Combine(root, "workspace");
        Directory.CreateDirectory(workspace);
        try
        {
            foreach (var file in source.GetProperty("files").EnumerateObject())
            {
                var target = Path.Combine(workspace, file.Name);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                await File.WriteAllTextAsync(target, file.Value.GetString());
            }
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            var client = new WorkflowClient(source, resumed: false);
            var conversation = new ConversationSession(workspace, "fixture-model", null);
            var store = new ConversationStore(workspace, Path.Combine(root, "sessions"));
            var path = store.NewPath(conversation);
            var tools = source.GetProperty("tools").EnumerateArray().Select(value => value.GetString()!).ToArray();
            var run = await ConversationRun.OpenAsync(new PiAgent(client, new CodingTools(workspace), selectedTools: tools),
                conversation, save: token => store.SaveAsync(conversation, path, token));
            var events = new List<AgentLifecycleEvent>();
            await foreach (var item in run.RunEventsAsync("Complete the fixture coding workflow.", deadline.Token)) events.Add(item);
            Assert.True(source.GetProperty("requests").GetArrayLength() == client.Requests,
                string.Join("\n", events.Where(item => item.IsError == true || item.Error is not null)
                    .Select(item => item.Type + ": " + item.Error + " " + item.Text)));
            foreach (var expected in source.GetProperty("outcomes").EnumerateArray())
            {
                var callId = expected.GetProperty("callId").GetString();
                var finished = Assert.Single(events, item => item.Type == "tool_execution_finished" && item.ToolCallId == callId);
                Assert.Equal(expected.GetProperty("tool").GetString(), finished.Tool);
                Assert.Equal(expected.GetProperty("isError").GetBoolean(), finished.IsError);
                Assert.Equal(Text(expected.GetProperty("result").GetProperty("content")), finished.Text);
                AssertMetadata(expected.GetProperty("result"), finished.Details, finished.StructuredContent);
                var started = Assert.Single(events, item => item.Type == "tool_execution_started" && item.ToolCallId == callId);
                Assert.True(events.IndexOf(started) < events.IndexOf(finished));
                foreach (var update in events.Where(item => item.Type == "tool_execution_update" && item.ToolCallId == callId))
                    Assert.InRange(events.IndexOf(update), events.IndexOf(started) + 1, events.IndexOf(finished) - 1);
            }
            var calls = conversation.ActiveMessages().SelectMany(message => message.Contents).OfType<FunctionCallContent>().ToArray();
            Assert.Equal(source.GetProperty("plan").GetArrayLength(), calls.Length);
            Assert.Equal(calls.Length, calls.Select(call => call.CallId).Distinct().Count());
            await store.SaveAsync(conversation, path, deadline.Token);
            Assert.True(File.Exists(path));
            var restored = await store.LoadAsync(path, deadline.Token);
            var saved = restored.ActiveMessages().SelectMany(message => message.Contents).OfType<FunctionResultContent>().ToArray();
            Assert.Equal(calls.Length, saved.Length);
            Assert.Equal(calls.Select(call => call.CallId).Order(), saved.Select(result => result.CallId).Order());
            foreach (var expected in source.GetProperty("outcomes").EnumerateArray())
            {
                var result = Assert.Single(saved, item => item.CallId == expected.GetProperty("callId").GetString());
                Assert.Equal(Text(expected.GetProperty("result").GetProperty("content")), ResultText(result));
                if (ToolResultOutput.TryReadContract(result, out var contract))
                {
                    Assert.Equal(expected.GetProperty("isError").GetBoolean(), contract.IsError);
                    AssertMetadata(expected.GetProperty("result"), contract.Details, contract.StructuredContent);
                }
                else if (ToolResultOutput.TryRead(result.Result, out _, out var details))
                    AssertMetadata(expected.GetProperty("result"), details, null);
            }
            var resumedClient = new WorkflowClient(source, resumed: true);
            var resumed = await ConversationRun.OpenAsync(new PiAgent(resumedClient, new CodingTools(workspace), selectedTools: tools), restored);
            await foreach (var _ in resumed.RunEventsAsync("Inspect the saved outcomes without repeating tools.", deadline.Token)) { }
            Assert.Equal(1, resumedClient.Requests);
            Assert.Equal(saved.Length, restored.ActiveMessages().SelectMany(message => message.Contents).OfType<FunctionResultContent>().Count());
            foreach (var file in source.GetProperty("finalFiles").EnumerateObject())
            {
                var target = Path.Combine(workspace, file.Name);
                Assert.Equal(file.Value.GetProperty("text").GetString(), await File.ReadAllTextAsync(target, deadline.Token));
                var expectedMode = file.Name == "generated/result.txt" ? 0x180 : file.Value.GetProperty("mode").GetInt32();
                Assert.Equal((UnixFileMode)expectedMode, File.GetUnixFileMode(target));
            }
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    public static IEnumerable<object[]> Errors()
    {
        using var source = SearchProcessProtocolTests.SourceFixture("agent", "workflow");
        foreach (var error in source.RootElement.GetProperty("failures").EnumerateArray())
            yield return [error.GetProperty("item").GetProperty("tool").GetString()!, error.GetRawText()];
    }

    [Theory]
    [InlineData("read")]
    [InlineData("write")]
    [InlineData("edit")]
    [InlineData("bash")]
    [InlineData("grep")]
    [InlineData("find")]
    [InlineData("ls")]
    public async Task ExcludedBuiltinStaysUnavailableWhenRestoringAnEarlierAllToolLoadout(string excluded)
    {
        using var source = SearchProcessProtocolTests.SourceFixture("agent", "workflow");
        var root = Path.Combine(Path.GetTempPath(), "pisharp-excluded-builtin-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "src"));
        try
        {
            await File.WriteAllTextAsync(Path.Combine(root, "src", "value.txt"), "private-fixture-marker");
            var names = source.RootElement.GetProperty("tools").EnumerateArray().Select(value => value.GetString()!).ToArray();
            var empty = new JsonObject
            {
                ["tools"] = JsonSerializer.SerializeToNode(names),
                ["plan"] = new JsonArray(),
                ["requests"] = new JsonArray(new JsonArray())
            };
            using var firstFixture = JsonDocument.Parse(empty.ToJsonString());
            var coding = new CodingTools(root);
            var conversation = new ConversationSession(root, "fixture-model", null);
            var warm = await ConversationRun.OpenAsync(new PiAgent(new WorkflowClient(firstFixture.RootElement, false),
                coding, selectedTools: names), conversation);
            await foreach (var _ in warm.RunEventsAsync("Record the initial complete loadout.")) { }
            var arguments = excluded switch
            {
                "write" => JsonSerializer.SerializeToNode(new { path = "unexpected.txt", content = "executed" }),
                "edit" => JsonSerializer.SerializeToNode(new { path = "src/value.txt", edits = new[] { new { oldText = "private-fixture-marker", newText = "executed" } } }),
                "bash" => JsonSerializer.SerializeToNode(new { command = "printf executed > unexpected.txt" }),
                "grep" => JsonSerializer.SerializeToNode(new { pattern = "private-fixture-marker", path = "src/value.txt" }),
                "find" => JsonSerializer.SerializeToNode(new { pattern = "*.txt" }),
                "ls" => JsonSerializer.SerializeToNode(new { path = "src" }),
                _ => JsonSerializer.SerializeToNode(new { path = "src/value.txt" })
            };
            var next = new JsonObject
            {
                ["tools"] = JsonSerializer.SerializeToNode(names.Where(name => name != excluded)),
                ["plan"] = new JsonArray(new JsonObject { ["tool"] = excluded, ["callId"] = "excluded-call", ["arguments"] = arguments }),
                ["requests"] = new JsonArray(new JsonArray(), new JsonArray()),
                ["unavailable"] = true
            };
            using var nextFixture = JsonDocument.Parse(next.ToJsonString());
            var client = new WorkflowClient(nextFixture.RootElement, false);
            var restored = ConversationSession.Parse(conversation.ToJson());
            var run = await ConversationRun.OpenAsync(new PiAgent(client, coding, selectedTools: names,
                excludedTools: [excluded]), restored);
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await foreach (var _ in run.RunEventsAsync("Try an excluded tool from an old loadout.", deadline.Token)) { }
            Assert.Equal(2, client.Requests);
            Assert.True(client.UnavailableResultObserved);
            Assert.DoesNotContain(excluded, run.ActiveToolNames);
            Assert.Single(restored.ActiveMessages().SelectMany(message => message.Contents).OfType<FunctionResultContent>());
            Assert.Equal("private-fixture-marker", await File.ReadAllTextAsync(Path.Combine(root, "src", "value.txt"), deadline.Token));
            Assert.False(File.Exists(Path.Combine(root, "unexpected.txt")));
            Assert.Single(Directory.GetFiles(root, "*", SearchOption.AllDirectories));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Theory]
    [MemberData(nameof(Errors))]
    public async Task BuiltinFailureRemainsActionableFailedAndPersistedAtTheModelBoundary(string tool, string fixture)
    {
        if (!OperatingSystem.IsLinux()) return;
        using var source = SearchProcessProtocolTests.SourceFixture("agent", "workflow");
        using var row = JsonDocument.Parse(fixture);
        var expected = row.RootElement;
        var projected = new JsonObject
        {
            ["tools"] = JsonNode.Parse(source.RootElement.GetProperty("tools").GetRawText()),
            ["plan"] = new JsonArray(JsonNode.Parse(expected.GetProperty("item").GetRawText())),
            ["requests"] = new JsonArray(new JsonArray(), JsonNode.Parse(expected.GetProperty("providerResults").GetRawText()))
        };
        var root = Path.Combine(Path.GetTempPath(), "pisharp-agent-error-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "src"));
        try
        {
            await File.WriteAllTextAsync(Path.Combine(root, "src", "value.txt"), "unchanged");
            using var nativeFixture = JsonDocument.Parse(projected.ToJsonString().Replace("<workspace>", root, StringComparison.Ordinal));
            var client = new WorkflowClient(nativeFixture.RootElement, resumed: false);
            var conversation = new ConversationSession(root, "fixture-model", null);
            var names = source.RootElement.GetProperty("tools").EnumerateArray().Select(value => value.GetString()!).ToArray();
            var run = await ConversationRun.OpenAsync(new PiAgent(client, new CodingTools(root), selectedTools: names), conversation);
            var events = new List<AgentLifecycleEvent>();
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await foreach (var item in run.RunEventsAsync("Observe a failed tool without changing files.", deadline.Token)) events.Add(item);
            Assert.True(client.Requests == 2, string.Join("\n", events.Where(item => item.Error is not null).Select(item => item.Error)));
            var finished = Assert.Single(events, item => item.Type == "tool_execution_finished" && item.Tool == tool);
            Assert.True(finished.IsError);
            Assert.Equal(Text(expected.GetProperty("outcome").GetProperty("result").GetProperty("content")),
                finished.Text!.Replace(root, "<workspace>", StringComparison.Ordinal));
            AssertMetadata(expected.GetProperty("outcome").GetProperty("result"), finished.Details, finished.StructuredContent);
            var stored = ConversationSession.Parse(conversation.ToJson()).ActiveMessages()
                .SelectMany(message => message.Contents).OfType<FunctionResultContent>().Single();
            Assert.True(ToolResultOutput.TryReadContract(stored, out var contract));
            Assert.True(contract.IsError);
            Assert.Equal(finished.Text, contract.Text);
            Assert.Equal(finished.Error, contract.Error);
            Assert.Equal("unchanged", await File.ReadAllTextAsync(Path.Combine(root, "src", "value.txt"), deadline.Token));
            Assert.False(Directory.Exists(Path.Combine(root, "absent")));
            Assert.Single(Directory.GetFiles(root, "*", SearchOption.AllDirectories));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static void AssertMetadata(JsonElement expected, object? details, JsonElement? structured)
    {
        var expectedDetails = expected.TryGetProperty("details", out var supplied) ? supplied : JsonSerializer.SerializeToElement<object?>(null);
        var actualDetails = JsonSerializer.SerializeToElement(details, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.True(JsonElement.DeepEquals(expectedDetails, actualDetails), "Expected details: " + expectedDetails + "; actual: " + actualDetails);
        if (!expected.TryGetProperty("structuredContent", out var expectedStructured)) return;
        Assert.NotNull(structured);
        var expectedNode = JsonNode.Parse(expectedStructured.GetRawText());
        var actualNode = JsonNode.Parse(structured.Value.GetRawText());
        if (expectedNode is JsonObject expectedObject && expectedObject.ContainsKey("wall_time_seconds"))
        {
            Assert.True(actualNode!["wall_time_seconds"]!.GetValue<double>() >= 0);
            expectedObject.Remove("wall_time_seconds");
            actualNode!.AsObject().Remove("wall_time_seconds");
        }
        Assert.True(JsonNode.DeepEquals(expectedNode, actualNode), "Structured content differs.");
    }

    private static string Text(JsonElement content) => string.Concat(content.EnumerateArray()
        .Where(part => part.GetProperty("type").GetString() == "text").Select(part => part.GetProperty("text").GetString()));

    private static string ResultText(FunctionResultContent result)
    {
        if (ToolResultOutput.TryReadContract(result, out var contract)) return contract.Text;
        if (ToolResultOutput.TryRead(result.Result, out var text, out _)) return text;
        return result.Result is JsonElement { ValueKind: JsonValueKind.String } json
            ? json.GetString()! : Assert.IsType<string>(result.Result);
    }

    private static string ProviderText(object? value) => value is JsonElement json
        ? json.ValueKind == JsonValueKind.String ? json.GetString()! :
            throw new InvalidOperationException("Provider received non-text tool metadata: " + json)
        : Assert.IsType<string>(value);

    private sealed class WorkflowClient(JsonElement fixture, bool resumed) : IChatClient
    {
        public int Requests { get; private set; }
        public bool UnavailableResultObserved { get; private set; }

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            var index = Requests++;
            var expected = resumed ? fixture.GetProperty("resumedResults") : fixture.GetProperty("requests")[index];
            var results = messages.SelectMany(message => message.Contents).OfType<FunctionResultContent>().ToArray();
            if (index > 0 && fixture.TryGetProperty("unavailable", out var unavailable) && unavailable.GetBoolean())
            {
                Assert.Contains("not found", ProviderText(Assert.Single(results).Result), StringComparison.OrdinalIgnoreCase);
                UnavailableResultObserved = true;
            }
            else
            {
                Assert.Equal(expected.GetArrayLength(), results.Length);
                foreach (var item in expected.EnumerateArray())
                {
                    var result = Assert.Single(results, result => result.CallId == item.GetProperty("callId").GetString());
                    Assert.Equal(Text(item.GetProperty("content")), ProviderText(result.Result));
                }
            }
            Assert.Equal(fixture.GetProperty("tools").EnumerateArray().Select(value => value.GetString()).Order(),
                options!.Tools!.Select(tool => tool.Name).Order());
            var plan = fixture.GetProperty("plan");
            if (!resumed && index < plan.GetArrayLength())
            {
                var item = plan[index];
                var arguments = item.GetProperty("arguments").EnumerateObject()
                    .ToDictionary(property => property.Name, property => (object?)property.Value.Clone());
                yield return new ChatResponseUpdate(ChatRole.Assistant,
                    [new FunctionCallContent(item.GetProperty("callId").GetString()!, item.GetProperty("tool").GetString()!, arguments)]);
            }
            else yield return new ChatResponseUpdate(ChatRole.Assistant, resumed ? "history retained" : "workflow complete");
            await Task.CompletedTask;
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }
}
