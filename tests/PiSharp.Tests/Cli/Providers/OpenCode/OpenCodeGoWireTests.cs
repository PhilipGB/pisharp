using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;
using PiSharp.Cli;
using PiSharp.Runtime;
using PiSharp.Runtime.Providers;

namespace PiSharp.Tests;

public sealed class OpenCodeGoWireTests
{
    private const string FixtureKey = "opencode-go-wire-fixture-key";

    [Theory]
    [InlineData("anthropic-messages", "claude-haiku-5-5", "/inference/go", "/inference/go/v1/messages")]
    [InlineData("google-generative-ai", "gemini-4-argon", "/inference/go/v1",
        "/inference/go/v1/models/gemini-4-argon:streamGenerateContent")]
    [InlineData("openai-completions", "kimi-k3", "/inference/go/v1", "/inference/go/v1/chat/completions")]
    [InlineData("openai-responses", "gpt-6-luna", "/inference/go/v1", "/inference/go/v1/responses")]
    public async Task OpenCodeGoRoutesEveryProtocolThroughItsNativeEndpoint(string protocol, string modelId,
        string basePath, string expectedPath)
    {
        var (listener, port) = TestLoopbackListener.Start();
        using var listenerOwner = listener;
        var paths = new List<string>();
        var authHeaders = new List<(string? ApiKey, string? Bearer, string? GoogleKey, string? Session)>();
        var bodies = new List<string>();
        var server = Task.Run(async () =>
        {
            for (var index = 0; index < 2; index++)
            {
                var request = await listener.GetContextAsync();
                paths.Add(request.Request.Url?.AbsolutePath ?? "");
                authHeaders.Add((request.Request.Headers["x-api-key"],
                    request.Request.Headers["Authorization"], request.Request.Headers["x-goog-api-key"],
                    request.Request.Headers["x-opencode-session"]));
                using var reader = new StreamReader(request.Request.InputStream);
                bodies.Add(await reader.ReadToEndAsync());
                await WriteSseAsync(request.Response, protocol, index == 0 ? ToolCallBody(protocol) : TextBody(protocol, "done"));
            }
        });

        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var selection = Selection(protocol, port, basePath, modelId);
        var echo = AIFunctionFactory.Create((string text) => "echo:" + text, name: "echo_ext");
        var agent = new PiAgent(ProviderChatClientFactory.Create(selection, new ProviderRetrySettings { MaxRetries = 0 }),
            new CodingTools(Path.GetTempPath()), extensionTools: [echo]);
        var session = await agent.CreateSessionAsync(deadline.Token);
        var answer = new StringBuilder();
        await foreach (var update in agent.RunStreamingAsync("use echo", session, deadline.Token))
            answer.Append(update.Text);
        await server.WaitAsync(deadline.Token);

        Assert.Equal("done", answer.ToString());
        Assert.Equal([expectedPath, expectedPath], paths);
        Assert.All(authHeaders, headers => Assert.Equal(protocol switch
        {
            "anthropic-messages" => FixtureKey,
            _ => null
        }, headers.ApiKey));
        Assert.All(authHeaders, headers => Assert.Equal(protocol == "google-generative-ai" ? FixtureKey : null,
            headers.GoogleKey));
        Assert.All(authHeaders, headers => Assert.Equal(protocol is "openai-completions" or "openai-responses"
            ? "Bearer " + FixtureKey : null, headers.Bearer));
        Assert.All(authHeaders, headers => Assert.Null(headers.Session));

        AssertRequest(protocol, modelId, bodies[0]);
        Assert.Contains("echo:ping", bodies[1], StringComparison.Ordinal);
        Assert.Contains(protocol switch
        {
            "anthropic-messages" => "toolu_1",
            "google-generative-ai" => "call/1",
            "openai-completions" => "call-1",
            _ => "call_1"
        }, bodies[1], StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("anthropic-messages", "claude-haiku-5-5", "/inference/go", "/inference/go/v1/messages")]
    [InlineData("google-generative-ai", "gemini-4-argon", "/inference/go/v1",
        "/inference/go/v1/models/gemini-4-argon:streamGenerateContent")]
    [InlineData("openai-completions", "kimi-k3", "/inference/go/v1", "/inference/go/v1/chat/completions")]
    [InlineData("openai-responses", "gpt-6-luna", "/inference/go/v1", "/inference/go/v1/responses")]
    public async Task OpenCodeGoSendsOnlyTheCallerOrConversationSessionHeader(string protocol, string modelId,
        string basePath, string expectedPath)
    {
        var (listener, port) = TestLoopbackListener.Start();
        using var listenerOwner = listener;
        var sessions = new List<string?>();
        var server = Task.Run(async () =>
        {
            for (var index = 0; index < 4; index++)
            {
                var request = await listener.GetContextAsync();
                Assert.Equal(expectedPath, request.Request.Url?.AbsolutePath);
                sessions.Add(request.Request.Headers["x-opencode-session"]);
                await WriteSseAsync(request.Response, protocol, TextBody(protocol, "ok"));
            }
        });

        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var client = ProviderChatClientFactory.Create(Selection(protocol, port, basePath, modelId),
            new ProviderRetrySettings { MaxRetries = 0 });
        Assert.Equal("ok", await ReadTextAsync(client, SessionOptions("conversation-1"), deadline.Token));
        Assert.Equal("ok", await ReadTextAsync(client, null, deadline.Token));
        Assert.Equal("ok", await ReadTextAsync(client, SessionOptions("conversation-2"), deadline.Token));

        var wire = new ProviderWireTestSupport(openCodeSessionHeader: "caller-supplied");
        using (wire.Enter(deadline.Token))
            Assert.Equal("ok", await ReadTextAsync(client, SessionOptions("conversation-3"), deadline.Token));

        await server.WaitAsync(deadline.Token);
        Assert.Equal(["conversation-1", null, "conversation-2", "caller-supplied"], sessions);
    }

    [Fact]
    public async Task SessionHeaderIsNotSentToNonOpenCodeProviders()
    {
        var (listener, port) = TestLoopbackListener.Start();
        using var listenerOwner = listener;
        string? session = "unset";
        var server = Task.Run(async () =>
        {
            var request = await listener.GetContextAsync();
            session = request.Request.Headers["x-opencode-session"];
            await WriteSseAsync(request.Response, "anthropic-messages",
                TextBody("anthropic-messages", "ok"));
        });
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var model = new ModelDescriptor("claude-sonnet-4-6", "anthropic", 1_000_000, "pinned",
            Provider: "anthropic", Api: "anthropic-messages",
            BaseUrl: $"http://127.0.0.1:{port}");
        var provider = new ProviderProfile("anthropic", "Anthropic", new Uri($"http://127.0.0.1:{port}"),
            true, false, "ANTHROPIC_API_KEY", null, [model]);
        using var client = ProviderChatClientFactory.Create(
            new ModelSelection(provider, model, FixtureKey, true, "fixture"));
        Assert.Equal("ok", await ReadTextAsync(client, SessionOptions("conversation-1"), deadline.Token));
        await server.WaitAsync(deadline.Token);
        Assert.Null(session);
    }

    [Theory]
    [InlineData("anthropic-messages", "claude-haiku-5-5", "/inference/go", "/inference/go/v1/messages")]
    [InlineData("google-generative-ai", "gemini-4-argon", "/inference/go/v1",
        "/inference/go/v1/models/gemini-4-argon:streamGenerateContent")]
    [InlineData("openai-completions", "kimi-k3", "/inference/go/v1", "/inference/go/v1/chat/completions")]
    [InlineData("openai-responses", "gpt-6-luna", "/inference/go/v1", "/inference/go/v1/responses")]
    public async Task OpenCodeGoParsesUsageAndSendsThinkingControls(string protocol, string modelId,
        string basePath, string expectedPath)
    {
        var (listener, port) = TestLoopbackListener.Start();
        using var listenerOwner = listener;
        string? requestBody = null;
        var server = Task.Run(async () =>
        {
            var request = await listener.GetContextAsync();
            Assert.Equal(expectedPath, request.Request.Url?.AbsolutePath);
            using var reader = new StreamReader(request.Request.InputStream);
            requestBody = await reader.ReadToEndAsync();
            await WriteSseAsync(request.Response, protocol, TextBody(protocol, "ok"));
        });

        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var client = ProviderChatClientFactory.Create(Selection(protocol, port, basePath, modelId),
            new ProviderRetrySettings { MaxRetries = 0 });
        var options = new ChatOptions { Reasoning = ThinkingLevels.ToOptions("high") };
        var updates = new List<ChatResponseUpdate>();
        await foreach (var update in client.GetStreamingResponseAsync(
            [new ChatMessage(ChatRole.User, "hello")], options, deadline.Token))
            updates.Add(update);
        await server.WaitAsync(deadline.Token);

        Assert.Equal("ok", string.Concat(updates.Select(update => update.Text)));
        var usage = Assert.Single(updates.SelectMany(update => update.Contents).OfType<UsageContent>()).Details;
        Assert.Equal(10, usage.InputTokenCount);
        Assert.Equal(5, usage.OutputTokenCount);
        Assert.Equal(15, usage.TotalTokenCount);

        using var body = JsonDocument.Parse(requestBody!);
        var root = body.RootElement;
        switch (protocol)
        {
            case "anthropic-messages":
                Assert.True(root.TryGetProperty("thinking", out _) || root.TryGetProperty("output_config", out _));
                break;
            case "google-generative-ai":
                Assert.True(root.GetProperty("generationConfig").TryGetProperty("thinkingConfig", out _));
                break;
            case "openai-completions":
                Assert.Equal("high", root.GetProperty("reasoning_effort").GetString());
                break;
            default:
                Assert.Equal("high", root.GetProperty("reasoning").GetProperty("effort").GetString());
                break;
        }
    }

    [Theory]
    [InlineData("anthropic-messages", "claude-haiku-5-5", "/inference/go")]
    [InlineData("google-generative-ai", "gemini-4-argon", "/inference/go/v1")]
    [InlineData("openai-completions", "kimi-k3", "/inference/go/v1")]
    [InlineData("openai-responses", "gpt-6-luna", "/inference/go/v1")]
    public async Task OpenCodeGoPropagatesCancellationAndProviderErrors(string protocol, string modelId,
        string basePath)
    {
        {
            var (listener, port) = TestLoopbackListener.Start();
            using var listenerOwner = listener;
            var received = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var serverDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            var server = Task.Run(async () =>
            {
                var request = await listener.GetContextAsync();
                received.TrySetResult();
                try { await Task.Delay(Timeout.Infinite, serverDeadline.Token); }
                catch (OperationCanceledException) { }
            });
            using var cancel = new CancellationTokenSource();
            using var client = ProviderChatClientFactory.Create(Selection(protocol, port, basePath, modelId),
                new ProviderRetrySettings { MaxRetries = 0 });
            var running = ReadTextAsync(client, null, cancel.Token);
            await received.Task.WaitAsync(serverDeadline.Token);
            cancel.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running);
            serverDeadline.Cancel();
            await server;
        }

        {
            var (listener, port) = TestLoopbackListener.Start();
            using var listenerOwner = listener;
            using var serverDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            var server = Task.Run(async () =>
            {
                var request = await listener.GetContextAsync();
                request.Response.StatusCode = 401;
                request.Response.ContentType = "application/json";
                await using var writer = new StreamWriter(request.Response.OutputStream);
                await writer.WriteAsync("""{"error":{"message":"invalid credential"}}""");
                await writer.FlushAsync();
                request.Response.Close();
            });
            using var client = ProviderChatClientFactory.Create(Selection(protocol, port, basePath, modelId),
                new ProviderRetrySettings { MaxRetries = 0 });
            var error = await Assert.ThrowsAnyAsync<Exception>(async () =>
                await ReadTextAsync(client, null, serverDeadline.Token));
            await server.WaitAsync(serverDeadline.Token);
            Assert.DoesNotContain(FixtureKey, error.ToString(), StringComparison.Ordinal);
            Assert.True(error.ToString().Contains("401", StringComparison.Ordinal) ||
                error.ToString().Contains("Unauthorized", StringComparison.Ordinal) ||
                error.ToString().Contains("invalid credential", StringComparison.Ordinal), error.ToString());
        }
    }

