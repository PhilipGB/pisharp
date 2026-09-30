using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using PiSharp.Cli;
using PiSharp.Runtime.Providers;
using PiSharp.Runtime;

namespace PiSharp.Tests;

public sealed class ProviderChatClientFactoryTests
{
    [Fact]
    public void ProtocolSelectionDoesNotTreatEveryProviderAsOpenAiCompletions()
    {
        var official = Selection("openai", "https://api.openai.com/v1");
        Assert.Equal("openai-responses", ProviderChatClientFactory.ResolveProtocol(official));
        Assert.Equal("openai-completions", ProviderChatClientFactory.ResolveProtocol(Selection("openai", "https://api.openai.com/v1", "openai-completions")));
        Assert.Equal("openai-completions", ProviderChatClientFactory.ResolveProtocol(Selection("openrouter", "https://openrouter.ai/api/v1")));
        Assert.Equal("mistral-conversations", ProviderChatClientFactory.ResolveProtocol(Selection("mistral", "https://api.mistral.ai/v1")));
        Assert.Equal("openai-responses", ProviderChatClientFactory.ResolveProtocol(Selection("custom", "http://localhost:1234/v1", "openai-responses")));
        Assert.Equal("anthropic-messages", ProviderChatClientFactory.ResolveProtocol(Selection("anthropic", "https://api.anthropic.com")));
        Assert.Equal("anthropic-messages", ProviderChatClientFactory.ResolveProtocol(Selection("custom", "http://localhost:1234", "anthropic-messages")));
        Assert.Throws<NotSupportedException>(() => ProviderChatClientFactory.ResolveProtocol(Selection("custom", "http://localhost:1234/v1", "google-generative-ai")));
        Assert.Throws<InvalidOperationException>(() => ProviderChatClientFactory.ResolveProtocol(Selection("openai", "https://untrusted.test/v1")));
    }

    [Fact]
    public async Task ExplicitResponsesProtocolStreamsThroughResponsesEndpoint()
    {
        using var listener = StartLoopbackListener(out var port);
        var server = Task.Run(async () =>
        {
            var request = await listener.GetContextAsync();
            Assert.Equal("/v1/responses", request.Request.Url?.AbsolutePath);
            Assert.Equal("Bearer fixture-key", request.Request.Headers["Authorization"]);
            using var reader = new StreamReader(request.Request.InputStream);
            Assert.Contains("fixture-model", await reader.ReadToEndAsync());
            request.Response.ContentType = "text/event-stream";
            await using var writer = new StreamWriter(request.Response.OutputStream);
            await writer.WriteAsync("data: {\"type\":\"response.created\",\"response\":{\"id\":\"resp_1\",\"object\":\"response\",\"created_at\":1,\"model\":\"fixture-model\",\"status\":\"in_progress\",\"output\":[]}}\n\n");
            await writer.WriteAsync("data: {\"type\":\"response.output_text.delta\",\"item_id\":\"item_1\",\"output_index\":0,\"content_index\":0,\"delta\":\"hello\"}\n\n");
            await writer.WriteAsync("data: [DONE]\n\n");
            await writer.FlushAsync();
            request.Response.Close();
        });
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var selection = Selection("fixture", $"http://127.0.0.1:{port}/v1", "openai-responses");
        var agent = new PiAgent(ProviderChatClientFactory.Create(selection), new CodingTools(Path.GetTempPath()));
        var session = await agent.CreateSessionAsync(deadline.Token);
        var text = "";
        await foreach (var update in agent.RunStreamingAsync("say hello", session, deadline.Token)) text += update.Text;
        await server.WaitAsync(deadline.Token);
        Assert.Equal("hello", text);
    }

    [Fact]
    public async Task ResponsesToolCallContinuesUsingCanonicalToolResult()
    {
        using var listener = StartLoopbackListener(out var port);
        var requests = new List<string>();
        var server = Task.Run(async () =>
        {
            for (var i = 0; i < 2; i++)
            {
                var request = await listener.GetContextAsync();
                Assert.Equal("/v1/responses", request.Request.Url?.AbsolutePath);
                using var reader = new StreamReader(request.Request.InputStream);
                requests.Add(await reader.ReadToEndAsync());
                request.Response.ContentType = "text/event-stream";
                await using var writer = new StreamWriter(request.Response.OutputStream);
                await writer.WriteAsync($"data: {{\"type\":\"response.created\",\"response\":{{\"id\":\"resp_{i}\",\"object\":\"response\",\"created_at\":1,\"model\":\"fixture-model\",\"status\":\"in_progress\",\"output\":[]}}}}\n\n");
                if (i == 0)
                {
                    await writer.WriteAsync("data: {\"type\":\"response.output_item.added\",\"output_index\":0,\"item\":{\"id\":\"fc_1\",\"type\":\"function_call\",\"call_id\":\"call_1\",\"name\":\"echo_ext\",\"arguments\":\"\"}}\n\n");
                    await writer.WriteAsync("data: {\"type\":\"response.function_call_arguments.done\",\"output_index\":0,\"item_id\":\"fc_1\",\"arguments\":\"{\\\"text\\\":\\\"ping\\\"}\"}\n\n");
                    await writer.WriteAsync("data: {\"type\":\"response.output_item.done\",\"output_index\":0,\"item\":{\"id\":\"fc_1\",\"type\":\"function_call\",\"call_id\":\"call_1\",\"name\":\"echo_ext\",\"arguments\":\"{\\\"text\\\":\\\"ping\\\"}\"}}\n\n");
                }
                else
                    await writer.WriteAsync("data: {\"type\":\"response.output_text.delta\",\"item_id\":\"msg_1\",\"output_index\":0,\"content_index\":0,\"delta\":\"done\"}\n\n");
                await writer.WriteAsync($"data: {{\"type\":\"response.completed\",\"response\":{{\"id\":\"resp_{i}\",\"object\":\"response\",\"created_at\":1,\"model\":\"fixture-model\",\"status\":\"completed\",\"output\":[]}}}}\n\n");
                await writer.WriteAsync("data: [DONE]\n\n");
                await writer.FlushAsync();
                request.Response.Close();
            }
        });
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var selection = Selection("fixture", $"http://127.0.0.1:{port}/v1", "openai-responses");
        var echo = Microsoft.Extensions.AI.AIFunctionFactory.Create((string text) => "echo:" + text, name: "echo_ext");
        var agent = new PiAgent(ProviderChatClientFactory.Create(selection), new CodingTools(Path.GetTempPath()), extensionTools: [echo]);
        var session = await agent.CreateSessionAsync(deadline.Token);
        var text = "";
        await foreach (var update in agent.RunStreamingAsync("use echo", session, deadline.Token)) text += update.Text;
        await server.WaitAsync(deadline.Token);
        Assert.Equal("done", text);
        Assert.Contains("echo:ping", requests[1]);
        Assert.DoesNotContain("previous_response_id", requests[1]);
    }
    [Fact]
    public async Task ResponsesTransportSendsInlineImageAsImageContentNotText()
    {
        using var listener = StartLoopbackListener(out var port);
        var png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+/bX8AAAAASUVORK5CYII=");
        var server = Task.Run(async () =>
        {
            var request = await listener.GetContextAsync();
            Assert.Equal("/v1/responses", request.Request.Url?.AbsolutePath);
            using var reader = new StreamReader(request.Request.InputStream);
            using var body = System.Text.Json.JsonDocument.Parse(await reader.ReadToEndAsync());
            var input = body.RootElement.GetProperty("input");
            var parts = input.EnumerateArray().Last().GetProperty("content").EnumerateArray().ToArray();
            Assert.Contains(parts, part => part.GetProperty("type").GetString() == "input_text" &&
                part.GetProperty("text").GetString() == "describe image");
            Assert.Contains(parts, part => part.GetProperty("type").GetString() == "input_image" &&
                part.GetProperty("image_url").GetString()!.StartsWith("data:image/png;base64,", StringComparison.Ordinal));
            request.Response.ContentType = "application/json";
            await using var writer = new StreamWriter(request.Response.OutputStream);
            await writer.WriteAsync("""
                {"id":"resp_image","object":"response","created_at":1,"model":"fixture-model","status":"completed","output":[{"id":"msg_1","type":"message","role":"assistant","status":"completed","content":[{"type":"output_text","text":"seen","annotations":[]}]}]}
                """);
            await writer.FlushAsync();
            request.Response.Close();
        });
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var selection = Selection("fixture", $"http://127.0.0.1:{port}/v1", "openai-responses");
        var prompt = new Microsoft.Extensions.AI.ChatMessage(Microsoft.Extensions.AI.ChatRole.User,
            [new Microsoft.Extensions.AI.TextContent("describe image"), new Microsoft.Extensions.AI.DataContent(png, "image/png")]);
        var response = await ProviderChatClientFactory.Create(selection).GetResponseAsync([prompt], cancellationToken: deadline.Token);
        await server.WaitAsync(deadline.Token);
        Assert.Equal("seen", response.Text);
    }

