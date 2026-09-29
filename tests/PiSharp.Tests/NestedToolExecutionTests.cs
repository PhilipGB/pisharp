using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Extensions.AI;
using PiSharp.Runtime;
using PiSharp.Runtime.Extensions;
using PiSharp.Runtime.Sessions;

namespace PiSharp.Tests;

public sealed class NestedToolExecutionTests
{
    [Fact]
    public async Task NestedCallsUseWrappedToolsAndPersistHierarchicalLifecycleAndSessionMetadata()
    {
        var cwd = Path.Combine(Path.GetTempPath(), "pisharp-nested-tools-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(cwd);
        try
        {
            var protectedInvocations = 0;
            var callHooks = new List<(string CallId, string? Parent, string Name)>();
            var resultHooks = new List<(string CallId, string? Parent, string Name)>();
            var hookedResults = new List<(string CallId, string? Value, bool IsError)>();
            var leaf = AIFunctionFactory.Create((string value) => value == "fail"
                ? throw new InvalidOperationException("leaf failed")
                : "leaf:" + value, name: "leaf");
            var protectedTool = AIFunctionFactory.Create(() =>
            {
                protectedInvocations++;
                return "should not run";
            }, name: "protected");
            var child = AIFunctionFactory.Create(async (string value, AIFunctionArguments arguments,
                CancellationToken cancellationToken) =>
            {
                var context = PiSharpToolExecutionContext.Get(arguments)!;
                var success = await context.ExecuteToolAsync("leaf", new Dictionary<string, object?> { ["value"] = value }, cancellationToken);
                var failed = await context.ExecuteToolAsync("leaf", new Dictionary<string, object?> { ["value"] = "fail" }, cancellationToken);
                var blocked = await context.ExecuteToolAsync("protected", cancellationToken: cancellationToken);
                Assert.False(success.IsError);
                Assert.True(failed.IsError);
                Assert.True(blocked.IsError);
                return success.Text + "|" + failed.Error + "|" + blocked.Error;
            }, name: "child");
            var orchestrate = AIFunctionFactory.Create(async (AIFunctionArguments arguments,
                CancellationToken cancellationToken) =>
            {
                var result = await PiSharpToolExecutionContext.Get(arguments)!.ExecuteToolAsync("child",
                    new Dictionary<string, object?> { ["value"] = "work" }, cancellationToken);
                Assert.False(result.IsError);
                return result.Text;
            }, name: "orchestrate");
            var client = new NestedToolClient();
            var conversation = new ConversationSession(cwd, "fixture", null);
            var agent = new PiAgent(client, new CodingTools(cwd), noBuiltinTools: true,
                extensionToolRegistrations:
                [
                    new(orchestrate, ToolExposure.ModelOnly),
                    new(child, ToolExposure.Deferred),
                    new(leaf, ToolExposure.Deferred),
                    new(protectedTool, ToolExposure.Deferred)
                ],
                extensionToolCallHooks:
                [
                    (context, _) =>
                    {
                        callHooks.Add((context.ToolCallId, context.ParentToolCallId, context.ToolName));
                        if (context.ToolName == "leaf" && context.Arguments.TryGetValue("value", out var value) &&
                            Equals(value, "work"))
                            context.Arguments["value"] = "hooked";
                        return ValueTask.FromResult(context.ToolName == "protected"
                            ? PiSharpToolCallDecision.Block("protected by extension policy")
                            : PiSharpToolCallDecision.Allow);
                    }
                ],
                extensionToolResultHooks:
                [
                    (context, _) =>
                    {
                        resultHooks.Add((context.ToolCallId, context.ParentToolCallId, context.ToolName));
                        if (!context.IsError && context.ToolName == "leaf")
                            context.Result = "hook:" + context.Result;
                        hookedResults.Add((context.ToolCallId, context.Result?.ToString(), context.IsError));
                        return ValueTask.CompletedTask;
                    }
                ]);
            var saveCount = 0;
            var run = await ConversationRun.OpenAsync(agent, conversation, save: _ =>
            {
                Interlocked.Increment(ref saveCount);
                return Task.CompletedTask;
            });
            var events = new List<AgentLifecycleEvent>();
            await foreach (var item in run.RunEventsAsync("orchestrate")) events.Add(item);

            Assert.Equal(["orchestrate"], client.FirstRequestToolNames!);
            Assert.Equal(["root-call"], client.ContinuationResults!);
            Assert.Collection(events.Where(item => item.Type == "tool_execution_started"),
                item => { Assert.Equal("root-call", item.ToolCallId); Assert.Null(item.ParentToolCallId); },
                item => { Assert.Equal("root-call/1", item.ToolCallId); Assert.Equal("root-call", item.ParentToolCallId); },
                item => { Assert.Equal("root-call/1/1", item.ToolCallId); Assert.Equal("root-call/1", item.ParentToolCallId); },
                item => { Assert.Equal("root-call/1/2", item.ToolCallId); Assert.Equal("root-call/1", item.ParentToolCallId); },
                item => { Assert.Equal("root-call/1/3", item.ToolCallId); Assert.Equal("root-call/1", item.ParentToolCallId); });
            Assert.Equal(5, events.Count(item => item.Type == "tool_execution_finished"));
            Assert.Contains(events, item => item.ToolCallId == "root-call/1/2" && item.IsError == true);
            Assert.Contains(events, item => item.ToolCallId == "root-call/1/3" &&
                item.IsError == true && item.Error == "protected by extension policy");
            Assert.Equal(5, resultHooks.Count);
            Assert.Equal("hook:leaf:hooked", hookedResults.Single(item => item.CallId == "root-call/1/1").Value);
            Assert.Equal("hook:leaf:hooked", events.Single(item => item.ToolCallId == "root-call/1/1" &&
                item.Type == "tool_execution_finished").Text);
            Assert.Equal(0, protectedInvocations);
            Assert.Equal(["root-call", "root-call/1", "root-call/1/1", "root-call/1/2", "root-call/1/3"],
                callHooks.Select(item => item.CallId));
            Assert.Equal(callHooks.Select(item => (item.CallId, item.Parent, item.Name)).OrderBy(item => item.CallId),
                resultHooks.Select(item => (item.CallId, item.Parent, item.Name)).OrderBy(item => item.CallId));
            Assert.Equal(5, conversation.Tree.Entries.Count(entry => entry.Type == "tool_intent"));
            Assert.Equal(5, conversation.Tree.Entries.Count(entry => entry.Type == "tool_outcome"));
            Assert.True(saveCount >= 9);

            var resultMessages = conversation.ActiveMessages().Where(message => message.Role == ChatRole.Tool).ToArray();
            var rootResult = Assert.Single(resultMessages.SelectMany(message => message.Contents).OfType<FunctionResultContent>());
            Assert.Equal("root-call", rootResult.CallId);
            var exported = PiJsonlSessionInterchange.ProjectEntries(conversation)
                .Single(entry => entry.TryGetProperty("message", out var message) &&
                    message.TryGetProperty("role", out var role) && role.GetString() == "toolResult");
            var nestedCalls = exported.GetProperty("message").GetProperty("nestedCalls");
            Assert.Equal(4, nestedCalls.GetProperty("calls").GetArrayLength());
            Assert.Equal("error", nestedCalls.GetProperty("calls")[2].GetProperty("status").GetString());
            Assert.Equal("error", nestedCalls.GetProperty("calls")[3].GetProperty("status").GetString());
            Assert.True(nestedCalls.GetProperty("complete").GetBoolean());

            var imported = PiJsonlSessionInterchange.Import(PiJsonlSessionInterchange.Export(conversation));
            var reexported = PiJsonlSessionInterchange.ProjectEntries(imported)
                .Single(entry => entry.TryGetProperty("message", out var message) &&
                    message.TryGetProperty("role", out var role) && role.GetString() == "toolResult");
            Assert.Equal(4, reexported.GetProperty("message").GetProperty("nestedCalls").GetProperty("calls").GetArrayLength());
        }
        finally { Directory.Delete(cwd, recursive: true); }
    }

    private sealed class NestedToolClient : IChatClient
    {
        private int _requests;
        public string[]? FirstRequestToolNames { get; private set; }
        public string[]? ContinuationResults { get; private set; }

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            if (Interlocked.Increment(ref _requests) == 1)
            {
                FirstRequestToolNames = options?.Tools?.OfType<AIFunction>().Select(tool => tool.Name).ToArray();
                yield return new ChatResponseUpdate(ChatRole.Assistant,
                    [new FunctionCallContent("root-call", "orchestrate", new Dictionary<string, object?>())]);
            }
            else
            {
                ContinuationResults = messages.SelectMany(message => message.Contents)
                    .OfType<FunctionResultContent>().Select(result => result.CallId).ToArray();
                yield return new ChatResponseUpdate(ChatRole.Assistant, "done");
            }
            await Task.CompletedTask;
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }
}
