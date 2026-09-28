using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Microsoft.Extensions.AI;
using PiSharp.Cli;
using PiSharp.Runtime.Providers;

namespace PiSharp.Tests;

public sealed class MistralProviderCompatibilityTests
{
    [Fact]
    public async Task MistralMapsNativeReasoningEffortAndNormalizesToolCallIds()
    {
        using var listener = StartListener(out var port);
        string? requestBody = null;
        var server = Task.Run(async () =>
        {
            var request = await listener.GetContextAsync();
            Assert.Equal("/v1/chat/completions", request.Request.Url?.AbsolutePath);
            Assert.Equal("Bearer fixture-key", request.Request.Headers["Authorization"]);
            using var reader = new StreamReader(request.Request.InputStream);
            requestBody = await reader.ReadToEndAsync();
            request.Response.ContentType = "application/json";
            await using var writer = new StreamWriter(request.Response.OutputStream);
            await writer.WriteAsync("""
                {"id":"mistral-response","object":"chat.completion","created":1,"model":"fixture-model","choices":[{"index":0,"message":{"role":"assistant","content":"ok"},"finish_reason":"stop"}],"usage":{"prompt_tokens":5,"completion_tokens":1,"total_tokens":6}}
                """);
            await writer.FlushAsync();
            request.Response.Close();
        });
        using var mapDocument = JsonDocument.Parse("""
            {"off":"none","minimal":null,"low":null,"medium":null,"high":null,"xhigh":null,"max":"max"}
            """);
        var model = new ModelDescriptor("fixture-model", "mistral", 128000, "fixture", Reasoning: true,
            Provider: "mistral", Api: "mistral-conversations", ThinkingLevelMap: mapDocument.RootElement.Clone());
        var selection = new ModelSelection(new ProviderProfile("mistral", "Mistral", new Uri($"http://127.0.0.1:{port}/v1"),
            true, false, null, null, [model], Api: "mistral-conversations"), model, "fixture-key", true, "fixture");
        var longCallId = "legacy-tool-call-identifier-one";
        var secondLongCallId = "legacy-tool-call-identifier-two";
        ChatMessage[] messages =
        [
            new(ChatRole.Assistant,
            [
                new FunctionCallContent(longCallId, "read", new Dictionary<string, object?> { ["path"] = "one.txt" }),
                new FunctionCallContent(secondLongCallId, "read", new Dictionary<string, object?> { ["path"] = "two.txt" })
            ]),
            new(ChatRole.Tool,
            [
                new FunctionResultContent(longCallId, "first"),
                new FunctionResultContent(secondLongCallId, "second")
            ]),
            new(ChatRole.User, "continue")
        ];
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        var response = await ProviderChatClientFactory.Create(selection).GetResponseAsync(messages,
            new ChatOptions { Reasoning = new ReasoningOptions { Effort = ReasoningEffort.ExtraHigh } }, deadline.Token);
        await server.WaitAsync(deadline.Token);

        Assert.Equal("ok", response.Text);
        using var sent = JsonDocument.Parse(requestBody!);
        var root = sent.RootElement;
        Assert.Equal("max", root.GetProperty("reasoning_effort").GetString());
        Assert.False(root.TryGetProperty("prompt_mode", out _));
        var calls = root.GetProperty("messages")[0].GetProperty("tool_calls").EnumerateArray().ToArray();
        var firstId = calls[0].GetProperty("id").GetString()!;
        var secondId = calls[1].GetProperty("id").GetString()!;
        Assert.Equal(9, firstId.Length);
        Assert.Equal(9, secondId.Length);
        Assert.NotEqual(firstId, secondId);
        var resultIds = root.GetProperty("messages")[1].GetProperty("tool_call_id").GetString();
        Assert.Equal(firstId, resultIds);
        Assert.Equal(secondId, root.GetProperty("messages")[2].GetProperty("tool_call_id").GetString());
    }

    [Fact]
    public async Task MistralUsesPromptModeOnlyForEnabledReasoningWithoutEffortMap()
    {
        using var listener = StartListener(out var port);
        string? requestBody = null;
        var server = RespondOnce(listener, body => requestBody = body);
        var model = new ModelDescriptor("magistral-medium-latest", "mistral", 128000, "fixture", Reasoning: true,
            Provider: "mistral", Api: "mistral-conversations");
        var selection = new ModelSelection(new ProviderProfile("mistral", "Mistral", new Uri($"http://127.0.0.1:{port}/v1"),
            true, false, null, null, [model], Api: "mistral-conversations"), model, "fixture-key", true, "fixture");
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        await ProviderChatClientFactory.Create(selection).GetResponseAsync(
            [new ChatMessage(ChatRole.User, "hello")],
            new ChatOptions { Reasoning = ThinkingLevels.ToOptions("medium") }, deadline.Token);
        await server.WaitAsync(deadline.Token);

        using var sent = JsonDocument.Parse(requestBody!);
        Assert.Equal("reasoning", sent.RootElement.GetProperty("prompt_mode").GetString());
        Assert.False(sent.RootElement.TryGetProperty("reasoning_effort", out _));
    }

