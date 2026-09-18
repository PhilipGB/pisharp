using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;
using PiSharp.Cli;
using PiSharp.Core.Settings;

namespace PiSharp.Core.Tests;

/// <summary>
/// Tests the provider-level retry semantics (pinned retryProviderRequest): exact request
/// counts, Retry-After / retry-after-ms honoring, the x-should-retry override, the
/// maxRetryDelayMs cap, and the before-content-only rule.
/// </summary>
public sealed class ProviderRetryClientTests
{
    [Fact]
    public async Task RateLimitedRequestRetriesUntilSuccess()
    {
        var settings = await Settings("""{"retry":{"provider":{"maxRetries":3,"maxRetryDelayMs":60000}}}""");
        var stub = new ScriptedChatClient(
            () => throw new ProviderHttpException(
                429, new Dictionary<string, string> { ["retry-after-ms"] = "10" }, "slow down"),
            () => throw new ProviderHttpException(
                429, new Dictionary<string, string> { ["retry-after-ms"] = "10" }, "slow down"),
            () => Ok());

        var client = new ProviderRetryClient(stub, settings);
        var response = await client.GetResponseAsync([new ChatMessage(ChatRole.User, "hi")]);
        Assert.Equal("ok", response.Text);
        Assert.Equal(3, stub.CallCount);
    }

    [Fact]
    public async Task NonRetryableStatusFailsImmediately()
    {
        var settings = await Settings("""{"retry":{"provider":{"maxRetries":3}}}""");
        var stub = new ScriptedChatClient(
            () => throw new ProviderHttpException(400, new Dictionary<string, string>(), "bad request"),
            () => Ok());

        var client = new ProviderRetryClient(stub, settings);
        var error = await Assert.ThrowsAsync<ProviderHttpException>(
            () => client.GetResponseAsync([new ChatMessage(ChatRole.User, "hi")]));
        Assert.Equal(400, error.Status);
        Assert.Equal(1, stub.CallCount);
    }

    [Fact]
    public async Task ShouldRetryFalseHeaderBeatsRetryableStatus()
    {
        var settings = await Settings("""{"retry":{"provider":{"maxRetries":3}}}""");
        var stub = new ScriptedChatClient(
            () => throw new ProviderHttpException(
                429, new Dictionary<string, string> { ["x-should-retry"] = "false" }, "nope"),
            () => Ok());

        var client = new ProviderRetryClient(stub, settings);
        await Assert.ThrowsAsync<ProviderHttpException>(
            () => client.GetResponseAsync([new ChatMessage(ChatRole.User, "hi")]));
        Assert.Equal(1, stub.CallCount);
    }

