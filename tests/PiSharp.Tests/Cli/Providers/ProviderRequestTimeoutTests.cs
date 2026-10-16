using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;
using PiSharp.Cli;

namespace PiSharp.Tests;

public sealed class ProviderRequestTimeoutTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AResponseReturnedAfterTheDeadlineIsRejectedWhenTheProviderIgnoredCancellation(bool streaming)
    {
        using var inner = new SilentAbortClient(responseDelayMs: 180);
        using var client = new ProviderRequestTimeoutChatClient(inner, requestTimeoutMs: 80, idleTimeoutMs: 2000);
        var response = streaming ? client.GetStreamingResponseAsync([]).ToChatResponseAsync() : client.GetResponseAsync([]);
        await Assert.ThrowsAsync<TimeoutException>(() => response);
    }

    [Fact]
    public async Task DisabledIdleTimeoutAcceptsAResponseInsideTheRequestDeadline()
    {
        using var inner = new SilentAbortClient(responseDelayMs: 150);
        using var client = new ProviderRequestTimeoutChatClient(inner, requestTimeoutMs: 2000, idleTimeoutMs: 0);
        var response = await client.GetStreamingResponseAsync([]).ToChatResponseAsync();
        Assert.Equal("", response.Text);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ProviderDeadlineIsReportedWhenTheInnerClientEndsSilentlyOnAbort(bool streaming)
    {
        using var inner = new SilentAbortClient();
        using var client = new ProviderRequestTimeoutChatClient(inner, requestTimeoutMs: 80, idleTimeoutMs: 2000);
        var response = streaming ? client.GetStreamingResponseAsync([]).ToChatResponseAsync() : client.GetResponseAsync([]);
        var error = await Assert.ThrowsAsync<TimeoutException>(() => response);
        Assert.Contains("request timed out", error.Message);
    }

    [Fact]
    public async Task IdleDeadlineIsReportedWhenTheInnerStreamEndsSilentlyOnAbort()
    {
        using var inner = new SilentAbortClient();
        using var client = new ProviderRequestTimeoutChatClient(inner, requestTimeoutMs: 2000, idleTimeoutMs: 80);
        var error = await Assert.ThrowsAsync<TimeoutException>(() => client.GetStreamingResponseAsync([]).ToChatResponseAsync());
        Assert.Contains("no update", error.Message);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CallerCancellationIsReportedWhenTheInnerClientEndsSilently(bool streaming)
    {
        using var inner = new SilentAbortClient();
        using var client = new ProviderRequestTimeoutChatClient(inner, requestTimeoutMs: 2000, idleTimeoutMs: 2000);
        using var cancellation = new CancellationTokenSource();
        var response = streaming
            ? client.GetStreamingResponseAsync([], cancellationToken: cancellation.Token).ToChatResponseAsync(cancellation.Token)
            : client.GetResponseAsync([], cancellationToken: cancellation.Token);
        await inner.Entered.Task.WaitAsync(TimeSpan.FromSeconds(1));
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => response);
    }

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

    private sealed class SilentAbortClient(int? responseDelayMs = null) : IChatClient
    {
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            await WaitForAbortAsync(cancellationToken);
            return new(new ChatMessage(ChatRole.Assistant, ""));
        }
        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await WaitForAbortAsync(cancellationToken);
            yield break;
        }
        private async Task WaitForAbortAsync(CancellationToken token)
        {
            var canceled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var registration = token.Register(() => canceled.TrySetResult());
            Entered.TrySetResult();
            if (responseDelayMs is { } delay) await Task.Delay(delay);
            else await canceled.Task;
        }
        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }
}
