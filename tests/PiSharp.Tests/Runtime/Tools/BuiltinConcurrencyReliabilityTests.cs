using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.AI;
using PiSharp.Cli.Protocols;
using PiSharp.Runtime;
using PiSharp.Runtime.Sessions;
using PiSharp.Runtime.Tools;

namespace PiSharp.Tests;

public sealed class BuiltinConcurrencyReliabilityTests
{
    [Fact]
    public async Task IndependentBuiltinsFinishWhileBashWaitsAndRetainEveryOutcomeAcrossCompaction()
    {
        if (!OperatingSystem.IsLinux()) return;
        using var fixture = SearchProcessProtocolTests.SourceFixture("concurrent", "agent");
        var source = fixture.RootElement;
        var root = Path.Combine(Path.GetTempPath(), "pisharp-concurrent-builtins-" + Guid.NewGuid().ToString("N"));
        var workspace = Path.Combine(root, "workspace");
        Directory.CreateDirectory(workspace);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var writes = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<List<AgentLifecycleEvent>>? running = null;
        try
        {
            foreach (var file in source.GetProperty("files").EnumerateObject())
            {
                var target = Path.Combine(workspace, file.Name);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                await File.WriteAllTextAsync(target, file.Value.GetString(), deadline.Token);
            }
            Assert.Equal(0, (await DirectProcessExecution.ExecuteAsync("mkfifo", [Path.Combine(workspace, "gate")],
                workspace, null, deadline.Token)).ExitCode);
            var conversation = new ConversationSession(workspace, "fixture-model", null);
            conversation.Append(new ChatMessage(ChatRole.User, "Earlier unrelated conversation."));
            conversation.Append(new ChatMessage(ChatRole.Assistant, new string('x', 100_000)));
            var client = new ParallelClient(source);
            var tools = source.GetProperty("tools").EnumerateArray().Select(value => value.GetString()!).ToArray();
            var store = new ConversationStore(workspace, Path.Combine(root, "sessions"));
            var path = store.NewPath(conversation);
            var run = await ConversationRun.OpenAsync(new PiAgent(client, new CodingTools(workspace), selectedTools: tools),
                conversation, save: token => store.SaveAsync(conversation, path, token));
            running = Task.Run(async () =>
            {
                var events = new List<AgentLifecycleEvent>();
                await foreach (var item in run.RunEventsAsync("Run independent tools together while Bash waits.", deadline.Token))
                {
                    events.Add(item);
                    if (item.Type == "tool_execution_update" && item.Text?.Contains("batch-ready", StringComparison.Ordinal) == true)
                        ready.TrySetResult();
                    if (events.Count(item => item.Type == "tool_execution_finished" && item.Tool == "write") == 2)
                        writes.TrySetResult();
                }
                return events;
            }, deadline.Token);
            await Task.WhenAll(ready.Task, writes.Task).WaitAsync(deadline.Token);
            Assert.False(running.IsCompleted);
            Assert.Equal(1, client.Requests);
            Assert.Equal("first mutation\n", await File.ReadAllTextAsync(Path.Combine(workspace, "out", "first.txt"), deadline.Token));
            Assert.Equal("second mutation\n", await File.ReadAllTextAsync(Path.Combine(workspace, "out", "second.txt"), deadline.Token));
            await Task.Run(() => File.WriteAllText(Path.Combine(workspace, "gate"), "release\n"), deadline.Token).WaitAsync(deadline.Token);
            var events = await running.WaitAsync(deadline.Token);
            Assert.Equal(2, client.Requests);
            var expected = source.GetProperty("outcomes").EnumerateArray().ToArray();
            var finished = events.Where(item => item.Type == "tool_execution_finished").ToArray();
            Assert.Equal(expected.Length, finished.Length);
            foreach (var result in expected)
            {
                var actual = Assert.Single(finished, item => item.ToolCallId == result.GetProperty("callId").GetString());
                Assert.False(actual.IsError);
                Assert.Equal(Text(result.GetProperty("content")), actual.Text);
            }
            foreach (var file in source.GetProperty("finalFiles").EnumerateObject())
                Assert.Equal(file.Value.GetString(), await File.ReadAllTextAsync(Path.Combine(workspace, file.Name), deadline.Token));
            await store.SaveAsync(conversation, path, deadline.Token);
            var loaded = await store.LoadAsync(path, deadline.Token);
            AssertCorrelatedResults(loaded, source);
            var plan = Assert.IsType<ConversationSession.CompactionPlan>(loaded.PrepareCompaction(keepRecentTokens: 10_000));
            loaded.AppendCompaction(plan, "Earlier unrelated turn summarized.", keepRecentTokens: 10_000);
            await store.SaveAsync(loaded, path, deadline.Token);
            var compacted = await store.LoadAsync(path, deadline.Token);
            AssertCorrelatedResults(compacted, source);
            var retainedResults = compacted.ContextMessages().SelectMany(message => message.Contents).OfType<FunctionResultContent>().ToArray();
            Assert.Equal(expected.Length, retainedResults.Length);
            Assert.True(source.GetProperty("compaction").GetProperty("actualSessionFileReload").GetBoolean());
            var orderedIds = source.GetProperty("plan").EnumerateArray().Select(item => item.GetProperty("callId").GetString()).ToArray();
            Assert.Equal(orderedIds, retainedResults.Select(result => result.CallId));
            Assert.Equal(orderedIds,
                source.GetProperty("compaction").GetProperty("retainedResultIds").EnumerateArray().Select(item => item.GetString()));
            var projected = RpcSessionMessageProjector.Project(compacted, "openai-completions");
            var rpcResults = projected.OfType<JsonObject>().Where(message => message["role"]?.GetValue<string>() == "toolResult").ToArray();
            Assert.Equal(expected.Length, rpcResults.Length);
            foreach (var result in expected)
            {
                var actual = Assert.Single(rpcResults, item => item["toolCallId"]?.GetValue<string>() == result.GetProperty("callId").GetString());
                Assert.Equal(result.GetProperty("tool").GetString(), actual["toolName"]!.GetValue<string>());
            }
        }
        finally
        {
            deadline.Cancel();
            if (running is not null)
                try { await running.WaitAsync(TimeSpan.FromSeconds(10)); } catch (OperationCanceledException) { }
            Directory.Delete(root, recursive: true);
        }
    }