    private static ModelSelection Selection(string protocol, int port, string basePath, string modelId)
    {
        var model = new ModelDescriptor(modelId, "opencode-go", 1_000_000, "pinned catalogue", true,
            Provider: "opencode-go", Name: modelId, MaxOutputTokens: 64_000, Input: ["text", "image"],
            Api: protocol, BaseUrl: $"http://127.0.0.1:{port}{basePath}");
        var provider = new ProviderProfile("opencode-go", "OpenCode Go",
            new Uri($"http://127.0.0.1:{port}/inference/go"), true, false, "OPENCODE_API_KEY", null, [model]);
        return new ModelSelection(provider, model, FixtureKey, true, "OPENCODE_API_KEY");
    }

    private static ChatOptions SessionOptions(string sessionId)
    {
        var options = new ChatOptions();
        options.AdditionalProperties = new AdditionalPropertiesDictionary
        {
            [ProviderSessionMetadata.ConversationSessionIdProperty] = sessionId
        };
        return options;
    }

    private static async Task<string> ReadTextAsync(IChatClient client, ChatOptions? options,
        CancellationToken cancellationToken)
    {
        var text = new StringBuilder();
        await foreach (var update in client.GetStreamingResponseAsync(
            [new ChatMessage(ChatRole.User, "hello")], options, cancellationToken))
            text.Append(update.Text);
        return text.ToString();
    }

