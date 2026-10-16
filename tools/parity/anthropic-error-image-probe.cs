using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Microsoft.Extensions.AI;
using PiSharp.Cli;
using PiSharp.Runtime.Providers;
using PiSharp.Runtime.Sessions;

const string png = "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+aL1cAAAAASUVORK5CYII=";
var results = new List<object>();
foreach (var isError in new[] { false, true })
{
    using var reservation = new TcpListener(IPAddress.Loopback, 0);
    reservation.Start();
    var port = ((IPEndPoint)reservation.LocalEndpoint).Port;
    reservation.Stop();
    using var listener = new HttpListener();
    listener.Prefixes.Add($"http://127.0.0.1:{port}/");
    listener.Start();
    using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
    JsonElement payload = default;
    var serving = Task.Run(async () =>
    {
        var request = await listener.GetContextAsync().WaitAsync(deadline.Token);
        using var body = await JsonDocument.ParseAsync(request.Request.InputStream, cancellationToken: deadline.Token);
        payload = body.RootElement.Clone();
        request.Response.ContentType = "application/json";
        await using var writer = new StreamWriter(request.Response.OutputStream);
        await writer.WriteAsync("""
            {"id":"msg_fixture","type":"message","role":"assistant","model":"claude-opus-5","content":[{"type":"text","text":"ok"}],"stop_reason":"end_turn","stop_sequence":null,"usage":{"input_tokens":4,"output_tokens":2}}
            """);
        await writer.FlushAsync(deadline.Token);
    }, deadline.Token);
    var model = new ModelDescriptor("claude-opus-5", "fixture", 128000, "configured", Provider: "fixture", Api: "anthropic-messages");
    var profile = new ProviderProfile("fixture", "Fixture", new Uri($"http://127.0.0.1:{port}"), true,
        false, null, null, [model], Api: "anthropic-messages");
    using var client = ProviderChatClientFactory.Create(new ModelSelection(profile, model, "fixture-key", true, "fixture"));
    var toolResult = new FunctionResultContent("call_image", new List<AIContent>
    {
        new TextContent("tool text"), new DataContent(Convert.FromBase64String(png), "image/png")
    }) { Exception = isError ? new PiSharp.Runtime.Tools.ToolFailureException("Tool failed.") : null };
    var messages = new[]
    {
        new ChatMessage(ChatRole.User, "before"),
        new ChatMessage(ChatRole.Assistant, [new FunctionCallContent("call_image", "read", new Dictionary<string, object?> { ["path"] = "image.png" })]),
        new ChatMessage(ChatRole.Tool, [toolResult])
    };
    var original = JsonSerializer.Serialize(messages, AIJsonUtilities.DefaultOptions);
    if ((await client.GetResponseAsync(messages, cancellationToken: deadline.Token)).Text != "ok")
        throw new InvalidOperationException("Provider response failed.");
    await serving;
    var result = payload.GetProperty("messages").EnumerateArray()
        .SelectMany(message => message.GetProperty("content").EnumerateArray())
        .Single(block => block.GetProperty("type").GetString() == "tool_result");
    results.Add(new { isError, resultIsError = result.GetProperty("is_error").GetBoolean(),
        blocks = result.GetProperty("content"), historyUnchanged = original == JsonSerializer.Serialize(messages, AIJsonUtilities.DefaultOptions) });
}
Console.WriteLine(JsonSerializer.Serialize(results));
