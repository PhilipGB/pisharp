using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using PiSharp.Cli;

namespace PiSharp.Tests;

public sealed class LlamaRouterManagementClientTests
{
    [Fact]
    public void ProgressParsingCombinesStagesAndFileBytesLikeCurrentPi()
    {
        using var load = JsonDocument.Parse("""
            {"progress":{"current":"loading_context","stages":["loading_model","loading_context"],"value":0.25}}
            """);
        using var download = JsonDocument.Parse("""
            {"progress":{"first.gguf":{"done":1024,"total":4096},"second.gguf":{"done":1,"total":4}}}
            """);

        var loadProgress = LlamaRouterClient.ParseLoadProgress(load.RootElement);
        var downloadProgress = LlamaRouterClient.ParseDownloadProgress(download.RootElement);

        Assert.Equal("Loading loading context", loadProgress?.Message);
        Assert.Equal(0.625, loadProgress?.Ratio);
        Assert.Equal("Downloading model", downloadProgress?.Message);
        Assert.Equal(0.25, downloadProgress?.Ratio);
        Assert.Equal("1.00 KiB / 4.00 KiB", downloadProgress?.Detail);
    }

    [Fact]
    public async Task LoadAndUnloadUseAuthenticatedRouterEndpointsAndWaitForCatalogState()
    {
        var state = "unloaded";
        var requests = new ConcurrentQueue<(string Method, string Path, string? Authorization, string? Body)>();
        using var http = new HttpClient(new RouterHandler(async (request, cancellationToken) =>
        {
            var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            requests.Enqueue((request.Method.Method, request.RequestUri!.PathAndQuery,
                request.Headers.Authorization?.ToString(), body));
            if (request.RequestUri.AbsolutePath == "/models/sse")
                return Sse("data: {\"model\":\"qwen\",\"event\":\"model_status\",\"data\":{\"status\":\"loading\",\"progress\":{\"current\":\"loading_model\",\"stages\":[\"loading_model\",\"loading_context\"],\"value\":0.5}}}\n\n");
            if (request.Method == HttpMethod.Post && request.RequestUri.AbsolutePath == "/models/load")
            {
                state = "loaded";
                return Json("{}");
            }
            if (request.Method == HttpMethod.Post && request.RequestUri.AbsolutePath == "/models/unload")
            {
                state = "unloaded";
                return Json("{}");
            }
            if (request.Method == HttpMethod.Get && request.RequestUri.AbsolutePath == "/models")
                return Json($"{{\"data\":[{{\"id\":\"qwen\",\"status\":{{\"value\":\"{state}\"}}}}]}}");
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }));
        var client = new LlamaRouterClient(http, new Uri("http://llama.test:8080/v1"), "router-secret");
        var progress = new List<LlamaRouterProgress>();

        var loaded = await client.LoadAndWaitAsync("qwen", progress.Add, CancellationToken.None);
        await client.UnloadAndWaitAsync("qwen", CancellationToken.None);

        Assert.Equal("loaded", loaded.Status.Value);
        Assert.Contains(progress, item => item.Message == "Loading model");
        Assert.Contains(requests, item => item.Method == "POST" && item.Path == "/models/load" &&
            item.Body == "{\"model\":\"qwen\"}" && item.Authorization == "Bearer router-secret");
        Assert.Contains(requests, item => item.Method == "POST" && item.Path == "/models/unload" &&
            item.Body == "{\"model\":\"qwen\"}" && item.Authorization == "Bearer router-secret");
        Assert.All(requests, request => Assert.Equal("Bearer router-secret", request.Authorization));
        var trace = requests.ToArray();
        var watchIndex = Array.FindIndex(trace, item => item.Method == "GET" && item.Path == "/models/sse");
        var loadIndex = Array.FindIndex(trace, item => item.Method == "POST" && item.Path == "/models/load");
        Assert.True(watchIndex >= 0 && loadIndex > watchIndex,
            "The llama.cpp SSE watcher must start before the load request, as current Pi does.");
    }

    [Fact]
    public async Task DownloadWaitsForCompletionAndForcesRouterCatalogReload()
    {
        var requests = new ConcurrentQueue<(string Method, string Path, string? Authorization, string? Body)>();
        using var http = new HttpClient(new RouterHandler(async (request, cancellationToken) =>
        {
            var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            requests.Enqueue((request.Method.Method, request.RequestUri!.PathAndQuery,
                request.Headers.Authorization?.ToString(), body));
            if (request.RequestUri.AbsolutePath == "/models/sse")
                return Sse("data: {\"model\":\"owner/model:Q4_K_M\",\"event\":\"download_finished\",\"data\":{}}\n\n");
            if (request.Method == HttpMethod.Post && request.RequestUri.AbsolutePath == "/models")
                return Json("{}");
            if (request.Method == HttpMethod.Get && request.RequestUri.AbsolutePath == "/models")
                return Json("""{"data":[{"id":"owner/model:Q4_K_M","status":{"value":"loaded"}}]}""");
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }));
        var client = new LlamaRouterClient(http, new Uri("http://llama.test:8080"), "router-secret");
        var progress = new List<LlamaRouterProgress>();

        var models = await client.DownloadAndWaitAsync("owner/model:Q4_K_M", progress.Add, CancellationToken.None);

        Assert.Equal("owner/model:Q4_K_M", Assert.Single(models).Id);
        Assert.Contains(progress, item => item.Message == "Downloading model");
        var trace = requests.ToArray();
        var watchIndex = Array.FindIndex(trace, item => item.Method == "GET" && item.Path == "/models/sse");
        var downloadIndex = Array.FindIndex(trace, item => item.Method == "POST" && item.Path == "/models");
        Assert.True(watchIndex >= 0 && downloadIndex > watchIndex,
            "The llama.cpp SSE watcher must start before the router download request, as current Pi does.");
        Assert.Contains(requests, item => item.Method == "POST" && item.Path == "/models" &&
            item.Body == "{\"model\":\"owner/model:Q4_K_M\"}" && item.Authorization == "Bearer router-secret");
        Assert.Contains(requests, item => item.Method == "GET" && item.Path == "/models?reload=1");
        Assert.All(requests, request => Assert.Equal("Bearer router-secret", request.Authorization));
    }

    [Fact]
    public async Task LoadWaitHonorsCancellationWhileRouterRemainsLoading()
    {
        var requests = new ConcurrentQueue<string>();
        using var http = new HttpClient(new RouterHandler((request, _) =>
        {
            requests.Enqueue(request.Method.Method + " " + request.RequestUri!.PathAndQuery);
            if (request.RequestUri.AbsolutePath == "/models/sse") return Task.FromResult(Sse(""));
            if (request.Method == HttpMethod.Post && request.RequestUri.AbsolutePath == "/models/load")
                return Task.FromResult(Json("{}"));
            if (request.Method == HttpMethod.Get && request.RequestUri.AbsolutePath == "/models")
                return Task.FromResult(Json("""{"data":[{"id":"qwen","status":{"value":"loading"}}]}"""));
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }));
        var client = new LlamaRouterClient(http, new Uri("http://llama.test:8080"), null);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            client.LoadAndWaitAsync("qwen", _ => { }, cancellation.Token));

        Assert.Contains("POST /models/load", requests);
        Assert.Contains("GET /models", requests);
    }

    private static HttpResponseMessage Json(string value) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(value, Encoding.UTF8, "application/json")
    };

    private static HttpResponseMessage Sse(string value) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(value, Encoding.UTF8, "text/event-stream")
    };

    private sealed class RouterHandler(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            send(request, cancellationToken);
    }
}