    private static void AssertRequest(string protocol, string modelId, string body)
    {
        using var document = JsonDocument.Parse(body);
        var root = document.RootElement;
        if (protocol != "google-generative-ai")
            Assert.Contains(modelId, body, StringComparison.Ordinal);
        switch (protocol)
        {
            case "anthropic-messages":
                Assert.False(string.IsNullOrWhiteSpace(root.GetProperty("system")[0].GetProperty("text").GetString()));
                Assert.Contains(root.GetProperty("messages").EnumerateArray(),
                    message => message.GetProperty("role").GetString() == "user" &&
                        message.GetProperty("content")[0].GetProperty("text").GetString() == "use echo");
                Assert.Contains(root.GetProperty("tools").EnumerateArray(), tool =>
                    tool.GetProperty("name").GetString() == "echo_ext");
                break;
            case "google-generative-ai":
                Assert.False(string.IsNullOrWhiteSpace(root.GetProperty("systemInstruction")
                    .GetProperty("parts")[0].GetProperty("text").GetString()));
                Assert.Contains(root.GetProperty("contents").EnumerateArray(), content =>
                    content.GetProperty("role").GetString() == "user" &&
                    content.GetProperty("parts")[0].GetProperty("text").GetString() == "use echo");
                Assert.Contains(root.GetProperty("tools")[0].GetProperty("functionDeclarations")
                    .EnumerateArray(), tool => tool.GetProperty("name").GetString() == "echo_ext");
                break;
            case "openai-completions":
                Assert.Contains(root.GetProperty("messages").EnumerateArray(),
                    message => message.GetProperty("role").GetString() is "system" or "developer");
                Assert.Contains(root.GetProperty("messages").EnumerateArray(), message =>
                    message.GetProperty("role").GetString() == "user" &&
                    message.GetProperty("content")[0].GetProperty("text").GetString() == "use echo");
                Assert.Contains(root.GetProperty("tools").EnumerateArray(), tool =>
                    tool.GetProperty("function").GetProperty("name").GetString() == "echo_ext");
                break;
            default:
                Assert.False(string.IsNullOrWhiteSpace(root.GetProperty("instructions").GetString()));
                Assert.Contains(root.GetProperty("input").EnumerateArray(), item =>
                    item.GetProperty("role").GetString() == "user" &&
                    item.GetProperty("content")[0].GetProperty("text").GetString() == "use echo");
                Assert.Contains(root.GetProperty("tools").EnumerateArray(), tool =>
                    tool.GetProperty("name").GetString() == "echo_ext");
                break;
        }
    }

