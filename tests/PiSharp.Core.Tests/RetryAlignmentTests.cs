using Microsoft.Extensions.AI;
using PiSharp.Cli;
using PiSharp.Core.Settings;

namespace PiSharp.Core.Tests;

/// <summary>
/// Final retry/timeout alignment (pinned retryProviderRequest + isRetryableAssistantError +
/// retryAssistantCall): the x-should-retry override beats any status, context overflow is
/// never provider-retried (the compaction layer owns it), the turn-level classifier uses the
/// pinned error-message patterns (billing/quota never retried), and turn retries compose with
/// provider retries by giving every restarted turn a fresh provider budget.
/// </summary>
public sealed class RetryAlignmentTests
{
    [Fact]
    public async Task ProviderLayerNeverRetriesContextOverflow()
    {
        // Overflow is deterministic (400): exactly one downstream request even with a large
        // provider budget — recovery belongs to the compaction layer, not provider retry.
        var settings = await Settings("""{"retry":{"provider":{"maxRetries":5,"maxRetryDelayMs":60000}}}""");
        var stub = new ScriptedClient(() => throw new ProviderHttpException(
            400,
            new Dictionary<string, string>(),
            "prompt is too long: 198955 tokens > 196608 maximum allowed"));

        var client = new ProviderRetryClient(stub, settings);
        var error = await Assert.ThrowsAsync<ProviderHttpException>(
            () => client.GetResponseAsync([new ChatMessage(ChatRole.User, "hi")]));
        Assert.Equal(400, error.Status);
        Assert.Equal(1, stub.CallCount);
    }

    [Fact]
    public async Task ShouldRetryTrueHeaderOverridesNonRetryableStatus()
    {
        // Pinned: the x-should-retry header wins before the status table is consulted.
        var settings = await Settings("""{"retry":{"provider":{"maxRetries":1,"maxRetryDelayMs":60000}}}""");
        var stub = new ScriptedClient(
            () => throw new ProviderHttpException(
                400, new Dictionary<string, string> { ["x-should-retry"] = "true" }, "server says retry"),
            () => Task.FromResult(new ChatResponse(
                new ChatMessage(ChatRole.Assistant, new AIContent[] { new TextContent("ok") }))));

        var client = new ProviderRetryClient(stub, settings);
        var response = await client.GetResponseAsync([new ChatMessage(ChatRole.User, "hi")]);
        Assert.Equal("ok", response.Text);
        Assert.Equal(2, stub.CallCount);
    }

    [Theory]
    [InlineData("Rate limit reached for gpt-4o. Please try your request again.")]
    [InlineData("429 Too Many Requests")]
    [InlineData("upstream 503: service unavailable")]
    [InlineData("Request timed out after 300s of inactivity.")]
    [InlineData("socket hang up")]
    [InlineData("stream ended before a terminal response event")]
    [InlineData("overloaded_error")]
    public void TurnClassifierMarksTransientProviderErrors(string message)
    {
        Assert.True(RetryPolicy.IsRetryableAssistantErrorMessage(message), message);
    }

    [Theory]
    [InlineData("insufficient_quota: upgrade to pay-as-you-go")]
    [InlineData("quota exceeded for this billing plan")]
    [InlineData("Monthly usage limit reached. Please enable available balance usage.")]
    [InlineData("GoUsageLimitError: weekly limit")]
    [InlineData("prompt is too long: 198955 tokens > 196608 maximum allowed")]
    [InlineData("No API key for ghost/x")]
    public void TurnClassifierNeverMarksLimitOrDeterministicErrors(string message)
    {
        Assert.False(RetryPolicy.IsRetryableAssistantErrorMessage(message), message);
    }

    [Fact]
    public void BridgeIdleTimeoutFailsStructurallyTransient()
    {
        // The bridge's idle-timeout message is not in the pinned message patterns; it is
        // retryable at the turn level because the bridge throws it as a status-less
        // HttpRequestException (transport failure, pinned "status undefined" class).
        var error = new HttpRequestException(
            "The provider stopped sending data after 300s of inactivity.");
        Assert.False(RetryPolicy.IsRetryableAssistantErrorMessage(error.Message));
        Assert.True(RetryPolicy.IsRetryableTurnFailure(error));
    }

    [Fact]
    public void TurnClassifierReadsWrappedExceptionChains()
    {
        var wrapped = new InvalidOperationException(
            "Agent turn failed",
            new ProviderHttpException(429, new Dictionary<string, string>(), "Rate limit reached"));
        Assert.True(RetryPolicy.IsRetryableTurnFailure(wrapped));

        var billing = new InvalidOperationException(
            "Agent turn failed",
            new ProviderHttpException(429, new Dictionary<string, string>(), "insufficient_quota"));
        Assert.False(RetryPolicy.IsRetryableTurnFailure(billing));
    }

    [Fact]
    public async Task TurnRetriesComposeWithFreshProviderBudgets()
    {
        // Pinned composition: the turn layer restarts the whole assistant attempt; every
        // restart builds a fresh provider request through the provider-retry layer, so a
        // persistent 500 costs (providerBudget + 1) requests per turn attempt.
        var settings = await Settings("""{"retry":{"provider":{"maxRetries":2,"maxRetryDelayMs":60000}}}""");
        var stub = new ScriptedClient(Always500, Always500, Always500, Always500, Always500,
            Always500, Always500, Always500, Always500, Always500);
        var provider = new ProviderRetryClient(stub, settings);

        var turnPolicy = new RetryPolicyOptions(Enabled: true, MaxRetries: 2, BaseDelay: TimeSpan.Zero, MaximumDelay: TimeSpan.Zero);
        var retryNumber = 0;
        Exception? lastError = null;
        try
        {
            while (true)
            {
                try
                {
                    // The exact filter AgentTurnRunner.RunSingleAsync applies before restarting.
                    await provider.GetResponseAsync([new ChatMessage(ChatRole.User, "hi")]);
                    Assert.Fail("persistent 500 must not succeed");
                }
                catch (Exception exception) when (
                    turnPolicy.Enabled &&
                    retryNumber < turnPolicy.MaxRetries &&
                    RetryPolicy.IsRetryableTurnFailure(exception))
                {
                    retryNumber++;
                }
            }
        }
        catch (Exception exception)
        {
            lastError = exception;
        }

        Assert.IsType<ProviderHttpException>(lastError);
        Assert.Equal(2, retryNumber);
        // 3 provider attempts x 3 turn attempts = 9 downstream requests.
        Assert.Equal(9, stub.CallCount);
    }

    private static Task<ChatResponse> Always500() => throw new ProviderHttpException(
        500, new Dictionary<string, string>(), "internal server error");

    private static Task<SettingsManager> Settings(string json) =>
        SettingsManager.CreateFromStorageAsync(new InMemorySettingsStorage(json, null));

    /// <summary>IChatClient stub that pops scripted outcomes and counts every call.</summary>
    private sealed class ScriptedClient(params Func<Task<ChatResponse>>[] outcomes) : IChatClient
    {
        private readonly Queue<Func<Task<ChatResponse>>> _script =
            new([.. outcomes]);

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
            if (_script.Count == 0)
            {
                throw new InvalidOperationException("scripted client exhausted");
            }

            return _script.Dequeue()();
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }
}
