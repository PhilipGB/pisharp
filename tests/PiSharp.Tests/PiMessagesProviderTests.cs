using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;
using PiSharp.Cli;
using PiSharp.Runtime.Providers;

namespace PiSharp.Tests;

public sealed class PiMessagesProviderTests
{
    [Fact]
    public void RequestMapperPreservesPiContextImagesToolsAndProviderOptions()
    {
        var image = new DataContent(new byte[] { 1, 2, 3 }, "image/png");
        var user = new ChatMessage(ChatRole.User, [new TextContent("look"), image])
        {
            CreatedAt = DateTimeOffset.FromUnixTimeMilliseconds(1234)
        };
        var signature = new TextReasoningContent("thinking") { ProtectedData = "sig" };
        var assistant = new ChatMessage(ChatRole.Assistant,
        [
            new TextContent("answer"),
            signature,
            new FunctionCallContent("call-1", "read", new Dictionary<string, object?> { ["path"] = "a.txt" })
        ])
        {
            CreatedAt = DateTimeOffset.FromUnixTimeMilliseconds(2345)
        };
        var toolResult = new ChatMessage(ChatRole.Tool, [new FunctionResultContent("call-1", "file contents")])
        {
            AuthorName = "read",
            CreatedAt = DateTimeOffset.FromUnixTimeMilliseconds(3456)
        };
        var echo = AIFunctionFactory.Create((string text) => text, name: "echo");
        var options = new ChatOptions
        {
            Instructions = "system instructions",
            Temperature = 0.25f,
            MaxOutputTokens = 512,
            Reasoning = ThinkingLevels.ToOptions("high"),
            ConversationId = "session-1",
            Tools = [echo],
            ToolMode = ChatToolMode.RequireAny,
            AdditionalProperties = new AdditionalPropertiesDictionary
            {
                ["piMessages.cacheRetention"] = "long"
            }
        };

        var request = PiMessagesRequestMapper.Build("fixture-model", [user, assistant, toolResult], options);
        var context = request["context"]!["messages"]!.AsArray();

        Assert.Equal("fixture-model", request["model"]!.GetValue<string>());
        Assert.Equal("system", context[0]!["role"]!.GetValue<string>());
        Assert.Equal("system instructions", context[0]!["content"]!.GetValue<string>());
        Assert.Equal("echo", context[0]!["toolsAdded"]![0]!["name"]!.GetValue<string>());
        Assert.Equal("look", context[1]!["content"]![0]!["text"]!.GetValue<string>());
        Assert.Equal(Convert.ToBase64String([1, 2, 3]), context[1]!["content"]![1]!["data"]!.GetValue<string>());
        Assert.Equal(1234, context[1]!["timestamp"]!.GetValue<long>());
        Assert.Equal("thinking", context[2]!["content"]![1]!["thinking"]!.GetValue<string>());
        Assert.Equal("sig", context[2]!["content"]![1]!["thinkingSignature"]!.GetValue<string>());
        Assert.Equal("call-1", context[2]!["content"]![2]!["id"]!.GetValue<string>());
        Assert.Equal("read", context[3]!["toolName"]!.GetValue<string>());
        Assert.Equal("file contents", context[3]!["content"]![0]!["text"]!.GetValue<string>());
        Assert.Equal("high", request["options"]!["reasoning"]!.GetValue<string>());
        Assert.Equal("long", request["options"]!["cacheRetention"]!.GetValue<string>());
        Assert.Equal("session-1", request["options"]!["sessionId"]!.GetValue<string>());
        Assert.Equal("required", request["options"]!["toolChoice"]!.GetValue<string>());
    }

