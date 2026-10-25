using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Microsoft.Extensions.AI;
using PiSharp.Cli;
using PiSharp.Runtime;
using PiSharp.Runtime.Providers;
using PiSharp.Runtime.Sessions;
using PiSharp.Runtime.Tools;

var inactivePng = Convert.FromBase64String(args[0]);
var activePng = Convert.FromBase64String(args[1]);
var results = new List<object>();
foreach (var (api, provider, modelId) in new[]
{
    ("openai-responses", "openai-fixture", "gpt-5.4"),
    ("openai-completions", "openai-fixture", "gpt-4o-mini"),
    ("anthropic-messages", "anthropic-fixture", "claude-sonnet-4-6")
})
{
    using var portListener = new TcpListener(IPAddress.Loopback, 0);
    portListener.Start();
    var port = ((IPEndPoint)portListener.LocalEndpoint).Port;
    portListener.Stop();
    var baseUrl = api == "anthropic-messages" ? $"http://127.0.0.1:{port}" : $"http://127.0.0.1:{port}/v1";
    using var listener = new HttpListener();
    listener.Prefixes.Add($"http://127.0.0.1:{port}/");
    listener.Start();

    using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
    var capturesTask = Task.Run(async () =>
    {
        var captures = new List<object>();
        for (var index = 0; index < 4; index++)
        {
            var request = await listener.GetContextAsync().WaitAsync(deadline.Token);
            using var reader = new StreamReader(request.Request.InputStream);
            var body = await reader.ReadToEndAsync(deadline.Token);
            request.Response.ContentType = "text/event-stream";
            request.Response.SendChunked = true;
            await using var writer = new StreamWriter(request.Response.OutputStream);
            await writer.WriteAsync(ResponseEvents(api));
            await writer.FlushAsync(deadline.Token);
            request.Response.Close();
            using var document = JsonDocument.Parse(body);
            captures.Add(new { path = request.Request.Url?.AbsolutePath, payload = document.RootElement.Clone() });
        }
        return captures;
    }, deadline.Token);

    var model = new ModelDescriptor(modelId, provider, null, "fixture", Reasoning: false,
        Provider: provider, Input: ["text", "image"], Api: api);
    var profile = new ProviderProfile(provider, provider, new Uri(baseUrl), true, false, null, null, [model]);
    var selection = new ModelSelection(profile, model, "fixture-key", true, "fixture");
    var root = Path.Combine(Path.GetTempPath(), $"pisharp-image-wire-session-{Guid.NewGuid():N}");
    Directory.CreateDirectory(root);
    var store = new ConversationStore(root, Path.Combine(root, "sessions"));
    var conversation = new ConversationSession(root, modelId, baseUrl, provider);
    var sessionPath = store.NewPath(conversation);
    var firstAgent = new PiAgent(ProviderChatClientFactory.Create(selection), new CodingTools(root), noTools: true);
    var firstRun = await ConversationRun.OpenAsync(firstAgent, conversation, deadline.Token);
    await foreach (var _ in firstRun.RunStreamingAsync("base", deadline.Token)) { }
    var branchAnchor = conversation.Tree.ActivePath().Last(entry => entry.Type == "chat").Id;
    await foreach (var _ in firstRun.RunStreamingAsync(new ChatMessage(ChatRole.User,
        [new TextContent("inactive image"), new DataContent(inactivePng, "image/png")]),
        "inactive image", deadline.Token)) { }
    await firstRun.SelectAsync(branchAnchor, deadline.Token);
    await foreach (var _ in firstRun.RunStreamingAsync(new ChatMessage(ChatRole.User,
        [new TextContent("inspect λ"), new DataContent(activePng, "image/png")]), "inspect λ", deadline.Token)) { }
    await store.SaveAsync(conversation, sessionPath, deadline.Token);

    var reopened = await store.LoadAsync(sessionPath, deadline.Token);
    var resumedAgent = new PiAgent(ProviderChatClientFactory.Create(selection), new CodingTools(root), noTools: true);
    var resumedRun = await ConversationRun.OpenAsync(resumedAgent, reopened, deadline.Token);
    await foreach (var _ in resumedRun.RunStreamingAsync("continue", deadline.Token)) { }

    var captures = await capturesTask.WaitAsync(deadline.Token);
    results.Add(new { api, model = modelId, captures });
    Directory.Delete(root, recursive: true);
}

Console.WriteLine(JsonSerializer.Serialize(results));

static string ResponseEvents(string api) => api switch
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
