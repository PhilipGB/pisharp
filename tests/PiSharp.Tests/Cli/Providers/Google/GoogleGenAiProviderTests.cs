using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;
using PiSharp.Cli;
using PiSharp.Runtime;
using PiSharp.Runtime.Providers;

namespace PiSharp.Tests;

public sealed class GoogleGenAiProviderTests
{
    [Fact]
    public async Task GoogleProfileUsesCurrentPiCatalogAndGeminiCredential()
    {
        var root = Path.Combine(Path.GetTempPath(), "pisharp-google-catalog-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            using var http = new HttpClient();
            var runtime = await ProviderModelRuntime.CreateAsync(root, false,
                name => name == "GEMINI_API_KEY" ? "fixture-gemini-key" : null, http, offline: true);

            var models = await runtime.ListModelsAsync("google");
            var model = Assert.Single(models, item => item.Id == "gemini-3.5-flash");
            var selection = await runtime.ResolveAsync("google", null);

            Assert.Equal(22, models.Count);
            Assert.Equal("google-generative-ai", model.Api);
            Assert.Equal("google", model.Provider);
            Assert.Equal(["text", "image"], model.Input);
            Assert.Equal(1_048_576, model.ContextLength);
            Assert.Equal("google-generative-ai", ProviderChatClientFactory.ResolveProtocol(selection));
            Assert.Equal("fixture-gemini-key", selection.ApiKey);
            Assert.Equal("GEMINI_API_KEY", selection.AuthSource);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task GoogleGenAiUsesItsRealSseHttpProtocolAndProjectsMafOptionsAndUsage()
    {
        using var listener = StartListener(out var port);
        string? requestPath = null;
        string? query = null;
        string? apiKey = null;
        string? authorization = null;
        string? accept = null;
        string? requestBody = null;
        var server = Task.Run(async () =>
        {
            var request = await listener.GetContextAsync();
            requestPath = request.Request.Url?.AbsolutePath;
            query = request.Request.Url?.Query;
            apiKey = request.Request.Headers["x-goog-api-key"];
            authorization = request.Request.Headers["Authorization"];
            accept = request.Request.Headers["Accept"];
            using var body = await JsonDocument.ParseAsync(request.Request.InputStream);
            requestBody = body.RootElement.GetRawText();
            request.Response.ContentType = "text/event-stream";
            await using var writer = new StreamWriter(request.Response.OutputStream);
            await writer.WriteAsync("data: {\"responseId\":\"google-resp-1\",\"candidates\":[{\"content\":{\"role\":\"model\",\"parts\":[{\"text\":\"checking\",\"thought\":true,\"thoughtSignature\":\"QUJDRA==\"},{\"text\":\"hello\"},{\"functionCall\":{\"id\":\"call/1\",\"name\":\"echo\",\"args\":{\"text\":\"ping\"}},\"thoughtSignature\":\"QUJDRA==\"}]},\"finishReason\":\"STOP\"}],\"usageMetadata\":{\"promptTokenCount\":100,\"cachedContentTokenCount\":20,\"candidatesTokenCount\":30,\"thoughtsTokenCount\":10,\"totalTokenCount\":140}}\n\n");
            await writer.FlushAsync();
            request.Response.Close();
        });

        var endpoint = new Uri($"http://127.0.0.1:{port}/v1beta");
        var model = GoogleModel("gemini-3.5-flash") with { BaseUrl = endpoint.ToString() };
        var provider = new ProviderProfile("google", "Google", endpoint,
            true, false, "GEMINI_API_KEY", null, [model], Api: "google-generative-ai");
        var selection = new ModelSelection(provider, model, "fixture-gemini-key", true, "fixture");
        var echo = AIFunctionFactory.Create((string text) => "echo:" + text, name: "echo");
        var png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+/bX8AAAAASUVORK5CYII=");
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var client = ProviderChatClientFactory.Create(selection);
        var updates = new List<ChatResponseUpdate>();
        await foreach (var update in client.GetStreamingResponseAsync(
            [new ChatMessage(ChatRole.User, [new TextContent("say hello"), new DataContent(png, "image/png")])],
            new ChatOptions
            {
                Instructions = "fixture system prompt",
                Temperature = 0.25f,
                MaxOutputTokens = 2048,
                Reasoning = ThinkingLevels.ToOptions("high"),
                Tools = [echo],
                ToolMode = ChatToolMode.RequireAny
            }, deadline.Token))
            updates.Add(update);
        await server.WaitAsync(deadline.Token);

        Assert.Equal("/v1beta/models/gemini-3.5-flash:streamGenerateContent", requestPath);
        Assert.Equal("?alt=sse", query);
        Assert.Equal("fixture-gemini-key", apiKey);
        Assert.Null(authorization);
        Assert.Contains("text/event-stream", accept, StringComparison.OrdinalIgnoreCase);
        using var sent = JsonDocument.Parse(requestBody!);
        var root = sent.RootElement;
        Assert.Equal("fixture system prompt", root.GetProperty("systemInstruction").GetProperty("parts")[0].GetProperty("text").GetString());
        var userParts = root.GetProperty("contents")[0].GetProperty("parts");
        Assert.Equal("say hello", userParts[0].GetProperty("text").GetString());
        Assert.Equal(Convert.ToBase64String(png), userParts[1].GetProperty("inlineData").GetProperty("data").GetString());
        Assert.Equal(0.25, root.GetProperty("generationConfig").GetProperty("temperature").GetDouble(), 3);
        Assert.Equal(2048, root.GetProperty("generationConfig").GetProperty("maxOutputTokens").GetInt32());
        Assert.True(root.GetProperty("generationConfig").GetProperty("thinkingConfig").GetProperty("includeThoughts").GetBoolean());
        Assert.Equal("HIGH", root.GetProperty("generationConfig").GetProperty("thinkingConfig").GetProperty("thinkingLevel").GetString());
        Assert.Equal("echo", root.GetProperty("tools")[0].GetProperty("functionDeclarations")[0].GetProperty("name").GetString());
        Assert.Equal("ANY", root.GetProperty("toolConfig").GetProperty("functionCallingConfig").GetProperty("mode").GetString());

        var thinking = Assert.IsType<TextReasoningContent>(Assert.Single(updates.SelectMany(update => update.Contents)
            .OfType<TextReasoningContent>()));
        Assert.Equal("checking", thinking.Text);
        Assert.Equal("QUJDRA==", thinking.ProtectedData);
        Assert.Contains(updates.SelectMany(update => update.Contents).OfType<TextContent>(), text => text.Text == "hello");
        var functionCall = Assert.Single(updates.SelectMany(update => update.Contents).OfType<FunctionCallContent>());
        Assert.Equal("call/1", functionCall.CallId);
        Assert.Equal("echo", functionCall.Name);
        Assert.Equal("tool_calls", updates[^1].FinishReason?.ToString());
        var usage = Assert.Single(updates.SelectMany(update => update.Contents).OfType<UsageContent>()).Details;
        Assert.Equal(100, usage.InputTokenCount);
        Assert.Equal(40, usage.OutputTokenCount);
        Assert.Equal(20, usage.CachedInputTokenCount);
        Assert.Equal(10, usage.ReasoningTokenCount);
        Assert.Equal(140, usage.TotalTokenCount);
    }

    [Fact]
    public async Task GoogleFunctionCallsExecuteThroughMafAndReplayCorrelatedResults()
    {
        using var listener = StartListener(out var port);
        var requests = new List<string>();
        var server = Task.Run(async () =>
        {
            for (var index = 0; index < 2; index++)
            {
                var request = await listener.GetContextAsync();
                using var reader = new StreamReader(request.Request.InputStream);
                requests.Add(await reader.ReadToEndAsync());
                request.Response.ContentType = "text/event-stream";
                await using var writer = new StreamWriter(request.Response.OutputStream);
                var payload = index == 0
                    ? "{\"responseId\":\"google-tool-1\",\"candidates\":[{\"content\":{\"role\":\"model\",\"parts\":[{\"functionCall\":{\"id\":\"call/one\",\"name\":\"echo\",\"args\":{\"text\":\"ping\"}},\"thoughtSignature\":\"QUJDRA==\"}]},\"finishReason\":\"STOP\"}]}"
                    : "{\"responseId\":\"google-tool-2\",\"candidates\":[{\"content\":{\"role\":\"model\",\"parts\":[{\"text\":\"completed\"}]},\"finishReason\":\"STOP\"}]}";
                await writer.WriteAsync("data: " + payload + "\n\n");
                await writer.FlushAsync();
                request.Response.Close();
            }
        });

        var endpoint = new Uri($"http://127.0.0.1:{port}/v1beta");
        var model = GoogleModel("gemini-3.5-flash") with { BaseUrl = endpoint.ToString() };
        var provider = new ProviderProfile("google", "Google", endpoint, true, false,
            "GEMINI_API_KEY", null, [model], Api: "google-generative-ai");
        var selection = new ModelSelection(provider, model, "fixture-gemini-key", true, "fixture");
        var echo = AIFunctionFactory.Create((string text) => "echo:" + text, name: "echo");
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var agent = new PiAgent(ProviderChatClientFactory.Create(selection), new CodingTools(Path.GetTempPath()),
            extensionTools: [echo]);
        var session = await agent.CreateSessionAsync(deadline.Token);
        var answer = new StringBuilder();
        await foreach (var update in agent.RunStreamingAsync("use echo", session, deadline.Token))
            if (update.Text is { } text) answer.Append(text);
        await server.WaitAsync(deadline.Token);

        Assert.Equal("completed", answer.ToString());
        Assert.Equal(2, requests.Count);
        using var continuation = JsonDocument.Parse(requests[1]);
        var contents = continuation.RootElement.GetProperty("contents").EnumerateArray().ToArray();
        var modelTurn = Assert.Single(contents, item => item.TryGetProperty("role", out var role) && role.GetString() == "model");
        var callPart = Assert.Single(modelTurn.GetProperty("parts").EnumerateArray());
        Assert.Equal("call_one", callPart.GetProperty("functionCall").GetProperty("id").GetString());
        Assert.Equal("QUJDRA==", callPart.GetProperty("thoughtSignature").GetString());
        var functionResponses = contents.SelectMany(item => item.GetProperty("parts").EnumerateArray())
            .Where(item => item.TryGetProperty("functionResponse", out _)).ToArray();
        var functionResponse = Assert.Single(functionResponses).GetProperty("functionResponse");
        Assert.Equal("echo", functionResponse.GetProperty("name").GetString());
        Assert.Equal("call_one", functionResponse.GetProperty("id").GetString());
        Assert.Equal("echo:ping", functionResponse.GetProperty("response").GetProperty("output").GetString());
    }

    [Fact]
    public void GoogleHistoryPreservesSameModelSignaturesAndRoutesToolImagesByGeminiVersion()
    {
        var image = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+/bX8AAAAASUVORK5CYII=");
        var signedReasoning = new TextReasoningContent("")
        {
            ProtectedData = "QUJDRA==",
            AdditionalProperties = SignatureProperties("gemini-3.5-flash", "QUJDRA==")
        };
        var signedEmptyText = new TextContent("")
        {
            AdditionalProperties = SignatureProperties("gemini-3.5-flash", "QUJDRA==")
        };
        var call = new FunctionCallContent("call/one", "read", new Dictionary<string, object?> { ["path"] = "image.png" })
        {
            AdditionalProperties = SignatureProperties("gemini-3.5-flash", "QUJDRA==")
        };
        ChatMessage[] messages =
        [
            new(ChatRole.User, "inspect"),
            new(ChatRole.Assistant, [signedReasoning, signedEmptyText, call]),
            new(ChatRole.Tool, [new FunctionResultContent("call/one", "image read"), new DataContent(image, "image/png")])
        ];
        var persistedMessages = JsonSerializer.Serialize(messages, AIJsonUtilities.DefaultOptions);
        messages = JsonSerializer.Deserialize<ChatMessage[]>(persistedMessages, AIJsonUtilities.DefaultOptions)!;

        using var geminiThree = new GoogleGenAiChatClient(new HttpClient(), new Uri("https://example.test/v1beta"), "key", GoogleModel("gemini-3.5-flash"));
        var geminiThreeRequest = geminiThree.BuildRequest(messages, null);
        var geminiThreeContents = geminiThreeRequest["contents"]!.AsArray();
        var geminiThreeParts = geminiThreeContents[1]!["parts"]!.AsArray();
        Assert.Equal("", geminiThreeParts[0]!["text"]!.GetValue<string>());
        Assert.True(geminiThreeParts[0]!["thought"]!.GetValue<bool>());
        Assert.Equal("QUJDRA==", geminiThreeParts[0]!["thoughtSignature"]!.GetValue<string>());
        Assert.Equal("", geminiThreeParts[1]!["text"]!.GetValue<string>());
        Assert.Equal("QUJDRA==", geminiThreeParts[1]!["thoughtSignature"]!.GetValue<string>());
        var geminiThreeCall = geminiThreeParts[2]!["functionCall"]!;
        Assert.Equal("call_one", geminiThreeCall["id"]!.GetValue<string>());
        Assert.Equal("QUJDRA==", geminiThreeParts[2]!["thoughtSignature"]!.GetValue<string>());
        var geminiThreeResult = geminiThreeContents[2]!["parts"]![0]!["functionResponse"]!;
        Assert.Equal("call_one", geminiThreeResult["id"]!.GetValue<string>());
        Assert.Equal("image/png", geminiThreeResult["parts"]![0]!["inlineData"]!["mimeType"]!.GetValue<string>());
        Assert.Equal(3, geminiThreeContents.Count);

        using var geminiTwo = new GoogleGenAiChatClient(new HttpClient(), new Uri("https://example.test/v1beta"), "key", GoogleModel("gemini-2.5-flash"));
        var geminiTwoRequest = geminiTwo.BuildRequest(messages, null);
        var geminiTwoContents = geminiTwoRequest["contents"]!.AsArray();
        Assert.Equal(4, geminiTwoContents.Count);
        var geminiTwoParts = geminiTwoContents[1]!["parts"]!.AsArray();
        Assert.Single(geminiTwoParts);
        Assert.Null(geminiTwoParts[0]!["functionCall"]!["id"]);
        Assert.Null(geminiTwoParts[0]!["thoughtSignature"]);
        Assert.Null(geminiTwoContents[2]!["parts"]![0]!["functionResponse"]!["id"]);
        Assert.Equal("image read", geminiTwoContents[2]!["parts"]![0]!["functionResponse"]!["response"]!["output"]!.GetValue<string>());
        Assert.Equal("Tool result image:", geminiTwoContents[3]!["parts"]![0]!["text"]!.GetValue<string>());
        Assert.Equal("image/png", geminiTwoContents[3]!["parts"]![1]!["inlineData"]!["mimeType"]!.GetValue<string>());
    }

    private static ModelDescriptor GoogleModel(string id) =>
        GoogleGenAiModelCatalog.Load().Single(model => model.Id == id);

    private static AdditionalPropertiesDictionary SignatureProperties(string modelId, string signature) => new()
    {
        ["pisharp.google.thoughtSignature"] = signature,
        ["pisharp.google.signatureProvider"] = "google",
        ["pisharp.google.signatureModel"] = modelId
    };

    private static HttpListener StartListener(out int port)
    {
        using var reservation = new TcpListener(IPAddress.Loopback, 0);
        reservation.Start();
        port = ((IPEndPoint)reservation.LocalEndpoint).Port;
        reservation.Stop();
        var listener = new HttpListener();
        listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        listener.Start();
        return listener;
    }
}
