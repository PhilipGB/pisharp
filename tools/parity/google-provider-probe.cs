using System.Text.Json;
using Microsoft.Extensions.AI;
using PiSharp.Cli;
using PiSharp.Runtime.Providers;

var cases = new[]
{
    new { id = "genai-gemini-2.5", api = "google-generative-ai", provider = "google", model = "gemini-2.5-flash" },
    new { id = "genai-gemini-3.5", api = "google-generative-ai", provider = "google", model = "gemini-3.5-flash" },
    new { id = "genai-gemma-4", api = "google-generative-ai", provider = "google", model = "gemma-4-26b-a4b-it" },
    new { id = "vertex-gemini-2.5", api = "google-vertex", provider = "google-vertex", model = "gemini-2.5-flash" },
    new { id = "vertex-gemini-3.5", api = "google-vertex", provider = "google-vertex", model = "gemini-3.5-flash" },
    new { id = "vertex-gemma-4", api = "google-vertex", provider = "google-vertex", model = "gemma-4-26b-a4b-it" }
};
var image = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+/bX8AAAAASUVORK5CYII=");
var results = new List<object>();
foreach (var item in cases)
{
    var model = new ModelDescriptor(item.model, null, 128_000, "fixture", Reasoning: true,
        Provider: item.provider, Input: ["text", "image"], Api: item.api);
    var signature = SignatureProperties(item.provider, item.model, "QUJDRA==");
    var call = new FunctionCallContent("call/one", "read", new Dictionary<string, object?> { ["path"] = "image.png" })
    {
        AdditionalProperties = signature
    };
    var messages = new ChatMessage[]
    {
        new(ChatRole.User, [new TextContent("inspect"), new DataContent(image, "image/png")]),
        new(ChatRole.Assistant,
        [
            new TextReasoningContent("checking") { ProtectedData = "QUJDRA==", AdditionalProperties = SignatureProperties(item.provider, item.model, "QUJDRA==") },
            new TextContent("") { AdditionalProperties = SignatureProperties(item.provider, item.model, "QUJDRA==") },
            call
        ]),
        new(ChatRole.Tool, [new FunctionResultContent("call/one", "image read"), new DataContent(image, "image/png")])
    };
    using var client = new GoogleGenAiChatClient(new HttpClient(), new Uri("https://example.test/v1beta"), "key", model,
        item.api == "google-vertex" ? new GoogleVertexRequestOptions(null, null, false) : null);
    var request = client.BuildRequest(messages, null);
    results.Add(new { id = item.id, contents = request["contents"] });
}
Console.WriteLine(JsonSerializer.Serialize(results));

static AdditionalPropertiesDictionary SignatureProperties(string provider, string model, string signature) => new()
{
    ["pisharp.google.thoughtSignature"] = signature,
    ["pisharp.google.signatureProvider"] = provider,
    ["pisharp.google.signatureModel"] = model
};
