using System.Net;
using System.Text.Json;
using PiSharp.Runtime.Classifiers;

namespace PiSharp.Tests;

public sealed class LlamaClassifierTests
{
    [Fact]
    public async Task NativeClassifierRejectsImagesBeforeSendingRouterRequests()
    {
        var requests = 0;
        using var http = new HttpClient(new Handler(_ =>
        {
            requests++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError));
        }));
        using var input = JsonDocument.Parse("""{"state":{"text":"fixture"},"questions":{"safe":{"type":"bool","instructions":"safe?","criteria":{"true":"safe","false":"unsafe"}}},"images":[{"type":"image","data":"AQID","mimeType":"image/png"}]}""");
        var context = input.RootElement.Deserialize<ClassifierContext>(new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        var result = await new LlamaClassifierClient(http).ClassifyAsync(
            new("llama.cpp", "fixture", "llama-cpp-classify", new Uri("http://fixture/v1")), context, null);
        Assert.Equal("error", result.StopReason);
        Assert.Equal("llama.cpp classification does not support image input", result.ErrorMessage);
        Assert.Equal(0, requests);
        Assert.Empty(result.Answers);
    }

    [Fact]
    public async Task NativeLabelReadoutEscalatesAndReturnsBooleanProbability()
    {
        var depths = new List<int>();
        using var http = new HttpClient(new Handler(async request =>
        {
            using var document = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
            var body = document.RootElement;
            Assert.Equal("fixture", body.GetProperty("model").GetString());
            var result = request.RequestUri!.AbsolutePath switch
            {
                "/tokenize" => body.GetProperty("content").GetString() switch
                {
                    "\n" => "{\"tokens\":[1]}",
                    "\nYes" => "{\"tokens\":[1,2]}",
                    "\nNo" => "{\"tokens\":[1,3]}",
                    _ => throw new InvalidOperationException("Unexpected label")
                },
                "/apply-template" => "{\"prompt\":\"rendered<think>\"}",
                "/completion" => Completion(body),
                _ => throw new InvalidOperationException("Wrong endpoint")
            };
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(result) };
        }));
        var context = new ClassifierContext(JsonSerializer.SerializeToElement(new { text = "state" }),
            new Dictionary<string, ClassifierQuestion> { ["safe"] = new ClassifierBoolQuestion("safe?", new Dictionary<string, string>()) });
        var result = await new LlamaClassifierClient(http).ClassifyAsync(
            new("llama", "fixture", "llama-cpp-classify", new Uri("http://fixture/v1")), context, null);
        Assert.Equal("stop", result.StopReason);
        Assert.Equal(0.75, Assert.IsType<ClassifierBoolAnswer>(result.Answers["safe"]).Probability, 8);
        Assert.Equal(new[] { 256, 4096 }, depths);
        Assert.Null(result.Usage);

        string Completion(JsonElement body)
        {
            Assert.Equal("rendered<think></think>", body.GetProperty("prompt").GetString());
            Assert.Equal(1, body.GetProperty("n_predict").GetInt32());
            Assert.False(body.GetProperty("post_sampling_probs").GetBoolean());
            Assert.True(body.GetProperty("cache_prompt").GetBoolean());
            var depth = body.GetProperty("n_probs").GetInt32();
            depths.Add(depth);
            return depth == 256 ? "{\"completion_probabilities\":[{\"top_logprobs\":[{\"id\":2,\"logprob\":0}]}]}" :
                JsonSerializer.Serialize(new { completion_probabilities = new[] { new { top_logprobs = new[] { new { id = 2, logprob = Math.Log(0.75) }, new { id = 3, logprob = Math.Log(0.25) } } } } });
        }
    }

    [Theory]
    [InlineData("choice")]
    [InlineData("score")]
    public async Task FallbackObjectTokensProduceTypedDistributionsAndCacheByModel(string kind)
    {
        var tokenCalls = 0;
        using var http = new HttpClient(new Handler(async request =>
        {
            using var document = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
            var body = document.RootElement;
            string response;
            if (request.RequestUri!.AbsolutePath == "/tokenize")
            {
                tokenCalls++;
                var label = body.GetProperty("content").GetString();
                response = label is "A" or "0" ? "{\"tokens\":[{\"id\":2}]}" :
                    label is "B" or "1" ? "{\"tokens\":[{\"id\":3}]}" : "{\"tokens\":[9,10]}";
            }
            else if (request.RequestUri.AbsolutePath == "/apply-template")
            {
                var content = body.GetProperty("messages")[1].GetProperty("content").GetString()!;
                Assert.Contains("State:", content);
                Assert.Contains("Question: judge", content);
                Assert.False(body.GetProperty("chat_template_kwargs").GetProperty("enable_thinking").GetBoolean());
                response = "{\"prompt\":\"rendered\"}";
            }
            else response = "{\"completion_probabilities\":[{\"top_logprobs\":[{\"id\":2,\"logprob\":0},{\"id\":3,\"logprob\":0}]}]}";
            return new(HttpStatusCode.OK) { Content = new StringContent(response) };
        }));
        var client = new LlamaClassifierClient(http);
        ClassifierQuestion question = kind == "choice" ? new ClassifierChoiceQuestion("judge", new Dictionary<string, string> { ["a"] = "first", ["b"] = "second" }) : new ClassifierScoreQuestion("judge", ["low", "high"]);
        var context = new ClassifierContext(JsonSerializer.SerializeToElement(new { value = "data" }), new Dictionary<string, ClassifierQuestion> { ["q"] = question });
        var model = new ClassifierModel("local", "one", "llama-cpp-classify", new Uri("http://fixture/v1/"));
        var result = await client.ClassifyAsync(model, context, null);
        Assert.Equal("stop", result.StopReason);
        if (kind == "choice")
        {
            var answer = Assert.IsType<ClassifierChoiceAnswer>(result.Answers["q"]);
            Assert.Equal("a", answer.Choice);
            Assert.Equal(0.5, answer.Probabilities["b"]);
            Assert.Equal(0, answer.Confidence);
        }
        else
        {
            var answer = Assert.IsType<ClassifierScoreAnswer>(result.Answers["q"]);
            Assert.Equal(0.5, answer.Score);
            Assert.Equal(0, answer.Confidence);
        }
        Assert.Equal(6, tokenCalls);
        Assert.Equal("stop", (await client.ClassifyAsync(model, context, null)).StopReason);
        Assert.Equal(6, tokenCalls);
        Assert.Equal("stop", (await client.ClassifyAsync(model with { Id = "two" }, context, null)).StopReason);
        Assert.Equal(12, tokenCalls);
    }

    [Fact]
    public async Task NativeLabelTokensAreResolvedConcurrentlyAndCached()
    {
        var tokenCalls = 0;
        var fourTokenRequests = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var templateRequest = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseTokenRequests = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var http = new HttpClient(new Handler(async request =>
        {
            using var document = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
            var body = document.RootElement;
            string response;
            if (request.RequestUri!.AbsolutePath == "/tokenize")
            {
                if (Interlocked.Increment(ref tokenCalls) == 4) fourTokenRequests.TrySetResult();
                await releaseTokenRequests.Task;
                response = body.GetProperty("content").GetString() switch
                {
                    "\n" => "{\"tokens\":[1]}",
                    "\nYes" => "{\"tokens\":[1,2]}",
                    "\nNo" => "{\"tokens\":[1,3]}",
                    _ => throw new InvalidOperationException("Unexpected label")
                };
            }
            else if (request.RequestUri.AbsolutePath == "/apply-template")
            {
                templateRequest.TrySetResult();
                var content = body.GetProperty("messages")[1].GetProperty("content").GetString();
                Assert.Contains("Question: safe?\n\nYes means: safe\nNo means: unsafe\n\nAnswer Yes or No.", content);
                response = "{\"prompt\":\"rendered\"}";
            }
            else response = "{\"completion_probabilities\":[{\"top_logprobs\":[{\"id\":2,\"logprob\":0},{\"id\":3,\"logprob\":0}]}]}";
            return new(HttpStatusCode.OK) { Content = new StringContent(response) };
        }));

        var client = new LlamaClassifierClient(http);
        var model = new ClassifierModel("local", "m", "llama-cpp-classify", new Uri("http://fixture/v1"));
        var context = new ClassifierContext(JsonSerializer.SerializeToElement(new { value = "data" }),
            new Dictionary<string, ClassifierQuestion>
            {
                ["safe"] = new ClassifierBoolQuestion("safe?", new Dictionary<string, string>
                {
                    ["true"] = "safe",
                    ["false"] = "unsafe"
                })
            });
        var classification = client.ClassifyAsync(model, context, null);
        bool allLabelRequestsArrived;
        try
        {
            var parallelWork = Task.WhenAll(fourTokenRequests.Task, templateRequest.Task);
            var first = await Task.WhenAny(parallelWork, classification).WaitAsync(TimeSpan.FromSeconds(5));
            allLabelRequestsArrived = ReferenceEquals(first, parallelWork);
        }
        catch (TimeoutException)
        {
            allLabelRequestsArrived = false;
        }
        finally
        {
            releaseTokenRequests.TrySetResult();
        }

        Assert.True(allLabelRequestsArrived, "All label token requests should be in flight together.");
        Assert.Equal("stop", (await classification).StopReason);
        Assert.Equal(4, tokenCalls);
        Assert.Equal("stop", (await client.ClassifyAsync(model, context, null)).StopReason);
        Assert.Equal(4, tokenCalls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InvalidTemperatureOrLaterQuestionFailsBeforeAnyRequest(bool temperature)
    {
        using var http = new HttpClient(new Handler(_ => throw new InvalidOperationException("Must not send")));
        var context = new ClassifierContext(JsonSerializer.SerializeToElement(new { }), new Dictionary<string, ClassifierQuestion>
        {
            ["first"] = new ClassifierBoolQuestion("safe", new Dictionary<string, string>()),
            ["invalid"] = new ClassifierScoreQuestion("score", ["only"])
        });
        var result = await new LlamaClassifierClient(http).ClassifyAsync(new("local", "m", "llama-cpp-classify", new Uri("http://fixture")), context, null,
            options: temperature ? new(Temperature: 0) : null);
        Assert.Equal("error", result.StopReason);
        Assert.DoesNotContain("Must not send", result.ErrorMessage);
        Assert.Empty(result.Answers);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MissingAndUnderflowedLabelsReturnErrorsWithoutPartialAnswers(bool underflow)
    {
        var depths = new List<int>();
        using var http = new HttpClient(new Handler(async request =>
        {
            using var document = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
            var body = document.RootElement;
            string response;
            if (request.RequestUri!.AbsolutePath == "/tokenize")
                response = body.GetProperty("content").GetString() switch
                {
                    "\n" => "{\"tokens\":[1]}",
                    "\nYes" => "{\"tokens\":[1,2]}",
                    _ => "{\"tokens\":[1,3]}"
                };
            else if (request.RequestUri.AbsolutePath == "/apply-template") response = "{\"prompt\":\"p\"}";
            else
            {
                depths.Add(body.GetProperty("n_probs").GetInt32());
                response = underflow ? "{\"completion_probabilities\":[{\"top_logprobs\":[{\"id\":2,\"logprob\":-1e31},{\"id\":3,\"logprob\":-1e31}]}]}" : "{\"completion_probabilities\":[{\"top_logprobs\":[]}]}";
            }
            return new(HttpStatusCode.OK) { Content = new StringContent(response) };
        }));
        var context = new ClassifierContext(JsonSerializer.SerializeToElement(new { }), new Dictionary<string, ClassifierQuestion> { ["q"] = new ClassifierBoolQuestion("safe", new Dictionary<string, string>()) });
        var result = await new LlamaClassifierClient(http).ClassifyAsync(new("local", "m", "llama-cpp-classify", new Uri("http://fixture")), context, null);
        Assert.Equal("error", result.StopReason);
        Assert.Empty(result.Answers);
        Assert.Equal(underflow ? new[] { 256 } : new[] { 256, 4096, 32768 }, depths);
        Assert.Contains(underflow ? "no probability" : "32768", result.ErrorMessage);
    }

    private sealed class Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request);
    }
}