    [Fact]
    public async Task NonStreamingResponsesDoNotEnableProviderOwnedHistory()
    {
        using var listener = StartLoopbackListener(out var port);
        var server = Task.Run(async () =>
        {
            var request = await listener.GetContextAsync();
            Assert.Equal("/v1/responses", request.Request.Url?.AbsolutePath);
            request.Response.ContentType = "application/json";
            await using var writer = new StreamWriter(request.Response.OutputStream);
            await writer.WriteAsync("""
                {"id":"resp_42","object":"response","created_at":1,"model":"fixture-model","status":"completed","output":[{"id":"msg_1","type":"message","role":"assistant","status":"completed","content":[{"type":"output_text","text":"ok","annotations":[]}]}],"usage":{"input_tokens":11,"output_tokens":2,"total_tokens":13}}
                """);
            await writer.FlushAsync();
            request.Response.Close();
        });
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var selection = Selection("fixture", $"http://127.0.0.1:{port}/v1", "openai-responses");
        var response = await ProviderChatClientFactory.Create(selection).GetResponseAsync(
            [new Microsoft.Extensions.AI.ChatMessage(Microsoft.Extensions.AI.ChatRole.User, "hello")], cancellationToken: deadline.Token);
        await server.WaitAsync(deadline.Token);
        Assert.Equal("ok", response.Text);
        Assert.Null(response.ConversationId);
        Assert.Equal(11, response.Usage?.InputTokenCount);
    }

    [Fact]
    public async Task OpenAiProviderRetrySettingControlsTotalAttemptCount()
    {
        using (var listener = StartLoopbackListener(out var port))
        {
            var server = Task.Run(async () =>
            {
                var request = await listener.GetContextAsync();
                request.Response.StatusCode = 503;
                await using var writer = new StreamWriter(request.Response.OutputStream);
                await writer.WriteAsync("{\"error\":{\"message\":\"temporary\"}}");
                await writer.FlushAsync();
                request.Response.Close();
            });
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var selection = Selection("fixture", $"http://127.0.0.1:{port}/v1", "openai-responses");
            await Assert.ThrowsAnyAsync<Exception>(() => ProviderChatClientFactory.Create(selection).GetResponseAsync(
                [new Microsoft.Extensions.AI.ChatMessage(Microsoft.Extensions.AI.ChatRole.User, "hello")], cancellationToken: deadline.Token));
            await server.WaitAsync(deadline.Token);
        }

        using (var listener = StartLoopbackListener(out var port))
        {
            var requests = 0;
            var server = Task.Run(async () =>
            {
                for (var index = 0; index < 2; index++)
                {
                    var request = await listener.GetContextAsync();
                    requests++;
                    if (index == 0)
                    {
                        request.Response.StatusCode = 503;
                        await using var writer = new StreamWriter(request.Response.OutputStream);
                        await writer.WriteAsync("{\"error\":{\"message\":\"temporary\"}}");
                        await writer.FlushAsync();
                    }
                    else
                    {
                        request.Response.ContentType = "application/json";
                        await using var writer = new StreamWriter(request.Response.OutputStream);
                        await writer.WriteAsync("""
                            {"id":"resp_retry","object":"response","created_at":1,"model":"fixture-model","status":"completed","output":[{"id":"msg_1","type":"message","role":"assistant","status":"completed","content":[{"type":"output_text","text":"retried","annotations":[]}]}],"usage":{"input_tokens":1,"output_tokens":1,"total_tokens":2}}
                            """);
                        await writer.FlushAsync();
                    }
                    request.Response.Close();
                }
            });
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            var selection = Selection("fixture", $"http://127.0.0.1:{port}/v1", "openai-responses");
            var response = await ProviderChatClientFactory.Create(selection, new ProviderRetrySettings(MaxRetries: 1))
                .GetResponseAsync([new Microsoft.Extensions.AI.ChatMessage(Microsoft.Extensions.AI.ChatRole.User, "hello")], cancellationToken: deadline.Token);
            await server.WaitAsync(deadline.Token);
            Assert.Equal("retried", response.Text);
            Assert.Equal(2, requests);
        }
    }

    [Fact]
    public async Task AnthropicProviderRetrySettingControlsTotalAttemptCount()
    {
        using (var listener = StartLoopbackListener(out var port))
        {
            var server = Task.Run(async () =>
            {
                var request = await listener.GetContextAsync();
                request.Response.StatusCode = 500;
                await using var writer = new StreamWriter(request.Response.OutputStream);
                await writer.WriteAsync("{\"error\":{\"message\":\"temporary\"}}");
                await writer.FlushAsync();
                request.Response.Close();
            });
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var selection = Selection("fixture", $"http://127.0.0.1:{port}", "anthropic-messages");
            await Assert.ThrowsAnyAsync<Exception>(() => ProviderChatClientFactory.Create(selection).GetResponseAsync(
                [new Microsoft.Extensions.AI.ChatMessage(Microsoft.Extensions.AI.ChatRole.User, "hello")], cancellationToken: deadline.Token));
            await server.WaitAsync(deadline.Token);
        }

        using (var listener = StartLoopbackListener(out var port))
        {
            var requests = 0;
            var server = Task.Run(async () =>
            {
                for (var index = 0; index < 2; index++)
                {
                    var request = await listener.GetContextAsync();
                    requests++;
                    if (index == 0)
                    {
                        request.Response.StatusCode = 500;
                        await using var writer = new StreamWriter(request.Response.OutputStream);
                        await writer.WriteAsync("{\"error\":{\"message\":\"temporary\"}}");
                        await writer.FlushAsync();
                    }
                    else
                    {
                        request.Response.ContentType = "application/json";
                        await using var writer = new StreamWriter(request.Response.OutputStream);
                        await writer.WriteAsync("""
                            {"id":"msg_retry","type":"message","role":"assistant","model":"fixture-model","content":[{"type":"text","text":"retried"}],"stop_reason":"end_turn","stop_sequence":null,"usage":{"input_tokens":1,"output_tokens":1}}
                            """);
                        await writer.FlushAsync();
                    }
                    request.Response.Close();
                }
            });
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            var selection = Selection("fixture", $"http://127.0.0.1:{port}", "anthropic-messages");
            var response = await ProviderChatClientFactory.Create(selection, new ProviderRetrySettings(MaxRetries: 1))
                .GetResponseAsync([new Microsoft.Extensions.AI.ChatMessage(Microsoft.Extensions.AI.ChatRole.User, "hello")], cancellationToken: deadline.Token);
            await server.WaitAsync(deadline.Token);
            Assert.Equal("retried", response.Text);
            Assert.Equal(2, requests);
        }
    }

