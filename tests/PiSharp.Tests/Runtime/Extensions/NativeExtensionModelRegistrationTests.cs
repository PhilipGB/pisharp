using System.Text.Json;
using Microsoft.Extensions.AI;
using PiSharp.Extensions.Abstractions;
using PiSharp.Runtime.Extensions;
using PiSharp.Runtime.Mcp;
using PiSharp.Runtime.Providers;
using PiSharp.Runtime.Resources;
using PiSharp.Runtime.VirtualModels;

namespace PiSharp.Tests;

public sealed class NativeExtensionModelRegistrationTests
{
    [Fact]
    public async Task PublicModelRegistrationsPreserveOwnersAndAdaptSafeVirtualRouteData()
    {
        var registration = new ExtensionRegistration();
        var restore = registration.CreateConfigurationCheckpoint();
        var adapter = new NativeExtensionRegistrationAdapter(registration);
        var source = Source("/extensions/model-fixture.dll");
        PiSharpExtensionVirtualModelRouteRequest? publicRequest = null;
        registration.SetCurrentSourceInfo(source);
        try
        {
            adapter.AddProvider(new("fixture-provider", "Fixture provider",
            [new("small", "Small", ContextLength: 4096, MaxOutputTokens: 1024, Input: ["text"], Reasoning: true)],
                _ => new RecordingClient()));
            adapter.AddMcpServer("fixture-mcp", new()
            {
                Type = "stdio",
                Command = "python3",
                Arguments = ["server.py"],
                Environment = new Dictionary<string, string> { ["FIXTURE_MODE"] = "safe" },
                Exposure = "direct"
            });
            adapter.AddVirtualModel(new("fixture-router", "auto", "Automatic fixture model",
                (request, _) =>
                {
                    publicRequest = request;
                    request.Messages[0].Contents.OfType<TextContent>().Single().Text = "callback-local mutation";
                    request.Messages[0].Contents.OfType<DataContent>().Single().Name = "callback-local image";
                    request.Messages[0].Contents.Add(new TextContent("callback-local mutation"));
                    return Task.FromResult(new PiSharpExtensionVirtualModelRoute("fixture-provider", "small", "high",
                        JsonSerializer.SerializeToElement(new { turn = 2 })));
                })
            {
                ThinkingLevels = ["low", "high"],
                ContextLength = 4096,
                MaxOutputTokens = 1024,
                Input = ["text"]
            });
        }
        finally { registration.SetCurrentSourceInfo(null); }

        var provider = Assert.Single(registration.ExtensionProviders);
        Assert.Equal(source.Path, provider.ExtensionPath);
        Assert.Equal("small", Assert.Single(provider.Definition.Models).Id);
        var server = Assert.Single(registration.McpServers);
        Assert.Equal(source.Path, server.ExtensionPath);
        Assert.Equal("python3", server.Configuration.Command);
        Assert.Equal("safe", server.Configuration.Environment["FIXTURE_MODE"]);
        var virtualModel = Assert.Single(registration.VirtualModels.Models);
        Assert.Equal(source.Path, virtualModel.ExtensionPath);

        var originalMessage = new ChatMessage(ChatRole.User,
            [new TextContent("hello"), new DataContent(new byte[] { 1, 2, 3 }, "image/png")])
        {
            RawRepresentation = new object()
        };
        var originalState = JsonSerializer.SerializeToElement(new { turn = 1 });
        var request = new VirtualModelRouteRequest(virtualModel.Model, "low", "retry", [originalMessage],
            new ModelDescriptor("large", null, 8192, "previous", Provider: "physical"), "medium",
            new ModelDescriptor("failed", null, 2048, "failed", Provider: "physical"), "high",
            new ChatMessage(ChatRole.Assistant, "failed message"), originalState, "fixture failure");
        var route = await registration.VirtualModels.RouteAsync(virtualModel, request, CancellationToken.None);

        Assert.Equal("fixture-provider", route.Provider);
        Assert.Equal("small", route.Model);
        Assert.Equal(2, route.State!.Value.GetProperty("turn").GetInt32());
        Assert.Equal("retry", publicRequest!.Reason);
        Assert.Equal("low", publicRequest.ThinkingLevel);
        Assert.Equal("physical", publicRequest.PreviousModel!.Provider);
        Assert.Equal("large", publicRequest.PreviousModel.Id);
        Assert.Equal("medium", publicRequest.PreviousThinkingLevel);
        Assert.Equal("failed", publicRequest.FailedModel!.Id);
        Assert.Equal("fixture failure", publicRequest.FailedError);
        Assert.Equal("failed message", publicRequest.FailedMessage!.Text);
        Assert.Equal(1, publicRequest.State!.Value.GetProperty("turn").GetInt32());
        Assert.Equal("hello", originalMessage.Text);
        Assert.Equal(2, originalMessage.Contents.Count);
        Assert.All(publicRequest.Messages[0].Contents.OfType<TextContent>(),
            content => Assert.Null(content.RawRepresentation));
        Assert.Null(publicRequest.Messages[0].RawRepresentation);
        Assert.Null(Assert.Single(originalMessage.Contents.OfType<DataContent>()).Name);

        restore();
        Assert.Empty(registration.ExtensionProviders);
        Assert.Empty(registration.McpServers);
        Assert.Empty(registration.VirtualModels.Models);
    }

