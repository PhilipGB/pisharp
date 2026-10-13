using System.Net;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;
using PiSharp.Cli;

namespace PiSharp.Tests;

public sealed class ProviderRetryChatClientTests
{
    [Fact]
    public async Task StreamingFailureAfterAnUpdateIsNotRetried()
    {
        var inner = new PartialThenFailingChatClient();
        var client = new ProviderRetryChatClient(inner, maxRetries: 2, maxRetryDelayMs: 0);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        await using var updates = client.GetStreamingResponseAsync([], cancellationToken: cancellation.Token)
            .GetAsyncEnumerator(cancellation.Token);

        Assert.True(await updates.MoveNextAsync());
        Assert.Equal("partial", updates.Current.Text);
        await Assert.ThrowsAsync<HttpRequestException>(() => updates.MoveNextAsync().AsTask());

        Assert.Equal(1, inner.Attempts);
    }

    private sealed class PartialThenFailingChatClient : IChatClient
    {
        public int Attempts { get; private set; }

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            Attempts++;
            yield return new ChatResponseUpdate(ChatRole.Assistant, "partial");
            await Task.Yield();
            throw new HttpRequestException("connection dropped", null, HttpStatusCode.ServiceUnavailable);
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }
}
