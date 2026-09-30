using PiSharp.Cli;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using PiSharp.Runtime.Classifiers;

namespace PiSharp.Tests;

public sealed class ClassifierCatalogTests
{
    [Fact]
    public async Task NativeClassifierCatalogIsSeparateFromChatAndUsesExistingAuthenticationPrecedence()
    {
        var root = Path.Combine(Path.GetTempPath(), "pisharp-classifier-catalog-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            using var http = new HttpClient();
            var runtime = await ProviderModelRuntime.CreateAsync(root, false,
                name => name == "TYPESAFE_API_KEY" ? "environment-key" : null, http, offline: true);
            var typesafe = runtime.GetProvider("typesafe");
            Assert.Equal("jev-latest", Assert.Single(typesafe.Classifiers!).Id);
            Assert.Empty(typesafe.Models);
            Assert.Equal("environment-key", (await runtime.ResolveAuthAsync("typesafe")).Key);
            Assert.Equal(7, runtime.GetProvider("openrouter").Classifiers!.Count);
            Assert.DoesNotContain(runtime.GetProvider("openrouter").Models, model => model.Id == "typesafe/jev-1.13");
        }
        finally { Directory.Delete(root, recursive: true); }
    }
    [Fact]
    public async Task ConfiguredClassifierUsesRealHttpAndCannotBeSelectedAsChat()
    {
        var root = Path.Combine(Path.GetTempPath(), "pisharp-classifier-http-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        using var reservation = new TcpListener(IPAddress.Loopback, 0);
        reservation.Start();
        var port = ((IPEndPoint)reservation.LocalEndpoint).Port;
        reservation.Stop();
        using var listener = new HttpListener();
        listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        listener.Start();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(12));
        try
        {
            var configuration = new
            {
                providers = new Dictionary<string, object>
                {
                    ["fixture"] = new
                    {
                        baseUrl = $"http://127.0.0.1:{port}/v1",
                        apiKey = "configured-key",
                        models = new object[]
                {
                    new { id = "chat", type = "chat" },
                    new { id = "classifier", type = "classifier", api = "typesafe-system-one", contextWindow = 4096 }
                }
                    }
                }
            };
            await File.WriteAllTextAsync(Path.Combine(root, "models.json"), JsonSerializer.Serialize(configuration));
            using var http = new HttpClient();
            var providers = await ProviderModelRuntime.CreateAsync(root, false, _ => null, http, offline: true);
            Assert.Equal("chat", Assert.Single(await providers.ListModelsAsync("fixture")).Id);
            await Assert.ThrowsAsync<ArgumentException>(() => providers.ResolveAsync("fixture", "classifier"));
            var runtime = new ProviderClassifierRuntime(providers, http);
            Assert.Equal(4096, Assert.Single(runtime.ListModels("fixture")).ContextWindow);
            var server = Task.Run(async () =>
            {
                var request = await listener.GetContextAsync().WaitAsync(deadline.Token);
                Assert.Equal("/v1/systemone", request.Request.Url!.AbsolutePath);
                Assert.Equal("Bearer configured-key", request.Request.Headers["Authorization"]);
                await using var writer = new StreamWriter(request.Response.OutputStream);
                request.Response.ContentType = "application/json";
                await writer.WriteAsync("""{"answers":{"safe":{"type":"noul","noul":0.9}}}""");
                await writer.FlushAsync(deadline.Token);
                request.Response.Close();
            }, deadline.Token);
            var result = await runtime.ClassifyAsync("fixture", "classifier", new ClassifierContext(
                JsonSerializer.SerializeToElement(new { text = "fixture" }), new Dictionary<string, ClassifierQuestion>
                { ["safe"] = new ClassifierBoolQuestion("safe?", new Dictionary<string, string> { ["true"] = "safe", ["false"] = "unsafe" }) }), deadline.Token);
            Assert.Equal("stop", result.StopReason);
            Assert.Equal(0.9, Assert.IsType<ClassifierBoolAnswer>(result.Answers["safe"]).Probability);
            await server.WaitAsync(deadline.Token);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

}