    [Fact]
    public async Task MistralStreamsTheMappedThinkingEffort()
    {
        using var listener = StartListener(out var port);
        string? requestBody = null;
        var server = Task.Run(async () =>
        {
            var request = await listener.GetContextAsync();
            Assert.Equal("/v1/chat/completions", request.Request.Url?.AbsolutePath);
            using var reader = new StreamReader(request.Request.InputStream);
            requestBody = await reader.ReadToEndAsync();
            request.Response.ContentType = "text/event-stream";
            await using var writer = new StreamWriter(request.Response.OutputStream);
            await writer.WriteAsync("data: {\"id\":\"mistral-stream\",\"object\":\"chat.completion.chunk\",\"created\":1,\"model\":\"fixture-model\",\"choices\":[{\"index\":0,\"delta\":{\"role\":\"assistant\",\"content\":\"streamed\"},\"finish_reason\":null}]}\n\n");
            await writer.WriteAsync("data: {\"id\":\"mistral-stream\",\"object\":\"chat.completion.chunk\",\"created\":1,\"model\":\"fixture-model\",\"choices\":[{\"index\":0,\"delta\":{},\"finish_reason\":\"stop\"}]}\n\n");
            await writer.WriteAsync("data: [DONE]\n\n");
            await writer.FlushAsync();
            request.Response.Close();
        });
        using var mapDocument = JsonDocument.Parse("""
            {"off":"none","minimal":null,"low":"low","medium":"medium","high":"high","xhigh":null,"max":null}
            """);
        var map = mapDocument.RootElement.Clone();
        var model = new ModelDescriptor("fixture-model", "mistral", 128000, "fixture", Reasoning: true,
            Provider: "mistral", Api: "mistral-conversations", ThinkingLevelMap: map);
        var selection = new ModelSelection(new ProviderProfile("mistral", "Mistral", new Uri($"http://127.0.0.1:{port}/v1"),
            true, false, null, null, [model], Api: "mistral-conversations"), model, "fixture-key", true, "fixture");
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var output = new System.Text.StringBuilder();

        await foreach (var update in ProviderChatClientFactory.Create(selection).GetStreamingResponseAsync(
            [new ChatMessage(ChatRole.User, "hello")],
            new ChatOptions { Reasoning = ThinkingLevels.ToOptions("high", map) }, deadline.Token))
            if (update.Text is { } text) output.Append(text);
        await server.WaitAsync(deadline.Token);

        Assert.Equal("streamed", output.ToString());
        using var sent = JsonDocument.Parse(requestBody!);
        Assert.Equal("high", sent.RootElement.GetProperty("reasoning_effort").GetString());
        Assert.False(sent.RootElement.TryGetProperty("prompt_mode", out _));
    }

    [Fact]
    public async Task MistralSendsConfiguredOffEffortWhenReasoningIsDisabled()
    {
        using var listener = StartListener(out var port);
        string? requestBody = null;
        var server = RespondOnce(listener, body => requestBody = body);
        using var mapDocument = JsonDocument.Parse("""
            {"off":"none","low":"low","medium":"medium","high":"high"}
            """);
        var model = new ModelDescriptor("fixture-model", "mistral", 128000, "fixture", Reasoning: true,
            Provider: "mistral", Api: "mistral-conversations", ThinkingLevelMap: mapDocument.RootElement.Clone());
        var selection = new ModelSelection(new ProviderProfile("mistral", "Mistral", new Uri($"http://127.0.0.1:{port}/v1"),
            true, false, null, null, [model], Api: "mistral-conversations"), model, "fixture-key", true, "fixture");
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        await ProviderChatClientFactory.Create(selection).GetResponseAsync(
            [new ChatMessage(ChatRole.User, "hello")], cancellationToken: deadline.Token);
        await server.WaitAsync(deadline.Token);

        using var sent = JsonDocument.Parse(requestBody!);
        Assert.Equal("none", sent.RootElement.GetProperty("reasoning_effort").GetString());
        Assert.False(sent.RootElement.TryGetProperty("prompt_mode", out _));
    }

    private static async Task RespondOnce(HttpListener listener, Action<string> capture)
    {
        var request = await listener.GetContextAsync();
        using var reader = new StreamReader(request.Request.InputStream);
        capture(await reader.ReadToEndAsync());
        request.Response.ContentType = "application/json";
        await using var writer = new StreamWriter(request.Response.OutputStream);
        await writer.WriteAsync("""
            {"id":"mistral-response","object":"chat.completion","created":1,"model":"fixture-model","choices":[{"index":0,"message":{"role":"assistant","content":"ok"},"finish_reason":"stop"}],"usage":{"prompt_tokens":1,"completion_tokens":1,"total_tokens":2}}
            """);
        await writer.FlushAsync();
        request.Response.Close();
    }

    private static HttpListener StartListener(out int port)
    {
        using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        socket.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        port = ((IPEndPoint)socket.LocalEndPoint!).Port;
        var listener = new HttpListener();
        listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        listener.Start();
        return listener;
    }
}
