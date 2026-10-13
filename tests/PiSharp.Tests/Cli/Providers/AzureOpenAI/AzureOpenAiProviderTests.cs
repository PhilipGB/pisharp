using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Microsoft.Extensions.AI;
using PiSharp.Cli;
using PiSharp.Runtime;
using PiSharp.Runtime.Providers;

namespace PiSharp.Tests;

public sealed class AzureOpenAiProviderTests
{
    [Theory]
    [InlineData("https://marc-quicktests-resource.cognitiveservices.azure.com", "https://marc-quicktests-resource.cognitiveservices.azure.com/openai/v1")]
    [InlineData("https://my-resource.openai.azure.com/openai", "https://my-resource.openai.azure.com/openai/v1")]
    [InlineData("https://my-resource.ai.azure.com/openai/v1/responses?api-version=old", "https://my-resource.ai.azure.com/openai/v1")]
    [InlineData("https://my-resource.openai.azure.com/openai/v1", "https://my-resource.openai.azure.com/openai/v1")]
    [InlineData("https://my-proxy.example.com/v1?custom=true", "https://my-proxy.example.com/v1?custom=true")]
    public void AzureEndpointNormalizationMatchesCurrentPi(string configured, string expected)
    {
        var endpoint = AzureOpenAiEndpoint.FromEnvironment(name => name == "AZURE_OPENAI_BASE_URL" ? configured : null);
        Assert.Equal(expected, endpoint.ToString().TrimEnd('/'));
    }