    [Fact]
    public async Task ServerDelayAboveCapFailsImmediately()
    {
        var settings = await Settings("""{"retry":{"provider":{"maxRetries":3,"maxRetryDelayMs":1000}}}""");
        var stub = new ScriptedChatClient(
            () => throw new ProviderHttpException(
                429, new Dictionary<string, string> { ["retry-after-ms"] = "120000" }, "wait forever"),
            () => Ok());

        var client = new ProviderRetryClient(stub, settings);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => client.GetResponseAsync([new ChatMessage(ChatRole.User, "hi")]));
        Assert.Contains("retry delay", error.Message);
        Assert.Equal(1, stub.CallCount);
    }

    [Fact]
    public async Task TransportFailureRetriesWithBackoff()
    {
        var settings = await Settings("""{"retry":{"provider":{"maxRetries":2,"maxRetryDelayMs":60000}}}""");
        var stub = new ScriptedChatClient(
            () => throw new HttpRequestException("connection reset"),
            () => throw new HttpRequestException("connection reset"),
            () => Ok());

        var client = new ProviderRetryClient(stub, settings);
        var response = await client.GetResponseAsync([new ChatMessage(ChatRole.User, "hi")]);
        Assert.Equal("ok", response.Text);
        Assert.Equal(3, stub.CallCount);
    }

    [Fact]
    public async Task ProviderRetryIsDisabledUnlessConfigured()
    {
        // No retry.provider settings: maxRetries defaults to 0 (pinned default).
        var settings = await Settings("{}");
        var stub = new ScriptedChatClient(
            () => throw new ProviderHttpException(429, new Dictionary<string, string>(), "slow down"),
            () => Ok());

        var client = new ProviderRetryClient(stub, settings);
        await Assert.ThrowsAsync<ProviderHttpException>(
            () => client.GetResponseAsync([new ChatMessage(ChatRole.User, "hi")]));
        Assert.Equal(1, stub.CallCount);
    }

    [Fact]
    public async Task StreamingRetriesOnlyBeforeFirstChunk()
    {
        var settings = await Settings("""{"retry":{"provider":{"maxRetries":2,"maxRetryDelayMs":60000}}}""");
        var stub = new ScriptedStreamingChatClient(
            () => throw new ProviderHttpException(
                429, new Dictionary<string, string> { ["retry-after-ms"] = "10" }, "slow down"),
            () => CreateChunks("first", "second"));

        var client = new ProviderRetryClient(stub, settings);
        var texts = new List<string>();
        await foreach (var update in client.GetStreamingResponseAsync([new ChatMessage(ChatRole.User, "hi")]))
        {
            texts.Add(update.Text ?? string.Empty);
        }

        Assert.Equal("firstsecond", string.Join(string.Empty, texts));
        Assert.Equal(2, stub.CallCount);
    }

    [Fact]
    public async Task StreamingFailureAfterFirstChunkDoesNotRetry()
    {
        var settings = await Settings("""{"retry":{"provider":{"maxRetries":2,"maxRetryDelayMs":60000}}}""");
        var stub = new ScriptedStreamingChatClient(
            () => CreateChunksAfter("first", new ProviderHttpException(429, new Dictionary<string, string>(), "slow down")));

        var client = new ProviderRetryClient(stub, settings);
        var texts = new List<string>();
        var error = await Assert.ThrowsAsync<ProviderHttpException>(
            async () =>
            {
                await foreach (var update in client.GetStreamingResponseAsync([new ChatMessage(ChatRole.User, "hi")]))
                {
                    texts.Add(update.Text ?? string.Empty);
                }
            });

        Assert.Equal(429, error.Status);
        Assert.Equal(1, stub.CallCount);
        Assert.Equal("first", string.Join(string.Empty, texts));
    }

    /// <summary>
    /// Item 1 regression: a perpetually-retryable streaming failure must be bounded by the
    /// budget — maxRetries = N means at most N + 1 total downstream requests, then the last
    /// error surfaces. Before the fix the streaming path never checked the budget and looped
    /// forever (retriesRemaining drove negative).
    /// </summary>
    [Fact]
    public async Task StreamingRetryBudgetBoundsTotalRequests()
    {
        var settings = await Settings("""{"retry":{"provider":{"maxRetries":2,"maxRetryDelayMs":60000}}}""");
        Func<IAsyncEnumerable<ChatResponseUpdate>> fail = () => throw new ProviderHttpException(
            429, new Dictionary<string, string> { ["retry-after-ms"] = "10" }, "slow down");
        var stub = new ScriptedStreamingChatClient(fail, fail, fail);

        var client = new ProviderRetryClient(stub, settings);
        var error = await Assert.ThrowsAsync<ProviderHttpException>(
            async () =>
            {
                await foreach (var _ in client.GetStreamingResponseAsync([new ChatMessage(ChatRole.User, "hi")]))
                {
                }
            });

        Assert.Equal(429, error.Status);
        Assert.Equal(3, stub.CallCount);
    }

    /// <summary>The (N+1)th attempt still runs: a success on the final budgeted attempt is returned.</summary>
    [Fact]
    public async Task StreamingSucceedsOnFinalBudgetAttempt()
    {
        var settings = await Settings("""{"retry":{"provider":{"maxRetries":2,"maxRetryDelayMs":60000}}}""");
        Func<IAsyncEnumerable<ChatResponseUpdate>> fail = () => throw new ProviderHttpException(
            429, new Dictionary<string, string> { ["retry-after-ms"] = "10" }, "slow down");
        var stub = new ScriptedStreamingChatClient(fail, fail, () => CreateChunks("ok"));

        var client = new ProviderRetryClient(stub, settings);
        var texts = new List<string>();
        await foreach (var update in client.GetStreamingResponseAsync([new ChatMessage(ChatRole.User, "hi")]))
        {
            texts.Add(update.Text ?? string.Empty);
        }

        Assert.Equal("ok", string.Join(string.Empty, texts));
        Assert.Equal(3, stub.CallCount);
    }

    /// <summary>With maxRetries = 0 (the default) exactly one request is made, no retry.</summary>
    [Fact]
    public async Task StreamingWithZeroRetriesMakesExactlyOneRequest()
    {
        var settings = await Settings("{}");
        var stub = new ScriptedStreamingChatClient(
            () => throw new ProviderHttpException(429, new Dictionary<string, string>(), "slow down"),
            () => CreateChunks("ok"));

        var client = new ProviderRetryClient(stub, settings);
        await Assert.ThrowsAsync<ProviderHttpException>(
            async () =>
            {
                await foreach (var _ in client.GetStreamingResponseAsync([new ChatMessage(ChatRole.User, "hi")]))
                {
                }
            });

        Assert.Equal(1, stub.CallCount);
    }

    private static Task<ChatResponse> Ok() =>
        Task.FromResult(new ChatResponse(
            new ChatMessage(ChatRole.Assistant, new AIContent[] { new TextContent("ok") })));

    private static async IAsyncEnumerable<ChatResponseUpdate> CreateChunks(params string[] texts)
    {
        foreach (var text in texts)
        {
            yield return new ChatResponseUpdate(ChatRole.Assistant, new AIContent[] { new TextContent(text) });
        }
    }

    private static async IAsyncEnumerable<ChatResponseUpdate> CreateChunksAfter(
        string firstText,
        Exception failure)
    {
        yield return new ChatResponseUpdate(ChatRole.Assistant, new AIContent[] { new TextContent(firstText) });
        throw failure;
    }

    private static Task<SettingsManager> Settings(string json) =>
        SettingsManager.CreateFromStorageAsync(new InMemorySettingsStorage(json, null));

    /// <summary>IChatClient stub whose GetResponseAsync pops scripted outcomes in order.</summary>
    private sealed class ScriptedChatClient : IChatClient
    {
        private readonly Queue<Func<Task<ChatResponse>>> _script;

        public ScriptedChatClient(params Func<Task<ChatResponse>>[] outcomes)
        {
            _script = new Queue<Func<Task<ChatResponse>>>(outcomes);
        }

        public int CallCount { get; private set; }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            return _script.Dequeue()();
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }

    /// <summary>IChatClient stub for streaming: pops scripted stream builders in order.</summary>
    private sealed class ScriptedStreamingChatClient : IChatClient
    {
        private readonly Queue<Func<IAsyncEnumerable<ChatResponseUpdate>>> _script;

        public ScriptedStreamingChatClient(params Func<IAsyncEnumerable<ChatResponseUpdate>>[] outcomes)
        {
            _script = new Queue<Func<IAsyncEnumerable<ChatResponseUpdate>>>(outcomes);
        }

        public int CallCount { get; private set; }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            return _script.Dequeue()();
        }
    }
}
