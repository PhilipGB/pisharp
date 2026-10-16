using System.Net;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;
using PiSharp.Cli;

namespace PiSharp.Tests;

public sealed class ProviderRetryCancellationTests
{
    [Theory]
    [InlineData(false, 0)]
    [InlineData(false, 1)]
    [InlineData(true, 0)]
    [InlineData(true, 1)]
    public async Task CancellationWhileAProviderRejectsTheRequestSettlesAsCanceledWithoutRetry(bool streaming, int maxRetries)
    {
        using var cancellation = new CancellationTokenSource();
        using var rejecting = new RejectingClient(cancellation);
        using var client = new ProviderRetryChatClient(rejecting, maxRetries, maxRetryDelayMs: 0);
        var response = streaming
            ? client.GetStreamingResponseAsync([new(ChatRole.User, "hello")], cancellationToken: cancellation.Token)
                .ToChatResponseAsync(cancellation.Token)
            : client.GetResponseAsync([new(ChatRole.User, "hello")], cancellationToken: cancellation.Token);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => response);
        Assert.Equal(1, rejecting.Requests);
    }

    private sealed class RejectingClient(CancellationTokenSource cancellation) : IChatClient
    {
        internal int Requests { get; private set; }
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            Requests++;
            cancellation.Cancel();
            return Task.FromException<ChatResponse>(Rejection());
        }
        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            Requests++;
            await Task.Yield();
            cancellation.Cancel();
            if (Requests > 0) throw Rejection();
            yield break;
        }
        private static HttpRequestException Rejection() => new("rate limited", null, HttpStatusCode.TooManyRequests);
        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }
}