    [Fact]
    public async Task AzureResponsesUsesItsCredentialAndStaticCurrentPiCatalog()
    {
        var root = Path.Combine(Path.GetTempPath(), "pisharp-azure-catalog-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            using var http = new HttpClient(new RejectUnexpectedRequestHandler());
            var environment = new Dictionary<string, string>
            {
                ["AZURE_OPENAI_RESOURCE_NAME"] = "sample-resource",
                ["AZURE_OPENAI_API_KEY"] = "azure-key",
                ["OPENAI_API_KEY"] = "must-not-cross-provider-boundary"
            };
            var runtime = await ProviderModelRuntime.CreateAsync(root, false,
                name => environment.GetValueOrDefault(name), http);

            var models = await runtime.ListModelsAsync("azure-openai-responses");
            var model = Assert.Single(models, item => item.Id == "gpt-4.1-mini");
            var selection = await runtime.ResolveAsync("azure-openai-responses", model.Id);

            Assert.Equal(43, models.Count);
            Assert.Equal("azure-openai-responses", model.Api);
            Assert.Equal(1_047_576, model.ContextLength);
            Assert.Equal(32_768, model.MaxOutputTokens);
            Assert.Equal(["text", "image"], model.Input);
            Assert.Equal(0.1m, model.Pricing?.CachedInput);
            Assert.Equal(new ModelImageResizeOptions(2000, 2000, 4_718_592, 80), model.InputLimits?.Images?.Resize);
            Assert.Equal("azure-key", selection.ApiKey);
            Assert.Equal("azure-openai-responses", ProviderChatClientFactory.ResolveProtocol(selection));
            Assert.Equal(new Uri("https://sample-resource.openai.azure.com/openai/v1"), selection.Connection.Endpoint);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task AzureResponsesSendsMappedDeploymentApiKeyAndApiVersionToResponsesEndpoint()
    {
        using var listener = StartListener(out var port);
        string? requestPath = null;
        string? apiKeyHeader = null;
        string? authorizationHeader = null;
        string? requestBody = null;
        string? apiVersion = null;
        var server = Task.Run(async () =>
        {
            var request = await listener.GetContextAsync();
            requestPath = request.Request.Url?.AbsolutePath;
            apiVersion = request.Request.QueryString["api-version"];
            apiKeyHeader = request.Request.Headers["api-key"];
            authorizationHeader = request.Request.Headers["Authorization"];
            using var reader = new StreamReader(request.Request.InputStream);
            requestBody = await reader.ReadToEndAsync();
            request.Response.ContentType = "text/event-stream";
            await using var writer = new StreamWriter(request.Response.OutputStream);
            await writer.WriteAsync("data: {\"type\":\"response.created\",\"response\":{\"id\":\"resp_azure\",\"object\":\"response\",\"created_at\":1,\"model\":\"deployment-gpt41mini\",\"status\":\"in_progress\",\"output\":[]}}\n\n");
            await writer.WriteAsync("data: {\"type\":\"response.output_text.delta\",\"item_id\":\"item_azure\",\"output_index\":0,\"content_index\":0,\"delta\":\"azure response\"}\n\n");
            await writer.WriteAsync("data: {\"type\":\"response.completed\",\"response\":{\"id\":\"resp_azure\",\"object\":\"response\",\"created_at\":1,\"model\":\"deployment-gpt41mini\",\"status\":\"completed\",\"output\":[]}}\n\n");
            await writer.WriteAsync("data: [DONE]\n\n");
            await writer.FlushAsync();
            request.Response.Close();
        });
        var root = Path.Combine(Path.GetTempPath(), "pisharp-azure-request-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            using var http = new HttpClient(new RejectUnexpectedRequestHandler());
            var environment = new Dictionary<string, string>
            {
                ["AZURE_OPENAI_BASE_URL"] = $"http://127.0.0.1:{port}/openai/v1",
                ["AZURE_OPENAI_API_VERSION"] = "preview",
                ["AZURE_OPENAI_DEPLOYMENT_NAME_MAP"] = "gpt-5-mini=deployment-gpt5mini",
                ["AZURE_OPENAI_API_KEY"] = "azure-key"
            };
            var runtime = await ProviderModelRuntime.CreateAsync(root, false,
                name => environment.GetValueOrDefault(name), http);
            var selection = await runtime.ResolveAsync("azure-openai-responses", "gpt-5-mini");
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));

            var output = new System.Text.StringBuilder();
            await foreach (var update in ProviderChatClientFactory.Create(selection).GetStreamingResponseAsync(
                [new ChatMessage(ChatRole.User, "hello")], new ChatOptions
                {
                    ConversationId = new string('c', 70),
                    MaxOutputTokens = 3,
                    Reasoning = ThinkingLevels.ToOptions("high")
                },
                deadline.Token))
                if (update.Text is { } text) output.Append(text);
            await server.WaitAsync(deadline.Token);

            Assert.Equal("azure response", output.ToString());
            Assert.Equal("/openai/v1/responses", requestPath);
            Assert.Equal("preview", apiVersion);
            Assert.Equal("azure-key", apiKeyHeader);
            Assert.Null(authorizationHeader);
            using var sent = JsonDocument.Parse(requestBody!);
            Assert.Equal("deployment-gpt5mini", sent.RootElement.GetProperty("model").GetString());
            Assert.Equal(new string('c', 64), sent.RootElement.GetProperty("prompt_cache_key").GetString());
            Assert.False(sent.RootElement.GetProperty("store").GetBoolean());
            Assert.Equal(16, sent.RootElement.GetProperty("max_output_tokens").GetInt32());
            Assert.Equal("high", sent.RootElement.GetProperty("reasoning").GetProperty("effort").GetString());
            Assert.Equal("auto", sent.RootElement.GetProperty("reasoning").GetProperty("summary").GetString());
            Assert.Contains("reasoning.encrypted_content", sent.RootElement.GetProperty("include").EnumerateArray()
                .Select(item => item.GetString()));
        }
        finally
        {
            listener.Close();
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task AzureResponsesRequiresAnEndpointBeforeCreatingAClient()
    {
        var root = Path.Combine(Path.GetTempPath(), "pisharp-azure-endpoint-required-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            using var http = new HttpClient(new RejectUnexpectedRequestHandler());
            var runtime = await ProviderModelRuntime.CreateAsync(root, false,
                name => name == "AZURE_OPENAI_API_KEY" ? "azure-key" : null, http);
            var selection = await runtime.ResolveAsync("azure-openai-responses", "gpt-5-mini");

            var error = Assert.Throws<InvalidOperationException>(() => ProviderChatClientFactory.Create(selection));
            Assert.Contains("AZURE_OPENAI_BASE_URL or AZURE_OPENAI_RESOURCE_NAME", error.Message, StringComparison.Ordinal);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task CustomProviderCannotBorrowAzureCredentialEnvironment()
    {
        var root = Path.Combine(Path.GetTempPath(), "pisharp-azure-credential-boundary-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            await File.WriteAllTextAsync(Path.Combine(root, "models.json"), """
                {"providers":{"fixture":{"baseUrl":"https://fixture.test/v1","apiKeyEnv":"AZURE_OPENAI_API_KEY","models":[{"id":"demo"}]}}}
                """);
            using var http = new HttpClient(new RejectUnexpectedRequestHandler());
            var environment = new Dictionary<string, string>
            {
                ["AZURE_OPENAI_BASE_URL"] = "https://sample-resource.openai.azure.com",
                ["AZURE_OPENAI_API_KEY"] = "azure-key"
            };

            var error = await Assert.ThrowsAsync<InvalidDataException>(() => ProviderModelRuntime.CreateAsync(root,
                false, name => environment.GetValueOrDefault(name), http));
            Assert.Contains("cannot borrow AZURE_OPENAI_API_KEY", error.Message, StringComparison.Ordinal);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task AzurePromptCacheKeyIsStablePerMAFSession()
    {
        var root = Path.Combine(Path.GetTempPath(), "pisharp-azure-cache-session-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var requestContext = new AzureOpenAiRequestContext();
            var client = new CapturePromptCacheKeyClient(requestContext);
            var agent = new PiAgent(new AzureOpenAiChatOptionsClient(client, requestContext), new CodingTools(root), noTools: true);
            var session = await agent.CreateSessionAsync();
            await foreach (var _ in agent.RunStreamingAsync("hello", session)) { }
            await foreach (var _ in agent.RunStreamingAsync("again", session)) { }

            Assert.Equal(2, client.PromptCacheKeys.Count);
            Assert.False(string.IsNullOrWhiteSpace(client.PromptCacheKeys[0]));
            Assert.Equal(client.PromptCacheKeys[0], client.PromptCacheKeys[1]);

            var otherSession = await agent.CreateSessionAsync();
            await foreach (var _ in agent.RunStreamingAsync("separate", otherSession)) { }
            Assert.NotEqual(client.PromptCacheKeys[0], client.PromptCacheKeys[2]);
        }
        finally { Directory.Delete(root, true); }
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

    private sealed class CapturePromptCacheKeyClient(AzureOpenAiRequestContext requestContext) : IChatClient
    {
        private readonly List<string?> _promptCacheKeys = [];
        public IReadOnlyList<string?> PromptCacheKeys => _promptCacheKeys;

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            _promptCacheKeys.Add(requestContext.Current?.PromptCacheKey);
            yield return new ChatResponseUpdate(ChatRole.Assistant, "done");
            await Task.CompletedTask;
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }
}
