using System.Text.Json;
using Microsoft.Extensions.AI;
using PiSharp.Cli;
using PiSharp.Core;
using PiSharp.Runtime.Providers;
using PiSharp.Tests;

var inputs = JsonSerializer.Deserialize<string[]>(File.ReadAllText(args[0]))!;
var parsed = inputs.Select(input => new { input, repaired = ToolArgumentJson.Repair(input), parsed = ToolArgumentJson.ParseStreaming(input) }).ToArray();
var native = new List<object>();
foreach (var protocol in new[] { "openai-completions", "openai-responses", "anthropic-messages" })
    foreach (var input in new[]
    {
        """{"path":"a.tex","content":"\usepackage{x} caf\u00e9 \url{x}","mode":"w"}""",
        """{"text":"valid\\usepackage \u00e9"}""",
        """{"text":"ab\u00""",
        """{"path":"target.txt","content":"replacement", BROKEN}""",
        "null",
        "[]"
    })
    {
        await using var server = new ProviderToolArgumentServer(protocol, input, streaming: true);
        var model = new ModelDescriptor("fixture-model", "Fixture", 128000, "configured", Provider: "fixture", Api: protocol);
        var provider = new ProviderProfile("fixture", "Fixture", server.Endpoint, true, false, null, null, [model], Api: protocol);
        using var client = ProviderChatClientFactory.Create(new ModelSelection(provider, model, "fixture-key", true, "fixture"));
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var response = await client.GetStreamingResponseAsync([new ChatMessage(ChatRole.User, "fixture")],
            cancellationToken: deadline.Token).ToChatResponseAsync(deadline.Token);
        var call = response.Messages.SelectMany(message => message.Contents).OfType<FunctionCallContent>().Single();
        native.Add(new { protocol, input, arguments = call.Arguments,
            hasError = call.Exception is not null, errorIncludesInput = call.Exception?.Message.Contains(input) == true,
            wire = ProviderToolArgumentServer.Response(protocol, input, true) });
    }
Console.WriteLine(JsonSerializer.Serialize(new { parsed, native }));