    [Theory]
    [InlineData("openai-responses")]
    [InlineData("anthropic-messages")]
    public async Task ProviderRetryDelayAboveConfiguredMaximumFailsWithoutAnotherRequest(string protocol)
    {
        using var listener = StartLoopbackListener(out var port);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        var requests = 0;
        var server = Task.Run(async () =>
        {
            var request = await listener.GetContextAsync().WaitAsync(deadline.Token);
            Interlocked.Increment(ref requests);
            request.Response.StatusCode = 429;
            request.Response.Headers[HttpResponseHeader.RetryAfter] = "1";
            request.Response.ContentType = "application/json";
            await using var writer = new StreamWriter(request.Response.OutputStream);
            await writer.WriteAsync(protocol == "anthropic-messages"
                ? "{\"type\":\"error\",\"error\":{\"type\":\"rate_limit_error\",\"message\":\"rate limited\"}}"
                : "{\"error\":{\"message\":\"rate limited\"}}");
            await writer.FlushAsync();
            request.Response.Close();
            listener.Stop();
        });

        var selection = Selection("fixture", protocol == "anthropic-messages"
            ? $"http://127.0.0.1:{port}"
            : $"http://127.0.0.1:{port}/v1", protocol);
        var error = await Assert.ThrowsAnyAsync<Exception>(() => ProviderChatClientFactory.Create(selection,
            new ProviderRetrySettings(MaxRetries: 1, MaxRetryDelayMs: 100))
            .GetResponseAsync([new Microsoft.Extensions.AI.ChatMessage(Microsoft.Extensions.AI.ChatRole.User, "hello")],
                cancellationToken: deadline.Token));
        await server.WaitAsync(deadline.Token);

        Assert.Contains("Server requested 1s retry delay (max: 1s)", error.Message);
        Assert.Equal(1, requests);
    }

    [Theory]
    [InlineData("not-a-date")]
    [InlineData("Infinity")]
    public async Task InvalidRetryAfterUsesExponentialFallbackInsteadOfRetryingImmediately(string retryAfter)
    {
        using var listener = StartLoopbackListener(out var port);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        var requests = 0;
        var secondRequestDelayMs = 0L;
        var startedAt = System.Diagnostics.Stopwatch.GetTimestamp();
        var server = Task.Run(async () =>
        {
            for (var attempt = 0; attempt < 2; attempt++)
            {
                var request = await listener.GetContextAsync().WaitAsync(deadline.Token);
                Interlocked.Increment(ref requests);
                if (attempt == 1)
                    secondRequestDelayMs = (long)System.Diagnostics.Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds;
                request.Response.StatusCode = 429;
                request.Response.Headers[HttpResponseHeader.RetryAfter] = retryAfter;
                request.Response.ContentType = "application/json";
                await using var writer = new StreamWriter(request.Response.OutputStream);
                await writer.WriteAsync("{\"error\":{\"message\":\"rate limited\"}}");
                await writer.FlushAsync();
                request.Response.Close();
            }
            listener.Stop();
        });

        var selection = Selection("fixture", $"http://127.0.0.1:{port}/v1", "openai-responses");
        await Assert.ThrowsAnyAsync<Exception>(() => ProviderChatClientFactory.Create(selection,
            new ProviderRetrySettings(MaxRetries: 1))
            .GetResponseAsync([new Microsoft.Extensions.AI.ChatMessage(Microsoft.Extensions.AI.ChatRole.User, "hello")],
                cancellationToken: deadline.Token));
        await server.WaitAsync(deadline.Token);

        Assert.Equal(2, requests);
        Assert.True(secondRequestDelayMs >= 300,
            $"The retry arrived after {secondRequestDelayMs}ms instead of using exponential backoff.");
    }

    [Fact]
    public async Task ProviderRetryCanRestartAStreamBeforeItsFirstUpdate()
    {
        using var listener = StartLoopbackListener(out var port);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var requests = 0;
        var server = Task.Run(async () =>
        {
            for (var attempt = 0; attempt < 2; attempt++)
            {
                var request = await listener.GetContextAsync().WaitAsync(deadline.Token);
                Interlocked.Increment(ref requests);
                if (attempt == 0)
                {
                    request.Response.StatusCode = 429;
                    request.Response.Headers["Retry-After-Ms"] = "0";
                    request.Response.ContentType = "application/json";
                    await using var errorWriter = new StreamWriter(request.Response.OutputStream);
                    await errorWriter.WriteAsync("{\"error\":{\"message\":\"rate limited\"}}");
                    await errorWriter.FlushAsync();
                    request.Response.Close();
                    continue;
                }

                request.Response.ContentType = "text/event-stream";
                await using var writer = new StreamWriter(request.Response.OutputStream);
                await writer.WriteAsync("data: {\"type\":\"response.created\",\"response\":{\"id\":\"resp_1\",\"object\":\"response\",\"created_at\":1,\"model\":\"fixture-model\",\"status\":\"in_progress\",\"output\":[]}}\n\n");
                await writer.WriteAsync("data: {\"type\":\"response.output_text.delta\",\"item_id\":\"item_1\",\"output_index\":0,\"content_index\":0,\"delta\":\"hello\"}\n\n");
                await writer.WriteAsync("data: {\"type\":\"response.completed\",\"response\":{\"id\":\"resp_1\",\"object\":\"response\",\"created_at\":1,\"model\":\"fixture-model\",\"status\":\"completed\",\"output\":[]}}\n\n");
                await writer.WriteAsync("data: [DONE]\n\n");
                await writer.FlushAsync();
                request.Response.Close();
            }
        });

        var client = ProviderChatClientFactory.Create(Selection("fixture", $"http://127.0.0.1:{port}/v1", "openai-responses"),
            new ProviderRetrySettings(MaxRetries: 1));
        var text = "";
        await foreach (var update in client.GetStreamingResponseAsync(
            [new Microsoft.Extensions.AI.ChatMessage(Microsoft.Extensions.AI.ChatRole.User, "hello")],
            cancellationToken: deadline.Token))
            text += update.Text;
        await server.WaitAsync(deadline.Token);

        Assert.Equal("hello", text);
        Assert.Equal(2, requests);
    }

    [Theory]
    [InlineData("openai-responses")]
    [InlineData("anthropic-messages")]
    public async Task ProviderRetryHonorsNoRetryResponseHeader(string protocol)
    {
        using var listener = StartLoopbackListener(out var port);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(6));
        var server = Task.Run(async () =>
        {
            var request = await listener.GetContextAsync().WaitAsync(deadline.Token);
            request.Response.StatusCode = 429;
            request.Response.Headers[HttpResponseHeader.RetryAfter] = "1";
            request.Response.Headers["x-should-retry"] = "false";
            request.Response.ContentType = "application/json";
            await using var writer = new StreamWriter(request.Response.OutputStream);
            await writer.WriteAsync(protocol == "anthropic-messages"
                ? "{\"type\":\"error\",\"error\":{\"type\":\"rate_limit_error\",\"message\":\"rate limited\"}}"
                : "{\"error\":{\"message\":\"rate limited\"}}");
            await writer.FlushAsync();
            request.Response.Close();
            listener.Stop();
        });

        var selection = Selection("fixture", protocol == "anthropic-messages"
            ? $"http://127.0.0.1:{port}"
            : $"http://127.0.0.1:{port}/v1", protocol);
        var error = await Assert.ThrowsAnyAsync<Exception>(() => ProviderChatClientFactory.Create(selection,
            new ProviderRetrySettings(MaxRetries: 1, MaxRetryDelayMs: 100))
            .GetResponseAsync([new Microsoft.Extensions.AI.ChatMessage(Microsoft.Extensions.AI.ChatRole.User, "hello")],
                cancellationToken: deadline.Token));
        await server.WaitAsync(deadline.Token);

        Assert.DoesNotContain("Server requested", error.Message);
        Assert.Contains("rate limited", error.Message);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ProviderRetryDelayWaitRemainsCancellable(bool malformedRetryAfter)
    {
        using var listener = StartLoopbackListener(out var port);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(6));
        using var requestCancellation = new CancellationTokenSource();
        var requests = 0;
        var server = Task.Run(async () =>
        {
            var request = await listener.GetContextAsync().WaitAsync(deadline.Token);
            Interlocked.Increment(ref requests);
            request.Response.StatusCode = 429;
            if (malformedRetryAfter)
                request.Response.Headers[HttpResponseHeader.RetryAfter] = "not-a-date";
            else
                request.Response.Headers["Retry-After-Ms"] = "5000";
            request.Response.ContentType = "application/json";
            await using var writer = new StreamWriter(request.Response.OutputStream);
            await writer.WriteAsync("{\"error\":{\"message\":\"rate limited\"}}");
            await writer.FlushAsync();
            request.Response.Close();
            listener.Stop();
        });

