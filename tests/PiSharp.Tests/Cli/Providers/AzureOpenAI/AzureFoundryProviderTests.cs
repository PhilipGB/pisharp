using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;
using PiSharp.Cli;
using PiSharp.Runtime;

namespace PiSharp.Tests;

// The loopback wire assertions pair with the pinned Pi Azure source/tests at a37306d437c528c355357bc9123524de2bf67267.
public sealed class AzureFoundryProviderTests
{
    [Fact]
    public async Task AzureFoundryStreamsPinnedCatalogModelWithDeploymentBearerAuthAndV1Path()
    {
        using var listener = StartListener(out var port);
        string? requestPath = null;
        string? requestQuery = null;
        string? authorization = null;
        string? apiKeyHeader = null;
        string? requestBody = null;
        var server = Task.Run(async () =>
        {
            var request = await listener.GetContextAsync();
            requestPath = request.Request.Url?.AbsolutePath;
            requestQuery = request.Request.Url?.Query;
            authorization = request.Request.Headers["Authorization"];
            apiKeyHeader = request.Request.Headers["api-key"];
            using var reader = new StreamReader(request.Request.InputStream);
            requestBody = await reader.ReadToEndAsync();
            request.Response.ContentType = "text/event-stream";
            await using var writer = new StreamWriter(request.Response.OutputStream);
            await writer.WriteAsync("data: {\"id\":\"chat_foundry\",\"object\":\"chat.completion.chunk\",\"created\":1,\"model\":\"deployment-deepseek\",\"choices\":[{\"index\":0,\"delta\":{\"reasoning_content\":\"thinking\"},\"finish_reason\":null}]}\n\n");
            await writer.WriteAsync("data: {\"id\":\"chat_foundry\",\"object\":\"chat.completion.chunk\",\"created\":1,\"model\":\"deployment-deepseek\",\"choices\":[{\"index\":0,\"delta\":{\"role\":\"assistant\",\"content\":\"foundry response\"},\"finish_reason\":null}]}\n\n");
            await writer.WriteAsync("data: {\"id\":\"chat_foundry\",\"object\":\"chat.completion.chunk\",\"created\":1,\"model\":\"deployment-deepseek\",\"choices\":[{\"index\":0,\"delta\":{},\"finish_reason\":\"stop\"}]}\n\n");
            await writer.WriteAsync("data: {\"id\":\"chat_foundry\",\"object\":\"chat.completion.chunk\",\"created\":1,\"model\":\"deployment-deepseek\",\"choices\":[],\"usage\":{\"prompt_tokens\":10,\"completion_tokens\":3,\"total_tokens\":13,\"prompt_tokens_details\":{\"cached_tokens\":4},\"completion_tokens_details\":{\"reasoning_tokens\":2}}}\n\n");
            await writer.WriteAsync("data: [DONE]\n\n");
            await writer.FlushAsync();
            request.Response.Close();
        });

        var root = CreateRoot("foundry-catalog");
        try
        {
            var environment = new Dictionary<string, string>
            {
                ["AZURE_OPENAI_BASE_URL"] = $"http://127.0.0.1:{port}/openai/v1",
                ["AZURE_OPENAI_API_VERSION"] = "preview",
                ["AZURE_OPENAI_DEPLOYMENT_NAME_MAP"] = "deepseek-v4-pro=deployment-deepseek",
                ["AZURE_OPENAI_API_KEY"] = "azure-key",
                ["OPENAI_API_KEY"] = "must-not-cross-provider-boundary"
            };
            using var http = new HttpClient(new RejectUnexpectedRequestHandler());
            var runtime = await ProviderModelRuntime.CreateAsync(root, false,
                name => environment.GetValueOrDefault(name), http);
            var models = await runtime.ListModelsAsync("azure");
            var model = Assert.Single(models);
            var selection = await runtime.ResolveAsync("azure", model.Id);

            Assert.Equal("deepseek-v4-pro", model.Id);
            Assert.Equal("DeepSeek V4 Pro", model.Name);
            Assert.Equal("azure", model.Provider);
            Assert.Equal("openai-completions", model.Api);
            Assert.True(model.Reasoning);
            Assert.Equal("azure-key", selection.ApiKey);
            Assert.Equal("openai-completions", ProviderChatClientFactory.ResolveProtocol(selection));
            Assert.Equal(new Uri($"http://127.0.0.1:{port}/openai/v1"), selection.Connection.Endpoint);

            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var output = new StringBuilder();
            var updates = new List<ChatResponseUpdate>();
            await foreach (var update in ProviderChatClientFactory.Create(selection, new ProviderRetrySettings(0, 10_000))
                .GetStreamingResponseAsync(
                    [new ChatMessage(ChatRole.System, "first"), new ChatMessage(new ChatRole("developer"), "second"),
                        new ChatMessage(ChatRole.User, "hello")],
                    new ChatOptions { Reasoning = ThinkingLevels.ToOptions("high") }, deadline.Token))
            {
                updates.Add(update);
                if (update.Text is { } text) output.Append(text);
            }
            await server.WaitAsync(deadline.Token);

            Assert.Equal("foundry response", output.ToString());
            Assert.Contains(updates.SelectMany(update => update.Contents).OfType<TextReasoningContent>(),
                item => item.Text == "thinking");
            var usage = Assert.Single(updates.SelectMany(update => update.Contents).OfType<UsageContent>()).Details;
            Assert.Equal(10, usage.InputTokenCount);
            Assert.Equal(3, usage.OutputTokenCount);
            Assert.Equal(4, usage.CachedInputTokenCount);
            Assert.Equal(2, usage.ReasoningTokenCount);
            Assert.Equal("/openai/v1/chat/completions", requestPath);
            Assert.Equal(string.Empty, requestQuery);
            Assert.Equal("Bearer azure-key", authorization);
            Assert.Null(apiKeyHeader);
            using var body = JsonDocument.Parse(requestBody!);
            Assert.Equal("deployment-deepseek", body.RootElement.GetProperty("model").GetString());
            Assert.Equal("high", body.RootElement.GetProperty("reasoning_effort").GetString());
            Assert.False(body.RootElement.TryGetProperty("api-version", out _));
            var messages = body.RootElement.GetProperty("messages");
            Assert.Equal(["system", "system", "user"], messages.EnumerateArray()
                .Select(message => message.GetProperty("role").GetString()!).ToArray());
            Assert.Equal(JsonValueKind.String, messages[2].GetProperty("content").ValueKind);
            Assert.DoesNotContain("prompt_cache", requestBody, StringComparison.Ordinal);
        }
        finally
        {
            listener.Close();
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task AzureFoundryToolContinuationMapsDeploymentAndReplaysReasoningContent()
    {
        using var listener = StartListener(out var port);
        var requests = new List<string>();
        var server = Task.Run(async () =>
        {
            for (var index = 0; index < 2; index++)
            {
                var request = await listener.GetContextAsync();
                Assert.Equal("/openai/v1/chat/completions", request.Request.Url?.AbsolutePath);
                Assert.Equal("Bearer azure-key", request.Request.Headers["Authorization"]);
                Assert.Null(request.Request.Headers["api-key"]);
                using var reader = new StreamReader(request.Request.InputStream);
                requests.Add(await reader.ReadToEndAsync());
                request.Response.ContentType = "text/event-stream";
                await using var writer = new StreamWriter(request.Response.OutputStream);
                if (index == 0)
                {
                    await writer.WriteAsync("data: {\"id\":\"chat_tool\",\"object\":\"chat.completion.chunk\",\"created\":1,\"model\":\"deployment-deepseek\",\"choices\":[{\"index\":0,\"delta\":{\"role\":\"assistant\",\"tool_calls\":[{\"index\":0,\"id\":\"call_1\",\"type\":\"function\",\"function\":{\"name\":\"echo_ext\",\"arguments\":\"{\\\"text\\\":\\\"ping\\\"}\"}}]},\"finish_reason\":null}]}\n\n");
                    await writer.WriteAsync("data: {\"id\":\"chat_tool\",\"object\":\"chat.completion.chunk\",\"created\":1,\"model\":\"deployment-deepseek\",\"choices\":[{\"index\":0,\"delta\":{},\"finish_reason\":\"tool_calls\"}]}\n\n");
                }
                else
                {
                    await writer.WriteAsync("data: {\"id\":\"chat_done\",\"object\":\"chat.completion.chunk\",\"created\":1,\"model\":\"deployment-deepseek\",\"choices\":[{\"index\":0,\"delta\":{\"role\":\"assistant\",\"content\":\"done\"},\"finish_reason\":null}]}\n\n");
                    await writer.WriteAsync("data: {\"id\":\"chat_done\",\"object\":\"chat.completion.chunk\",\"created\":1,\"model\":\"deployment-deepseek\",\"choices\":[{\"index\":0,\"delta\":{},\"finish_reason\":\"stop\"}]}\n\n");
                }
                await writer.WriteAsync("data: [DONE]\n\n");
                await writer.FlushAsync();
                request.Response.Close();
            }
        });
        var root = CreateRoot("foundry-tools");
        try
        {
            var selection = await ResolveFoundrySelectionAsync(root, port);
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var echo = AIFunctionFactory.Create((string text) => "echo:" + text, name: "echo_ext");
            var agent = new PiAgent(ProviderChatClientFactory.Create(selection, new ProviderRetrySettings(0, 10_000)),
                new CodingTools(root), extensionTools: [echo]);
            var session = await agent.CreateSessionAsync(deadline.Token);
            var output = new StringBuilder();
            await foreach (var update in agent.RunStreamingAsync("use echo", session, deadline.Token))
                if (update.Text is { } text) output.Append(text);
            await server.WaitAsync(deadline.Token);

            Assert.Equal("done", output.ToString());
            Assert.Equal(2, requests.Count);
            using var continuation = JsonDocument.Parse(requests[1]);
            var messages = continuation.RootElement.GetProperty("messages");
            Assert.Contains(messages.EnumerateArray(), message => message.GetProperty("role").GetString() == "tool" &&
                message.GetProperty("content").GetString() == "\"echo:ping\"");
            Assert.Contains(messages.EnumerateArray(), message => message.GetProperty("role").GetString() == "assistant" &&
                message.TryGetProperty("reasoning_content", out var reasoning) && reasoning.GetString() == string.Empty);
            Assert.All(requests, requestBody =>
            {
                using var body = JsonDocument.Parse(requestBody);
                Assert.Equal("deployment-deepseek", body.RootElement.GetProperty("model").GetString());
            });
        }
        finally
        {
            listener.Close();
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task AzureFoundrySurfacesLocalEndpointErrorsWithAzureCredential()
    {
        using var listener = StartListener(out var port);
        string? authorization = null;
        string? requestPath = null;
        string? requestBody = null;
        var server = Task.Run(async () =>
        {
            var request = await listener.GetContextAsync();
            authorization = request.Request.Headers["Authorization"];
            requestPath = request.Request.Url?.AbsolutePath;
            using var reader = new StreamReader(request.Request.InputStream);
            requestBody = await reader.ReadToEndAsync();
            request.Response.StatusCode = (int)HttpStatusCode.Unauthorized;
            request.Response.ContentType = "application/json";
            await using var writer = new StreamWriter(request.Response.OutputStream);
            await writer.WriteAsync("{\"error\":{\"message\":\"invalid API key\",\"type\":\"invalid_request_error\",\"code\":\"invalid_api_key\"}}");
            await writer.FlushAsync();
            request.Response.Close();
        });
        var root = CreateRoot("foundry-error");
        try
        {
            var selection = await ResolveFoundrySelectionAsync(root, port);
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var error = await Record.ExceptionAsync(async () => await ProviderChatClientFactory.Create(selection,
                new ProviderRetrySettings(0, 10_000)).GetResponseAsync([new ChatMessage(ChatRole.User, "hello")],
                cancellationToken: deadline.Token));
            await server.WaitAsync(deadline.Token);

            Assert.NotNull(error);
            Assert.Contains("invalid_api_key", error.ToString(), StringComparison.Ordinal);
            Assert.Equal("Bearer azure-key", authorization);
            Assert.Equal("/openai/v1/chat/completions", requestPath);
            using var body = JsonDocument.Parse(requestBody!);
            Assert.Equal("deployment-deepseek", body.RootElement.GetProperty("model").GetString());
        }
        finally
        {
            listener.Close();
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task AzureFoundryRequiresAnEndpointBeforeCreatingAClient()
    {
        var root = CreateRoot("foundry-endpoint-required");
        try
        {
            using var http = new HttpClient(new RejectUnexpectedRequestHandler());
            var runtime = await ProviderModelRuntime.CreateAsync(root, false,
                name => name == "AZURE_OPENAI_API_KEY" ? "azure-key" : null, http);
            var selection = await runtime.ResolveAsync("azure", "deepseek-v4-pro");

            var error = Assert.Throws<InvalidOperationException>(() => ProviderChatClientFactory.Create(selection));
            Assert.Contains("AZURE_OPENAI_BASE_URL or AZURE_OPENAI_RESOURCE_NAME", error.Message, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    private static async Task<ModelSelection> ResolveFoundrySelectionAsync(string root, int port)
    {
        var environment = new Dictionary<string, string>
        {
            ["AZURE_OPENAI_BASE_URL"] = $"http://127.0.0.1:{port}/openai/v1",
            ["AZURE_OPENAI_API_KEY"] = "azure-key",
            ["AZURE_OPENAI_DEPLOYMENT_NAME_MAP"] = "deepseek-v4-pro=deployment-deepseek"
        };
        using var http = new HttpClient(new RejectUnexpectedRequestHandler());
        var runtime = await ProviderModelRuntime.CreateAsync(root, false,
            name => environment.GetValueOrDefault(name), http);
        return await runtime.ResolveAsync("azure", "deepseek-v4-pro");
    }

    private static string CreateRoot(string category)
    {
        var root = Path.Combine(Path.GetTempPath(), "pisharp-azure-foundry-" + category + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
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

    private sealed class RejectUnexpectedRequestHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new InvalidOperationException($"Unexpected model discovery request: {request.Method} {request.RequestUri}");
    }
}
