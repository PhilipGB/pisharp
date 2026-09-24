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

    private static ModelSelection Selection(string provider, string url, string? api = null)
    {
        var model = new ModelDescriptor("fixture-model", provider, null, "fixture", Provider: provider, Api: api);
        return new(new ProviderProfile(provider, provider, new Uri(url), true, false, null, null, [model]), model,
            "fixture-key", true, "fixture");
    }
}
