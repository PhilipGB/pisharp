using System.Net;
using System.Text.Json;
using PiSharp.Cli;
using PiSharp.Runtime.Classifiers;

namespace PiSharp.Tests;

public sealed class CloudflareClassifierAuthTests
{
    [Theory]
    [InlineData(null, true)]
    [InlineData("ambient-account", true)]
    [InlineData("ambient-account", false)]
    public async Task StoredAccountOverridesAmbientAccountAtRequestTime(string? ambientAccount, bool storedAccount)
    {
        var root = Path.Combine(Path.GetTempPath(), "pisharp-cloudflare-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var authPath = Path.Combine(root, "auth.json");
            await new AuthStorage(authPath).StoreApiKeyAsync("cloudflare-workers-ai", "stored-key");
            if (storedAccount) await File.WriteAllTextAsync(authPath, """{"cloudflare-workers-ai":{"Type":"api_key","Key":"stored-key","Env":{"CLOUDFLARE_ACCOUNT_ID":"stored-account"}}}""");
            if (OperatingSystem.IsLinux()) File.SetUnixFileMode(authPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            var called = false;
            using var http = new HttpClient(new Handler(request =>
            {
                called = true;
                Assert.Contains("/accounts/" + (storedAccount ? "stored-account" : "ambient-account") + "/", request.RequestUri!.AbsolutePath);
                Assert.Equal("Bearer stored-key", request.Headers.Authorization!.ToString());
                return new(HttpStatusCode.OK) { Content = new StringContent("""{"success":true,"result":{"state":"Completed","result":{"answers":{"q":{"type":"noul","noul":0.8}}}}}""") };
            }));
            var providers = await ProviderModelRuntime.CreateAsync(root, false, name => name switch
            { "CLOUDFLARE_API_KEY" => "ambient-key", "CLOUDFLARE_ACCOUNT_ID" => ambientAccount, _ => null }, http, offline: true);
            var classifier = Assert.Single(new ProviderClassifierRuntime(providers, http).ListModels("cloudflare-workers-ai"));
            var result = await new ProviderClassifierRuntime(providers, http).ClassifyAsync("cloudflare-workers-ai", classifier.Id,
                new(JsonSerializer.SerializeToElement(new { }), new Dictionary<string, ClassifierQuestion> { ["q"] = new ClassifierBoolQuestion("safe", new Dictionary<string, string>()) }));
            Assert.Equal("stop", result.StopReason);
            Assert.True(called);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task StoredApiKeyEnvironmentMetadataSurvivesReloadAndIsCopied()
    {
        var root = Path.Combine(Path.GetTempPath(), "pisharp-auth-env-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var metadata = new Dictionary<string, string> { ["CLOUDFLARE_ACCOUNT_ID"] = "stored-account" };
            var path = Path.Combine(root, "auth.json");
            await new AuthStorage(path).StoreApiKeyAsync("cloudflare-workers-ai", "key", metadata);
            metadata["CLOUDFLARE_ACCOUNT_ID"] = "changed";
            var stored = await new AuthStorage(path).ReadAsync("cloudflare-workers-ai");
            Assert.Equal("stored-account", stored!.Env!["CLOUDFLARE_ACCOUNT_ID"]);
            Assert.Equal("key", stored.Key);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(send(request));
    }
}
