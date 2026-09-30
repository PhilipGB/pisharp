using System.Net;
using System.Text;
using System.Text.Json;
using PiSharp.Runtime.Classifiers;
using PiSharp.Runtime.Sessions;

namespace PiSharp.Tests;

public sealed class ClassifierProtocolTests
{
    [Theory]
    [InlineData("typesafe-system-one")]
    [InlineData("cloudflare-workers-ai-system-one")]
    public async Task SystemOneProjectsBooleanQuestionsParsesAnswersAndPricesUsage(string api)
    {
        var called = false;
        using var http = new HttpClient(new Handler(async request =>
        {
            called = true;
            Assert.Equal(api == "typesafe-system-one" ? "/base/systemone" : "/base/run", request.RequestUri!.AbsolutePath);
            Assert.Equal("Bearer fixture-key", request.Headers.Authorization!.ToString());
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
            var payload = body.RootElement;
            Assert.Equal("classifier", payload.GetProperty("model").GetString());
            if (api == "cloudflare-workers-ai-system-one") payload = payload.GetProperty("input");
            Assert.Equal("noul", payload.GetProperty("questions").GetProperty("safe").GetProperty("type").GetString());
            var result = """{"answers":{"safe":{"type":"noul","noul":0.8}},"usage":{"input_tokens":100,"output_tokens":10}}""";
            if (api == "cloudflare-workers-ai-system-one") result = "{\"success\":true,\"result\":{\"state\":\"Completed\",\"result\":" + result + "}}";
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(result, Encoding.UTF8, "application/json") };
        }));
        var response = await new SystemOneClassifierClient(http).ClassifyAsync(Model(api), Context(), "fixture-key");
        Assert.Equal("stop", response.StopReason);
        Assert.True(called);
        Assert.Equal(0.8, Assert.IsType<ClassifierBoolAnswer>(response.Answers["safe"]).Probability);
        Assert.Equal(100, response.Usage!.InputTokens);
        Assert.Equal(10, response.Usage.OutputTokens);
        Assert.Equal(0.0003m, response.Usage.Cost);
    }

    [Fact]
    public async Task MalformedAnswersRetainBilledUsage()
    {
        using var http = new HttpClient(new Handler(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        { Content = new StringContent("""{"answers":{},"usage":{"input_tokens":100,"output_tokens":10}}""") })));
        var response = await new SystemOneClassifierClient(http).ClassifyAsync(Model("typesafe-system-one"), Context(), "key");
        Assert.Equal("error", response.StopReason);
        Assert.Empty(response.Answers);
        Assert.Equal(0.0003m, response.Usage!.Cost);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MissingCredentialsAndCancellationReturnExplicitResultsWithoutSending(bool canceled)
    {
        var called = false;
        using var http = new HttpClient(new Handler(_ =>
        {
            called = true;
            throw new InvalidOperationException("Unexpected request");
        }));
        using var cancellation = new CancellationTokenSource();
        if (canceled) cancellation.Cancel();
        var response = await new SystemOneClassifierClient(http).ClassifyAsync(Model("typesafe-system-one"), Context(),
            canceled ? "key" : null, cancellation.Token);
        Assert.Equal(canceled ? "aborted" : "error", response.StopReason);
        Assert.False(called);
        Assert.Empty(response.Answers);
        Assert.Null(response.Usage);
    }

    [Theory]
    [InlineData("choice")]
    [InlineData("score")]
    public async Task TypedAnswersPreserveDistributionAndConfidence(string type)
    {
        var answer = type == "choice" ? """{"type":"choice","choice":"a","probabilities":{"a":0.7,"b":0.3},"confidence":0.7}""" :
            """{"type":"score","score":0.6,"confidence":0.9}""";
        using var http = new HttpClient(new Handler(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        { Content = new StringContent("{\"answers\":{\"question\":" + answer + "}}") })));
        ClassifierQuestion question = type == "choice" ?
            new ClassifierChoiceQuestion("choose", new Dictionary<string, string> { ["a"] = "first", ["b"] = "second" }) :
            new ClassifierScoreQuestion("score", ["low", "high"]);
        var context = new ClassifierContext(JsonSerializer.SerializeToElement(new { text = "fixture" }),
            new Dictionary<string, ClassifierQuestion> { ["question"] = question });
        var response = await new SystemOneClassifierClient(http).ClassifyAsync(Model("typesafe-system-one"), context, "key");
        Assert.Equal("stop", response.StopReason);
        if (type == "choice")
        {
            var choice = Assert.IsType<ClassifierChoiceAnswer>(response.Answers["question"]);
            Assert.Equal("a", choice.Choice);
            Assert.Equal(0.7, choice.Probabilities["a"]);
            Assert.Equal(0.7, choice.Confidence);
        }
        else
        {
            var score = Assert.IsType<ClassifierScoreAnswer>(response.Answers["question"]);
            Assert.Equal(0.6, score.Score);
            Assert.Equal(0.9, score.Confidence);
        }
    }

    [Fact]
    public async Task RetryAndRequestHeadersApplyAtClassifierTransportBoundary()
    {
        var attempts = 0;
        using var http = new HttpClient(new Handler(request =>
        {
            attempts++;
            if (attempts == 1) return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
            Assert.Equal("fixture", Assert.Single(request.Headers.GetValues("X-Classifier-Request")));
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new StringContent("""{"answers":{"safe":{"type":"noul","noul":0.8}}}""") });
        }));
        var response = await new SystemOneClassifierClient(http).ClassifyAsync(Model("typesafe-system-one"), Context(), "key",
            options: new ClassifierRequestOptions(MaxRetries: 1, Headers: new Dictionary<string, string> { ["X-Classifier-Request"] = "fixture" }));
        Assert.Equal("stop", response.StopReason);
        Assert.Equal(2, attempts);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InFlightDeadlineAndUserCancellationProduceDifferentResults(bool userCancellation)
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var http = new HttpClient(new BlockingHandler(started));
        using var cancellation = new CancellationTokenSource();
        var pending = new SystemOneClassifierClient(http).ClassifyAsync(Model("typesafe-system-one"), Context(), "key",
            cancellation.Token, new ClassifierRequestOptions(MaxRetries: 0, TimeoutMs: userCancellation ? null : 50));
        await started.Task.WaitAsync(TimeSpan.FromSeconds(3));
        if (userCancellation) cancellation.Cancel();
        var result = await pending.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(userCancellation ? "aborted" : "error", result.StopReason);
        if (!userCancellation) Assert.Contains("timed out", result.ErrorMessage);
        Assert.Empty(result.Answers);
    }

    [Fact]
    public async Task ExcessiveRetryAfterIsRejectedWithoutResending()
    {
        var attempts = 0;
        using var http = new HttpClient(new Handler(_ =>
        {
            attempts++;
            var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
            response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(60));
            return Task.FromResult(response);
        }));
        var result = await new SystemOneClassifierClient(http).ClassifyAsync(Model("typesafe-system-one"), Context(), "key",
            options: new ClassifierRequestOptions(MaxRetries: 2, MaxRetryDelayMs: 1));
        Assert.Equal("error", result.StopReason);
        Assert.Equal(1, attempts);
        Assert.Contains("maximum", result.ErrorMessage);
    }

    private sealed class BlockingHandler(TaskCompletionSource started) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            started.SetResult();
            await Task.Delay(Timeout.Infinite, cancellationToken);
            throw new InvalidOperationException("Unexpected completion");
        }
    }

    private static ClassifierModel Model(string api) => new("fixture", "classifier", api, new Uri("http://localhost/base"),
        Pricing: new ModelPricing(2m, 10m));
    private static ClassifierContext Context() => new(JsonSerializer.SerializeToElement(new { text = "fixture" }),
        new Dictionary<string, ClassifierQuestion> { ["safe"] = new ClassifierBoolQuestion("is safe", new Dictionary<string, string> { ["true"] = "safe", ["false"] = "unsafe" }) });
    private sealed class Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request);
    }
}
