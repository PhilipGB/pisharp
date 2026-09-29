using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Extensions.AI;
using PiSharp.Cli.Protocols;
using PiSharp.Runtime;
using PiSharp.Runtime.Extensions;
using PiSharp.Runtime.Sessions;

namespace PiSharp.Tests;

public sealed class ToolResultContractTests
{
    [Fact]
    public async Task OutputSchemaRejectsInvalidStructuredContent()
    {
        var root = Path.Combine(Path.GetTempPath(), "pisharp-result-schema-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            using var schemaDocument = JsonDocument.Parse("""{"type":"object","required":["count"],"properties":{"count":{"type":"integer"}}}""");
            using var invalidDocument = JsonDocument.Parse("""{"count":"wrong"}""");
            var tool = AIFunctionFactory.Create(() => new PiSharpToolResult("invalid", StructuredContent: invalidDocument.RootElement.Clone()),
                name: "parent");
            var client = new ResultClient();
            var conversation = new ConversationSession(root, "fixture", null);
            var run = await ConversationRun.OpenAsync(new PiAgent(client, new CodingTools(root), noBuiltinTools: true,
                extensionToolRegistrations: [new(tool, ToolExposure.ModelOnly,
                    OutputSchema: schemaDocument.RootElement.Clone())]), conversation,
                save: _ => Task.CompletedTask);
            var events = new List<AgentLifecycleEvent>();
            await foreach (var item in run.RunEventsAsync("validate")) events.Add(item);
            var finished = Assert.Single(events, item => item.Type == "tool_execution_finished");
            Assert.True(finished.IsError);
            Assert.Contains("does not match", finished.Error);
            Assert.Contains(conversation.Tree.Entries, entry => entry.Type == "tool_outcome" &&
                entry.Payload.GetProperty("error").GetString()!.Contains("does not match", StringComparison.Ordinal));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task SingleToolTerminationSkipsProviderContinuation()
    {
        var root = Path.Combine(Path.GetTempPath(), "pisharp-result-termination-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var tool = AIFunctionFactory.Create(() => new PiSharpToolResult("stop here", Terminate: true), name: "parent");
            var client = new ResultClient();
            var conversation = new ConversationSession(root, "fixture", null);
            var run = await ConversationRun.OpenAsync(new PiAgent(client, new CodingTools(root), noBuiltinTools: true,
                extensionToolRegistrations: [new(tool, ToolExposure.ModelOnly)]), conversation);
            var events = new List<AgentLifecycleEvent>();
            await foreach (var item in run.RunEventsAsync("stop")) events.Add(item);
            Assert.Equal(1, client.RequestCount);
            Assert.True(Assert.Single(events, item => item.Type == "tool_execution_finished").Terminate);
            var exported = PiJsonlSessionInterchange.ProjectEntries(conversation).Single(entry =>
                entry.TryGetProperty("message", out var message) && message.GetProperty("role").GetString() == "toolResult");
            Assert.True(exported.GetProperty("message").GetProperty("terminate").GetBoolean());
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task ReturnedErrorKeepsStructuredDataAndImageAcrossRootNestedAndResume()
    {
        var root = Path.Combine(Path.GetTempPath(), "pisharp-result-contract-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            using var document = JsonDocument.Parse("""{"count":2,"ok":false}""");
            using var schemaDocument = JsonDocument.Parse("""{"type":"object","required":["count"],"properties":{"count":{"type":"integer"}}}""");
            var structured = document.RootElement.Clone();
            var image = new PiSharpToolImage("image/png", Convert.ToBase64String([137, 80, 78, 71]));
            var usage = new UsageDetails { InputTokenCount = 3, OutputTokenCount = 5, TotalTokenCount = 8 };
            var leaf = AIFunctionFactory.Create(() => new PiSharpToolResult("leaf text", new { source = "leaf" },
                structured, Images: [image], Usage: usage), name: "leaf");
            var parent = AIFunctionFactory.Create(async (AIFunctionArguments arguments, CancellationToken token) =>
            {
                var nested = await PiSharpToolExecutionContext.Get(arguments)!.ExecuteToolAsync("leaf",
                    cancellationToken: token);
                Assert.False(nested.IsError);
                Assert.Equal("leaf text", nested.Text);
                Assert.Equal(2, nested.StructuredContent?.GetProperty("count").GetInt32());
                Assert.Single(nested.Images!);
                Assert.Equal(8, nested.Usage?.TotalTokenCount);
                return new PiSharpToolResult("returned failure", new { source = "parent" }, structured,
                    IsError: true, Error: "domain failure", Images: [image], Usage: usage);
            }, name: "parent");
            var client = new ResultClient();
            var conversation = new ConversationSession(root, "fixture", null);
            var run = await ConversationRun.OpenAsync(new PiAgent(client, new CodingTools(root), noBuiltinTools: true,
                extensionToolRegistrations:
                [
                    new(parent, ToolExposure.ModelOnly, OutputSchema: schemaDocument.RootElement.Clone()),
                    new(leaf, ToolExposure.Deferred, OutputSchema: schemaDocument.RootElement.Clone())
                ]), conversation, save: _ => Task.CompletedTask);
            var events = new List<AgentLifecycleEvent>();
            await foreach (var item in run.RunEventsAsync("use parent")) events.Add(item);

            var rootEnd = Assert.Single(events, item => item.Type == "tool_execution_finished" && item.Tool == "parent");
            Assert.True(rootEnd.IsError);
            Assert.Equal("domain failure", rootEnd.Error);
            Assert.Equal("returned failure", rootEnd.Text);
            Assert.Single(rootEnd.Images!);
            Assert.Equal(8, rootEnd.ToolUsage?.TotalTokenCount);
            var rpc = new RpcEventProjector();
            var projectedEnd = rpc.Project(rootEnd, conversation, null, null, runAccepted: true, api: null)
                .Select(item => JsonSerializer.SerializeToElement(item))
                .Single(item => item.GetProperty("type").GetString() == "tool_execution_end");
            var projectedResult = projectedEnd.GetProperty("result");
            Assert.Equal("returned failure", projectedResult.GetProperty("content")[0].GetProperty("text").GetString());
            Assert.Equal("image/png", projectedResult.GetProperty("content")[1].GetProperty("mimeType").GetString());
            Assert.Equal(2, projectedResult.GetProperty("structuredContent").GetProperty("count").GetInt32());
            Assert.Equal(8, projectedResult.GetProperty("usage").GetProperty("totalTokens").GetInt32());
            var nestedEnd = Assert.Single(events, item => item.Type == "tool_execution_finished" && item.Tool == "leaf");
            Assert.False(nestedEnd.IsError);
            Assert.Equal("root-call", nestedEnd.ParentToolCallId);

            var rootResult = Assert.Single(conversation.ActiveMessages().SelectMany(message => message.Contents)
                .OfType<FunctionResultContent>());
            Assert.Null(rootResult.Exception);
            Assert.True(PiSharp.Runtime.Tools.ToolResultOutput.TryReadContract(rootResult.Result, out var contract));
            Assert.True(contract.IsError);
            Assert.False(contract.Terminate);
            Assert.Equal(2, contract.StructuredContent?.GetProperty("count").GetInt32());
            Assert.Equal(8, contract.Usage?.TotalTokenCount);
            Assert.Equal("returned failure", client.ProviderResult);
            Assert.Single(client.ProviderImages!);
            var parentOutcome = conversation.Tree.Entries.Single(entry => entry.Type == "tool_outcome" &&
                entry.Payload.GetProperty("result").GetString() == "returned failure");
            Assert.True(parentOutcome.Payload.GetProperty("returnedError").GetBoolean());
            Assert.Equal("domain failure", parentOutcome.Payload.GetProperty("error").GetString());

            var restored = ConversationSession.Parse(conversation.ToJson());
            var restoredResult = Assert.Single(restored.ActiveMessages().SelectMany(message => message.Contents)
                .OfType<FunctionResultContent>());
            Assert.Null(restoredResult.Exception);
            Assert.True(PiSharp.Runtime.Tools.ToolResultOutput.TryReadContract(restoredResult.Result, out var restoredContract));
            Assert.Equal("domain failure", restoredContract.Error);

            var imported = PiJsonlSessionInterchange.Import(PiJsonlSessionInterchange.Export(conversation));
            var importedResult = Assert.Single(imported.ActiveMessages().SelectMany(message => message.Contents)
                .OfType<FunctionResultContent>());
            Assert.True(PiSharp.Runtime.Tools.ToolResultOutput.TryReadContract(importedResult.Result, out var importedContract));
            Assert.True(importedContract.IsError);
            Assert.Equal(2, importedContract.StructuredContent?.GetProperty("count").GetInt32());
            var exportedResult = PiJsonlSessionInterchange.ProjectEntries(conversation).Single(entry =>
                entry.TryGetProperty("message", out var message) && message.GetProperty("role").GetString() == "toolResult");
            Assert.Equal(8, exportedResult.GetProperty("message").GetProperty("usage").GetProperty("totalTokens").GetInt32());
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private sealed class ResultClient : IChatClient
    {
        private int _requests;
        public int RequestCount => _requests;
        public string? ProviderResult { get; private set; }
        public DataContent[]? ProviderImages { get; private set; }

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            if (Interlocked.Increment(ref _requests) == 1)
                yield return new ChatResponseUpdate(ChatRole.Assistant,
                    [new FunctionCallContent("root-call", "parent", new Dictionary<string, object?>())]);
            else
            {
                ProviderResult = messages.SelectMany(message => message.Contents).OfType<FunctionResultContent>()
                    .Single().Result as string;
                ProviderImages = messages.SelectMany(message => message.Contents).OfType<DataContent>().ToArray();
                yield return new ChatResponseUpdate(ChatRole.Assistant, "done");
            }
            await Task.CompletedTask;
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }
}
