using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;
using PiSharp.Cli;
using PiSharp.Runtime;
using PiSharp.Runtime.Providers;
using PiSharp.Runtime.Sessions;

namespace PiSharp.Tests;

public sealed class MistralProviderCompatibilityTests
{
    [Fact]
    public async Task MistralMapsNativeReasoningEffortAndNormalizesToolCallIds()
    {
        using var listener = StartListener(out var port);
        string? requestBody = null;
        string? wireHeader = null;
        var server = Task.Run(async () =>
        {
            var request = await listener.GetContextAsync();
            Assert.Equal("/v1/chat/completions", request.Request.Url?.AbsolutePath);
            Assert.Equal("Bearer fixture-key", request.Request.Headers["Authorization"]);
            wireHeader = request.Request.Headers["x-pisharp-wire-probe"];
            using var reader = new StreamReader(request.Request.InputStream);
            requestBody = await reader.ReadToEndAsync();
            request.Response.ContentType = "text/event-stream";
            await using var writer = new StreamWriter(request.Response.OutputStream);
            await writer.WriteAsync("data: {\"id\":\"mistral-response\",\"model\":\"fixture-model\",\"choices\":[{\"index\":0,\"delta\":{\"content\":\"ok\"},\"finish_reason\":null}]}\n\n");
            await writer.WriteAsync("data: {\"id\":\"mistral-response\",\"model\":\"fixture-model\",\"choices\":[{\"index\":0,\"delta\":{},\"finish_reason\":\"stop\"}],\"usage\":{\"prompt_tokens\":5,\"completion_tokens\":1,\"total_tokens\":6}}\n\n");
            await writer.WriteAsync("data: [DONE]\n\n");
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
        var wire = new ProviderWireTestSupport();
        using var wireScope = wire.Enter(deadline.Token);

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
        Assert.Equal("1atqmgfbb", firstId);
        Assert.Equal("r4d1cd49y", secondId);
        Assert.NotEqual(firstId, secondId);
        var resultIds = root.GetProperty("messages").EnumerateArray().Skip(1).Take(2)
            .Select(message => message.GetProperty("tool_call_id").GetString()).ToArray();
        Assert.Equal(firstId, resultIds[0]);
        Assert.Equal(secondId, resultIds[1]);
        using var wirePayload = JsonDocument.Parse(requestBody!);
        Assert.True(wirePayload.RootElement.GetProperty("wire_probe").GetBoolean());
        Assert.Equal("transformed", wireHeader);
        wire.AssertHooks("mistral-conversations", responseExposed: true);
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
    public async Task MistralNativeStreamPreservesReasoningToolContinuationImagesAndCachedUsage()
    {
        using var listener = StartListener(out var port);
        string? requestBody = null;
        string? affinity = null;
        string? userAgent = null;
        var server = Task.Run(async () =>
        {
            var request = await listener.GetContextAsync();
            Assert.Equal("text/event-stream", request.Request.Headers["Accept"]);
            affinity = request.Request.Headers["x-affinity"];
            userAgent = request.Request.Headers["User-Agent"];
            using var reader = new StreamReader(request.Request.InputStream);
            requestBody = await reader.ReadToEndAsync();
            request.Response.ContentType = "text/event-stream";
            await using var writer = new StreamWriter(request.Response.OutputStream);
            await writer.WriteAsync("data: {\"id\":\"response-1\",\"model\":\"fixture-model\",\"choices\":[{\"index\":0,\"finish_reason\":null,\"delta\":{\"content\":[{\"type\":\"thinking\",\"thinking\":[{\"type\":\"text\",\"text\":\"reason\"}]}]}}]}\n\n");
            await writer.WriteAsync("data: {\"id\":\"response-1\",\"model\":\"fixture-model\",\"choices\":[{\"index\":0,\"finish_reason\":null,\"delta\":{\"content\":[{\"type\":\"text\",\"text\":\"answer\"}]}}]}\n\n");
            await writer.WriteAsync("data: {\"id\":\"response-1\",\"model\":\"fixture-model\",\"choices\":[{\"index\":0,\"finish_reason\":null,\"delta\":{\"tool_calls\":[{\"id\":\"call-12345\",\"index\":0,\"function\":{\"name\":\"lookup\",\"arguments\":\"{\\\"query\\\":\"}}]}}]}\n\n");
            await writer.WriteAsync("data: {\"id\":\"response-1\",\"model\":\"fixture-model\",\"choices\":[{\"index\":0,\"finish_reason\":\"tool_calls\",\"delta\":{\"tool_calls\":[{\"index\":0,\"function\":{\"name\":\"\",\"arguments\":\"\\\"pi\\\"}\"}}]}}],\"usage\":{\"prompt_tokens\":10,\"completion_tokens\":4,\"total_tokens\":14,\"prompt_tokens_details\":{\"cached_tokens\":3}}}\n\n");
            await writer.WriteAsync("data: [DONE]\n\n");
            await writer.FlushAsync();
            request.Response.Close();
        });
        using var mapDocument = JsonDocument.Parse("""
            {"off":"none","minimal":null,"low":"low","medium":"medium","high":"high","xhigh":null,"max":"max"}
            """);
        var model = new ModelDescriptor("fixture-model", "mistral", 128000, "fixture", Reasoning: true,
            Provider: "mistral", Api: "mistral-conversations", ThinkingLevelMap: mapDocument.RootElement.Clone());
        var selection = new ModelSelection(new ProviderProfile("mistral", "Mistral", new Uri($"http://127.0.0.1:{port}/v1"),
            true, false, null, null, [model], Api: "mistral-conversations"), model, "fixture-key", true, "fixture");
        ChatMessage[] messages =
        [
            new(ChatRole.Assistant,
            [
                new TextReasoningContent("reason"),
                new TextContent("answer"),
                new FunctionCallContent("legacy-tool-call-id", "lookup", new Dictionary<string, object?> { ["query"] = "pi" })
            ]),
            new(ChatRole.Tool, [new FunctionResultContent("legacy-tool-call-id", "found")]),
            new(ChatRole.User, [new TextContent("continue"), new DataContent(new byte[] { 1, 2, 3 }, "image/png")])
        ];
        var lookupTool = AIFunctionFactory.Create((string query) => query, name: "lookup", description: "Look up a value");
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var updates = new List<ChatResponseUpdate>();
        await foreach (var update in ProviderChatClientFactory.Create(selection).GetStreamingResponseAsync(messages,
            new ChatOptions
            {
                Reasoning = new ReasoningOptions { Effort = ReasoningEffort.ExtraHigh },
                Tools = [lookupTool],
                ToolMode = ChatToolMode.RequireAny,
                ConversationId = "session-1"
            }, deadline.Token))
            updates.Add(update);
        await server.WaitAsync(deadline.Token);

        var contents = updates.SelectMany(update => update.Contents).ToArray();
        Assert.Equal("reason", Assert.Single(contents.OfType<TextReasoningContent>()).Text);
        Assert.Contains(contents.OfType<TextContent>(), text => text.Text == "answer");
        var returnedCall = Assert.Single(contents.OfType<FunctionCallContent>());
        Assert.Null(returnedCall.Exception);
        Assert.Equal("pi", Assert.IsType<JsonElement>(returnedCall.Arguments!["query"]).GetString());
        var usage = Assert.Single(contents.OfType<UsageContent>()).Details;
        Assert.Equal(7, usage.InputTokenCount);
        Assert.Equal(3, usage.CachedInputTokenCount);
        Assert.Equal(14, usage.TotalTokenCount);
        Assert.Equal(ChatFinishReason.ToolCalls, updates.Last(update => update.FinishReason is not null).FinishReason);

        using var sent = JsonDocument.Parse(requestBody!);
        var root = sent.RootElement;
        Assert.Equal("session-1", affinity);
        Assert.StartsWith("pi (", userAgent);
        Assert.Equal("max", root.GetProperty("reasoning_effort").GetString());
        Assert.Equal("session-1", root.GetProperty("prompt_cache_key").GetString());
        Assert.Equal("required", root.GetProperty("tool_choice").GetString());
        Assert.Equal("lookup", root.GetProperty("tools")[0].GetProperty("function").GetProperty("name").GetString());
        var requestMessages = root.GetProperty("messages").EnumerateArray().ToArray();
        var outgoingCall = requestMessages[0].GetProperty("tool_calls")[0];
        var outgoingCallId = outgoingCall.GetProperty("id").GetString();
        Assert.Equal(9, outgoingCallId!.Length);
        Assert.Equal(outgoingCallId, requestMessages[1].GetProperty("tool_call_id").GetString());
        Assert.Equal("lookup", requestMessages[1].GetProperty("name").GetString());
        Assert.Equal("{\"query\":\"pi\"}", outgoingCall.GetProperty("function").GetProperty("arguments").GetString());
        Assert.Equal("thinking", requestMessages[0].GetProperty("content")[0].GetProperty("type").GetString());
        Assert.Equal("data:image/png;base64,AQID", requestMessages[2].GetProperty("content")[1].GetProperty("image_url").GetString());
    }

    [Fact]
    public async Task MistralFinishReasonErrorRetriesBeforeOutputAndNeverReplaysAfterText()
    {
        using (var listener = StartListener(out var noReplayPort))
        using (var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10)))
        {
            var requests = 0;
            var server = Task.Run(async () =>
            {
                for (var attempt = 0; attempt < 2; attempt++)
                {
                    var request = await listener.GetContextAsync().WaitAsync(deadline.Token);
                    Interlocked.Increment(ref requests);
                    using var reader = new StreamReader(request.Request.InputStream);
                    await reader.ReadToEndAsync(deadline.Token);
                    request.Response.ContentType = "text/event-stream";
                    await using var writer = new StreamWriter(request.Response.OutputStream);
                    if (attempt == 0)
                        await writer.WriteAsync("data: {\"id\":\"response-error\",\"choices\":[{\"index\":0,\"delta\":{},\"finish_reason\":\"error\"}],\"usage\":{\"prompt_tokens\":1,\"completion_tokens\":0,\"total_tokens\":1}}\n\ndata: [DONE]\n\n");
                    else
                        await writer.WriteAsync("data: {\"id\":\"response-ok\",\"model\":\"fixture-model\",\"choices\":[{\"index\":0,\"delta\":{\"content\":\"recovered\"},\"finish_reason\":null}]}\n\ndata: {\"id\":\"response-ok\",\"choices\":[{\"index\":0,\"delta\":{},\"finish_reason\":\"stop\"}]}\n\ndata: [DONE]\n\n");
                    await writer.FlushAsync(deadline.Token);
                    request.Response.Close();
                }
            }, deadline.Token);
            var model = new ModelDescriptor("fixture-model", "mistral", 128000, "fixture",
                Provider: "mistral", Api: "mistral-conversations");
            var selection = new ModelSelection(new ProviderProfile("mistral", "Mistral", new Uri($"http://127.0.0.1:{noReplayPort}/v1"),
                true, false, null, null, [model], Api: "mistral-conversations"), model, "fixture-key", true, "fixture");
            var cwd = Path.Combine(Path.GetTempPath(), "pisharp-mistral-error-retry-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(cwd);
            try
            {
                var session = new ConversationSession(cwd, "fixture-model", null);
                var run = await ConversationRun.OpenAsync(new PiAgent(ProviderChatClientFactory.Create(selection),
                    new CodingTools(cwd), noTools: true,
                    retryPolicy: new ProviderRetryPolicy(maxRetries: 1, delay: TimeSpan.Zero)), session);
                var events = new List<AgentLifecycleEvent>();
                await foreach (var item in run.RunEventsAsync("hello", deadline.Token)) events.Add(item);
                await server.WaitAsync(deadline.Token);

                Assert.Equal(2, requests);
                Assert.Single(events, item => item.Type == "model_retry_scheduled");
                Assert.Contains(events, item => item.Type == "model_text_delta" && item.Text == "recovered");
                Assert.Contains(events, item => item.Type == "turn_completed");
            }
            finally { Directory.Delete(cwd, recursive: true); }
        }

        using (var listener = StartListener(out var terminalPort))
        using (var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10)))
        {
            var requests = 0;
            var server = Task.Run(async () =>
            {
                var request = await listener.GetContextAsync().WaitAsync(deadline.Token);
                Interlocked.Increment(ref requests);
                using var reader = new StreamReader(request.Request.InputStream);
                await reader.ReadToEndAsync(deadline.Token);
                request.Response.ContentType = "text/event-stream";
                await using var writer = new StreamWriter(request.Response.OutputStream);
                await writer.WriteAsync("data: {\"id\":\"response-partial\",\"model\":\"fixture-model\",\"choices\":[{\"index\":0,\"delta\":{\"content\":\"partial\"},\"finish_reason\":null}]}\n\ndata: {\"id\":\"response-partial\",\"choices\":[{\"index\":0,\"delta\":{},\"finish_reason\":\"error\"}]}\n\ndata: [DONE]\n\n");
                await writer.FlushAsync(deadline.Token);
                request.Response.Close();
            }, deadline.Token);
            var model = new ModelDescriptor("fixture-model", "mistral", 128000, "fixture",
                Provider: "mistral", Api: "mistral-conversations");
            var selection = new ModelSelection(new ProviderProfile("mistral", "Mistral", new Uri($"http://127.0.0.1:{terminalPort}/v1"),
                true, false, null, null, [model], Api: "mistral-conversations"), model, "fixture-key", true, "fixture");
            var cwd = Path.Combine(Path.GetTempPath(), "pisharp-mistral-no-replay-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(cwd);
            try
            {
                var session = new ConversationSession(cwd, "fixture-model", null);
                var run = await ConversationRun.OpenAsync(new PiAgent(ProviderChatClientFactory.Create(selection),
                    new CodingTools(cwd), noTools: true,
                    retryPolicy: new ProviderRetryPolicy(maxRetries: 1, delay: TimeSpan.Zero)), session);
                var events = new List<AgentLifecycleEvent>();
                await foreach (var item in run.RunEventsAsync("hello", deadline.Token)) events.Add(item);
                await server.WaitAsync(deadline.Token);

                Assert.Equal(1, requests);
                Assert.Contains(events, item => item.Type == "model_text_delta" && item.Text == "partial");
                Assert.DoesNotContain(events, item => item.Type == "model_retry_scheduled");
                Assert.Contains(events, item => item.Type == "turn_failed");
            }
            finally { Directory.Delete(cwd, recursive: true); }
        }
    }

    [Fact]
    public async Task MistralUsesRetryResponseHeadersAndPreservesTerminalHttpErrorText()
    {
        using (var listener = StartListener(out var latePort))
        using (var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10)))
        {
            var requests = 0;
            var server = Task.Run(async () =>
            {
                for (var attempt = 0; attempt < 2; attempt++)
                {
                    var request = await listener.GetContextAsync().WaitAsync(deadline.Token);
                    Interlocked.Increment(ref requests);
                    using var reader = new StreamReader(request.Request.InputStream);
                    await reader.ReadToEndAsync(deadline.Token);
                    if (attempt == 0)
                    {
                        request.Response.StatusCode = (int)HttpStatusCode.TooManyRequests;
                        request.Response.Headers["x-should-retry"] = "true";
                        request.Response.Headers["retry-after-ms"] = "0";
                        request.Response.ContentType = "application/json";
                        await using var errorWriter = new StreamWriter(request.Response.OutputStream);
                        await errorWriter.WriteAsync("{\"error\":\"busy\"}");
                        await errorWriter.FlushAsync(deadline.Token);
                    }
                    else
                    {
                        request.Response.ContentType = "text/event-stream";
                        await using var writer = new StreamWriter(request.Response.OutputStream);
                        await writer.WriteAsync("data: {\"id\":\"retry-ok\",\"model\":\"fixture-model\",\"choices\":[{\"index\":0,\"delta\":{\"content\":\"ok\"},\"finish_reason\":null}]}\n\ndata: {\"id\":\"retry-ok\",\"choices\":[{\"index\":0,\"delta\":{},\"finish_reason\":\"stop\"}]}\n\ndata: [DONE]\n\n");
                        await writer.FlushAsync(deadline.Token);
                    }
                    request.Response.Close();
                }
            }, deadline.Token);
            var model = new ModelDescriptor("fixture-model", "mistral", 128000, "fixture",
                Provider: "mistral", Api: "mistral-conversations");
            var selection = new ModelSelection(new ProviderProfile("mistral", "Mistral", new Uri($"http://127.0.0.1:{latePort}/v1"),
                true, false, null, null, [model], Api: "mistral-conversations"), model, "fixture-key", true, "fixture");

            var response = await ProviderChatClientFactory.Create(selection,
                new ProviderRetrySettings(MaxRetries: 1, TimeoutMs: 2000, MaxRetryDelayMs: 10000))
                .GetResponseAsync([new ChatMessage(ChatRole.User, "hello")], cancellationToken: deadline.Token);
            await server.WaitAsync(deadline.Token);

            Assert.Equal(2, requests);
            Assert.Equal("ok", response.Text);
        }

