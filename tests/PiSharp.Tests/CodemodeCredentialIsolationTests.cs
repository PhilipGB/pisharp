using System.Net;
using System.Text.Json;
using PiSharp.Cli;
using PiSharp.Runtime.Classifiers;

namespace PiSharp.Tests;

public sealed class CodemodeCredentialIsolationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PrimaryRuntimeKeyDoesNotAuthenticateAnotherClassifierProvider(bool selected)
    {
        var root = Path.Combine(Path.GetTempPath(), "pisharp-codemode-credential-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var sent = false;
            using var http = new HttpClient(new Handler(_ =>
            {
                sent = true;
                return new(HttpStatusCode.OK) { Content = new StringContent("""{"answers":{"q":{"type":"noul","noul":0.8}}}""") };
            }));
            var providers = await ProviderModelRuntime.CreateAsync(root, true, _ => null, http, runtimeApiKey: "primary-key", offline: true);
            if (selected) await providers.ResolveAsync("local", providers.GetProvider("local").Models[0].Id);
            var result = await new ProviderClassifierRuntime(providers, http).ClassifyAsync("typesafe", "jev-latest",
                new(JsonSerializer.SerializeToElement(new { }), new Dictionary<string, ClassifierQuestion> { ["q"] = new ClassifierBoolQuestion("safe", new Dictionary<string, string>()) }));
            Assert.Equal("error", result.StopReason);
            Assert.False(sent);
            Assert.Empty(await new ProviderCodemodeModels(providers, http).GetAvailableAsync("classifier", "typesafe", CancellationToken.None));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task RuntimeKeyStaysBoundToItsInitialProviderAcrossModelSwitches()
    {
        var root = Path.Combine(Path.GetTempPath(), "pisharp-codemode-key-owner-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            await File.WriteAllTextAsync(Path.Combine(root, "models.json"), """
                {"providers":{"fixture":{"baseUrl":"http://127.0.0.1:9999/v1","apiKey":"configured-key","models":[{"id":"chat"},{"id":"classifier","type":"classifier","api":"typesafe-system-one"}]}}}
                """);
            var requests = 0;
            using var http = new HttpClient(new Handler(request =>
            {
                requests++;
                Assert.Equal("Bearer primary-key", request.Headers.Authorization!.ToString());
                return new(HttpStatusCode.OK) { Content = new StringContent("""{"answers":{"q":{"type":"noul","noul":0.8}}}""") };
            }));
            var providers = await ProviderModelRuntime.CreateAsync(root, true, _ => null, http, runtimeApiKey: "primary-key", offline: true);
            await providers.ResolveAsync("fixture", "chat");
            await providers.ResolveAsync("local", providers.GetProvider("local").Models[0].Id);
            Assert.Equal("not-needed", (await providers.ResolveAuthAsync("local", useRuntimeOverride: true)).Key);
            var models = new ProviderCodemodeModels(providers, http);
            var available = await models.GetAvailableAsync("classifier", null, CancellationToken.None);
            Assert.Equal("fixture", Assert.Single(available).GetProperty("provider").GetString());
            Assert.DoesNotContain("primary-key", available[0].GetRawText());
            var result = await models.ClassifyAsync("fixture", "classifier",
                new(JsonSerializer.SerializeToElement(new { }), new Dictionary<string, ClassifierQuestion> { ["q"] = new ClassifierBoolQuestion("safe", new Dictionary<string, string>()) }), CancellationToken.None);
            Assert.Equal("stop", result.StopReason);
            Assert.Equal(1, requests);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => Task.FromResult(send(request));
    }
}
