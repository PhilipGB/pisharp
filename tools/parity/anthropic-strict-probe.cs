using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Anthropic.Exceptions;
using Anthropic.Models.Beta.Messages;
using Microsoft.Extensions.AI;
using PiSharp.Runtime.Sessions;
using PiSharp.Tests;

var cases = JsonSerializer.Deserialize<JsonElement[]>(File.ReadAllText(args[0]))!;
var results = new List<object>();
foreach (var scenario in cases)
{
    var id = scenario.GetProperty("id").GetString()!;
    var streaming = scenario.GetProperty("streaming").GetBoolean();
    var errorMessage = scenario.GetProperty("errorMessage").GetString()!;
    var status = scenario.GetProperty("status").GetInt32();
    var errorType = scenario.GetProperty("errorType").GetString()!;
    var errorBody = JsonSerializer.Serialize(new { type = "error", error = new { type = errorType, message = errorMessage } });
    using var fixture = new AnthropicStrictHttpFixture((_, attempt) =>
        scenario.GetProperty("streamError").GetBoolean() ? (200, "event: error\ndata: " + errorBody + "\n\n") :
        attempt == 1 && status != 200 || scenario.GetProperty("secondError").GetBoolean()
            ? (status, errorBody) : (200, """
                {"id":"msg_fixture","type":"message","role":"assistant","model":"fixture","content":[{"type":"text","text":"ok"}],"stop_reason":"end_turn","stop_sequence":null,"usage":{"input_tokens":4,"output_tokens":2}}
                """));
    using var client = fixture.Client(nativeChanges: scenario.GetProperty("native").GetBoolean(),
        strictSupport: scenario.GetProperty("supported").GetBoolean());
    var messages = scenario.GetProperty("messages").EnumerateArray().Select(message =>
    {
        var chat = new ChatMessage(new ChatRole(message.GetProperty("role").GetString()!), message.GetProperty("content").GetString());
        SystemMessageTranscript.ImportMetadata(chat, message);
        return chat;
    }).ToArray();
    var original = JsonSerializer.Serialize(messages, AIJsonUtilities.DefaultOptions);
    var hooks = 0;
    var options = new ChatOptions { RawRepresentationFactory = _ =>
    {
        hooks++;
        if (scenario.GetProperty("hookError").GetBoolean())
            throw new AnthropicBadRequestException { StatusCode = HttpStatusCode.BadRequest, ResponseBody = errorBody };
        return new MessageCreateParams { Model = "fixture", MaxTokens = 123, Messages = [] };
    } };
    var succeeded = false;
    var diagnostic = false;
    try
    {
        var response = streaming
            ? await client.GetStreamingResponseAsync(messages, options, fixture.Token).ToChatResponseAsync(fixture.Token)
            : await client.GetResponseAsync(messages, options, fixture.Token);
        succeeded = response.Text == "ok";
        diagnostic = response.Messages.Any(message => message.AdditionalProperties?.ContainsKey("diagnostics") == true);
        if (scenario.GetProperty("repeat").GetBoolean())
            _ = await client.GetResponseAsync(messages, options, fixture.Token);
    }
    catch (Exception error) when (error is AnthropicException or InvalidOperationException)
    {
        diagnostic = error.Data.Contains("diagnostics");
    }
    await fixture.StopAsync();
    var traces = fixture.Payloads.Select(payload => new
    {
        tools = payload.GetProperty("tools"),
        additions = payload.GetProperty("messages").EnumerateArray()
            .Where(message => message.GetProperty("content").ValueKind == JsonValueKind.Array)
            .SelectMany(message => message.GetProperty("content").EnumerateArray())
            .Where(block => block.GetProperty("type").GetString() == "tool_addition")
            .Select(block => block.GetProperty("tool").GetProperty("definition")).ToArray(),
        maxTokens = payload.GetProperty("max_tokens").GetInt32()
    }).ToArray();
    results.Add(new { id, succeeded, hooks, diagnostic, traces,
        historyUnchanged = original == JsonSerializer.Serialize(messages, AIJsonUtilities.DefaultOptions) });
}
Console.WriteLine(JsonSerializer.Serialize(results));