        using (var listener = StartListener(out var port))
        using (var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10)))
        {
            var requests = 0;
            var server = Task.Run(async () =>
            {
                var request = await listener.GetContextAsync().WaitAsync(deadline.Token);
                Interlocked.Increment(ref requests);
                using var reader = new StreamReader(request.Request.InputStream);
                await reader.ReadToEndAsync(deadline.Token);
                request.Response.StatusCode = (int)HttpStatusCode.BadRequest;
                request.Response.ContentType = "application/json";
                await using var writer = new StreamWriter(request.Response.OutputStream);
                await writer.WriteAsync("{\"error\":\"invalid request\"}");
                await writer.FlushAsync(deadline.Token);
                request.Response.Close();
            }, deadline.Token);
            var model = new ModelDescriptor("fixture-model", "mistral", 128000, "fixture",
                Provider: "mistral", Api: "mistral-conversations");
            var selection = new ModelSelection(new ProviderProfile("mistral", "Mistral", new Uri($"http://127.0.0.1:{port}/v1"),
                true, false, null, null, [model], Api: "mistral-conversations"), model, "fixture-key", true, "fixture");

            var error = await Assert.ThrowsAsync<HttpRequestException>(() =>
                ProviderChatClientFactory.Create(selection).GetResponseAsync([new ChatMessage(ChatRole.User, "hello")],
                    cancellationToken: deadline.Token));
            await server.WaitAsync(deadline.Token);

            Assert.Equal(1, requests);
            Assert.Equal("Mistral API error (400): {\"error\":\"invalid request\"}", error.Message);
            Assert.Equal(HttpStatusCode.BadRequest, error.StatusCode);
        }
    }

    [Fact]
    public async Task MistralHeaderTimeoutStopsAtResponseHeaders()
    {
        const int headerTimeoutMs = 1_000;
        using (var listener = StartListener(out var longPort))
        using (var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10)))
        {
            var server = Task.Run(async () =>
            {
                var request = await listener.GetContextAsync().WaitAsync(deadline.Token);
                using var reader = new StreamReader(request.Request.InputStream);
                await reader.ReadToEndAsync(deadline.Token);
                await Task.Delay(1_500, deadline.Token);
                try
                {
                    request.Response.ContentType = "text/event-stream";
                    await request.Response.OutputStream.WriteAsync(Encoding.UTF8.GetBytes("data: [DONE]\n\n"), deadline.Token);
                }
                catch (Exception error) when (error is IOException or HttpListenerException or ObjectDisposedException) { }
                finally
                {
                    try { request.Response.Close(); }
                    catch (HttpListenerException) { }
                }
            }, deadline.Token);
            var model = new ModelDescriptor("fixture-model", "mistral", 128000, "fixture",
                Provider: "mistral", Api: "mistral-conversations");
            var selection = new ModelSelection(new ProviderProfile("mistral", "Mistral", new Uri($"http://127.0.0.1:{longPort}/v1"),
                true, false, null, null, [model], Api: "mistral-conversations"), model, "fixture-key", true, "fixture");

            var error = await Assert.ThrowsAsync<TimeoutException>(() =>
                ProviderChatClientFactory.Create(selection, new ProviderRetrySettings(TimeoutMs: headerTimeoutMs))
                    .GetResponseAsync([new ChatMessage(ChatRole.User, "hello")], cancellationToken: deadline.Token));
            await server.WaitAsync(deadline.Token);
            Assert.Equal($"Mistral response headers timed out after {headerTimeoutMs}ms", error.Message);
        }

        using (var listener = StartListener(out var port))
        using (var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10)))
        {
            var server = Task.Run(async () =>
            {
                var request = await listener.GetContextAsync().WaitAsync(deadline.Token);
                using var reader = new StreamReader(request.Request.InputStream);
                await reader.ReadToEndAsync(deadline.Token);
                request.Response.ContentType = "text/event-stream";
                request.Response.SendChunked = true;
                await request.Response.OutputStream.WriteAsync(Encoding.UTF8.GetBytes("\n"), deadline.Token);
                await request.Response.OutputStream.FlushAsync(deadline.Token);
                await Task.Delay(1_500, deadline.Token);
                await request.Response.OutputStream.WriteAsync(Encoding.UTF8.GetBytes(
                    "data: {\"id\":\"late-body\",\"model\":\"fixture-model\",\"choices\":[{\"index\":0,\"delta\":{\"content\":\"late\"},\"finish_reason\":null}]}\n\ndata: {\"id\":\"late-body\",\"choices\":[{\"index\":0,\"delta\":{},\"finish_reason\":\"stop\"}]}\n\ndata: [DONE]\n\n"), deadline.Token);
                await request.Response.OutputStream.FlushAsync(deadline.Token);
                request.Response.Close();
            }, deadline.Token);
            var model = new ModelDescriptor("fixture-model", "mistral", 128000, "fixture",
                Provider: "mistral", Api: "mistral-conversations");
            var selection = new ModelSelection(new ProviderProfile("mistral", "Mistral", new Uri($"http://127.0.0.1:{port}/v1"),
                true, false, null, null, [model], Api: "mistral-conversations"), model, "fixture-key", true, "fixture");

            var response = await ProviderChatClientFactory.Create(selection,
                new ProviderRetrySettings(TimeoutMs: headerTimeoutMs), httpIdleTimeoutMs: 3000)
                .GetResponseAsync([new ChatMessage(ChatRole.User, "hello")], cancellationToken: deadline.Token);
            await server.WaitAsync(deadline.Token);
            Assert.Equal("late", response.Text);
        }
    }

    [Fact]
    public async Task MistralStreamHonorsCallerCancellationAfterHeaders()
    {
        using var listener = StartListener(out var port);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var cancellation = new CancellationTokenSource();
        var headersSent = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseServer = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var server = Task.Run(async () =>
        {
            var request = await listener.GetContextAsync().WaitAsync(deadline.Token);
            using var reader = new StreamReader(request.Request.InputStream);
            await reader.ReadToEndAsync(deadline.Token);
            request.Response.ContentType = "text/event-stream";
            request.Response.SendChunked = true;
            await request.Response.OutputStream.WriteAsync(Encoding.UTF8.GetBytes("\n"), deadline.Token);
            await request.Response.OutputStream.FlushAsync(deadline.Token);
            headersSent.TrySetResult();
            await releaseServer.Task.WaitAsync(deadline.Token);
            try
            {
                await request.Response.OutputStream.WriteAsync(Encoding.UTF8.GetBytes("data: [DONE]\n\n"), deadline.Token);
                await request.Response.OutputStream.FlushAsync(deadline.Token);
            }
            catch (Exception error) when (error is IOException or HttpListenerException or ObjectDisposedException) { }
            finally { request.Response.Close(); }
        }, deadline.Token);
        var model = new ModelDescriptor("fixture-model", "mistral", 128000, "fixture",
            Provider: "mistral", Api: "mistral-conversations");
        var selection = new ModelSelection(new ProviderProfile("mistral", "Mistral", new Uri($"http://127.0.0.1:{port}/v1"),
            true, false, null, null, [model], Api: "mistral-conversations"), model, "fixture-key", true, "fixture");
        await using var updates = ProviderChatClientFactory.Create(selection,
            new ProviderRetrySettings(TimeoutMs: 3000), httpIdleTimeoutMs: 1000)
            .GetStreamingResponseAsync([new ChatMessage(ChatRole.User, "hello")], cancellationToken: cancellation.Token)
            .GetAsyncEnumerator(cancellation.Token);

        var pendingUpdate = updates.MoveNextAsync().AsTask();
        await headersSent.Task.WaitAsync(deadline.Token);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pendingUpdate);
        releaseServer.TrySetResult();
        await server.WaitAsync(deadline.Token);
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
        request.Response.ContentType = "text/event-stream";
        await using var writer = new StreamWriter(request.Response.OutputStream);
        await writer.WriteAsync("data: {\"id\":\"mistral-response\",\"model\":\"fixture-model\",\"choices\":[{\"index\":0,\"delta\":{\"content\":\"ok\"},\"finish_reason\":null}]}\n\n");
        await writer.WriteAsync("data: {\"id\":\"mistral-response\",\"model\":\"fixture-model\",\"choices\":[{\"index\":0,\"delta\":{},\"finish_reason\":\"stop\"}],\"usage\":{\"prompt_tokens\":1,\"completion_tokens\":1,\"total_tokens\":2}}\n\n");
        await writer.WriteAsync("data: [DONE]\n\n");
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
