using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using PiSharp.Cli;
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
    public async Task CodexResponsesUsesAccountScopedHeadersSessionAffinityAndRealResponsesSseClient()
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
        var server = Task.Run(async () =>
        {
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
        var selection = new ModelSelection(provider, model, token, true, "fixture token");
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var output = new StringBuilder();
        await foreach (var update in ProviderChatClientFactory.Create(selection).GetStreamingResponseAsync(
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
        Assert.Equal("/backend-api/codex/responses", requestPath);
        Assert.Equal($"Bearer {token}", authorization);
        Assert.Equal("account-fixture", accountId);
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