    [Fact]
    public async Task ProviderFactoryUsesPiMessagesHttpAndMapsStreamingResponse()
    {
        using var listener = StartListener(out var port);
        const string apiKey = "pi-messages-fixture-key";
        string? authorization = null;
        string? accept = null;
        string? path = null;
        string? body = null;
        var server = Task.Run(async () =>
        {
            var context = await listener.GetContextAsync();
            authorization = context.Request.Headers["Authorization"];
            accept = context.Request.Headers["Accept"];
            path = context.Request.Url?.PathAndQuery;
            using var reader = new StreamReader(context.Request.InputStream, context.Request.ContentEncoding);
            body = await reader.ReadToEndAsync();
            context.Response.StatusCode = (int)HttpStatusCode.OK;
            context.Response.ContentType = "text/event-stream";
            var events = new object[]
            {
                new { type = "start" },
                new { type = "text_start", contentIndex = 0 },
                new { type = "text_delta", contentIndex = 0, delta = "Hello" },
                new { type = "text_end", contentIndex = 0, content = "Hello", contentSignature = "text-sig" },
                new { type = "thinking_start", contentIndex = 1 },
                new { type = "thinking_delta", contentIndex = 1, delta = "reason" },
                new { type = "thinking_end", contentIndex = 1, content = "reason", contentSignature = "think-sig" },
                new { type = "toolcall_start", contentIndex = 2, id = "call-2", toolName = "echo" },
                new { type = "toolcall_delta", contentIndex = 2, delta = "{\"text\":\"ping\"}" },
                new
                {
                    type = "toolcall_end", contentIndex = 2,
                    toolCall = new { type = "toolCall", id = "call-2", name = "echo", arguments = new { text = "ping" } }
                },
                new
                {
                    type = "done", reason = "toolUse", responseId = "response-7", providerThinkingLevel = "high",
                    usage = new
                    {
                        input = 40, output = 15, cacheRead = 5, cacheWrite = 8, totalTokens = 55,
                        cost = new { input = 0.01, output = 0.02, cacheRead = 0.003, cacheWrite = 0.004, total = 0.037 }
                    }
                }
            };
            var payload = string.Join("\r\n\r\n", events.Select(item =>
                "data: " + JsonSerializer.Serialize(item))) + "\r\n\r\n";
            var responseBytes = Encoding.UTF8.GetBytes(payload);
            context.Response.ContentLength64 = responseBytes.Length;
            await context.Response.OutputStream.WriteAsync(responseBytes);
            context.Response.Close();
        });

        var endpoint = new Uri($"http://127.0.0.1:{port}/v1");
        var model = new ModelDescriptor("fixture-model", "custom", 32000, "configured",
            Reasoning: true, Provider: "custom", Api: "pi-messages", BaseUrl: endpoint.ToString());
        var provider = new ProviderProfile("custom", "Custom Pi Messages", endpoint, true, false, null, null,
            [model], Api: "pi-messages");
        var selection = new ModelSelection(provider, model, apiKey, true, "models.json");
        Assert.Equal("pi-messages", ProviderChatClientFactory.ResolveProtocol(selection));
        using var client = ProviderChatClientFactory.Create(selection,
            new ProviderRetrySettings(MaxRetries: 0, TimeoutMs: 5000, MaxRetryDelayMs: 60000), httpIdleTimeoutMs: 5000);
        var options = new ChatOptions
        {
            Instructions = "fixture instructions",
            Temperature = 0.4f,
            MaxOutputTokens = 1024,
            ConversationId = "conversation-9",
            AdditionalProperties = new AdditionalPropertiesDictionary
            {
                ["piMessages.debug"] = true,
                ["piMessages.cacheRetention"] = "short"
            }
        };

        var updates = new List<ChatResponseUpdate>();
        await foreach (var update in client.GetStreamingResponseAsync([new ChatMessage(ChatRole.User, "hello")], options))
            updates.Add(update);
        await server.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.NotEmpty(updates);
        Assert.True(updates.Any(update => update.ResponseId == "response-7"),
            string.Join("; ", updates.Select(update =>
                $"id={update.ResponseId ?? "null"}, finish={update.FinishReason?.Value ?? "null"}, text={update.Text}, errors={string.Join(",", update.Contents.OfType<ErrorContent>().Select(item => item.Message))}")));
        var response = updates.ToChatResponse();

        Assert.Equal("Bearer " + apiKey, authorization);
        Assert.Equal("text/event-stream", accept);
        Assert.Equal("/v1/messages?debug=1", path);
        using var sent = JsonDocument.Parse(body!);
        Assert.Equal("fixture-model", sent.RootElement.GetProperty("model").GetString());
        Assert.Equal("fixture instructions", sent.RootElement.GetProperty("context").GetProperty("messages")[0]
            .GetProperty("content").GetString());
        Assert.Equal("conversation-9", sent.RootElement.GetProperty("options").GetProperty("sessionId").GetString());
        Assert.Equal("short", sent.RootElement.GetProperty("options").GetProperty("cacheRetention").GetString());

        Assert.Equal("response-7", response.ResponseId);
        Assert.Equal("tool_calls", response.FinishReason?.Value);
        var assistant = Assert.Single(response.Messages);
        Assert.Equal("Hello", assistant.Text);
        var reasoning = Assert.Single(assistant.Contents.OfType<TextReasoningContent>());
        Assert.Equal("reason", reasoning.Text);
        var call = Assert.Single(assistant.Contents.OfType<FunctionCallContent>());
        Assert.Equal("call-2", call.CallId);
        Assert.Equal("ping", Assert.IsType<JsonElement>(call.Arguments!["text"]).GetString());
        var usage = response.Usage!;
        Assert.Equal(40, usage.InputTokenCount);
        Assert.Equal(15, usage.OutputTokenCount);
        Assert.Equal(5, usage.CachedInputTokenCount);
        Assert.Equal(55, usage.TotalTokenCount);
        Assert.Equal(8, usage.AdditionalCounts!["piMessages.cacheWrite"]);
        var signatures = PiMessagesRequestMapper.GetStoredJson(assistant.AdditionalProperties,
            PiMessagesRequestMapper.TextSignaturesKey)!.AsObject();
        Assert.Equal("text-sig", signatures["0"]!.GetValue<string>());
        var replay = PiMessagesRequestMapper.Build("fixture-model", [assistant], null)["context"]!["messages"]![0]!;
        Assert.Equal("response-7", replay["responseId"]!.GetValue<string>());
        Assert.Equal("custom", replay["provider"]!.GetValue<string>());
        Assert.Equal("pi-messages", replay["api"]!.GetValue<string>());
        Assert.Equal("text-sig", replay["content"]![0]!["textSignature"]!.GetValue<string>());
        Assert.Equal("think-sig", replay["content"]![1]!["thinkingSignature"]!.GetValue<string>());
        Assert.Equal(0.01, replay["usage"]!["cost"]!["input"]!.GetValue<double>());
    }