        var client = ProviderChatClientFactory.Create(Selection("fixture", $"http://127.0.0.1:{port}/v1", "openai-responses"),
            new ProviderRetrySettings(MaxRetries: 1, MaxRetryDelayMs: 0));
        var requestTask = client.GetResponseAsync(
            [new Microsoft.Extensions.AI.ChatMessage(Microsoft.Extensions.AI.ChatRole.User, "hello")],
            cancellationToken: requestCancellation.Token);
        await server.WaitAsync(deadline.Token);
        await Task.Delay(50, deadline.Token);
        requestCancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => requestTask);
        Assert.Equal(1, requests);
    }

    [Theory]
    [InlineData("openai-responses")]
    [InlineData("anthropic-messages")]
    public async Task ProviderTimeoutSettingCancelsAStalledLoopbackResponse(string protocol)
    {
        using var listener = StartLoopbackListener(out var port);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(6));
        var requests = 0;
        var server = Task.Run(async () =>
        {
            var request = await listener.GetContextAsync().WaitAsync(deadline.Token);
            Interlocked.Increment(ref requests);
            try
            {
                await Task.Delay(2_500, deadline.Token);
                request.Response.ContentType = "application/json";
                await using var writer = new StreamWriter(request.Response.OutputStream);
                await writer.WriteAsync(protocol == "openai-responses" ? """
                    {"id":"resp_slow","object":"response","created_at":1,"model":"fixture-model","status":"completed","output":[{"id":"msg_1","type":"message","role":"assistant","status":"completed","content":[{"type":"output_text","text":"late","annotations":[]}]}]}
                    """ : """
                    {"id":"msg_slow","type":"message","role":"assistant","model":"fixture-model","content":[{"type":"text","text":"late"}],"stop_reason":"end_turn","stop_sequence":null,"usage":{"input_tokens":1,"output_tokens":1}}
                    """);
                await writer.FlushAsync();
            }
            catch (Exception error) when (error is IOException or HttpListenerException or ObjectDisposedException) { }
            finally { request.Response.Close(); }
        });
        var selection = Selection("fixture", protocol == "openai-responses"
            ? $"http://127.0.0.1:{port}/v1" : $"http://127.0.0.1:{port}", protocol);
        var client = ProviderChatClientFactory.Create(selection,
            new ProviderRetrySettings(TimeoutMs: 1_500), httpIdleTimeoutMs: 3_000);
        var timer = System.Diagnostics.Stopwatch.StartNew();

        var error = await Assert.ThrowsAsync<TimeoutException>(() => client.GetResponseAsync(
            [new Microsoft.Extensions.AI.ChatMessage(Microsoft.Extensions.AI.ChatRole.User, "hello")],
            cancellationToken: deadline.Token));

        Assert.Contains("timed out", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.True(timer.Elapsed < TimeSpan.FromMilliseconds(2_200), $"The provider request took {timer.Elapsed}.");
        await server.WaitAsync(deadline.Token);
        Assert.Equal(1, requests);
    }

    [Theory]
    [InlineData("openai-responses")]
    [InlineData("anthropic-messages")]
    public async Task ProviderStreamIdleSettingCancelsAStalledLoopbackStream(string protocol)
    {
        using var listener = StartLoopbackListener(out var port);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        var requests = 0;
        var server = Task.Run(async () =>
        {
            var request = await listener.GetContextAsync().WaitAsync(deadline.Token);
            Interlocked.Increment(ref requests);
            using var reader = new StreamReader(request.Request.InputStream);
            await reader.ReadToEndAsync(deadline.Token);
            request.Response.ContentType = "text/event-stream";
            request.Response.SendChunked = true;
            await using var writer = new StreamWriter(request.Response.OutputStream);
            try
            {
                if (protocol == "openai-responses")
                {
                    await writer.WriteAsync("data: {\"type\":\"response.created\",\"response\":{\"id\":\"resp_idle\",\"object\":\"response\",\"created_at\":1,\"model\":\"fixture-model\",\"status\":\"in_progress\",\"output\":[]}}\n\n");
                    await writer.WriteAsync("data: {\"type\":\"response.output_text.delta\",\"item_id\":\"item_1\",\"output_index\":0,\"content_index\":0,\"delta\":\"first\"}\n\n");
                }
                else
                {
                    await writer.WriteAsync("event: message_start\ndata: {\"type\":\"message_start\",\"message\":{\"id\":\"msg_idle\",\"type\":\"message\",\"role\":\"assistant\",\"model\":\"fixture-model\",\"content\":[],\"stop_reason\":null,\"stop_sequence\":null,\"usage\":{\"input_tokens\":1,\"output_tokens\":0}}}\n\n");
                    await writer.WriteAsync("event: content_block_start\ndata: {\"type\":\"content_block_start\",\"index\":0,\"content_block\":{\"type\":\"text\",\"text\":\"\"}}\n\n");
                    await writer.WriteAsync("event: content_block_delta\ndata: {\"type\":\"content_block_delta\",\"index\":0,\"delta\":{\"type\":\"text_delta\",\"text\":\"first\"}}\n\n");
                }
                await writer.FlushAsync();
                await Task.Delay(900, deadline.Token);
                await writer.WriteAsync(protocol == "openai-responses"
                    ? "data: {\"type\":\"response.completed\",\"response\":{\"id\":\"resp_idle\",\"object\":\"response\",\"created_at\":1,\"model\":\"fixture-model\",\"status\":\"completed\",\"output\":[]}}\n\ndata: [DONE]\n\n"
                    : "event: message_stop\ndata: {\"type\":\"message_stop\"}\n\n");
                await writer.FlushAsync();
            }
            catch (Exception error) when (error is IOException or HttpListenerException or ObjectDisposedException) { }
            finally { request.Response.Close(); }
        });
        var selection = Selection("fixture", protocol == "openai-responses"
            ? $"http://127.0.0.1:{port}/v1" : $"http://127.0.0.1:{port}", protocol);
        var client = ProviderChatClientFactory.Create(selection,
            new ProviderRetrySettings(TimeoutMs: 5_000), httpIdleTimeoutMs: 250);
        var updates = 0;
        var firstUpdate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var streaming = Task.Run(async () =>
        {
            await foreach (var _ in client.GetStreamingResponseAsync(
                [new Microsoft.Extensions.AI.ChatMessage(Microsoft.Extensions.AI.ChatRole.User, "hello")],
                cancellationToken: deadline.Token))
            {
                Interlocked.Increment(ref updates);
                firstUpdate.TrySetResult();
            }
        });
        await firstUpdate.Task.WaitAsync(deadline.Token);
        var timer = System.Diagnostics.Stopwatch.StartNew();

        var error = await Assert.ThrowsAsync<TimeoutException>(() => streaming);

        Assert.Contains("no update", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.True(Volatile.Read(ref updates) > 0, "The provider did not emit the initial stream update.");
        Assert.True(timer.Elapsed < TimeSpan.FromMilliseconds(700), $"The provider stream took {timer.Elapsed}.");
        await server.WaitAsync(deadline.Token);
        Assert.Equal(1, requests);
    }

    [Theory]
    [InlineData("openai-responses")]
    [InlineData("openai-completions")]
    [InlineData("anthropic-messages")]
    public async Task ProviderStreamIdleDeadlineResetsOnSseBytesBeforeTheNextParsedUpdate(string protocol)
    {
        using var listener = StartLoopbackListener(out var port);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        var server = Task.Run(async () =>
        {
            var request = await listener.GetContextAsync().WaitAsync(deadline.Token);
            using var reader = new StreamReader(request.Request.InputStream);
            await reader.ReadToEndAsync(deadline.Token);
            request.Response.ContentType = "text/event-stream";
            request.Response.SendChunked = true;
            await using var writer = new StreamWriter(request.Response.OutputStream);
            try
            {
                var initialEvents = protocol switch
                {
                    "openai-responses" => "data: {\"type\":\"response.created\",\"response\":{\"id\":\"resp_wire\",\"object\":\"response\",\"created_at\":1,\"model\":\"fixture-model\",\"status\":\"in_progress\",\"output\":[]}}\n\n",
                    "openai-completions" => "data: {\"id\":\"chatcmpl_wire\",\"object\":\"chat.completion.chunk\",\"created\":1,\"model\":\"fixture-model\",\"choices\":[{\"index\":0,\"delta\":{\"role\":\"assistant\",\"content\":\"first\"},\"finish_reason\":null}]}\n\n",
                    _ => "event: message_start\ndata: {\"type\":\"message_start\",\"message\":{\"id\":\"msg_wire\",\"type\":\"message\",\"role\":\"assistant\",\"model\":\"fixture-model\",\"content\":[],\"stop_reason\":null,\"stop_sequence\":null,\"usage\":{\"input_tokens\":1,\"output_tokens\":0}}}\n\nevent: content_block_start\ndata: {\"type\":\"content_block_start\",\"index\":0,\"content_block\":{\"type\":\"text\",\"text\":\"\"}}\n\nevent: content_block_delta\ndata: {\"type\":\"content_block_delta\",\"index\":0,\"delta\":{\"type\":\"text_delta\",\"text\":\"first\"}}\n\n"
                };
                await writer.WriteAsync(initialEvents);
                await writer.FlushAsync();
                await Task.Delay(50, deadline.Token);

                var partialEvent = protocol switch
                {
                    "openai-responses" => "data: {\"type\":\"response.output_text.delta\",\"item_id\":\"item_1\",\"output_index\":0,\"content_index\":0,\"delta\":\"wire activity\"}",
                    "openai-completions" => "data: {\"id\":\"chatcmpl_wire\",\"object\":\"chat.completion.chunk\",\"created\":1,\"model\":\"fixture-model\",\"choices\":[{\"index\":0,\"delta\":{\"content\":\"wire activity\"},\"finish_reason\":null}]}",
                    _ => "event: content_block_delta\ndata: {\"type\":\"content_block_delta\",\"index\":0,\"delta\":{\"type\":\"text_delta\",\"text\":\"wire activity\"}}"
                };
                for (var offset = 0; offset < partialEvent.Length; offset += 10)
                {
                    await writer.WriteAsync(partialEvent.AsMemory(offset, Math.Min(10, partialEvent.Length - offset)), deadline.Token);
                    await writer.FlushAsync(deadline.Token);
                    await Task.Delay(80, deadline.Token);
                }
                await writer.WriteAsync("\n\n");
                var finalEvents = protocol switch
                {
                    "openai-responses" => "data: {\"type\":\"response.completed\",\"response\":{\"id\":\"resp_wire\",\"object\":\"response\",\"created_at\":1,\"model\":\"fixture-model\",\"status\":\"completed\",\"output\":[]}}\n\ndata: [DONE]\n\n",
                    "openai-completions" => "data: {\"id\":\"chatcmpl_wire\",\"object\":\"chat.completion.chunk\",\"created\":1,\"model\":\"fixture-model\",\"choices\":[{\"index\":0,\"delta\":{},\"finish_reason\":\"stop\"}]}\n\ndata: [DONE]\n\n",
                    _ => "event: content_block_stop\ndata: {\"type\":\"content_block_stop\",\"index\":0}\n\nevent: message_delta\ndata: {\"type\":\"message_delta\",\"delta\":{\"stop_reason\":\"end_turn\",\"stop_sequence\":null},\"usage\":{\"output_tokens\":2}}\n\nevent: message_stop\ndata: {\"type\":\"message_stop\"}\n\n"
                };
                await writer.WriteAsync(finalEvents);
                await writer.FlushAsync();
            }
            catch (Exception error) when (error is IOException or HttpListenerException or ObjectDisposedException) { }
            finally { request.Response.Close(); }
        });

        var selection = Selection("fixture", protocol == "anthropic-messages"
            ? $"http://127.0.0.1:{port}" : $"http://127.0.0.1:{port}/v1", protocol);
        var client = ProviderChatClientFactory.Create(selection,
            new ProviderRetrySettings(TimeoutMs: 5_000), httpIdleTimeoutMs: 250);
        var firstUpdate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var text = new System.Text.StringBuilder();
        var streaming = Task.Run(async () =>
        {
            await foreach (var update in client.GetStreamingResponseAsync(
                [new Microsoft.Extensions.AI.ChatMessage(Microsoft.Extensions.AI.ChatRole.User, "hello")],
                cancellationToken: deadline.Token))
            {
                firstUpdate.TrySetResult();
                text.Append(update.Text);
            }
        });
        await firstUpdate.Task.WaitAsync(deadline.Token);

        await streaming.WaitAsync(deadline.Token);
        await server.WaitAsync(deadline.Token);

        Assert.Contains("wire activity", text.ToString());
    }

    [Fact]
    public async Task AnthropicMessagesUsesNativeHeadersAndBody()
    {
        using var listener = StartLoopbackListener(out var port);
        var server = Task.Run(async () =>
        {
            var request = await listener.GetContextAsync();
            Assert.Equal("/v1/messages", request.Request.Url?.AbsolutePath);
            Assert.Equal("fixture-key", request.Request.Headers["x-api-key"]);
            Assert.Null(request.Request.Headers["Authorization"]);
            Assert.NotNull(request.Request.Headers["anthropic-version"]);
            using var reader = new StreamReader(request.Request.InputStream);
            using var body = System.Text.Json.JsonDocument.Parse(await reader.ReadToEndAsync());
            Assert.Equal("fixture-model", body.RootElement.GetProperty("model").GetString());
            Assert.Equal(321, body.RootElement.GetProperty("max_tokens").GetInt32());
            Assert.Equal("hello", body.RootElement.GetProperty("messages")[0].GetProperty("content")[0]
                .GetProperty("text").GetString());
            request.Response.ContentType = "application/json";
            await using var writer = new StreamWriter(request.Response.OutputStream);
            await writer.WriteAsync("""
                {"id":"msg_fixture","type":"message","role":"assistant","model":"fixture-model","content":[{"type":"text","text":"native reply"}],"stop_reason":"end_turn","stop_sequence":null,"usage":{"input_tokens":5,"output_tokens":2}}
                """);
            await writer.FlushAsync();
            request.Response.Close();
        });
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var selection = Selection("fixture", $"http://127.0.0.1:{port}", "anthropic-messages");
        var response = await ProviderChatClientFactory.Create(selection with { Model = selection.Model with { MaxOutputTokens = 321 } })
            .GetResponseAsync([new Microsoft.Extensions.AI.ChatMessage(Microsoft.Extensions.AI.ChatRole.User, "hello")],
                cancellationToken: deadline.Token);
        await server.WaitAsync(deadline.Token);
        Assert.Equal("native reply", response.Text);
        Assert.Equal(5, response.Usage?.InputTokenCount);
    }

    [Fact]
    public async Task AnthropicWorkloadIdentityExchangesFileTokenAndCachesFederatedBearerToken()
    {
        using var listener = StartLoopbackListener(out var port);
        var root = Path.Combine(Path.GetTempPath(), "pisharp-anthropic-federation-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var identityTokenFile = Path.Combine(root, "identity.jwt");
        await File.WriteAllTextAsync(identityTokenFile, "fixture-identity-jwt");
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var paths = new List<string>();
        var exchangeBodies = new List<string>();
        var messageHeaders = new List<(string? Authorization, string? ApiKey)>();
        var server = Task.Run(async () =>
        {
            for (var index = 0; index < 5; index++)
            {
                var request = await listener.GetContextAsync().WaitAsync(deadline.Token);
                var path = request.Request.Url?.AbsolutePath ?? "";
                paths.Add(path);
                if (path.EndsWith("/oauth/token", StringComparison.Ordinal))
                {
                    using var reader = new StreamReader(request.Request.InputStream);
                    exchangeBodies.Add(await reader.ReadToEndAsync(deadline.Token));
                    request.Response.ContentType = "application/json";
                    await using var tokenWriter = new StreamWriter(request.Response.OutputStream);
                    await tokenWriter.WriteAsync(exchangeBodies.Count == 1
                        ? "{\"access_token\":\"expired-federated-token\",\"token_type\":\"Bearer\",\"expires_in\":3600}"
                        : "{\"access_token\":\"refreshed-federated-token\",\"token_type\":\"Bearer\",\"expires_in\":3600}");
                    await tokenWriter.FlushAsync();
                }
                else
                {
                    using var reader = new StreamReader(request.Request.InputStream);
                    _ = await reader.ReadToEndAsync(deadline.Token);
                    messageHeaders.Add((request.Request.Headers["Authorization"], request.Request.Headers["x-api-key"]));
                    request.Response.ContentType = "application/json";
                    if (request.Request.Headers["Authorization"] == "Bearer expired-federated-token")
                    {
                        await File.WriteAllTextAsync(identityTokenFile, "fixture-rotated-identity-jwt", deadline.Token);
                        request.Response.StatusCode = (int)HttpStatusCode.Unauthorized;
                        await using var expiredWriter = new StreamWriter(request.Response.OutputStream);
                        await expiredWriter.WriteAsync("{\"type\":\"error\",\"error\":{\"type\":\"authentication_error\",\"message\":\"expired\"}}");
                        await expiredWriter.FlushAsync();
                        request.Response.Close();
                        continue;
                    }
                    await using var messageWriter = new StreamWriter(request.Response.OutputStream);
                    await messageWriter.WriteAsync("""
                        {"id":"msg_federated","type":"message","role":"assistant","model":"fixture-model","content":[{"type":"text","text":"federated reply"}],"stop_reason":"end_turn","stop_sequence":null,"usage":{"input_tokens":5,"output_tokens":2}}
                        """);
                    await messageWriter.FlushAsync();
                }
                request.Response.Close();
            }
        }, deadline.Token);

        try
        {
            var environment = new Dictionary<string, string>
            {
                ["ANTHROPIC_FEDERATION_RULE_ID"] = "fdrl_fixture",
                ["ANTHROPIC_ORGANIZATION_ID"] = "org-fixture",
                ["ANTHROPIC_IDENTITY_TOKEN_FILE"] = identityTokenFile,
                ["ANTHROPIC_SERVICE_ACCOUNT_ID"] = "svac_fixture",
                ["ANTHROPIC_WORKSPACE_ID"] = "wrkspc_fixture"
            };
            using var http = new HttpClient();
            var runtime = await ProviderModelRuntime.CreateAsync(root, false,
                name => environment.GetValueOrDefault(name), http);
            var selection = await runtime.ResolveAsync("anthropic", "claude-sonnet-4-6");
            selection = selection with { Provider = selection.Provider with { Endpoint = new Uri($"http://127.0.0.1:{port}") } };
            var client = ProviderChatClientFactory.Create(selection,
                new ProviderRetrySettings(TimeoutMs: 5_000), httpIdleTimeoutMs: 5_000);

            var first = await client.GetResponseAsync(
                [new Microsoft.Extensions.AI.ChatMessage(Microsoft.Extensions.AI.ChatRole.User, "hello")],
                cancellationToken: deadline.Token);
            var second = await client.GetResponseAsync(
                [new Microsoft.Extensions.AI.ChatMessage(Microsoft.Extensions.AI.ChatRole.User, "hello again")],
                cancellationToken: deadline.Token);
            await server.WaitAsync(deadline.Token);

            Assert.Equal("federated reply", first.Text);
            Assert.Equal("federated reply", second.Text);
            Assert.Equal(3, messageHeaders.Count);
            Assert.All(messageHeaders, headers =>
            {
                Assert.Null(headers.ApiKey);
            });
            Assert.Equal(["Bearer expired-federated-token", "Bearer refreshed-federated-token", "Bearer refreshed-federated-token"],
                messageHeaders.Select(headers => headers.Authorization));
            Assert.Equal(["/v1/oauth/token", "/v1/messages", "/v1/oauth/token", "/v1/messages", "/v1/messages"], paths);
            Assert.Equal(2, exchangeBodies.Count);
            Assert.Contains("fixture-identity-jwt", exchangeBodies[0]);
            Assert.Contains("fixture-rotated-identity-jwt", exchangeBodies[1]);
            Assert.All(exchangeBodies, exchangeBody =>
            {
                Assert.Contains("urn:ietf:params:oauth:grant-type:jwt-bearer", exchangeBody);
                Assert.Contains("fdrl_fixture", exchangeBody);
                Assert.Contains("org-fixture", exchangeBody);
                Assert.Contains("svac_fixture", exchangeBody);
                Assert.Contains("wrkspc_fixture", exchangeBody);
            });
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task AnthropicAuthTokensWinOverApiKeyAndDoNotExchangeFederatedCredentials()
    {
        using var listener = StartLoopbackListener(out var port);
        var root = Path.Combine(Path.GetTempPath(), "pisharp-anthropic-auth-token-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var requests = new List<(string Path, string? Authorization, string? ApiKey, string? App,
            string? DirectAccess, string? Accept)>();
        var server = Task.Run(async () =>
        {
            for (var index = 0; index < 2; index++)
            {
                var request = await listener.GetContextAsync();
                requests.Add((request.Request.Url?.AbsolutePath ?? "", request.Request.Headers["Authorization"],
                    request.Request.Headers["x-api-key"], request.Request.Headers["x-app"],
                    request.Request.Headers["anthropic-dangerous-direct-browser-access"], request.Request.Headers["accept"]));
                using var reader = new StreamReader(request.Request.InputStream);
                _ = await reader.ReadToEndAsync(deadline.Token);
                request.Response.ContentType = "application/json";
                await using var writer = new StreamWriter(request.Response.OutputStream);
                await writer.WriteAsync("""
                    {"id":"msg_auth_token","type":"message","role":"assistant","model":"fixture-model","content":[{"type":"text","text":"bearer reply"}],"stop_reason":"end_turn","stop_sequence":null,"usage":{"input_tokens":1,"output_tokens":1}}
                    """);
                await writer.FlushAsync();
                request.Response.Close();
            }
        });

        try
        {
            var environment = new Dictionary<string, string>
            {
                ["ANTHROPIC_AUTH_TOKEN"] = "explicit-bearer-token",
                ["ANTHROPIC_OAUTH_TOKEN"] = "lower-priority-oauth-token",
                ["ANTHROPIC_API_KEY"] = "lower-priority-api-key",
                ["ANTHROPIC_FEDERATION_RULE_ID"] = "fdrl_fixture",
                ["ANTHROPIC_ORGANIZATION_ID"] = "org-fixture",
                ["ANTHROPIC_IDENTITY_TOKEN_FILE"] = Path.Combine(root, "unused-identity.jwt")
            };
            using var http = new HttpClient();
            var runtime = await ProviderModelRuntime.CreateAsync(root, false,
                name => environment.GetValueOrDefault(name), http);
            var selection = await runtime.ResolveAsync("anthropic", "claude-sonnet-4-6");
            selection = selection with { Provider = selection.Provider with { Endpoint = new Uri($"http://127.0.0.1:{port}") } };
            var response = await ProviderChatClientFactory.Create(selection).GetResponseAsync(
                [new Microsoft.Extensions.AI.ChatMessage(Microsoft.Extensions.AI.ChatRole.User, "hello")],
                cancellationToken: deadline.Token);
            environment.Remove("ANTHROPIC_AUTH_TOKEN");
            var oauthSelection = await runtime.ResolveAsync("anthropic", "claude-sonnet-4-6");
            oauthSelection = oauthSelection with { Provider = oauthSelection.Provider with { Endpoint = new Uri($"http://127.0.0.1:{port}") } };
            var oauthResponse = await ProviderChatClientFactory.Create(oauthSelection).GetResponseAsync(
                [new Microsoft.Extensions.AI.ChatMessage(Microsoft.Extensions.AI.ChatRole.User, "hello again")],
                cancellationToken: deadline.Token);
            await server.WaitAsync(deadline.Token);

            Assert.Equal("ANTHROPIC_AUTH_TOKEN", selection.AuthSource);
            Assert.Null(selection.AnthropicWorkloadIdentity);
            Assert.Equal("bearer reply", response.Text);
            Assert.Equal("ANTHROPIC_OAUTH_TOKEN", oauthSelection.AuthSource);
            Assert.True(oauthSelection.AnthropicIsOAuthToken);
            Assert.Null(oauthSelection.AnthropicWorkloadIdentity);
            Assert.Equal("bearer reply", oauthResponse.Text);
            Assert.Equal(2, requests.Count);
            Assert.All(requests, request =>
            {
                Assert.Equal("/v1/messages", request.Path);
                Assert.Null(request.ApiKey);
            });
            Assert.Equal("Bearer explicit-bearer-token", requests[0].Authorization);
            Assert.Equal("Bearer lower-priority-oauth-token", requests[1].Authorization);
            Assert.Equal("cli", requests[1].App);
            Assert.Equal("true", requests[1].DirectAccess);
            Assert.Equal("application/json", requests[1].Accept);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task AnthropicMessagesStreamsTextAndUsage()
    {
        using var listener = StartLoopbackListener(out var port);
        var server = Task.Run(async () =>
        {
            var request = await listener.GetContextAsync();
            Assert.Equal("/v1/messages", request.Request.Url?.AbsolutePath);
            using var reader = new StreamReader(request.Request.InputStream);
            using var body = System.Text.Json.JsonDocument.Parse(await reader.ReadToEndAsync());
            Assert.True(body.RootElement.GetProperty("stream").GetBoolean());
            request.Response.ContentType = "text/event-stream";
            await using var writer = new StreamWriter(request.Response.OutputStream);
            await writer.WriteAsync("""
                event: message_start
                data: {"type":"message_start","message":{"id":"msg_1","type":"message","role":"assistant","model":"fixture-model","content":[],"stop_reason":null,"stop_sequence":null,"usage":{"input_tokens":4,"output_tokens":0}}}

                event: content_block_start
                data: {"type":"content_block_start","index":0,"content_block":{"type":"text","text":""}}

                event: content_block_delta
                data: {"type":"content_block_delta","index":0,"delta":{"type":"text_delta","text":"streamed"}}

                event: content_block_stop
                data: {"type":"content_block_stop","index":0}

                event: message_delta
                data: {"type":"message_delta","delta":{"stop_reason":"end_turn","stop_sequence":null},"usage":{"output_tokens":2}}

                event: message_stop
                data: {"type":"message_stop"}

                """);
            await writer.FlushAsync();
            request.Response.Close();
        });
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var agent = new PiAgent(ProviderChatClientFactory.Create(Selection("fixture", $"http://127.0.0.1:{port}", "anthropic-messages")),
            new CodingTools(Path.GetTempPath()));
        var session = await agent.CreateSessionAsync(deadline.Token);
        var text = "";
        await foreach (var update in agent.RunStreamingAsync("hello", session, deadline.Token)) text += update.Text;
        await server.WaitAsync(deadline.Token);
        Assert.Equal("streamed", text);
    }

    [Fact]
    public async Task AnthropicToolUseContinuesWithToolResultOnNextMessagesRequest()
    {
        using var listener = StartLoopbackListener(out var port);
        var bodies = new List<string>();
        var server = Task.Run(async () =>
        {
            for (var i = 0; i < 2; i++)
            {
                var request = await listener.GetContextAsync();
                Assert.Equal("/v1/messages", request.Request.Url?.AbsolutePath);
                using var reader = new StreamReader(request.Request.InputStream);
                bodies.Add(await reader.ReadToEndAsync());
                request.Response.ContentType = "text/event-stream";
                await using var writer = new StreamWriter(request.Response.OutputStream);
                await writer.WriteAsync($"event: message_start\ndata: {{\"type\":\"message_start\",\"message\":{{\"id\":\"msg_{i}\",\"type\":\"message\",\"role\":\"assistant\",\"model\":\"fixture-model\",\"content\":[],\"stop_reason\":null,\"stop_sequence\":null,\"usage\":{{\"input_tokens\":4,\"output_tokens\":0}}}}}}\n\n");
                if (i == 0)
                {
                    await writer.WriteAsync("event: content_block_start\ndata: {\"type\":\"content_block_start\",\"index\":0,\"content_block\":{\"type\":\"tool_use\",\"id\":\"toolu_1\",\"name\":\"echo_ext\",\"input\":{}}}\n\n");
                    await writer.WriteAsync("event: content_block_delta\ndata: {\"type\":\"content_block_delta\",\"index\":0,\"delta\":{\"type\":\"input_json_delta\",\"partial_json\":\"{\\\"text\\\":\\\"ping\\\"}\"}}\n\n");
                    await writer.WriteAsync("event: content_block_stop\ndata: {\"type\":\"content_block_stop\",\"index\":0}\n\n");
                }
                else
                {
                    await writer.WriteAsync("event: content_block_start\ndata: {\"type\":\"content_block_start\",\"index\":0,\"content_block\":{\"type\":\"text\",\"text\":\"\"}}\n\n");
                    await writer.WriteAsync("event: content_block_delta\ndata: {\"type\":\"content_block_delta\",\"index\":0,\"delta\":{\"type\":\"text_delta\",\"text\":\"done\"}}\n\n");
                    await writer.WriteAsync("event: content_block_stop\ndata: {\"type\":\"content_block_stop\",\"index\":0}\n\n");
                }
                await writer.WriteAsync($"event: message_delta\ndata: {{\"type\":\"message_delta\",\"delta\":{{\"stop_reason\":\"{(i == 0 ? "tool_use" : "end_turn")}\",\"stop_sequence\":null}},\"usage\":{{\"output_tokens\":2}}}}\n\n");
                await writer.WriteAsync("event: message_stop\ndata: {\"type\":\"message_stop\"}\n\n");
                await writer.FlushAsync();
                request.Response.Close();
            }
        });
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var echo = Microsoft.Extensions.AI.AIFunctionFactory.Create((string text) => "echo:" + text, name: "echo_ext");
        var agent = new PiAgent(ProviderChatClientFactory.Create(Selection("fixture", $"http://127.0.0.1:{port}", "anthropic-messages")),
            new CodingTools(Path.GetTempPath()), extensionTools: [echo]);
        var session = await agent.CreateSessionAsync(deadline.Token);
        var text = "";
        await foreach (var update in agent.RunStreamingAsync("use echo", session, deadline.Token)) text += update.Text;
        await server.WaitAsync(deadline.Token);
        Assert.Equal("done", text);
        Assert.Contains("echo:ping", bodies[1]);
        Assert.Contains("toolu_1", bodies[1]);
    }

    [Fact]
    public async Task AnthropicMessagesSendsInlinePngAsNativeImageBlock()
    {
        using var listener = StartLoopbackListener(out var port);
        var server = Task.Run(async () =>
        {
            var request = await listener.GetContextAsync();
            Assert.Equal("/v1/messages", request.Request.Url?.AbsolutePath);
            using var reader = new StreamReader(request.Request.InputStream);
            using var body = System.Text.Json.JsonDocument.Parse(await reader.ReadToEndAsync());
            var parts = body.RootElement.GetProperty("messages")[0].GetProperty("content").EnumerateArray().ToArray();
            Assert.Contains(parts, part => part.GetProperty("type").GetString() == "text" &&
                part.GetProperty("text").GetString() == "describe image");
            Assert.Contains(parts, part => part.GetProperty("type").GetString() == "image" &&
                part.GetProperty("source").GetProperty("type").GetString() == "base64" &&
                part.GetProperty("source").GetProperty("media_type").GetString() == "image/png" &&
                part.GetProperty("source").GetProperty("data").GetString()!.StartsWith("iVBORw0KGgo", StringComparison.Ordinal));
            request.Response.ContentType = "application/json";
            await using var writer = new StreamWriter(request.Response.OutputStream);
            await writer.WriteAsync("""
                {"id":"msg_image","type":"message","role":"assistant","model":"fixture-model","content":[{"type":"text","text":"seen"}],"stop_reason":"end_turn","stop_sequence":null,"usage":{"input_tokens":7,"output_tokens":2}}
                """);
            await writer.FlushAsync();
            request.Response.Close();
        });
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+/bX8AAAAASUVORK5CYII=");
        var prompt = new Microsoft.Extensions.AI.ChatMessage(Microsoft.Extensions.AI.ChatRole.User,
            [new Microsoft.Extensions.AI.TextContent("describe image"), new Microsoft.Extensions.AI.DataContent(png, "image/png")]);
        var response = await ProviderChatClientFactory.Create(Selection("fixture", $"http://127.0.0.1:{port}", "anthropic-messages"))
            .GetResponseAsync([prompt], cancellationToken: deadline.Token);
        await server.WaitAsync(deadline.Token);
        Assert.Equal("seen", response.Text);
    }

    [Theory]
    [InlineData("claude-sonnet-4-6", "adaptive")]
    [InlineData("claude-opus-4-6", "adaptive")]
    [InlineData("claude-haiku-4-5", "enabled")]
    public async Task AnthropicReasoningUsesPinnedModelThinkingMode(string modelId, string expectedType)
    {
        using var listener = StartLoopbackListener(out var port);
        string? thinkingType = null;
        string? effort = null;
        long? budgetTokens = null;
        var server = Task.Run(async () =>
        {
            var request = await listener.GetContextAsync();
            using var reader = new StreamReader(request.Request.InputStream);
            using var body = System.Text.Json.JsonDocument.Parse(await reader.ReadToEndAsync());
            thinkingType = body.RootElement.TryGetProperty("thinking", out var thinking) &&
                thinking.TryGetProperty("type", out var type) ? type.GetString() : null;
            if (body.RootElement.TryGetProperty("output_config", out var output) && output.TryGetProperty("effort", out var value))
                effort = value.GetString();
            if (thinkingType == "enabled" && thinking.TryGetProperty("budget_tokens", out var budget))
                budgetTokens = budget.GetInt64();
            request.Response.ContentType = "application/json";
            await using var writer = new StreamWriter(request.Response.OutputStream);
            await writer.WriteAsync("""
                {"id":"msg_reasoning","type":"message","role":"assistant","model":"fixture-model","content":[{"type":"text","text":"ok"}],"stop_reason":"end_turn","stop_sequence":null,"usage":{"input_tokens":4,"output_tokens":2}}
                """);
            await writer.FlushAsync();
            request.Response.Close();
        });
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var selection = Selection("fixture", $"http://127.0.0.1:{port}", "anthropic-messages");
        var response = await ProviderChatClientFactory.Create(selection with { Model = selection.Model with { Id = modelId } })
            .GetResponseAsync([new Microsoft.Extensions.AI.ChatMessage(Microsoft.Extensions.AI.ChatRole.User, "hello")],
                new Microsoft.Extensions.AI.ChatOptions { Reasoning = ThinkingLevels.ToOptions("high") }, deadline.Token);
        await server.WaitAsync(deadline.Token);
        Assert.Equal(expectedType, thinkingType);
        if (expectedType == "adaptive") Assert.Equal("high", effort);
        else Assert.True(budgetTokens >= 1024);
        Assert.Equal("ok", response.Text);
    }

    [Fact]
    public async Task OpenAiCompletionsUsesCanonicalThinkingMapValueInProviderRequest()
    {
        using var listener = StartLoopbackListener(out var port);
        string? reasoningEffort = null;
        var server = Task.Run(async () =>
        {
            var request = await listener.GetContextAsync();
            using var reader = new StreamReader(request.Request.InputStream);
            using var body = JsonDocument.Parse(await reader.ReadToEndAsync());
            reasoningEffort = body.RootElement.GetProperty("reasoning_effort").GetString();
            request.Response.ContentType = "application/json";
            await using var writer = new StreamWriter(request.Response.OutputStream);
            await writer.WriteAsync("""
                {"id":"chatcmpl_map","object":"chat.completion","created":1,"model":"fixture-model","choices":[{"index":0,"message":{"role":"assistant","content":"ok"},"finish_reason":"stop"}],"usage":{"prompt_tokens":1,"completion_tokens":1,"total_tokens":2}}
                """);
            await writer.FlushAsync();
            request.Response.Close();
        });
        using var mapDocument = JsonDocument.Parse("{\"high\":\"low\"}");
        var map = mapDocument.RootElement.Clone();
        var selection = Selection("fixture", $"http://127.0.0.1:{port}/v1", "openai-completions") with
        {
            Model = Selection("fixture", $"http://127.0.0.1:{port}/v1", "openai-completions").Model with
            {
                Reasoning = true,
                ThinkingLevelMap = map
            }
        };
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var response = await ProviderChatClientFactory.Create(selection).GetResponseAsync(
            [new Microsoft.Extensions.AI.ChatMessage(Microsoft.Extensions.AI.ChatRole.User, "hello")],
            new Microsoft.Extensions.AI.ChatOptions { Reasoning = ThinkingLevels.ToOptions("high", map) }, deadline.Token);
        await server.WaitAsync(deadline.Token);

        Assert.Equal("low", reasoningEffort);
        Assert.Equal("ok", response.Text);
    }

    [Fact]
    public async Task UnsupportedConfiguredApiFailsBeforeProviderRequestWithoutStackTrace()
    {
        var root = Path.Combine(Path.GetTempPath(), "pisharp-api-" + Guid.NewGuid().ToString("N"));
        var agent = Path.Combine(root, "agent");
        Directory.CreateDirectory(agent);
        try
        {
            await File.WriteAllTextAsync(Path.Combine(agent, "models.json"), """
                {"providers":{"fixture":{"baseUrl":"http://127.0.0.1:1/v1","authHeader":false,"models":[{"id":"demo","api":"google-generative-ai"}]}}}
                """);
            var start = new System.Diagnostics.ProcessStartInfo("dotnet")
            {
                WorkingDirectory = root,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            foreach (var arg in new[] { typeof(CliArguments).Assembly.Location, "--provider", "fixture", "--model", "demo", "--no-session", "--print", "hello" })
                start.ArgumentList.Add(arg);
            start.Environment["PISHARP_AGENT_DIR"] = agent;
            using var process = System.Diagnostics.Process.Start(start)!;
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await process.WaitForExitAsync(timeout.Token);
            Assert.Equal(2, process.ExitCode);
            Assert.Equal("", await stdout);
            Assert.Contains("unsupported API 'google-generative-ai'", await stderr);
            Assert.DoesNotContain("Unhandled exception", await stderr);
        }
        finally { Directory.Delete(root, true); }
    }
    private static HttpListener StartLoopbackListener(out int port)
    {
        for (var attempt = 0; attempt < 10; attempt++)
        {
            using var reservation = new TcpListener(IPAddress.Loopback, 0);
            reservation.Start();
            var candidatePort = ((IPEndPoint)reservation.LocalEndpoint).Port;
            reservation.Stop();

            var listener = new HttpListener();
            listener.Prefixes.Add($"http://127.0.0.1:{candidatePort}/");
            try
            {
                listener.Start();
                port = candidatePort;
                return listener;
            }
            catch (HttpListenerException)
            {
                listener.Close();
                if (attempt == 9) throw;
            }
        }

        throw new InvalidOperationException("Could not reserve a loopback port for the provider fixture.");
    }

    private static ModelSelection Selection(string provider, string url, string? api = null)
    {
        var model = new ModelDescriptor("fixture-model", provider, null, "fixture", Provider: provider, Api: api);
        return new(new ProviderProfile(provider, provider, new Uri(url), true, false, null, null, [model]), model,
            "fixture-key", true, "fixture");
    }
}
