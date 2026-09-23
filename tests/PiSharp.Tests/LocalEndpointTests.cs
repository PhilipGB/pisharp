using System.ClientModel;
using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.AI;
using OpenAI;
using PiSharp.Cli;

namespace PiSharp.Tests;

public sealed class LocalEndpointTests
{
    [Fact]
    public async Task OpenAiAdapterStreamsAgainstLocalChatCompletionsEndpoint()
    {
        using var portReservation = new TcpListener(IPAddress.Loopback, 0);
        portReservation.Start();
        var port = ((IPEndPoint)portReservation.LocalEndpoint).Port;
        portReservation.Stop();
        using var listener = new HttpListener();
        listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        listener.Start();

        var server = Task.Run(async () =>
        {
            var request = await listener.GetContextAsync();
            Assert.Equal("/v1/chat/completions", request.Request.Url?.AbsolutePath);
            Assert.Equal("Bearer not-needed", request.Request.Headers["Authorization"]);
            using var reader = new StreamReader(request.Request.InputStream);
            var body = await reader.ReadToEndAsync();
            Assert.Contains("Qwen3.8-27B-GGUF", body);
            request.Response.StatusCode = 200;
            request.Response.ContentType = "text/event-stream";
            await using var writer = new StreamWriter(request.Response.OutputStream);
            await writer.WriteAsync("data: {\"id\":\"chatcmpl-1\",\"object\":\"chat.completion.chunk\",\"created\":1,\"model\":\"Qwen3.8-27B-GGUF\",\"choices\":[{\"index\":0,\"delta\":{\"role\":\"assistant\",\"content\":\"hello\"},\"finish_reason\":null}]}\n\n");
            await writer.WriteAsync("data: {\"id\":\"chatcmpl-1\",\"object\":\"chat.completion.chunk\",\"created\":1,\"model\":\"Qwen3.8-27B-GGUF\",\"choices\":[{\"index\":0,\"delta\":{},\"finish_reason\":\"stop\"}]}\n\n");
            await writer.WriteAsync("data: [DONE]\n\n");
            await writer.FlushAsync();
            request.Response.Close();
        });

        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var settings = ConnectionSettings.Resolve(true, key => key == "PISHARP_BASE_URL" ? $"http://127.0.0.1:{port}/v1" : null);
        var client = new OpenAIClient(new ApiKeyCredential(settings.ApiKey), new OpenAIClientOptions { Endpoint = settings.Endpoint });
        IChatClient chat = client.GetChatClient(settings.Model).AsIChatClient();
        var agent = new PiAgent(chat, new CodingTools(Path.GetTempPath()));
        var session = await agent.CreateSessionAsync(deadline.Token);
        var result = "";
        await foreach (var update in agent.RunStreamingAsync("Say hello", session, deadline.Token)) result += update.Text;
        await server.WaitAsync(deadline.Token);
        Assert.Equal("hello", result);
    }
}