    [Fact]
    public async Task NonSuccessResponseBecomesProviderErrorWithRedactedDiagnostics()
    {
        using var listener = StartListener(out var port);
        const string secret = "do-not-leak-this-token";
        var server = Task.Run(async () =>
        {
            var context = await listener.GetContextAsync();
            context.Response.StatusCode = (int)HttpStatusCode.Unauthorized;
            context.Response.ContentType = "application/json";
            var body = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
            {
                error = new { message = "Expired token", code = "unauthorized", details = secret }
            }));
            await context.Response.OutputStream.WriteAsync(body);
            context.Response.Close();
        });

        var endpoint = new Uri($"http://127.0.0.1:{port}/v1");
        var model = new ModelDescriptor("fixture-model", "custom", 32000, "configured", Provider: "custom",
            Api: "pi-messages", BaseUrl: endpoint.ToString());
        using var http = new HttpClient();
        using var client = new PiMessagesChatClient(http, endpoint, model, secret);
        var response = await client.GetResponseAsync([new ChatMessage(ChatRole.User, "hello")]);
        await server.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal("error", response.FinishReason?.Value);
        var error = Assert.Single(response.Messages.SelectMany(message => message.Contents).OfType<ErrorContent>());
        Assert.Contains("401", error.Message);
        Assert.Contains("Expired token", error.Message);
        Assert.DoesNotContain(secret, error.Message);
        Assert.DoesNotContain(secret, error.Details);
        Assert.Equal("unauthorized", error.ErrorCode);
        var diagnostic = PiMessagesRequestMapper.GetStoredJson(response.AdditionalProperties,
            "pisharp.piMessages.responseFailure");
        Assert.DoesNotContain(secret, diagnostic?.ToJsonString());
    }

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
