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
        Assert.Throws<NotSupportedException>(() => ProviderChatClientFactory.ResolveProtocol(Selection("custom", "http://localhost:1234/v1", "anthropic-messages")));
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
    public async Task UnsupportedConfiguredApiFailsBeforeProviderRequestWithoutStackTrace()
    {
        var root = Path.Combine(Path.GetTempPath(), "pisharp-api-" + Guid.NewGuid().ToString("N"));
        var agent = Path.Combine(root, "agent");
        Directory.CreateDirectory(agent);
        try
        {
            await File.WriteAllTextAsync(Path.Combine(agent, "models.json"), """
                {"providers":{"fixture":{"baseUrl":"http://127.0.0.1:1/v1","authHeader":false,"models":[{"id":"demo","api":"anthropic-messages"}]}}}
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
            Assert.Contains("unsupported API 'anthropic-messages'", await stderr);
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
