using System.Net;
using System.Text;
using PiSharp.Cli;

namespace PiSharp.Tests;

public sealed class ProviderModelRuntimeTests
{
    [Fact]
    public async Task CustomCatalogSelectsExactOrUnambiguousModelAndNeverSendsCloudKey()
    {
        var root = Path.Combine(Path.GetTempPath(), "pisharp-provider-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            await File.WriteAllTextAsync(Path.Combine(root, "models.json"), """
                {"providers":{"custom":{"baseUrl":"https://fixture.test/v1","apiKeyEnv":"PISHARP_API_KEY",
                 "models":[{"id":"reasoner","contextWindow":4096,"reasoning":true,"cost":{"input":1,"output":3}}]}}}
                """);
            using var handler = new ModelHandler();
            using var http = new HttpClient(handler);
            var environment = new Dictionary<string, string> { ["OPENAI_API_KEY"] = "unrelated-secret", ["PISHARP_API_KEY"] = "local-secret" };
            var runtime = await ProviderModelRuntime.CreateAsync(root, false, name => environment.GetValueOrDefault(name), http);
            var exact = await runtime.ResolveAsync("custom", "reasoner");
            Assert.Equal("reasoner", exact.Model.Id);
            Assert.Null(handler.Url); // Startup must not require a /models implementation.
            var selection = await runtime.ResolveAsync("custom", "reas");
            Assert.Equal("reasoner", selection.Model.Id);
            Assert.True(selection.Model.Reasoning);
            Assert.Equal(4096, selection.Model.ContextLength);
            Assert.Equal(1m, selection.Model.Pricing!.Input);
            Assert.Equal("local-secret", selection.ApiKey);
            Assert.Equal("https://fixture.test/v1/models", handler.Url);
            Assert.Equal("Bearer local-secret", handler.Authorization);
            Assert.DoesNotContain("unrelated-secret", handler.Authorization!);
            runtime.SetScope(["custom/other-*"]);
            await Assert.ThrowsAsync<ArgumentException>(() => runtime.ResolveAsync("custom", "reasoner"));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task PrivateCredentialsSurviveRestartAndRejectSymlinksAndPublicFiles()
    {
        var root = Path.Combine(Path.GetTempPath(), "pisharp-auth-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var file = Path.Combine(root, "auth.json");
            var storage = new AuthStorage(file);
            await storage.StoreApiKeyAsync("custom", "secret-test-value");
            Assert.Equal("secret-test-value", (await new AuthStorage(file).ReadAsync("custom"))!.Secret);
            Assert.DoesNotContain("secret-test-value", (await storage.ListAsync()).ToString()!);
            if (OperatingSystem.IsLinux())
            {
                Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(file));
                File.SetUnixFileMode(file, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.OtherRead);
                await Assert.ThrowsAsync<InvalidDataException>(() => storage.ReadAsync("custom"));
                File.SetUnixFileMode(file, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                var link = Path.Combine(root, "link.json");
                File.CreateSymbolicLink(link, file);
                await Assert.ThrowsAsync<InvalidDataException>(() => new AuthStorage(link).ReadAsync("custom"));
            }
            await storage.DeleteAsync("custom");
            Assert.Null(await storage.ReadAsync("custom"));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task CliRejectsMissingProviderCredentialsBeforeInference()
    {
        var root = Path.Combine(Path.GetTempPath(), "pisharp-provider-process-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var start = new System.Diagnostics.ProcessStartInfo("dotnet")
            {
                WorkingDirectory = root,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            foreach (var arg in new[] { typeof(CliArguments).Assembly.Location, "--provider", "openai", "--no-session", "--print", "hello" })
                start.ArgumentList.Add(arg);
            foreach (var name in new[] { "OPENAI_API_KEY", "PISHARP_API_KEY", "PISHARP_BASE_URL", "PISHARP_MODEL", "PISHARP_AUTH_PATH", "PISHARP_MODELS_PATH" })
                start.Environment.Remove(name);
            start.Environment["PISHARP_AGENT_DIR"] = root;
            using var process = System.Diagnostics.Process.Start(start)!;
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(2, process.ExitCode);
            Assert.Contains("not authenticated", await stderr);
            Assert.Equal("", await stdout);
        }
        finally { Directory.Delete(root, true); }
    }

    private sealed class ModelHandler : HttpMessageHandler
    {
        public string? Url { get; private set; }
        public string? Authorization { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Url = request.RequestUri?.ToString();
            Authorization = request.Headers.Authorization?.ToString();
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"data":[{"id":"reasoner","reasoning":true}]}""", Encoding.UTF8, "application/json")
            });
        }
    }
}
