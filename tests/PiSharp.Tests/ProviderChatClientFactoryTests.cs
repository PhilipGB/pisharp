using System.Net;
using System.Net.Sockets;
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
        Assert.Equal("openai-completions", ProviderChatClientFactory.ResolveProtocol(Selection("mistral", "https://api.mistral.ai/v1")));
        Assert.Equal("openai-responses", ProviderChatClientFactory.ResolveProtocol(Selection("custom", "http://localhost:1234/v1", "openai-responses")));
        Assert.Equal("anthropic-messages", ProviderChatClientFactory.ResolveProtocol(Selection("anthropic", "https://api.anthropic.com")));
        Assert.Equal("anthropic-messages", ProviderChatClientFactory.ResolveProtocol(Selection("custom", "http://localhost:1234", "anthropic-messages")));
        Assert.Throws<NotSupportedException>(() => ProviderChatClientFactory.ResolveProtocol(Selection("custom", "http://localhost:1234/v1", "google-generative-ai")));
        Assert.Throws<InvalidOperationException>(() => ProviderChatClientFactory.ResolveProtocol(Selection("openai", "https://untrusted.test/v1")));
    }

    [Fact]
    public async Task ExplicitResponsesProtocolStreamsThroughResponsesEndpoint()
    {
        using var listener = new HttpListener();
        var port = 0;
        // Start retries avoid a race between releasing the ephemeral port and binding HttpListener.
        for (var attempt = 0; attempt < 10; attempt++)
        {
            using var reservation = new TcpListener(IPAddress.Loopback, 0);
            reservation.Start();
            port = ((IPEndPoint)reservation.LocalEndpoint).Port;
            reservation.Stop();
            listener.Prefixes.Clear();
            listener.Prefixes.Add($"http://127.0.0.1:{port}/");
            try { listener.Start(); break; }
            catch (HttpListenerException) when (attempt < 9) { }
        }
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
        using var listener = new HttpListener();
        var port = 0;
        for (var attempt = 0; attempt < 10; attempt++)
        {
            using var reservation = new TcpListener(IPAddress.Loopback, 0);
            reservation.Start();
            port = ((IPEndPoint)reservation.LocalEndpoint).Port;
            reservation.Stop();
            listener.Prefixes.Clear();
            listener.Prefixes.Add($"http://127.0.0.1:{port}/");
            try { listener.Start(); break; }
            catch (HttpListenerException) when (attempt < 9) { }
        }
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
        using var listener = new HttpListener();
        var port = 0;
        for (var attempt = 0; attempt < 10; attempt++)
        {
            using var reservation = new TcpListener(IPAddress.Loopback, 0);
            reservation.Start();
            port = ((IPEndPoint)reservation.LocalEndpoint).Port;
            reservation.Stop();
            listener.Prefixes.Clear();
            listener.Prefixes.Add($"http://127.0.0.1:{port}/");
            try { listener.Start(); break; }
            catch (HttpListenerException) when (attempt < 9) { }
        }
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
        using var listener = new HttpListener();
        var port = 0;
        for (var attempt = 0; attempt < 10; attempt++)
        {
            using var reservation = new TcpListener(IPAddress.Loopback, 0);
            reservation.Start();
            port = ((IPEndPoint)reservation.LocalEndpoint).Port;
            reservation.Stop();
            listener.Prefixes.Clear();
            listener.Prefixes.Add($"http://127.0.0.1:{port}/");
            try { listener.Start(); break; }
            catch (HttpListenerException) when (attempt < 9) { }
        }
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
    public async Task AnthropicMessagesUsesNativeHeadersAndBody()
    {
        using var listener = new HttpListener();
        using var reservation = new TcpListener(IPAddress.Loopback, 0);
        reservation.Start();
        var port = ((IPEndPoint)reservation.LocalEndpoint).Port;
        reservation.Stop();
        listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        listener.Start();
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
    public async Task AnthropicMessagesStreamsTextAndUsage()
    {
        using var listener = new HttpListener();
        using var reservation = new TcpListener(IPAddress.Loopback, 0);
        reservation.Start();
        var port = ((IPEndPoint)reservation.LocalEndpoint).Port;
        reservation.Stop();
        listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        listener.Start();
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
        using var listener = new HttpListener();
        using var reservation = new TcpListener(IPAddress.Loopback, 0);
        reservation.Start();
        var port = ((IPEndPoint)reservation.LocalEndpoint).Port;
        reservation.Stop();
        listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        listener.Start();
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
        using var listener = new HttpListener();
        using var reservation = new TcpListener(IPAddress.Loopback, 0);
        reservation.Start();
        var port = ((IPEndPoint)reservation.LocalEndpoint).Port;
        reservation.Stop();
        listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        listener.Start();
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
        using var listener = new HttpListener();
        using var reservation = new TcpListener(IPAddress.Loopback, 0);
        reservation.Start();
        var port = ((IPEndPoint)reservation.LocalEndpoint).Port;
        reservation.Stop();
        listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        listener.Start();
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
    private static ModelSelection Selection(string provider, string url, string? api = null)
    {
        var model = new ModelDescriptor("fixture-model", provider, null, "fixture", Provider: provider, Api: api);
        return new(new ProviderProfile(provider, provider, new Uri(url), true, false, null, null, [model]), model,
            "fixture-key", true, "fixture");
    }
}
