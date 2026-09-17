using PiSharp.Core;

namespace PiSharp.Core.Tests;

public sealed class ContextOverflowPolicyTests
{
    [Theory]
    [InlineData("The prompt is too long: 200000 tokens > 128000 maximum context length.")]
    [InlineData("This model's maximum context length is 131072 tokens. However, your messages resulted in 150000 tokens.")]
    [InlineData("invalid_request_error: model_context_window_exceeded")]
    [InlineData("context_length_exceeded: input tokens (150000) exceed the model's maximum (131072)")]
    [InlineData("The input token count (150000) exceeds the maximum allowed input length of 131072 tokens.")]
    [InlineData("Range of input length should be `1` to `8192`")]
    [InlineData("The prompt has 150000 tokens, but the configured context size is 131072 tokens.")]
    [InlineData("Input is too long for requested model: 150000 tokens > 131072 tokens.")]
    [InlineData("413 {\"error\":{\"type\":\"request_too_large\",\"message\":\"Request exceeds the maximum size\"}}")]
    [InlineData("maximum prompt length is 32768")]
    [InlineData("reduce the length of the messages and retry")]
    [InlineData("exceeds the available context size")]
    [InlineData("greater than the context length of this model")]
    [InlineData("token limit exceeded")]
    public void RecognizesProviderOverflowText(string errorText)
    {
        Assert.True(ContextOverflowPolicy.IsContextOverflow(errorText));
    }

    [Theory]
    [InlineData("401 Unauthorized: invalid api key")]
    [InlineData("connection refused")]
    [InlineData("Rate limit reached. Please try again later.")]
    [InlineData("Too many requests, please try again later.")]
    // Bedrock throttling text contains "Too many tokens" but is explicitly an exclusion.
    [InlineData("Throttling error: Too many tokens, please wait before trying again.")]
    [InlineData("Service unavailable: please retry.")]
    [InlineData("The operation was canceled.")]
    [InlineData("")]
    public void DoesNotTreatNonOverflowErrorsAsOverflow(string errorText)
    {
        Assert.False(ContextOverflowPolicy.IsContextOverflow(errorText));
    }

    [Fact]
    public void NullOrWhitespaceTextIsNotOverflow()
    {
        Assert.False(ContextOverflowPolicy.IsContextOverflow((string?)null));
        Assert.False(ContextOverflowPolicy.IsContextOverflow("   "));
    }

    [Fact]
    public void InspectedExceptionChainFindsOverflowInInnerException()
    {
        var inner = new InvalidOperationException("prompt is too long: 900000 > 128000");
        var outer = new AggregateException("model call failed", inner);

        Assert.True(ContextOverflowPolicy.IsContextOverflow(outer));
        Assert.False(ContextOverflowPolicy.IsContextOverflow(new InvalidOperationException("transient network hiccup")));
    }
}
