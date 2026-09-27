using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;
using PiSharp.Cli;

namespace PiSharp.Tests;

public sealed class ProviderRequestTimeoutTests
{
    [Fact]
    public async Task StreamingIdleTimeoutCancelsTheProviderAfterItsLastUpdate()
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        var inner = new WaitingAfterFirstUpdateClient();
        var client = new ProviderRequestTimeoutChatClient(inner, requestTimeoutMs: 2_000, idleTimeoutMs: 80);
        await using var updates = client.GetStreamingResponseAsync([], cancellationToken: cancellation.Token)
            .GetAsyncEnumerator(cancellation.Token);

        Assert.True(await updates.MoveNextAsync());
        var timer = System.Diagnostics.Stopwatch.StartNew();
        var error = await Assert.ThrowsAsync<TimeoutException>(() => updates.MoveNextAsync().AsTask());

        Assert.Contains("no update", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.True(timer.Elapsed < TimeSpan.FromSeconds(1), $"The idle timeout took {timer.Elapsed}.");
        Assert.True(inner.CancellationObserved);
    }

    private sealed class WaitingAfterFirstUpdateClient : IChatClient
    {
        public bool CancellationObserved { get; private set; }

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            yield return new ChatResponseUpdate(ChatRole.Assistant, "first");
            try { await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken); }
            catch (OperationCanceledException)
            {
                CancellationObserved = true;
                throw;
            }
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }
}
