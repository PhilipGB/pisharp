using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;
using PiSharp.Runtime.Extensions;
using PiSharp.Runtime.Sessions;

namespace PiSharp.Tests;

public sealed class ObservedChatClientContextMessagesTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task RequestKeepsIdenticalOccurrencesAndReconcilesClonedSnapshot(bool streaming, bool snapshotIncludesOccurrence)
    {
        var earlier = PiSharpCustomMessageProjection.ToChatMessage(new("status", "finished"));
        earlier.MessageId = "custom-earlier";
        var pending = PiSharpCustomMessageProjection.ToChatMessage(new("status", "finished"));
        pending.MessageId = "custom-pending";
        var snapshot = new List<ChatMessage>
        {
            earlier,
            new(ChatRole.Assistant, [new FunctionCallContent("call", "hold", new Dictionary<string, object?>())]),
            new(ChatRole.Tool, [new FunctionResultContent("call", "tool result")])
        };
        if (snapshotIncludesOccurrence) snapshot.Add(pending.Clone());
        var provider = new RecordingClient();
        using var observed = new ObservedChatClient(provider, _ => { }, ProviderRetryPolicy.None, _ => [],
            takeContextMessagesForRequestAsync: _ => Task.FromResult<IReadOnlyList<ChatMessage>>([pending]));

        if (streaming)
            await foreach (var _ in observed.GetStreamingResponseAsync(snapshot)) { }
        else
            await observed.GetResponseAsync(snapshot);

        var request = Assert.Single(provider.Requests);
        Assert.Equal(["custom-earlier", "custom-pending"], request
            .Where(message => PiSharpCustomMessageProjection.TryRead(message, out _))
            .Select(message => message.MessageId));
        Assert.Equal(ChatRole.Tool, request[^2].Role);
        Assert.Equal("custom-pending", request[^1].MessageId);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailedContextPersistencePreventsProviderDispatch(bool streaming)
    {
        var provider = new RecordingClient();
        using var observed = new ObservedChatClient(provider, _ => { }, ProviderRetryPolicy.None, _ => [],
            takeContextMessagesForRequestAsync: _ => Task.FromException<IReadOnlyList<ChatMessage>>(
                new IOException("Session save failed.")));

        var error = await Assert.ThrowsAsync<IOException>(async () =>
        {
            if (streaming)
                await foreach (var _ in observed.GetStreamingResponseAsync([new(ChatRole.User, "continue")])) { }
            else
                await observed.GetResponseAsync([new(ChatRole.User, "continue")]);
        });

        Assert.Equal("Session save failed.", error.Message);
        Assert.Empty(provider.Requests);
    }

    private sealed class RecordingClient : IChatClient
    {
        public List<ChatMessage[]> Requests { get; } = [];

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            Requests.Add(messages.ToArray());
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "done")));
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            Requests.Add(messages.ToArray());
            yield return new ChatResponseUpdate(ChatRole.Assistant, "done");
            await Task.CompletedTask;
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }
}