    private static string TextBody(string protocol, string text) => protocol switch
    {
        "anthropic-messages" => """
            event: message_start
            data: {"type":"message_start","message":{"id":"msg_1","type":"message","role":"assistant","model":"fixture","content":[],"stop_reason":null,"stop_sequence":null,"usage":{"input_tokens":10,"output_tokens":0}}}

            event: content_block_start
            data: {"type":"content_block_start","index":0,"content_block":{"type":"text","text":""}}

            event: content_block_delta
            data: {"type":"content_block_delta","index":0,"delta":{"type":"text_delta","text":"__TEXT__"}}

            event: content_block_stop
            data: {"type":"content_block_stop","index":0}

            event: message_delta
            data: {"type":"message_delta","delta":{"stop_reason":"end_turn","stop_sequence":null},"usage":{"output_tokens":5}}

            event: message_stop
            data: {"type":"message_stop"}

            """.Replace("__TEXT__", text, StringComparison.Ordinal),
        "google-generative-ai" => """
            data: {"responseId":"google-resp-1","candidates":[{"content":{"role":"model","parts":[{"text":"__TEXT__"}]},"finishReason":"STOP"}],"usageMetadata":{"promptTokenCount":10,"candidatesTokenCount":5,"totalTokenCount":15}}

            """.Replace("__TEXT__", text, StringComparison.Ordinal),
        "openai-completions" => """
            data: {"id":"chatcmpl_open_code","object":"chat.completion.chunk","created":1,"model":"fixture","choices":[{"index":0,"delta":{"role":"assistant","content":"__TEXT__"},"finish_reason":null}]}

            data: {"id":"chatcmpl_open_code","object":"chat.completion.chunk","created":1,"model":"fixture","choices":[{"index":0,"delta":{},"finish_reason":"stop"}],"usage":{"prompt_tokens":10,"completion_tokens":5,"total_tokens":15}}

            data: [DONE]

            """.Replace("__TEXT__", text, StringComparison.Ordinal),
        _ => """
            data: {"type":"response.created","response":{"id":"resp_1","object":"response","created_at":1,"model":"fixture","status":"in_progress","output":[]}}

            data: {"type":"response.output_text.delta","item_id":"msg_1","output_index":0,"content_index":0,"delta":"__TEXT__"}

            data: {"type":"response.completed","response":{"id":"resp_1","object":"response","created_at":1,"model":"fixture","status":"completed","output":[],"usage":{"input_tokens":10,"output_tokens":5,"total_tokens":15}}}

            data: [DONE]

            """.Replace("__TEXT__", text, StringComparison.Ordinal)
    };

