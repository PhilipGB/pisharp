using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;
using PiSharp.Runtime;
using PiSharp.Runtime.Sessions;

namespace PiSharp.Tests;

public sealed class CapacityRetryTests
{
    [Theory]
    [InlineData("Selected model is at capacity", true)]
    [InlineData("SELECTED MODEL IS AT CAPACITY. Try another model.", true)]
    [InlineData("Selected model is at capacity; billing limit reached", false)]
    [InlineData("Your account capacity is exhausted", false)]
    public void CapacityClassificationKeepsAccountLimitsTerminal(string message, bool expected)
    {
        Assert.Equal(expected, AgentRunRetryPolicy.Default.CanRetry(new IOException(message), message, 0, true));
    }

    [Theory]
    [InlineData(true, 1, 2)]
    [InlineData(true, 0, 1)]
    [InlineData(false, 2, 1)]
    public async Task SharedAgentPathRetriesCapacityOnlyWithinTheEnabledBudget(bool enabled, int retries, int requests)
    {
        var client = new CapacityClient();
        var agent = new PiAgent(client, new CodingTools(Path.GetTempPath()), noTools: true,
            retryPolicy: ProviderRetryPolicy.None);
        var conversation = new ConversationSession(Path.GetTempPath(), "fixture", null);
        var run = await ConversationRun.OpenAsync(agent, conversation,
            retryPolicy: new AgentRunRetryPolicy(enabled, retries, TimeSpan.Zero, TimeSpan.Zero));
        var events = new List<AgentLifecycleEvent>();

        await foreach (var item in run.RunEventsAsync("go")) events.Add(item);

        Assert.Equal(requests, client.Requests);
        if (requests == 2)
        {
            var scheduled = Assert.Single(events, item => item.Type == "auto_retry_start");
            Assert.Equal("Selected model is at capacity", scheduled.Error);
            Assert.Equal(1, scheduled.RetryAttempt);
            Assert.True(Assert.Single(events, item => item.Type == "auto_retry_end").RetrySuccess);
            Assert.Contains(events, item => item.Type == "turn_completed");
            Assert.Equal("done", conversation.ContextMessages().Last().Text);
        }
        else
        {
            Assert.DoesNotContain(events, item => item.Type.StartsWith("auto_retry_", StringComparison.Ordinal));
            Assert.Contains(events, item => item.Type == "turn_failed" && item.WillRetry == false);
        }
    }

    private sealed class CapacityClient : IChatClient
    {
        public int Requests { get; private set; }
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            if (++Requests == 1) throw new IOException("Selected model is at capacity");
            yield return new ChatResponseUpdate(ChatRole.Assistant, "done");
            await Task.CompletedTask;
        }
        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }
}