    [Fact]
    public void ProviderAndVirtualRegistrationRejectMalformedOrAmbiguousOwnership()
    {
        var registration = new ExtensionRegistration();
        var adapter = new NativeExtensionRegistrationAdapter(registration);
        registration.SetCurrentSourceInfo(Source("/extensions/first.dll"));

        Assert.Throws<ArgumentException>(() => adapter.AddProvider(Provider("Uppercase")));
        var duplicateModel = Assert.Throws<InvalidOperationException>(() => adapter.AddProvider(Provider("fixture",
            new("model", Input: ["text"]), new("MODEL", Input: ["text"]))));
        Assert.Contains("/extensions/first.dll", duplicateModel.Message, StringComparison.Ordinal);
        Assert.Throws<ArgumentException>(() => adapter.AddProvider(Provider("fixture",
            new PiSharpExtensionProviderModel("model", Input: ["audio"]))));
        Assert.Throws<ArgumentException>(() => adapter.AddProvider(Provider("fixture",
            new PiSharpExtensionProviderModel("model", ContextLength: 0))));
        Assert.Throws<ArgumentException>(() => adapter.AddProvider(Provider("fixture",
            new PiSharpExtensionProviderModel("model",
            ThinkingLevelMap: JsonSerializer.SerializeToElement(new[] { "invalid" })))));

        adapter.AddProvider(Provider("fixture"));
        adapter.AddMcpServer("fixture-mcp", new() { Command = "python3", Arguments = ["server.py"] });
        adapter.AddVirtualModel(new("fixture-router", "auto", "Auto", (_, _) =>
            Task.FromResult(new PiSharpExtensionVirtualModelRoute("fixture", "model", "off"))));

        registration.SetCurrentSourceInfo(Source("/extensions/second.dll"));
        var providerConflict = Assert.Throws<InvalidOperationException>(() => adapter.AddProvider(Provider("fixture")));
        Assert.Contains("/extensions/first.dll", providerConflict.Message, StringComparison.Ordinal);
        Assert.Contains("/extensions/second.dll", providerConflict.Message, StringComparison.Ordinal);
        var serverConflict = Assert.Throws<InvalidOperationException>(() => adapter.AddMcpServer("fixture_mcp",
            new() { Command = "python3", Arguments = ["server.py"] }));
        Assert.Contains("fixture-mcp", serverConflict.Message, StringComparison.Ordinal);
        Assert.Contains("fixture_mcp", serverConflict.Message, StringComparison.Ordinal);
        Assert.Contains("/extensions/first.dll", serverConflict.Message, StringComparison.Ordinal);
        Assert.Contains("/extensions/second.dll", serverConflict.Message, StringComparison.Ordinal);
        var virtualConflict = Assert.Throws<InvalidOperationException>(() => adapter.AddVirtualModel(
            new("fixture-router", "auto", "Auto 2", (_, _) =>
                Task.FromResult(new PiSharpExtensionVirtualModelRoute("fixture", "model", "off")))));
        Assert.Contains("/extensions/first.dll", virtualConflict.Message, StringComparison.Ordinal);
        Assert.Contains("/extensions/second.dll", virtualConflict.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TypedMcpConfigurationRetainsHttpSettingsAndRejectsInvalidTransportMixes()
    {
        var registration = new ExtensionRegistration();
        var adapter = new NativeExtensionRegistrationAdapter(registration);
        registration.SetCurrentSourceInfo(Source("/extensions/http.dll"));

        adapter.AddMcpServer("fixture-http", new()
        {
            Type = "http",
            Url = new Uri("https://mcp.example.test/server"),
            Headers = new Dictionary<string, string> { ["X-Fixture"] = "value" },
            AuthProvider = "openai",
            Enabled = false,
            TimeoutSeconds = 12,
            Description = "fixture HTTP server"
        });

        var server = Assert.Single(registration.McpServers).Configuration;
        Assert.Null(server.Command);
        Assert.Equal(new Uri("https://mcp.example.test/server"), server.Url);
        Assert.Equal("value", server.Headers["X-Fixture"]);
        Assert.Equal("openai", server.AuthProvider);
        Assert.False(server.Enabled);
        Assert.Equal(TimeSpan.FromSeconds(12), server.Timeout);
        Assert.Equal("fixture HTTP server", server.Description);

        Assert.Throws<ArgumentException>(() => adapter.AddMcpServer("invalid-mix", new()
        {
            Command = "python3",
            Url = new Uri("https://mcp.example.test/server")
        }));
    }

    [Fact]
    public async Task ExtensionMcpStartupFailureReportsItsOwningPackageSource()
    {
        var root = Path.Combine(Path.GetTempPath(), "pisharp-extension-mcp-failure-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            using var catalog = ExtensionCatalog.Load(root, root, projectTrusted: true, discover: false);
            catalog.Registration.SetCurrentSourceInfo(Source("/extensions/failing-mcp.dll"));
            try
            {
                new NativeExtensionRegistrationAdapter(catalog.Registration).AddMcpServer("broken", new()
                {
                    Type = "stdio",
                    Command = "python3",
                    Arguments = ["-c", "raise RuntimeError('fixture credential marker')"],
                    Exposure = "direct",
                    TimeoutSeconds = 3
                });
            }
            finally { catalog.Registration.SetCurrentSourceInfo(null); }

            var configuration = await McpConfiguration.LoadAsync(root, root, projectTrusted: false);
            var error = Assert.Single(await McpRuntime.RegisterAsync(configuration, catalog, root));

            Assert.Contains("MCP server broken from extension /extensions/failing-mcp.dll could not connect",
                error, StringComparison.Ordinal);
            Assert.DoesNotContain("fixture credential marker", error, StringComparison.Ordinal);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task PublicVirtualRouteCallbackFailuresPropagateFromTheRegisteredRoute()
    {
        var registration = new ExtensionRegistration();
        var adapter = new NativeExtensionRegistrationAdapter(registration);
        registration.SetCurrentSourceInfo(Source("/extensions/route-failure.dll"));
        adapter.AddVirtualModel(new("fixture-router", "auto", "Auto", (_, _) =>
            Task.FromException<PiSharpExtensionVirtualModelRoute>(new InvalidOperationException("fixture route failure"))));
        registration.SetCurrentSourceInfo(null);
        var model = Assert.Single(registration.VirtualModels.Models);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => registration.VirtualModels.RouteAsync(model,
            new(model.Model, "low", "user", []), CancellationToken.None));

        Assert.Equal("fixture route failure", error.Message);
    }

    private static PiSharpExtensionProviderDefinition Provider(string id,
        params PiSharpExtensionProviderModel[] models) => new(id, "Fixture", models.Length == 0
            ? [new("model", Input: ["text"])] : models, _ => new RecordingClient())
        { AuthRequired = false };

    private static ResourceSourceInfo Source(string path) => new(path, "cli", "temporary", "top-level", null);

    private sealed class RecordingClient : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default) => Task.FromResult(
            new ChatResponse(new ChatMessage(ChatRole.Assistant, "fixture")));
        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            var response = await GetResponseAsync(messages, options, cancellationToken);
            foreach (var update in response.ToChatResponseUpdates()) yield return update;
        }
        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }
}
