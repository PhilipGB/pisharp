using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;
using PiSharp.Cli;
using PiSharp.Runtime.Providers;

namespace PiSharp.Tests;

public sealed class GoogleVertexProviderTests
{
    [Fact]
    public async Task VertexUsesCurrentPiCatalogAndResolvesApiKeyBeforeAdc()
    {
        var root = Path.Combine(Path.GetTempPath(), "pisharp-google-vertex-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var credentials = Path.Combine(root, "adc.json");
        await File.WriteAllTextAsync(credentials, "{}");
        try
        {
            var environment = new Dictionary<string, string?>
            {
                ["GOOGLE_CLOUD_PROJECT"] = "fixture-project",
                ["GOOGLE_CLOUD_LOCATION"] = "europe-west4",
                ["GOOGLE_APPLICATION_CREDENTIALS"] = credentials,
                ["GOOGLE_CLOUD_API_KEY"] = "fixture-vertex-key"
            };
            using var http = new HttpClient();
            var withApiKey = await ProviderModelRuntime.CreateAsync(root, false,
                name => environment.GetValueOrDefault(name), http, offline: true);
            var models = await withApiKey.ListModelsAsync("google-vertex");
            var selectedModel = Assert.Single(models, model => model.Id == "gemini-3.5-flash");
            var selection = await withApiKey.ResolveAsync("google-vertex", null);

            Assert.Equal(14, models.Count);
            Assert.Equal("google-vertex", selectedModel.Api);
            Assert.Equal("google-vertex", selectedModel.Provider);
            Assert.Equal("https://{location}-aiplatform.googleapis.com", selectedModel.BaseUrl);
            Assert.Equal("google-vertex", ProviderChatClientFactory.ResolveProtocol(selection));
            Assert.Equal("https://aiplatform.googleapis.com/", selection.Connection.Endpoint?.ToString());
            Assert.Equal("fixture-vertex-key", selection.ApiKey);
            Assert.Equal("GOOGLE_CLOUD_API_KEY", selection.AuthSource);

            environment["GOOGLE_CLOUD_API_KEY"] = null;
            var withAdc = await ProviderModelRuntime.CreateAsync(root, false,
                name => environment.GetValueOrDefault(name), http, offline: true);
            var adc = await withAdc.ResolveAuthAsync("google-vertex");
            Assert.True(adc.Authenticated);
            Assert.Equal(GoogleVertexProviderOptions.AdcAuthSource, adc.Source);
            Assert.Equal(GoogleVertexProviderOptions.AdcCredentialMarker, adc.Key);

            using var output = new StringWriter();
            using var error = new StringWriter();
            var status = await AuthStatusCommand.RunAsync(["check", "--provider", "google-vertex"], root,
                name => environment.GetValueOrDefault(name), output, error);
            var printed = await AuthStatusCommand.RunAsync(["print-api-key", "--provider", "google-vertex"], root,
                name => environment.GetValueOrDefault(name), output, error);
            Assert.Equal(0, status);
            Assert.Contains("credential available (Google Vertex Application Default Credentials)", output.ToString());
            Assert.Equal(1, printed);
            Assert.DoesNotContain(GoogleVertexProviderOptions.AdcCredentialMarker, output.ToString() + error);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task VertexApiKeyUsesPublisherRestPathAndGeminiRequestBody()
    {
        using var listener = StartListener(out var port);
        string? path = null;
        string? query = null;
        string? apiKey = null;
        string? authorization = null;
        string? requestBody = null;
        var server = Task.Run(async () =>
        {
            var request = await listener.GetContextAsync();
            path = request.Request.Url?.AbsolutePath;
            query = request.Request.Url?.Query;
            apiKey = request.Request.Headers["x-goog-api-key"];
            authorization = request.Request.Headers["Authorization"];
            using var reader = new StreamReader(request.Request.InputStream);
            requestBody = await reader.ReadToEndAsync();
            request.Response.ContentType = "text/event-stream";
            await using var writer = new StreamWriter(request.Response.OutputStream);
            await writer.WriteAsync("data: {\"responseId\":\"vertex-key-1\",\"candidates\":[{\"content\":{\"role\":\"model\",\"parts\":[{\"text\":\"vertex response\"}]},\"finishReason\":\"STOP\"}]}\n\n");
            await writer.FlushAsync();
            request.Response.Close();
        });

        var endpoint = new Uri($"http://127.0.0.1:{port}/");
        var provider = VertexProfile(endpoint, new GoogleVertexProviderOptions("fixture-project", "us-central1", null));
        var model = new ModelDescriptor("gemini-3.5-flash", "google-vertex", 1_048_576, "current Pi",
            Provider: "google-vertex", Api: "google-vertex", BaseUrl: endpoint.ToString());
        var selection = new ModelSelection(provider, model, "fixture-vertex-key", true, "GOOGLE_CLOUD_API_KEY");
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var client = ProviderChatClientFactory.Create(selection);
        var response = await client.GetResponseAsync([new ChatMessage(ChatRole.User, "hello vertex")],
            cancellationToken: deadline.Token);
        await server.WaitAsync(deadline.Token);

        Assert.Equal("vertex response", Assert.Single(response.Messages).Text);
        Assert.Equal("/v1/publishers/google/models/gemini-3.5-flash:streamGenerateContent", path);
        Assert.Equal("?alt=sse", query);
        Assert.Equal("fixture-vertex-key", apiKey);
        Assert.Null(authorization);
        using var sent = JsonDocument.Parse(requestBody!);
        Assert.Equal("hello vertex", sent.RootElement.GetProperty("contents")[0].GetProperty("parts")[0]
            .GetProperty("text").GetString());
    }

    [Fact]
    public async Task VertexAdcUsesProjectLocationPathAndBearerTokenWithoutApiKey()
    {
        using var listener = StartListener(out var port);
        string? path = null;
        string? apiKey = null;
        string? authorization = null;
        var server = Task.Run(async () =>
        {
            var request = await listener.GetContextAsync();
            path = request.Request.Url?.AbsolutePath;
            apiKey = request.Request.Headers["x-goog-api-key"];
            authorization = request.Request.Headers["Authorization"];
            request.Response.ContentType = "text/event-stream";
            await using var writer = new StreamWriter(request.Response.OutputStream);
            await writer.WriteAsync("data: {\"responseId\":\"vertex-adc-1\",\"candidates\":[{\"content\":{\"role\":\"model\",\"parts\":[{\"text\":\"adc response\"}]},\"finishReason\":\"STOP\"}]}\n\n");
            await writer.FlushAsync();
            request.Response.Close();
        });

        var endpoint = new Uri($"http://127.0.0.1:{port}/");
        var model = new ModelDescriptor("gemini-3.5-flash", "google-vertex", 1_048_576, "current Pi",
            Provider: "google-vertex", Api: "google-vertex");
        var vertex = new GoogleVertexRequestOptions("fixture project", "europe-west4", true,
            AccessTokenProvider: _ => Task.FromResult("fixture-access-token"));
        using var http = new HttpClient();
        using var client = new GoogleGenAiChatClient(http, endpoint, string.Empty, model, vertex);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var response = await client.GetResponseAsync([new ChatMessage(ChatRole.User, "hello adc")],
            cancellationToken: deadline.Token);
        await server.WaitAsync(deadline.Token);

        Assert.Equal("adc response", Assert.Single(response.Messages).Text);
        Assert.Equal("/v1/projects/fixture%20project/locations/europe-west4/publishers/google/models/gemini-3.5-flash:streamGenerateContent", path);
        Assert.Equal("Bearer fixture-access-token", authorization);
        Assert.Null(apiKey);
    }

    [Theory]
    [InlineData("global", "aiplatform.googleapis.com")]
    [InlineData("us", "aiplatform.us.rep.googleapis.com")]
    [InlineData("eu", "aiplatform.eu.rep.googleapis.com")]
    [InlineData("europe-west4", "europe-west4-aiplatform.googleapis.com")]
    public void VertexAdcUsesCurrentRegionalEndpointRules(string location, string hostname)
    {
        Assert.Equal(new Uri($"https://{hostname}"), GoogleVertexChatClientFactory.RegionalEndpoint(location));
    }

    private static ProviderProfile VertexProfile(Uri endpoint, GoogleVertexProviderOptions options) =>
        new("google-vertex", "Google Vertex AI", endpoint, true, false, "GOOGLE_CLOUD_API_KEY", null,
            [], Api: "google-vertex", GoogleVertex: options);

    private static HttpListener StartListener(out int port)
    {
        using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        socket.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        port = ((IPEndPoint)socket.LocalEndPoint!).Port;
        var listener = new HttpListener();
        listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        listener.Start();
        return listener;
    }
}
