using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Microsoft.Extensions.AI;
using PiSharp.Cli;
using PiSharp.Runtime;
using PiSharp.Runtime.Providers;
using PiSharp.Runtime.Tools;

using var casesDocument = JsonDocument.Parse(await File.ReadAllTextAsync(args[0]));
var results = new List<object>();
foreach (var (api, provider, modelId) in new[]
{
    ("openai-responses", "openai-fixture", "gpt-5.4"),
    ("openai-completions", "openai-fixture", "gpt-4o-mini"),
    ("anthropic-messages", "anthropic-fixture", "claude-sonnet-4-6")
})
{
    foreach (var scenario in casesDocument.RootElement.EnumerateArray())
    {
        using var portListener = new TcpListener(IPAddress.Loopback, 0);
        portListener.Start();
        var port = ((IPEndPoint)portListener.LocalEndpoint).Port;
        portListener.Stop();
        var baseUrl = api == "anthropic-messages" ? $"http://127.0.0.1:{port}" : $"http://127.0.0.1:{port}/v1";
        using var listener = new HttpListener();
        listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        listener.Start();
        var captureTask = Task.Run(async () =>
        {
            var request = await listener.GetContextAsync();
            using var reader = new StreamReader(request.Request.InputStream);
            var body = await reader.ReadToEndAsync();
            request.Response.ContentType = "text/event-stream";
            request.Response.SendChunked = true;
            await using var writer = new StreamWriter(request.Response.OutputStream);
            var events = api switch
            {
                "openai-responses" =>
                    "data: {\"type\":\"response.created\",\"response\":{\"id\":\"resp_wire\"}}\n\n" +
                    "data: {\"type\":\"response.output_text.delta\",\"item_id\":\"item_wire\",\"output_index\":0,\"content_index\":0,\"delta\":\"ok\"}\n\n" +
                    "data: {\"type\":\"response.completed\",\"response\":{\"id\":\"resp_wire\",\"status\":\"completed\",\"output\":[]}}\n\ndata: [DONE]\n\n",
                "openai-completions" =>
                    "data: {\"id\":\"chat_wire\",\"object\":\"chat.completion.chunk\",\"created\":1,\"model\":\"gpt-4o-mini\",\"choices\":[{\"index\":0,\"delta\":{\"role\":\"assistant\",\"content\":\"ok\"},\"finish_reason\":null}]}\n\n" +
                    "data: {\"id\":\"chat_wire\",\"object\":\"chat.completion.chunk\",\"created\":1,\"model\":\"gpt-4o-mini\",\"choices\":[{\"index\":0,\"delta\":{},\"finish_reason\":\"stop\"}]}\n\ndata: [DONE]\n\n",
                _ =>
                    "event: message_start\ndata: {\"type\":\"message_start\",\"message\":{\"id\":\"msg_wire\",\"type\":\"message\",\"role\":\"assistant\",\"model\":\"claude-sonnet-4-6\",\"content\":[],\"stop_reason\":null,\"stop_sequence\":null,\"usage\":{\"input_tokens\":1,\"output_tokens\":0}}}\n\n" +
                    "event: content_block_start\ndata: {\"type\":\"content_block_start\",\"index\":0,\"content_block\":{\"type\":\"text\",\"text\":\"\"}}\n\n" +
                    "event: content_block_delta\ndata: {\"type\":\"content_block_delta\",\"index\":0,\"delta\":{\"type\":\"text_delta\",\"text\":\"ok\"}}\n\n" +
                    "event: content_block_stop\ndata: {\"type\":\"content_block_stop\",\"index\":0}\n\n" +
                    "event: message_delta\ndata: {\"type\":\"message_delta\",\"delta\":{\"stop_reason\":\"end_turn\",\"stop_sequence\":null},\"usage\":{\"output_tokens\":1}}\n\n" +
                    "event: message_stop\ndata: {\"type\":\"message_stop\"}\n\n"
            };
            await writer.WriteAsync(events);
            await writer.FlushAsync();
            request.Response.Close();
            using var document = JsonDocument.Parse(body);
            return new { path = request.Request.Url?.AbsolutePath, payload = document.RootElement.Clone() };
        });

        var model = new ModelDescriptor(modelId, provider, null, "fixture", Reasoning: false,
        Provider: provider,
        Input: scenario.TryGetProperty("modelInput", out var modelInput)
            ? modelInput.EnumerateArray().Select(item => item.GetString()!).ToArray()
            : ["text", "image"],
        Api: api);
        var profile = new ProviderProfile(provider, provider, new Uri(baseUrl), true, false, null, null, [model]);
        var selection = new ModelSelection(profile, model, "fixture-key", true, "fixture");
        var agent = new PiAgent(ProviderChatClientFactory.Create(selection), new CodingTools(Path.GetTempPath()),
            noTools: true, supportsImages: model.Input?.Contains("image", StringComparer.Ordinal) != false);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var session = await agent.CreateSessionAsync(deadline.Token);
        var content = new List<AIContent>();
        foreach (var part in scenario.GetProperty("parts").EnumerateArray())
        {
            switch (part.GetProperty("type").GetString())
            {
                case "text":
                    content.Add(new TextContent(part.GetProperty("text").GetString()!));
                    break;
                case "image":
                    content.Add(new DataContent(
                        Convert.FromBase64String(part.GetProperty("data").GetString()!),
                        part.GetProperty("mimeType").GetString()!));
                    break;
                default:
                    throw new InvalidOperationException("Unknown provider wire scenario part.");
            }
        }

        var prompt = new ChatMessage(ChatRole.User, content);
        await foreach (var _ in agent.RunStreamingAsync(prompt, session, deadline.Token)) { }
        var capture = await captureTask.WaitAsync(deadline.Token);
        results.Add(new { api, model = modelId, scenario = scenario.GetProperty("id").GetString(), capture.path, capture.payload });
    }
}
Console.WriteLine(JsonSerializer.Serialize(results));