    private static string ToolCallBody(string protocol) => protocol switch
    {
        "anthropic-messages" => """
            event: message_start
            data: {"type":"message_start","message":{"id":"msg_tool","type":"message","role":"assistant","model":"fixture","content":[],"stop_reason":null,"stop_sequence":null,"usage":{"input_tokens":10,"output_tokens":0}}}

            event: content_block_start
            data: {"type":"content_block_start","index":0,"content_block":{"type":"tool_use","id":"toolu_1","name":"echo_ext","input":{}}}

            event: content_block_delta
            data: {"type":"content_block_delta","index":0,"delta":{"type":"input_json_delta","partial_json":"{\"text\":\"ping\"}"}}

            event: content_block_stop
            data: {"type":"content_block_stop","index":0}

            event: message_delta
            data: {"type":"message_delta","delta":{"stop_reason":"tool_use","stop_sequence":null},"usage":{"output_tokens":5}}

            event: message_stop
            data: {"type":"message_stop"}

            """,
        "google-generative-ai" => """
            data: {"responseId":"google-tool-1","candidates":[{"content":{"role":"model","parts":[{"functionCall":{"id":"call/1","name":"echo_ext","args":{"text":"ping"}}}]},"finishReason":"STOP"}],"usageMetadata":{"promptTokenCount":10,"candidatesTokenCount":5,"totalTokenCount":15}}

            """,
        "openai-completions" => """
            data: {"id":"chatcmpl_tool","object":"chat.completion.chunk","created":1,"model":"fixture","choices":[{"index":0,"delta":{"role":"assistant","tool_calls":[{"index":0,"id":"call-1","type":"function","function":{"name":"echo_ext","arguments":"{\"text\":\"ping\"}"}}]},"finish_reason":null}]}

            data: {"id":"chatcmpl_tool","object":"chat.completion.chunk","created":1,"model":"fixture","choices":[{"index":0,"delta":{},"finish_reason":"tool_calls"}],"usage":{"prompt_tokens":10,"completion_tokens":5,"total_tokens":15}}

            data: [DONE]

            """,
        _ => """
            data: {"type":"response.created","response":{"id":"resp_tool","object":"response","created_at":1,"model":"fixture","status":"in_progress","output":[]}}

            data: {"type":"response.output_item.added","output_index":0,"item":{"id":"fc_1","type":"function_call","call_id":"call_1","name":"echo_ext","arguments":""}}

            data: {"type":"response.function_call_arguments.done","output_index":0,"item_id":"fc_1","arguments":"{\"text\":\"ping\"}"}

            data: {"type":"response.output_item.done","output_index":0,"item":{"id":"fc_1","type":"function_call","call_id":"call_1","name":"echo_ext","arguments":"{\"text\":\"ping\"}"}}

            data: {"type":"response.completed","response":{"id":"resp_tool","object":"response","created_at":1,"model":"fixture","status":"completed","output":[],"usage":{"input_tokens":10,"output_tokens":5,"total_tokens":15}}}

            data: [DONE]

            """
    };

    private static async Task WriteSseAsync(HttpListenerResponse response, string protocol, string body)
    {
        response.ContentType = "text/event-stream";
        await using var writer = new StreamWriter(response.OutputStream);
        await writer.WriteAsync(body);
        await writer.FlushAsync();
        response.Close();
    }
}
