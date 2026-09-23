using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;
using PiSharp.Runtime;
using PiSharp.Runtime.Sessions;

namespace PiSharp.Tests;

public sealed class AgentLifecycleTests
{
    [Fact]
    public async Task RealProviderCallsAndToolInvocationAreOrderedAcrossTwoModelRequests()
    {
        var cwd = Path.Combine(Path.GetTempPath(), "pisharp-events-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(cwd);
        try
        {
            var session = new ConversationSession(cwd, "fixture", null);
            var run = await ConversationRun.OpenAsync(new PiAgent(new ToolClient(), new CodingTools(cwd)), session);
            var events = new List<AgentLifecycleEvent>();
            await foreach (var item in run.RunEventsAsync("write file")) events.Add(item);
            var names = events.Select(item => item.Type).ToList();
            var firstModel = names.IndexOf("model_request_started");
            var call = names.IndexOf("tool_execution_started");
            var outcome = names.IndexOf("tool_execution_finished");
            var secondModel = names.FindIndex(firstModel + 1, item => item == "model_request_started");
            Assert.Equal("prompt_accepted", names[0]);
            Assert.True(firstModel > 0 && call > firstModel && outcome > call && secondModel > outcome);
            Assert.Contains(events, item => item.Type == "model_text_delta" && item.Text == "done");
            Assert.Equal(["turn_completed", "agent_run_completed", "agent_settled"], names.TakeLast(3));
            Assert.Equal("made", await File.ReadAllTextAsync(Path.Combine(cwd, "result.txt")));
        }
        finally { Directory.Delete(cwd, recursive: true); }
    }

    [Fact]
    public async Task ProviderFailureNeverProducesSuccessfulCompletion()
    {
        var cwd = Path.GetTempPath();
        var session = new ConversationSession(cwd, "fixture", null);
        var run = await ConversationRun.OpenAsync(new PiAgent(new FailClient(), new CodingTools(cwd)), session);
        var events = new List<AgentLifecycleEvent>();
        await foreach (var item in run.RunEventsAsync("fail")) events.Add(item);
        Assert.Contains(events, item => item.Type == "model_request_failed" && item.Error == "provider broke");
        Assert.Contains(events, item => item.Type == "turn_failed");
        Assert.DoesNotContain(events, item => item.Type is "turn_completed" or "agent_run_completed");
        Assert.Equal("agent_settled", events[^1].Type);
    }

    private sealed class ToolClient : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            if (!messages.SelectMany(m => m.Contents).OfType<FunctionResultContent>().Any())
                yield return new ChatResponseUpdate(ChatRole.Assistant,
                    [new FunctionCallContent("call1", "write", new Dictionary<string, object?> { ["path"] = "result.txt", ["content"] = "made" })]);
            else yield return new ChatResponseUpdate(ChatRole.Assistant, "done");
            await Task.CompletedTask;
        }
        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    private sealed class FailClient : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            throw new IOException("provider broke");
#pragma warning disable CS0162
            yield break;
#pragma warning restore CS0162
        }
        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }
}
