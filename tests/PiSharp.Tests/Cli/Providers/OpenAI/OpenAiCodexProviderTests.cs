using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using PiSharp.Cli;
using PiSharp.Runtime;
using PiSharp.Runtime.Providers;

namespace PiSharp.Tests;

public sealed class OpenAiCodexProviderTests
{
    [Fact]
    public async Task CodexProfileUsesStaticCurrentPiCatalogAndItsOwnRequestProtocol()
    {
        var root = Path.Combine(Path.GetTempPath(), "pisharp-codex-catalog-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            using var http = new HttpClient();
            var runtime = await ProviderModelRuntime.CreateAsync(root, false, _ => null, http,
                runtimeApiKey: "fixture-codex-token", offline: true);

            var models = await runtime.ListModelsAsync("openai-codex");
            var gpt55 = Assert.Single(models, model => model.Id == "gpt-5.5");
            var selection = await runtime.ResolveAsync("openai-codex", "gpt-5.5");

            Assert.Equal(8, models.Count);
            Assert.Equal("openai-codex", gpt55.Provider);
            Assert.Equal("openai-codex-responses", gpt55.Api);
            Assert.True(selection.Provider.OAuthSupported);
            Assert.False(selection.Provider.ApiKeySupported);
            Assert.Equal(["text", "image"], gpt55.Input);
            Assert.Equal(272_000, gpt55.ContextLength);
            Assert.Equal(128_000, gpt55.MaxOutputTokens);
            Assert.Equal(new ModelImageResizeOptions(2000, 2000, 4_718_592, 80), gpt55.InputLimits?.Images?.Resize);
            Assert.Equal(1, models.Single(model => model.Id == "gpt-6-astra").Pricing?.Tiers?.Count);
            Assert.Equal("fixture-codex-token", selection.ApiKey);
            Assert.Equal("openai-codex-responses", ProviderChatClientFactory.ResolveProtocol(selection));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task CodexResponsesWebSocketProjectsInlinePngAndCompletedUsage()
    {
        using var listener = StartListener(out var port);
        string? requestBody = null;
        var server = Task.Run(async () =>
        {
            var context = await listener.GetContextAsync();
            using var socket = (await context.AcceptWebSocketAsync(null)).WebSocket;
            requestBody = await ReceiveWebSocketMessageAsync(socket);
            await SendWebSocketResponseAsync(socket, "resp_image", "image received");
        });

        var model = new ModelDescriptor("gpt-5.5", "openai-codex", 272_000, "fixture", true,
            Provider: "openai-codex", Name: "GPT-5.5", MaxOutputTokens: 128_000,
            Input: ["text", "image"], Api: "openai-codex-responses");
        var provider = new ProviderProfile("openai-codex", "OpenAI Codex",
            new Uri($"http://127.0.0.1:{port}/backend-api"), true, false, null, null, [model], Api: "openai-codex-responses");
        var selection = new ModelSelection(provider, model, CreateToken("account-fixture"), true, "fixture token");
        using var client = ProviderChatClientFactory.Create(selection);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var image = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+/bX8AAAAASUVORK5CYII=");
        var prompt = new Microsoft.Extensions.AI.ChatMessage(Microsoft.Extensions.AI.ChatRole.User,
        [
            new Microsoft.Extensions.AI.TextContent("Describe this image"),
            new Microsoft.Extensions.AI.DataContent(image, "image/png")
        ]);
        var output = new StringBuilder();
        var updates = new List<Microsoft.Extensions.AI.ChatResponseUpdate>();
        await foreach (var update in client.GetStreamingResponseAsync([prompt],
            new Microsoft.Extensions.AI.ChatOptions { ConversationId = "codex-image-fixture" }, deadline.Token))
        {
            updates.Add(update);
            if (update.Text is { } text) output.Append(text);
        }
        await server.WaitAsync(deadline.Token);

        Assert.Equal("image received", output.ToString());
        var usage = Assert.Single(updates.SelectMany(update => update.Contents)
            .OfType<Microsoft.Extensions.AI.UsageContent>()).Details;
        Assert.Equal(12, usage.InputTokenCount);
        Assert.Equal(3, usage.OutputTokenCount);
        Assert.Equal(15, usage.TotalTokenCount);
        using var sent = JsonDocument.Parse(requestBody!);
        var content = sent.RootElement.GetProperty("input")[0].GetProperty("content").EnumerateArray().ToArray();
        Assert.Contains(content, item => item.GetProperty("type").GetString() == "input_text" &&
            item.GetProperty("text").GetString() == "Describe this image");
        var inputImage = Assert.Single(content, item => item.GetProperty("type").GetString() == "input_image");
        Assert.Equal($"data:image/png;base64,{Convert.ToBase64String(image)}",
            inputImage.GetProperty("image_url").GetString());
    }

    [Fact]
    public async Task CodexResponsesUsesCachedAccountScopedWebSocketAndRealResponsesClient()
    {
        using var listener = StartListener(out var port);
        string? requestPath = null;
        string? authorization = null;
        string? accountId = null;
        string? originator = null;
        string? beta = null;
        string? sessionId = null;
        string? requestId = null;
        string? staleContinuationRequest = null;
        string? firstRequest = null;
        string? secondRequest = null;
        var server = Task.Run(async () =>
        {
            var context = await listener.GetContextAsync();
            requestPath = context.Request.Url?.AbsolutePath;
            authorization = context.Request.Headers["Authorization"];
            accountId = context.Request.Headers["chatgpt-account-id"];
            originator = context.Request.Headers["originator"];
            beta = context.Request.Headers["OpenAI-Beta"];
            sessionId = context.Request.Headers["session-id"];
            requestId = context.Request.Headers["x-client-request-id"];
            var accepted = await context.AcceptWebSocketAsync(null);
            using var socket = accepted.WebSocket;
            firstRequest = await ReceiveWebSocketMessageAsync(socket);
            await SendWebSocketResponseAsync(socket, "resp_codex_one", "First response");
            staleContinuationRequest = await ReceiveWebSocketMessageAsync(socket);
            await SendWebSocketErrorAsync(socket, "previous_response_not_found");
            socket.Dispose();

            var retryContext = await listener.GetContextAsync();
            var retryAccepted = await retryContext.AcceptWebSocketAsync(null);
            using var retrySocket = retryAccepted.WebSocket;
            secondRequest = await ReceiveWebSocketMessageAsync(retrySocket);
            await SendWebSocketResponseAsync(retrySocket, "resp_codex_two", "Second response");
        });

        var model = new ModelDescriptor("gpt-5.5", "openai-codex", 272_000, "fixture", true,
            Provider: "openai-codex", Name: "GPT-5.5", MaxOutputTokens: 128_000,
            Input: ["text", "image"], Api: "openai-codex-responses");
        var provider = new ProviderProfile("openai-codex", "OpenAI Codex",
            new Uri($"http://127.0.0.1:{port}/backend-api"), true, false, null, null, [model], Api: "openai-codex-responses");
        var token = CreateToken("account-fixture");
        var refreshedToken = CreateToken("account-refreshed");
        var selection = new ModelSelection(provider, model, token, true, "fixture token",
            _ => Task.FromResult((refreshedToken, "account-refreshed")));
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var client = ProviderChatClientFactory.Create(selection);
        var options = new Microsoft.Extensions.AI.ChatOptions { ConversationId = "codex-session-fixture" };
        var firstOutput = await ReadTextAsync(client, "Say hello one", options, deadline.Token);
        var secondOutput = await ReadMessagesAsync(client,
        [
            new Microsoft.Extensions.AI.ChatMessage(Microsoft.Extensions.AI.ChatRole.User, "Say hello one"),
            new Microsoft.Extensions.AI.ChatMessage(Microsoft.Extensions.AI.ChatRole.Assistant, "First response"),
            new Microsoft.Extensions.AI.ChatMessage(Microsoft.Extensions.AI.ChatRole.User, "Say hello two")
        ], options, deadline.Token);
        await server.WaitAsync(deadline.Token);

        Assert.Equal("First response", firstOutput);
        Assert.Equal("Second response", secondOutput);
        Assert.Equal("/backend-api/codex/responses", requestPath);
        Assert.Equal($"Bearer {refreshedToken}", authorization);
        Assert.Equal("account-refreshed", accountId);
        Assert.Equal("pi", originator);
        Assert.Equal("responses_websockets=2026-02-06", beta);
        Assert.Equal("codex-session-fixture", sessionId);
        Assert.Equal("codex-session-fixture", requestId);
        using var first = JsonDocument.Parse(firstRequest!);
        using var stale = JsonDocument.Parse(staleContinuationRequest!);
        using var second = JsonDocument.Parse(secondRequest!);
        Assert.Equal("response.create", first.RootElement.GetProperty("type").GetString());
        Assert.Equal("gpt-5.5", first.RootElement.GetProperty("model").GetString());
        Assert.Equal("codex-session-fixture", first.RootElement.GetProperty("prompt_cache_key").GetString());
        Assert.False(first.RootElement.GetProperty("store").GetBoolean());
        Assert.False(first.RootElement.TryGetProperty("previous_response_id", out _));
        Assert.Equal("resp_codex_one", stale.RootElement.GetProperty("previous_response_id").GetString());
        Assert.Single(stale.RootElement.GetProperty("input").EnumerateArray());
        Assert.Equal("response.create", second.RootElement.GetProperty("type").GetString());
        Assert.Contains("Say hello two", second.RootElement.GetRawText(), StringComparison.Ordinal);
        Assert.False(second.RootElement.TryGetProperty("previous_response_id", out _));
        var delta = second.RootElement.GetProperty("input");
        Assert.Equal(JsonValueKind.Array, delta.ValueKind);
        Assert.Equal(3, delta.GetArrayLength());
        Assert.Contains("Say hello two", delta.GetRawText(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task CodexResumedConversationUsesPersistedSessionIdAndResolvedCredentialsForStreamRequest()
    {
        using var listener = StartListener(out var port);
        string? authorization = null;
        string? accountId = null;
        string? sessionId = null;
        string? requestId = null;
        string? requestBody = null;
        var server = Task.Run(async () =>
        {
            var context = await listener.GetContextAsync();
            authorization = context.Request.Headers["Authorization"];
            accountId = context.Request.Headers["chatgpt-account-id"];
            sessionId = context.Request.Headers["session-id"];
            requestId = context.Request.Headers["x-client-request-id"];
            var accepted = await context.AcceptWebSocketAsync(null);
            using var socket = accepted.WebSocket;
            requestBody = await ReceiveWebSocketMessageAsync(socket);
            await SendWebSocketResponseAsync(socket, "resp_resumed", "Resumed response");
        });

        var model = new ModelDescriptor("gpt-5.5", "openai-codex", 272_000, "fixture", true,
            Provider: "openai-codex", Name: "GPT-5.5", MaxOutputTokens: 128_000,
            Input: ["text", "image"], Api: "openai-codex-responses");
        var provider = new ProviderProfile("openai-codex", "OpenAI Codex",
            new Uri($"http://127.0.0.1:{port}/backend-api"), true, false, null, null, [model], Api: "openai-codex-responses");
        var refreshedToken = CreateToken("account-refreshed");
        var resolverCalls = 0;
        var selection = new ModelSelection(provider, model, CreateToken("account-expired"), true, "fixture token",
            _ =>
            {
                Interlocked.Increment(ref resolverCalls);
                return Task.FromResult((refreshedToken, "account-refreshed"));
            });
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var client = ProviderChatClientFactory.Create(selection);
        var agent = new PiAgent(client, new CodingTools(Path.GetTempPath()));
        var conversation = new PiSharp.Runtime.Sessions.ConversationSession(Path.GetTempPath(), model.Id,
            provider.Endpoint.ToString(), provider.Id);
        conversation.Append(new Microsoft.Extensions.AI.ChatMessage(Microsoft.Extensions.AI.ChatRole.User,
            "Earlier question"));
        conversation.Append(new Microsoft.Extensions.AI.ChatMessage(Microsoft.Extensions.AI.ChatRole.Assistant,
            "Earlier answer"));
        var run = await PiSharp.Runtime.Sessions.ConversationRun.OpenAsync(agent, conversation, deadline.Token,
            provider: provider.Id, reasoningLevel: "off", autoCompactionEnabled: false,
            providerApi: "openai-codex-responses");
        var output = new StringBuilder();
        await foreach (var update in run.RunStreamingAsync("Continue the resumed conversation", deadline.Token))
            if (update.Text is { } text) output.Append(text);
        await server.WaitAsync(deadline.Token);

        Assert.Equal("Resumed response", output.ToString());
        Assert.Equal(1, resolverCalls);
        Assert.Equal($"Bearer {refreshedToken}", authorization);
        Assert.Equal("account-refreshed", accountId);
        Assert.Equal(conversation.Id, sessionId);
        Assert.Equal(conversation.Id, requestId);
        using var sent = JsonDocument.Parse(requestBody!);
        Assert.Equal(conversation.Id, sent.RootElement.GetProperty("prompt_cache_key").GetString());
        Assert.False(sent.RootElement.TryGetProperty("previous_response_id", out _));
        var input = sent.RootElement.GetProperty("input");
        Assert.Contains("Earlier question", input.GetRawText(), StringComparison.Ordinal);
        Assert.Contains("Earlier answer", input.GetRawText(), StringComparison.Ordinal);
        Assert.Contains("Continue the resumed conversation", input.GetRawText(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task CodexResponsesFallsBackToAccountScopedSseWhenWebSocketClosesBeforeFirstEvent()
    {
        using var listener = StartListener(out var port);
        string? requestPath = null;
        string? authorization = null;
        string? accountId = null;
        string? originator = null;
        string? beta = null;
        string? sessionId = null;
        string? requestId = null;
        string? accept = null;
        string? apiKeyHeader = null;
        string? requestBody = null;
        string? websocketBeta = null;
        string? websocketRequestBody = null;
        var server = Task.Run(async () =>
        {
            var websocketAttempt = await listener.GetContextAsync();
            websocketBeta = websocketAttempt.Request.Headers["OpenAI-Beta"];
            var accepted = await websocketAttempt.AcceptWebSocketAsync(null);
            using (accepted.WebSocket)
            {
                websocketRequestBody = await ReceiveWebSocketMessageAsync(accepted.WebSocket);
                accepted.WebSocket.Abort();
            }

            var request = await listener.GetContextAsync();
            requestPath = request.Request.Url?.AbsolutePath;
            authorization = request.Request.Headers["Authorization"];
            accountId = request.Request.Headers["chatgpt-account-id"];
            originator = request.Request.Headers["originator"];
            beta = request.Request.Headers["OpenAI-Beta"];
            sessionId = request.Request.Headers["session-id"];
            requestId = request.Request.Headers["x-client-request-id"];
            accept = request.Request.Headers["Accept"];
            apiKeyHeader = request.Request.Headers["api-key"];
            using var body = await JsonDocument.ParseAsync(request.Request.InputStream);
            requestBody = body.RootElement.GetRawText();
            request.Response.ContentType = "text/event-stream";
            await using var writer = new StreamWriter(request.Response.OutputStream);
            await writer.WriteAsync("data: {\"type\":\"response.created\",\"response\":{\"id\":\"resp_codex\",\"object\":\"response\",\"created_at\":1,\"model\":\"gpt-5.5\",\"status\":\"in_progress\",\"output\":[]}}\n\n");
            await writer.WriteAsync("data: {\"type\":\"response.output_text.delta\",\"item_id\":\"item_codex\",\"output_index\":0,\"content_index\":0,\"delta\":\"Codex response\"}\n\n");
            await writer.WriteAsync("data: {\"type\":\"response.completed\",\"response\":{\"id\":\"resp_codex\",\"object\":\"response\",\"created_at\":1,\"model\":\"gpt-5.5\",\"status\":\"completed\",\"output\":[]}}\n\n");
            await writer.WriteAsync("data: [DONE]\n\n");
            await writer.FlushAsync();
            request.Response.Close();
        });

        var model = new ModelDescriptor("gpt-5.5", "openai-codex", 272_000, "fixture", true,
            Provider: "openai-codex", Name: "GPT-5.5", MaxOutputTokens: 128_000,
            Input: ["text", "image"], Api: "openai-codex-responses");
        var provider = new ProviderProfile("openai-codex", "OpenAI Codex",
            new Uri($"http://127.0.0.1:{port}/backend-api"), true, false, null, null, [model], Api: "openai-codex-responses");
        var token = CreateToken("account-fixture");
        var refreshedToken = CreateToken("account-refreshed");
        var selection = new ModelSelection(provider, model, token, true, "fixture token",
            _ => Task.FromResult((refreshedToken, "account-refreshed")));
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var client = ProviderChatClientFactory.Create(selection);
        var output = new StringBuilder();
        await foreach (var update in client.GetStreamingResponseAsync(
            [new Microsoft.Extensions.AI.ChatMessage(Microsoft.Extensions.AI.ChatRole.User, "Say hello")],
            new Microsoft.Extensions.AI.ChatOptions
            {
                ConversationId = "codex-session-fixture",
                Instructions = "fixture system prompt",
                Reasoning = ThinkingLevels.ToOptions("high")
            }, deadline.Token))
            if (update.Text is { } text) output.Append(text);
        await server.WaitAsync(deadline.Token);

        Assert.Equal("Codex response", output.ToString());
        Assert.Equal("responses_websockets=2026-02-06", websocketBeta);
        using var websocketRequest = JsonDocument.Parse(websocketRequestBody!);
        Assert.Equal("response.create", websocketRequest.RootElement.GetProperty("type").GetString());
        Assert.Equal("/backend-api/codex/responses", requestPath);
        Assert.Equal($"Bearer {refreshedToken}", authorization);
        Assert.Equal("account-refreshed", accountId);
        Assert.Equal("pi", originator);
        Assert.Equal("responses=experimental", beta);
        Assert.Equal("codex-session-fixture", sessionId);
        Assert.Equal("codex-session-fixture", requestId);
        Assert.Contains("text/event-stream", accept, StringComparison.OrdinalIgnoreCase);
        Assert.Null(apiKeyHeader);
        using var sent = JsonDocument.Parse(requestBody!);
        Assert.Equal("gpt-5.5", sent.RootElement.GetProperty("model").GetString());
        Assert.Equal("fixture system prompt", sent.RootElement.GetProperty("instructions").GetString());
        Assert.Equal("codex-session-fixture", sent.RootElement.GetProperty("prompt_cache_key").GetString());
        Assert.False(sent.RootElement.GetProperty("store").GetBoolean());
        Assert.True(sent.RootElement.GetProperty("parallel_tool_calls").GetBoolean());
        Assert.Equal("auto", sent.RootElement.GetProperty("tool_choice").GetString());
        Assert.Equal("low", sent.RootElement.GetProperty("text").GetProperty("verbosity").GetString());
        Assert.Equal("high", sent.RootElement.GetProperty("reasoning").GetProperty("effort").GetString());
        Assert.Equal("auto", sent.RootElement.GetProperty("reasoning").GetProperty("summary").GetString());
        Assert.Contains("reasoning.encrypted_content", sent.RootElement.GetProperty("include").EnumerateArray()
            .Select(item => item.GetString()));
    }

    [Fact]
    public async Task CodexResponsesRetriesConnectionLimitThenFallsBackBeforeResponseStarts()
    {
        using var listener = StartListener(out var port);
        var websocketAttempts = 0;
        var server = Task.Run(async () =>
        {
            for (var attempt = 0; attempt < 2; attempt++)
            {
                var context = await listener.GetContextAsync();
                var accepted = await context.AcceptWebSocketAsync(null);
                using var socket = accepted.WebSocket;
                _ = await ReceiveWebSocketMessageAsync(socket);
                websocketAttempts++;
                await SendWebSocketErrorAsync(socket, "websocket_connection_limit_reached");
                socket.Abort();
            }

            var request = await listener.GetContextAsync();
            request.Response.ContentType = "text/event-stream";
            await using var writer = new StreamWriter(request.Response.OutputStream);
            await writer.WriteAsync("data: {\"type\":\"response.created\",\"response\":{\"id\":\"resp_fallback\",\"object\":\"response\",\"created_at\":1,\"model\":\"gpt-5.5\",\"status\":\"in_progress\",\"output\":[]}}\n\n");
            await writer.WriteAsync("data: {\"type\":\"response.output_text.delta\",\"item_id\":\"item_fallback\",\"output_index\":0,\"content_index\":0,\"delta\":\"SSE fallback\"}\n\n");
            await writer.WriteAsync("data: {\"type\":\"response.completed\",\"response\":{\"id\":\"resp_fallback\",\"object\":\"response\",\"created_at\":1,\"model\":\"gpt-5.5\",\"status\":\"completed\",\"output\":[]}}\n\n");
            await writer.WriteAsync("data: [DONE]\n\n");
            await writer.FlushAsync();
            request.Response.Close();
        });

        var model = new ModelDescriptor("gpt-5.5", "openai-codex", 272_000, "fixture", true,
            Provider: "openai-codex", Name: "GPT-5.5", MaxOutputTokens: 128_000,
            Input: ["text", "image"], Api: "openai-codex-responses");
        var provider = new ProviderProfile("openai-codex", "OpenAI Codex",
            new Uri($"http://127.0.0.1:{port}/backend-api"), true, false, null, null, [model], Api: "openai-codex-responses");
        var selection = new ModelSelection(provider, model, CreateToken("account-fixture"), true, "fixture token");
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var client = ProviderChatClientFactory.Create(selection);
        var output = await ReadTextAsync(client, "Say hello",
            new Microsoft.Extensions.AI.ChatOptions { ConversationId = "codex-limit-fixture" }, deadline.Token);
        await server.WaitAsync(deadline.Token);

        Assert.Equal("SSE fallback", output);
        Assert.Equal(2, websocketAttempts);
    }

    [Fact]
    public async Task CodexResponsesWebSocketPreservesFunctionCallUpdatesForTheAgentLoop()
    {
        using var listener = StartListener(out var port);
        string? requestBody = null;
        var server = Task.Run(async () =>
        {
            var context = await listener.GetContextAsync();
            var accepted = await context.AcceptWebSocketAsync(null);
            using var socket = accepted.WebSocket;
            requestBody = await ReceiveWebSocketMessageAsync(socket);
            var functionCall = new
            {
                id = "fc_codex",
                type = "function_call",
                status = "completed",
                call_id = "call_codex",
                name = "read_file",
                arguments = "{\"path\":\"sample.txt\"}"
            };
            await SendWebSocketEventAsync(socket, new
            {
                type = "response.created",
                response = new { id = "resp_tool", @object = "response", created_at = 1, model = "gpt-5.5", status = "in_progress", output = Array.Empty<object>() }
            });
            await SendWebSocketEventAsync(socket, new
            {
                type = "response.output_item.added",
                output_index = 0,
                item = new { id = "fc_codex", type = "function_call", status = "in_progress", call_id = "call_codex", name = "read_file", arguments = "" }
            });
            await SendWebSocketEventAsync(socket, new
            {
                type = "response.function_call_arguments.delta",
                item_id = "fc_codex",
                output_index = 0,
                delta = "{\"path\":\"sample.txt\"}"
            });
            await SendWebSocketEventAsync(socket, new
            {
                type = "response.function_call_arguments.done",
                item_id = "fc_codex",
                output_index = 0,
                arguments = "{\"path\":\"sample.txt\"}"
            });
            await SendWebSocketEventAsync(socket, new { type = "response.output_item.done", output_index = 0, item = functionCall });
            await SendWebSocketEventAsync(socket, new
            {
                type = "response.completed",
                response = new
                {
                    id = "resp_tool",
                    @object = "response",
                    created_at = 1,
                    model = "gpt-5.5",
                    status = "completed",
                    output = new object[] { functionCall }
                }
            });
        });

        var model = new ModelDescriptor("gpt-5.5", "openai-codex", 272_000, "fixture", true,
            Provider: "openai-codex", Name: "GPT-5.5", MaxOutputTokens: 128_000,
            Input: ["text", "image"], Api: "openai-codex-responses");
        var provider = new ProviderProfile("openai-codex", "OpenAI Codex",
            new Uri($"http://127.0.0.1:{port}/backend-api"), true, false, null, null, [model], Api: "openai-codex-responses");
        var selection = new ModelSelection(provider, model, CreateToken("account-fixture"), true, "fixture token");
        using var client = ProviderChatClientFactory.Create(selection);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var tool = Microsoft.Extensions.AI.AIFunctionFactory.Create((string path) => path, name: "read_file");
        var options = new Microsoft.Extensions.AI.ChatOptions
        {
            ConversationId = "codex-tool-fixture",
            Tools = [tool]
        };
        var calls = new List<Microsoft.Extensions.AI.FunctionCallContent>();
        await foreach (var update in client.GetStreamingResponseAsync(
            [new Microsoft.Extensions.AI.ChatMessage(Microsoft.Extensions.AI.ChatRole.User, "Read sample.txt")],
            options, deadline.Token))
            calls.AddRange(update.Contents.OfType<Microsoft.Extensions.AI.FunctionCallContent>());
        await server.WaitAsync(deadline.Token);

        Assert.NotEmpty(calls);
        var call = calls.Last();
        Assert.Equal("call_codex", call.CallId);
        Assert.Equal("read_file", call.Name);
        var arguments = JsonSerializer.SerializeToElement(call.Arguments);
        Assert.Equal("sample.txt", arguments.GetProperty("path").GetString());
        using var sent = JsonDocument.Parse(requestBody!);
        Assert.Contains(sent.RootElement.GetProperty("tools").EnumerateArray(),
            item => item.GetProperty("name").GetString() == "read_file");
    }

    [Fact]
    public async Task CodexResponsesProjectsGrammarToolsAndReplaysCustomCallsAsFunctionCalls()
    {
        using var listener = StartListener(out var port);
        string? requestBody = null;
        string? replayBody = null;
        var server = Task.Run(async () =>
        {
            var context = await listener.GetContextAsync();
            var accepted = await context.AcceptWebSocketAsync(null);
            using var socket = accepted.WebSocket;
            requestBody = await ReceiveWebSocketMessageAsync(socket);
            await SendWebSocketEventAsync(socket, new
            {
                type = "response.created",
                response = new { id = "resp_grammar", @object = "response", created_at = 1, model = "gpt-5.5", status = "in_progress", output = Array.Empty<object>() }
            });
            var customCall = new
            {
                id = "ctc_grammar",
                type = "custom_tool_call",
                status = "completed",
                call_id = "call_grammar",
                name = "constrained_echo",
                input = "hello"
            };
            await SendWebSocketEventAsync(socket, new
            {
                type = "response.output_item.added",
                output_index = 0,
                item = new { id = "ctc_grammar", type = "custom_tool_call", status = "in_progress", call_id = "call_grammar", name = "constrained_echo", input = "" }
            });
            await SendWebSocketEventAsync(socket, new
            {
                type = "response.custom_tool_call_input.delta",
                item_id = "ctc_grammar",
                output_index = 0,
                delta = "hel"
            });
            await SendWebSocketEventAsync(socket, new
            {
                type = "response.custom_tool_call_input.delta",
                item_id = "ctc_grammar",
                output_index = 0,
                delta = "lo"
            });
            await SendWebSocketEventAsync(socket, new
            {
                type = "response.custom_tool_call_input.done",
                item_id = "ctc_grammar",
                output_index = 0,
                input = "hello"
            });
            await SendWebSocketEventAsync(socket, new { type = "response.output_item.done", output_index = 0, item = customCall });
            await SendWebSocketEventAsync(socket, new
            {
                type = "response.completed",
                response = new
                {
                    id = "resp_grammar",
                    @object = "response",
                    created_at = 1,
                    model = "gpt-5.5",
                    status = "completed",
                    output = new object[] { customCall }
                }
            });
            replayBody = await ReceiveWebSocketMessageAsync(socket);
            await SendWebSocketResponseAsync(socket, "resp_grammar_final", "done");
        });

        var compatibility = JsonSerializer.SerializeToElement(new { supportsOpenAIGrammarTools = true });
        var model = new ModelDescriptor("gpt-5.5", "openai-codex", 272_000, "fixture", true,
            Provider: "openai-codex", Name: "GPT-5.5", MaxOutputTokens: 128_000,
            Input: ["text", "image"], Api: "openai-codex-responses", Compatibility: compatibility);
        var provider = new ProviderProfile("openai-codex", "OpenAI Codex",
            new Uri($"http://127.0.0.1:{port}/backend-api"), true, false, null, null, [model], Api: "openai-codex-responses");
        var selection = new ModelSelection(provider, model, CreateToken("account-fixture"), true, "fixture token");
        using var client = ProviderChatClientFactory.Create(selection);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        const string grammarDefinition = "start: WORD\n%import common.WORD\n%ignore \" \"";
        var policy = JsonSerializer.SerializeToElement(new
        {
            type = "grammar",
            variants = new { openai_lark = grammarDefinition }
        });
        var tool = Microsoft.Extensions.AI.AIFunctionFactory.Create((string value) => value,
            new Microsoft.Extensions.AI.AIFunctionFactoryOptions
            {
                Name = "constrained_echo",
                Description = "Echo a grammar-constrained value.",
                AdditionalProperties = new Dictionary<string, object?> { ["constrainedSampling"] = policy }
            });
        var options = new Microsoft.Extensions.AI.ChatOptions
        {
            ConversationId = "codex-grammar-fixture",
            Tools = [tool]
        };
        var calls = new List<Microsoft.Extensions.AI.FunctionCallContent>();
        await foreach (var update in client.GetStreamingResponseAsync(
            [new Microsoft.Extensions.AI.ChatMessage(Microsoft.Extensions.AI.ChatRole.User, "Echo hello")], options, deadline.Token))
            calls.AddRange(update.Contents.OfType<Microsoft.Extensions.AI.FunctionCallContent>());
        var call = Assert.Single(calls);
        Assert.Equal("call_grammar", call.CallId);
        Assert.Equal("constrained_echo", call.Name);
        var arguments = JsonSerializer.SerializeToElement(call.Arguments);
        Assert.Equal("hello", arguments.GetProperty("value").GetString());
        Microsoft.Extensions.AI.ChatMessage[] replayMessages =
        [
            new(Microsoft.Extensions.AI.ChatRole.User, "Echo hello"),
            new(Microsoft.Extensions.AI.ChatRole.Assistant, [call]),
            new(Microsoft.Extensions.AI.ChatRole.Tool,
                [new Microsoft.Extensions.AI.FunctionResultContent(call.CallId, "echo:hello")]),
            new(Microsoft.Extensions.AI.ChatRole.User, "Say done")
        ];
        var finalText = new StringBuilder();
        await foreach (var update in client.GetStreamingResponseAsync(replayMessages, options, deadline.Token))
            if (update.Text is { } text) finalText.Append(text);
        Assert.Equal("done", finalText.ToString());
        await server.WaitAsync(deadline.Token);
        using var sent = JsonDocument.Parse(requestBody!);
        var declaration = Assert.Single(sent.RootElement.GetProperty("tools").EnumerateArray());
        Assert.Equal("custom", declaration.GetProperty("type").GetString());
        Assert.Equal("constrained_echo", declaration.GetProperty("name").GetString());
        Assert.Equal("grammar", declaration.GetProperty("format").GetProperty("type").GetString());
        Assert.Equal("lark", declaration.GetProperty("format").GetProperty("syntax").GetString());
        Assert.Equal(grammarDefinition,
            declaration.GetProperty("format").GetProperty("definition").GetString());
        using var replay = JsonDocument.Parse(replayBody!);
        Assert.Equal("resp_grammar", replay.RootElement.GetProperty("previous_response_id").GetString());
        var replayInput = replay.RootElement.GetProperty("input").EnumerateArray().ToArray();
        Assert.Equal(2, replayInput.Length);
        Assert.Equal("custom_tool_call_output", replayInput[0].GetProperty("type").GetString());
        Assert.Equal("call_grammar", replayInput[0].GetProperty("call_id").GetString());
        Assert.Equal("input_text", replayInput[1].GetProperty("content")[0].GetProperty("type").GetString());
    }

    [Fact]
    public async Task CodexGrammarCustomToolEventsOnSseFallbackBecomeFunctionCallUpdates()
    {
        using var listener = StartListener(out var port);
        string? websocketRequestBody = null;
        var websocketAttempts = 0;
        var server = Task.Run(async () =>
        {
            for (var attempt = 0; attempt < 2; attempt++)
            {
                var context = await listener.GetContextAsync();
                var accepted = await context.AcceptWebSocketAsync(null);
                using var socket = accepted.WebSocket;
                websocketRequestBody = await ReceiveWebSocketMessageAsync(socket);
                websocketAttempts++;
                await SendWebSocketErrorAsync(socket, "websocket_connection_limit_reached");
                socket.Abort();
            }

            var fallback = await listener.GetContextAsync();
            fallback.Response.ContentType = "text/event-stream";
            await using var writer = new StreamWriter(fallback.Response.OutputStream);
            for (var index = 0; index < 1_024; index++) await writer.WriteLineAsync();
            await writer.WriteLineAsync("data: {\"type\":\"response.created\",\"response\":{\"id\":\"resp_grammar_sse\",\"object\":\"response\",\"created_at\":1,\"model\":\"gpt-5.5\",\"status\":\"in_progress\",\"output\":[]}}");
            await writer.WriteLineAsync();
            await writer.WriteLineAsync("data: {\"type\":\"response.output_item.added\",\"output_index\":0,\"item\":{\"id\":\"ctc_grammar_sse\",\"type\":\"custom_tool_call\",\"status\":\"in_progress\",\"call_id\":\"call_grammar_sse\",\"name\":\"constrained_echo\",\"input\":\"\"}}");
            await writer.WriteLineAsync();
            await writer.WriteLineAsync("data: {\"type\":\"response.custom_tool_call_input.delta\",\"item_id\":\"ctc_grammar_sse\",\"output_index\":0,\"delta\":\"hel\"}");
            await writer.WriteLineAsync();
            await writer.WriteLineAsync("data: {\"type\":\"response.custom_tool_call_input.delta\",\"item_id\":\"ctc_grammar_sse\",\"output_index\":0,\"delta\":\"lo\"}");
            await writer.WriteLineAsync();
            await writer.WriteLineAsync("data: {\"type\":\"response.custom_tool_call_input.done\",\"item_id\":\"ctc_grammar_sse\",\"output_index\":0,\"input\":\"hello\"}");
            await writer.WriteLineAsync();
            await writer.WriteLineAsync("data: {\"type\":\"response.output_item.done\",\"output_index\":0,\"item\":{\"id\":\"ctc_grammar_sse\",\"type\":\"custom_tool_call\",\"status\":\"completed\",\"call_id\":\"call_grammar_sse\",\"name\":\"constrained_echo\",\"input\":\"hello\"}}");
            await writer.WriteLineAsync();
            await writer.WriteLineAsync("data: {\"type\":\"response.completed\",\"response\":{\"id\":\"resp_grammar_sse\",\"object\":\"response\",\"created_at\":1,\"model\":\"gpt-5.5\",\"status\":\"completed\",\"output\":[{\"id\":\"ctc_grammar_sse\",\"type\":\"custom_tool_call\",\"status\":\"completed\",\"call_id\":\"call_grammar_sse\",\"name\":\"constrained_echo\",\"input\":\"hello\"}]}}");
            await writer.WriteLineAsync();
            await writer.WriteLineAsync("data: [DONE]");
            await writer.WriteLineAsync();
            await writer.FlushAsync();
            fallback.Response.Close();
        });

        var compatibility = JsonSerializer.SerializeToElement(new { supportsOpenAIGrammarTools = true });
        var model = new ModelDescriptor("gpt-5.5", "openai-codex", 272_000, "fixture", true,
            Provider: "openai-codex", Name: "GPT-5.5", MaxOutputTokens: 128_000,
            Input: ["text", "image"], Api: "openai-codex-responses", Compatibility: compatibility);
        var provider = new ProviderProfile("openai-codex", "OpenAI Codex",
            new Uri($"http://127.0.0.1:{port}/backend-api"), true, false, null, null, [model], Api: "openai-codex-responses");
        var selection = new ModelSelection(provider, model, CreateToken("account-fixture"), true, "fixture token");
        using var client = ProviderChatClientFactory.Create(selection);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        const string grammarDefinition = "start: WORD\n%import common.WORD\n%ignore \" \"";
        var policy = JsonSerializer.SerializeToElement(new
        {
            type = "grammar",
            variants = new { openai_lark = grammarDefinition }
        });
        var tool = Microsoft.Extensions.AI.AIFunctionFactory.Create((string value) => value,
            new Microsoft.Extensions.AI.AIFunctionFactoryOptions
            {
                Name = "constrained_echo",
                Description = "Echo a grammar-constrained value.",
                AdditionalProperties = new Dictionary<string, object?> { ["constrainedSampling"] = policy }
            });
        var options = new Microsoft.Extensions.AI.ChatOptions
        {
            ConversationId = "codex-grammar-sse-fixture",
            Tools = [tool]
        };
        var calls = new List<Microsoft.Extensions.AI.FunctionCallContent>();
        await foreach (var update in client.GetStreamingResponseAsync(
            [new Microsoft.Extensions.AI.ChatMessage(Microsoft.Extensions.AI.ChatRole.User, "Echo hello")], options, deadline.Token))
            calls.AddRange(update.Contents.OfType<Microsoft.Extensions.AI.FunctionCallContent>());
        await server.WaitAsync(deadline.Token);

        var call = Assert.Single(calls);
        Assert.Equal("call_grammar_sse", call.CallId);
        Assert.Equal("constrained_echo", call.Name);
        var arguments = JsonSerializer.SerializeToElement(call.Arguments);
        Assert.Equal("hello", arguments.GetProperty("value").GetString());
        Assert.Equal(2, websocketAttempts);
        using var sent = JsonDocument.Parse(websocketRequestBody!);
        Assert.Equal("custom", sent.RootElement.GetProperty("tools")[0].GetProperty("type").GetString());
    }

    [Fact]
    public async Task GrammarToolHistorySwitchesBackToStandardResponsesFunctionItems()
    {
        using var listener = StartListener(out var port);
        string? requestBody = null;
        var server = Task.Run(async () =>
        {
            var request = await listener.GetContextAsync();
            using (var reader = new StreamReader(request.Request.InputStream, request.Request.ContentEncoding))
                requestBody = await reader.ReadToEndAsync();
            request.Response.ContentType = "text/event-stream";
            await using var writer = new StreamWriter(request.Response.OutputStream);
            await writer.WriteLineAsync("data: {\"type\":\"response.created\",\"response\":{\"id\":\"resp_provider_switch\",\"object\":\"response\",\"created_at\":1,\"model\":\"grok-3-mini\",\"status\":\"in_progress\",\"output\":[]}}");
            await writer.WriteLineAsync();
            await writer.WriteLineAsync("data: {\"type\":\"response.output_text.delta\",\"item_id\":\"msg_provider_switch\",\"output_index\":0,\"content_index\":0,\"delta\":\"done\"}");
            await writer.WriteLineAsync();
            await writer.WriteLineAsync("data: {\"type\":\"response.completed\",\"response\":{\"id\":\"resp_provider_switch\",\"object\":\"response\",\"created_at\":1,\"model\":\"grok-3-mini\",\"status\":\"completed\",\"output\":[]}}");
            await writer.WriteLineAsync();
            await writer.WriteLineAsync("data: [DONE]");
            await writer.WriteLineAsync();
            await writer.FlushAsync();
            request.Response.Close();
        });

        var model = new ModelDescriptor("grok-3-mini", "xai", 131_072, "fixture", true,
            Provider: "xai", Name: "Grok 3 Mini", MaxOutputTokens: 16_384,
            Input: ["text"], Api: "openai-responses");
        var provider = new ProviderProfile("xai", "xAI",
            new Uri($"http://127.0.0.1:{port}/v1"), true, false, null, null, [model], Api: "openai-responses");
        var selection = new ModelSelection(provider, model, "fixture-api-key", true, "fixture key");
        using var client = ProviderChatClientFactory.Create(selection);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        const string grammarDefinition = "start: WORD\n%import common.WORD\n%ignore \" \"";
        var policy = JsonSerializer.SerializeToElement(new
        {
            type = "grammar",
            variants = new { openai_lark = grammarDefinition }
        });
        var tool = Microsoft.Extensions.AI.AIFunctionFactory.Create((string value) => value,
            new Microsoft.Extensions.AI.AIFunctionFactoryOptions
            {
                Name = "constrained_echo",
                Description = "Echo a grammar-constrained value.",
                AdditionalProperties = new Dictionary<string, object?> { ["constrainedSampling"] = policy }
            });
        var call = new Microsoft.Extensions.AI.FunctionCallContent("call_provider_switch", "constrained_echo",
            new Dictionary<string, object?> { ["value"] = "hello" });
        Microsoft.Extensions.AI.ChatMessage[] history =
        [
            new(Microsoft.Extensions.AI.ChatRole.User, "Echo hello"),
            new(Microsoft.Extensions.AI.ChatRole.Assistant, [call]),
            new(Microsoft.Extensions.AI.ChatRole.Tool,
                [new Microsoft.Extensions.AI.FunctionResultContent(call.CallId, "echo:hello")]),
            new(Microsoft.Extensions.AI.ChatRole.User, "Say done")
        ];
        var options = new Microsoft.Extensions.AI.ChatOptions { Tools = [tool] };
        var output = new StringBuilder();
        await foreach (var update in client.GetStreamingResponseAsync(history, options, deadline.Token))
            if (update.Text is { } text) output.Append(text);
        await server.WaitAsync(deadline.Token);

        Assert.Equal("done", output.ToString());
        using var sent = JsonDocument.Parse(requestBody!);
        var declaration = Assert.Single(sent.RootElement.GetProperty("tools").EnumerateArray());
        Assert.Equal("function", declaration.GetProperty("type").GetString());
        Assert.Equal("constrained_echo", declaration.GetProperty("name").GetString());
        var input = sent.RootElement.GetProperty("input").EnumerateArray().ToArray();
        var replayedCall = Assert.Single(input, item => item.GetProperty("type").GetString() == "function_call");
        var replayedOutput = Assert.Single(input, item => item.GetProperty("type").GetString() == "function_call_output");
        Assert.Equal("call_provider_switch", replayedCall.GetProperty("call_id").GetString());
        Assert.Equal("constrained_echo", replayedCall.GetProperty("name").GetString());
        using var arguments = JsonDocument.Parse(replayedCall.GetProperty("arguments").GetString()!);
        Assert.Equal("hello", arguments.RootElement.GetProperty("value").GetString());
        Assert.Equal("call_provider_switch", replayedOutput.GetProperty("call_id").GetString());
    }

    [Fact]
    public async Task CodexResponsesToolLoopSendsTheToolResultAsCachedWebSocketInput()
    {
        using var listener = StartListener(out var port);
        string? firstRequest = null;
        string? secondRequest = null;
        var server = Task.Run(async () =>
        {
            var context = await listener.GetContextAsync();
            var accepted = await context.AcceptWebSocketAsync(null);
            using var socket = accepted.WebSocket;
            firstRequest = await ReceiveWebSocketMessageAsync(socket);
            var functionCall = new
            {
                id = "fc_echo",
                type = "function_call",
                status = "completed",
                call_id = "call_echo",
                name = "echo_ext",
                arguments = "{\"text\":\"ping\"}"
            };
            await SendWebSocketEventAsync(socket, new
            {
                type = "response.created",
                response = new { id = "resp_tool", @object = "response", created_at = 1, model = "gpt-5.5", status = "in_progress", output = Array.Empty<object>() }
            });
            await SendWebSocketEventAsync(socket, new
            {
                type = "response.output_item.added",
                output_index = 0,
                item = new { id = "fc_echo", type = "function_call", status = "in_progress", call_id = "call_echo", name = "echo_ext", arguments = "" }
            });
            await SendWebSocketEventAsync(socket, new
            {
                type = "response.function_call_arguments.done",
                output_index = 0,
                item_id = "fc_echo",
                arguments = "{\"text\":\"ping\"}"
            });
            await SendWebSocketEventAsync(socket, new { type = "response.output_item.done", output_index = 0, item = functionCall });
            await SendWebSocketEventAsync(socket, new
            {
                type = "response.completed",
                response = new
                {
                    id = "resp_tool",
                    @object = "response",
                    created_at = 1,
                    model = "gpt-5.5",
                    status = "completed",
                    output = new object[] { functionCall }
                }
            });

            secondRequest = await ReceiveWebSocketMessageAsync(socket);
            await SendWebSocketEventAsync(socket, new
            {
                type = "response.created",
                response = new { id = "resp_final", @object = "response", created_at = 2, model = "gpt-5.5", status = "in_progress", output = Array.Empty<object>() }
            });
            await SendWebSocketEventAsync(socket, new
            {
                type = "response.output_text.delta",
                item_id = "msg_final",
                output_index = 0,
                content_index = 0,
                delta = "done"
            });
            await SendWebSocketEventAsync(socket, new
            {
                type = "response.completed",
                response = new { id = "resp_final", @object = "response", created_at = 2, model = "gpt-5.5", status = "completed", output = Array.Empty<object>() }
            });
        });

        var model = new ModelDescriptor("gpt-5.5", "openai-codex", 272_000, "fixture", true,
            Provider: "openai-codex", Name: "GPT-5.5", MaxOutputTokens: 128_000,
            Input: ["text", "image"], Api: "openai-codex-responses");
        var provider = new ProviderProfile("openai-codex", "OpenAI Codex",
            new Uri($"http://127.0.0.1:{port}/backend-api"), true, false, null, null, [model], Api: "openai-codex-responses");
        var selection = new ModelSelection(provider, model, CreateToken("account-fixture"), true, "fixture token");
        using var client = ProviderChatClientFactory.Create(selection);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var echo = Microsoft.Extensions.AI.AIFunctionFactory.Create((string text) => "echo:" + text, name: "echo_ext");
        var agent = new PiAgent(client, new CodingTools(Path.GetTempPath()), extensionTools: [echo]);
        var session = await agent.CreateSessionAsync(deadline.Token);
        var output = new StringBuilder();
        await foreach (var update in agent.RunStreamingAsync("use echo", session, deadline.Token))
            if (update.Text is { } text) output.Append(text);
        await server.WaitAsync(deadline.Token);

        Assert.Equal("done", output.ToString());
        using var first = JsonDocument.Parse(firstRequest!);
        using var second = JsonDocument.Parse(secondRequest!);
        Assert.Equal("response.create", first.RootElement.GetProperty("type").GetString());
        Assert.Equal("resp_tool", second.RootElement.GetProperty("previous_response_id").GetString());
        var input = Assert.Single(second.RootElement.GetProperty("input").EnumerateArray());
        Assert.Equal("function_call_output", input.GetProperty("type").GetString());
        using var toolResult = JsonDocument.Parse(input.GetProperty("output").GetString()!);
        Assert.Equal("echo:ping", toolResult.RootElement.GetString());
    }

    [Fact]
    public async Task CodexResponsesWebSocketCancellationBeforeFirstEventPropagates()
    {
        using var listener = StartListener(out var port);
        var requestReceived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var server = Task.Run(async () =>
        {
            var context = await listener.GetContextAsync();
            var accepted = await context.AcceptWebSocketAsync(null);
            using var socket = accepted.WebSocket;
            _ = await ReceiveWebSocketMessageAsync(socket);
            requestReceived.TrySetResult();
            await Task.Delay(TimeSpan.FromMilliseconds(500));
        });

        var model = new ModelDescriptor("gpt-5.5", "openai-codex", 272_000, "fixture", true,
            Provider: "openai-codex", Name: "GPT-5.5", MaxOutputTokens: 128_000,
            Input: ["text", "image"], Api: "openai-codex-responses");
        var provider = new ProviderProfile("openai-codex", "OpenAI Codex",
            new Uri($"http://127.0.0.1:{port}/backend-api"), true, false, null, null, [model], Api: "openai-codex-responses");
        var selection = new ModelSelection(provider, model, CreateToken("account-fixture"), true, "fixture token");
        using var client = ProviderChatClientFactory.Create(selection);
        using var cancellation = new CancellationTokenSource();
        var request = ReadTextAsync(client, "Wait for response",
            new Microsoft.Extensions.AI.ChatOptions { ConversationId = "codex-cancel-fixture" }, cancellation.Token);
        await requestReceived.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => request);
        await server.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task CodexResponsesWebSocketSurfacesResponseFailure()
    {
        using var listener = StartListener(out var port);
        var server = Task.Run(async () =>
        {
            var context = await listener.GetContextAsync();
            var accepted = await context.AcceptWebSocketAsync(null);
            using var socket = accepted.WebSocket;
            _ = await ReceiveWebSocketMessageAsync(socket);
            await SendWebSocketEventAsync(socket, new
            {
                type = "response.failed",
                response = new
                {
                    id = "resp_failed",
                    status = "failed",
                    error = new { code = "invalid_prompt", message = "The requested prompt is invalid." }
                }
            });
        });

        var model = new ModelDescriptor("gpt-5.5", "openai-codex", 272_000, "fixture", true,
            Provider: "openai-codex", Name: "GPT-5.5", MaxOutputTokens: 128_000,
            Input: ["text", "image"], Api: "openai-codex-responses");
        var provider = new ProviderProfile("openai-codex", "OpenAI Codex",
            new Uri($"http://127.0.0.1:{port}/backend-api"), true, false, null, null, [model], Api: "openai-codex-responses");
        var selection = new ModelSelection(provider, model, CreateToken("account-fixture"), true, "fixture token");
        using var client = ProviderChatClientFactory.Create(selection);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        var error = await Record.ExceptionAsync(async () =>
        {
            await foreach (var _ in client.GetStreamingResponseAsync(
                [new Microsoft.Extensions.AI.ChatMessage(Microsoft.Extensions.AI.ChatRole.User, "bad request")],
                cancellationToken: deadline.Token))
            {
            }
        });
        await server.WaitAsync(deadline.Token);

        Assert.NotNull(error);
        Assert.Contains("invalid_prompt", error.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.Contains("requested prompt is invalid", error.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CodexResponsesWebSocketCancellationAfterOutputOpensFreshConnectionForNextTurn()
    {
        using var listener = StartListener(out var port);
        var firstDeltaSent = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstRequest = (string?)null;
        var secondRequest = (string?)null;
        var server = Task.Run(async () =>
        {
            var firstContext = await listener.GetContextAsync();
            var firstAccepted = await firstContext.AcceptWebSocketAsync(null);
            using (var firstSocket = firstAccepted.WebSocket)
            {
                firstRequest = await ReceiveWebSocketMessageAsync(firstSocket);
                await SendWebSocketEventAsync(firstSocket, new
                {
                    type = "response.created",
                    response = new { id = "resp_cancelled", @object = "response", created_at = 1, model = "gpt-5.5", status = "in_progress", output = Array.Empty<object>() }
                });
                await SendWebSocketEventAsync(firstSocket, new
                {
                    type = "response.output_text.delta",
                    item_id = "msg_cancelled",
                    output_index = 0,
                    content_index = 0,
                    delta = "partial"
                });
                firstDeltaSent.TrySetResult();
                await Task.Delay(TimeSpan.FromMilliseconds(300));
            }

            var secondContext = await listener.GetContextAsync();
            var secondAccepted = await secondContext.AcceptWebSocketAsync(null);
            using var secondSocket = secondAccepted.WebSocket;
            secondRequest = await ReceiveWebSocketMessageAsync(secondSocket);
            await SendWebSocketResponseAsync(secondSocket, "resp_after_cancel", "recovered");
        });

        var model = new ModelDescriptor("gpt-5.5", "openai-codex", 272_000, "fixture", true,
            Provider: "openai-codex", Name: "GPT-5.5", MaxOutputTokens: 128_000,
            Input: ["text", "image"], Api: "openai-codex-responses");
        var provider = new ProviderProfile("openai-codex", "OpenAI Codex",
            new Uri($"http://127.0.0.1:{port}/backend-api"), true, false, null, null, [model], Api: "openai-codex-responses");
        var selection = new ModelSelection(provider, model, CreateToken("account-fixture"), true, "fixture token");
        using var client = ProviderChatClientFactory.Create(selection);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var cancellation = new CancellationTokenSource();
        var options = new Microsoft.Extensions.AI.ChatOptions { ConversationId = "codex-cancel-after-output" };
        await using var updates = client.GetStreamingResponseAsync(
            [new Microsoft.Extensions.AI.ChatMessage(Microsoft.Extensions.AI.ChatRole.User, "first turn")], options,
            cancellation.Token).GetAsyncEnumerator(cancellation.Token);
        string? partial = null;
        while (await updates.MoveNextAsync())
        {
            if (updates.Current.Text is { Length: > 0 } text)
            {
                partial = text;
                break;
            }
        }

        await firstDeltaSent.Task.WaitAsync(deadline.Token);
        Assert.Equal("partial", partial);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await updates.MoveNextAsync());

        var recovered = await ReadTextAsync(client, "second turn", options, deadline.Token);
        await server.WaitAsync(deadline.Token);

        Assert.Equal("recovered", recovered);
        using var first = JsonDocument.Parse(firstRequest!);
        using var second = JsonDocument.Parse(secondRequest!);
        Assert.Equal("response.create", first.RootElement.GetProperty("type").GetString());
        Assert.Equal("response.create", second.RootElement.GetProperty("type").GetString());
        Assert.False(second.RootElement.TryGetProperty("previous_response_id", out _));
    }

    private static async Task<string> ReadTextAsync(Microsoft.Extensions.AI.IChatClient client, string prompt,
        Microsoft.Extensions.AI.ChatOptions options, CancellationToken cancellationToken)
        => await ReadMessagesAsync(client,
            [new Microsoft.Extensions.AI.ChatMessage(Microsoft.Extensions.AI.ChatRole.User, prompt)],
            options, cancellationToken);

    private static async Task<string> ReadMessagesAsync(Microsoft.Extensions.AI.IChatClient client,
        IEnumerable<Microsoft.Extensions.AI.ChatMessage> messages, Microsoft.Extensions.AI.ChatOptions options,
        CancellationToken cancellationToken)
    {
        var output = new StringBuilder();
        await foreach (var update in client.GetStreamingResponseAsync(messages, options, cancellationToken))
            if (update.Text is { } text) output.Append(text);
        return output.ToString();
    }

    private static async Task<string> ReceiveWebSocketMessageAsync(WebSocket socket)
    {
        using var buffer = new MemoryStream();
        var bytes = new byte[4096];
        WebSocketReceiveResult result;
        do
        {
            result = await socket.ReceiveAsync(bytes, CancellationToken.None);
            buffer.Write(bytes, 0, result.Count);
        }
        while (!result.EndOfMessage);
        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    private static async Task SendWebSocketResponseAsync(WebSocket socket, string responseId, string text)
    {
        await SendAsync(new
        {
            type = "response.created",
            response = new { id = responseId, @object = "response", created_at = 1, model = "gpt-5.5", status = "in_progress", output = Array.Empty<object>() }
        });
        await SendAsync(new
        {
            type = "response.output_text.delta",
            item_id = "item_codex",
            output_index = 0,
            content_index = 0,
            delta = text
        });
        await SendAsync(new
        {
            type = "response.completed",
            response = new
            {
                id = responseId,
                @object = "response",
                created_at = 1,
                model = "gpt-5.5",
                status = "completed",
                usage = new { input_tokens = 12, output_tokens = 3, total_tokens = 15 },
                output = new object[]
                {
                    new
                    {
                        id = "msg_" + responseId,
                        type = "message",
                        role = "assistant",
                        status = "completed",
                        content = new[] { new { type = "output_text", text, annotations = Array.Empty<object>() } }
                    }
                }
            }
        });

        async Task SendAsync(object value)
        {
            var bytes = JsonSerializer.SerializeToUtf8Bytes(value);
            await socket.SendAsync(bytes, WebSocketMessageType.Text, true, CancellationToken.None);
        }
    }

    private static async Task SendWebSocketErrorAsync(WebSocket socket, string code)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new
        {
            type = "response.failed",
            response = new
            {
                id = "resp_stale",
                status = "failed",
                error = new { code, message = "Cached response not found." }
            }
        });
        await socket.SendAsync(bytes, WebSocketMessageType.Text, true, CancellationToken.None);
    }

    private static async Task SendWebSocketEventAsync(WebSocket socket, object value)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value);
        await socket.SendAsync(bytes, WebSocketMessageType.Text, true, CancellationToken.None);
    }

    private static string CreateToken(string accountId)
    {
        static string Encode(byte[] value) => Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var header = Encode(Encoding.UTF8.GetBytes("{}"));
        var payload = Encode(JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, object>
        {
            ["https://api.openai.com/auth"] = new Dictionary<string, string> { ["chatgpt_account_id"] = accountId }
        }));
        return $"{header}.{payload}.fixture-signature";
    }

    private static HttpListener StartListener(out int port)
    {
        var (listener, selectedPort) = TestLoopbackListener.Start();
        port = selectedPort;
        return listener;
    }
}