    private static void AssertCorrelatedResults(ConversationSession session, JsonElement source)
    {
        var contents = session.ActiveMessages().SelectMany(message => message.Contents).ToArray();
        var calls = contents.OfType<FunctionCallContent>().ToArray();
        var results = contents.OfType<FunctionResultContent>().ToArray();
        Assert.Equal(source.GetProperty("plan").GetArrayLength(), calls.Length);
        Assert.Equal(calls.Length, results.Length);
        Assert.Equal(calls.Select(call => call.CallId).Order(), results.Select(result => result.CallId).Order());
        Assert.Equal(calls.Length, calls.Select(call => call.CallId).Distinct().Count());
    }

    private static string Text(JsonElement content) => string.Concat(content.EnumerateArray()
        .Where(part => part.GetProperty("type").GetString() == "text").Select(part => part.GetProperty("text").GetString()));

    private sealed class ParallelClient(JsonElement fixture) : IChatClient
    {
        public int Requests { get; private set; }
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            if (Requests++ == 0)
            {
                yield return new ChatResponseUpdate(ChatRole.Assistant, fixture.GetProperty("plan").EnumerateArray()
                    .Select(item => (AIContent)new FunctionCallContent(item.GetProperty("callId").GetString()!,
                        item.GetProperty("tool").GetString()!, item.GetProperty("arguments").EnumerateObject()
                            .ToDictionary(property => property.Name, property => (object?)property.Value.Clone()))).ToArray());
            }
            else
            {
                var expected = fixture.GetProperty("requests")[1].EnumerateArray().ToArray();
                var results = messages.SelectMany(message => message.Contents).OfType<FunctionResultContent>().ToArray();
                Assert.Equal(expected.Select(result => result.GetProperty("callId").GetString()), results.Select(result => result.CallId));
                foreach (var item in expected)
                {
                    var result = Assert.Single(results, result => result.CallId == item.GetProperty("callId").GetString());
                    var value = result.Result is JsonElement json ? json.GetString() : Assert.IsType<string>(result.Result);
                    Assert.Equal(Text(item.GetProperty("content")), value);
                }
                yield return new ChatResponseUpdate(ChatRole.Assistant, "parallel tools complete");
            }
            await Task.CompletedTask;
        }
        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }
}
