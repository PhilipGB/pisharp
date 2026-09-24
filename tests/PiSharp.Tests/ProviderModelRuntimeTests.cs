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

    [Theory]
    [InlineData("openai", "PISHARP_FIXTURE_KEY")]
    [InlineData("custom", "OPENAI_API_KEY")]
    public async Task CustomEndpointCannotBorrowOpenAiIdentityOrEnvironment(string provider, string keyVariable)
    {
        var root = Path.Combine(Path.GetTempPath(), "pisharp-provider-isolation-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            await File.WriteAllTextAsync(Path.Combine(root, "models.json"), """
                {"providers":{"PROVIDER":{"baseUrl":"https://fixture.test/v1","apiKeyEnv":"KEY_ENV",
                 "models":[{"id":"fixture-model"}]}}}
                """.Replace("PROVIDER", provider, StringComparison.Ordinal).Replace("KEY_ENV", keyVariable, StringComparison.Ordinal));
            using var http = new HttpClient(new ModelHandler());
            var error = await Assert.ThrowsAsync<InvalidDataException>(() => ProviderModelRuntime.CreateAsync(root, false,
                name => name == "OPENAI_API_KEY" ? "unrelated-openai-key" : null, http));
            Assert.Contains("custom endpoint", error.Message, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("unrelated-openai-key", error.Message);
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

    [Fact]
    public async Task InteractiveLoginAndLogoutNeverSendUnauthenticatedPromptsOrEchoSecret()
    {
        if (!OperatingSystem.IsLinux() || !File.Exists("/usr/bin/script")) return;
        var root = Path.Combine(Path.GetTempPath(), "pisharp-provider-pty-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var agentDirectory = Path.Combine(root, "agent");
            Directory.CreateDirectory(agentDirectory);
            await File.WriteAllTextAsync(Path.Combine(agentDirectory, "models.json"), """
                {"providers":{"fixture":{"baseUrl":"http://127.0.0.1:1/v1","apiKeyEnv":"PISHARP_FIXTURE_KEY",
                 "models":[{"id":"fixture-model","reasoning":false},{"id":"fixture-alt","reasoning":false}]}}}
                """);
            var start = new System.Diagnostics.ProcessStartInfo("/usr/bin/script")
            {
                WorkingDirectory = root,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            start.ArgumentList.Add("-q");
            start.ArgumentList.Add("-e");
            start.ArgumentList.Add("-c");
            start.ArgumentList.Add($"dotnet '{typeof(CliArguments).Assembly.Location}' --provider fixture --no-tools --session-dir '{root}/sessions'");
            start.ArgumentList.Add("/dev/null");
            foreach (var name in new[] { "PISHARP_FIXTURE_KEY", "OPENAI_API_KEY", "PISHARP_API_KEY", "PISHARP_BASE_URL", "PISHARP_AUTH_PATH", "PISHARP_MODELS_PATH" })
                start.Environment.Remove(name);
            start.Environment["PISHARP_AGENT_DIR"] = agentDirectory;
            using var process = System.Diagnostics.Process.Start(start)!;
            var output = new StringBuilder();
            var draining = Task.Run(async () =>
            {
                var buffer = new char[1024];
                int count;
                while ((count = await process.StandardOutput.ReadAsync(buffer)) > 0)
                    lock (output) output.Append(buffer, 0, count);
            });
            var stderr = process.StandardError.ReadToEndAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            async Task WaitFor(string text)
            {
                while (true)
                {
                    lock (output) if (output.ToString().Contains(text, StringComparison.Ordinal)) return;
                    await Task.Delay(20, timeout.Token);
                }
            }
            try
            {
                await process.StandardInput.WriteAsync("before-login\n/login fixture oauth\n/login fixture api-key\n");
                await process.StandardInput.FlushAsync();
                await WaitFor("API key for fixture:");
                await process.StandardInput.WriteAsync("fixture-secret-not-for-transcript\n");
                await process.StandardInput.FlushAsync();
                await WaitFor("Authenticated fixture with api-key");
                await process.StandardInput.WriteAsync("/model fixture/fixture-alt\n/thinking high\n/model\n/logout\nafter-logout\n/quit\n");
                process.StandardInput.Close();
                await process.WaitForExitAsync(timeout.Token);
                await draining;
                var transcript = output.ToString();
                Assert.Equal(1, process.ExitCode); // Rejected prompts are failures, not successful inference.
                Assert.Contains("browser authorization is not implemented", transcript);
                Assert.Contains("Authenticated fixture with api-key", transcript);
                Assert.Contains("Model: fixture/fixture-alt", transcript);
                Assert.Contains("does not advertise reasoning support", transcript);
                Assert.Contains("Logged out fixture", transcript);
                Assert.Equal(2, transcript.Split("is not authenticated. Use /login", StringSplitOptions.None).Length - 1);
                Assert.DoesNotContain("fixture-secret-not-for-transcript", transcript);
                Assert.DoesNotContain("Connection refused", transcript);
                Assert.DoesNotContain("fixture-secret-not-for-transcript", await stderr);
                var store = new PiSharp.Runtime.Sessions.ConversationStore(root, Path.Combine(root, "sessions"));
                var session = await store.LoadAsync(Assert.Single(Directory.GetFiles(store.DirectoryPath, "*.session.json")));
                Assert.Empty(session.ActiveMessages());
                Assert.Equal("fixture-alt", session.Model);
                Assert.Equal("fixture", session.Provider);
                Assert.Null(await new AuthStorage(Path.Combine(agentDirectory, "auth.json")).ReadAsync("fixture"));
            }
            catch
            {
                if (!process.HasExited) process.Kill(entireProcessTree: true);
                throw;
            }
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task ConfiguredProviderStreamsThroughCliWithoutCatalogOrCloudCredential()
    {
        var root = Path.Combine(Path.GetTempPath(), "pisharp-provider-http-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        using var listener = StartLoopbackListener(out var port);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        try
        {
            var agentDirectory = Path.Combine(root, "agent");
            Directory.CreateDirectory(agentDirectory);
            await File.WriteAllTextAsync(Path.Combine(agentDirectory, "models.json"), """
                {"providers":{"fixture":{"baseUrl":"http://127.0.0.1:PORT/v1","models":[{"id":"fixture-model"}]}}}
                """.Replace("PORT", port.ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal));
            await new AuthStorage(Path.Combine(agentDirectory, "auth.json")).StoreApiKeyAsync("fixture", "fixture-only-key");
            var server = Task.Run(async () =>
            {
                var request = await listener.GetContextAsync().WaitAsync(timeout.Token);
                var path = request.Request.RawUrl;
                var authorization = request.Request.Headers["Authorization"];
                using var input = new StreamReader(request.Request.InputStream);
                var body = await input.ReadToEndAsync(timeout.Token);
                request.Response.ContentType = "text/event-stream";
                await using (var writer = new StreamWriter(request.Response.OutputStream))
                {
                    await writer.WriteAsync("data: {\"id\":\"chatcmpl-test\",\"object\":\"chat.completion.chunk\",\"created\":1,\"model\":\"fixture-model\",\"choices\":[{\"index\":0,\"delta\":{\"role\":\"assistant\",\"content\":\"PROVIDER_FLOW_OK\"},\"finish_reason\":null}]}\n\n");
                    await writer.WriteAsync("data: {\"id\":\"chatcmpl-test\",\"object\":\"chat.completion.chunk\",\"created\":1,\"model\":\"fixture-model\",\"choices\":[{\"index\":0,\"delta\":{},\"finish_reason\":\"stop\"}]}\n\n");
                    await writer.WriteAsync("data: [DONE]\n\n");
                    await writer.FlushAsync(timeout.Token);
                }
                request.Response.Close();
                return (path, authorization, body);
            }, timeout.Token);
            var start = new System.Diagnostics.ProcessStartInfo("dotnet")
            {
                WorkingDirectory = root,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            foreach (var argument in new[] { typeof(CliArguments).Assembly.Location, "--provider", "fixture", "--model", "fixture-model", "--no-session", "--no-tools", "--print", "provider workflow" })
                start.ArgumentList.Add(argument);
            foreach (var name in new[] { "PISHARP_API_KEY", "PISHARP_BASE_URL", "PISHARP_AUTH_PATH", "PISHARP_MODELS_PATH", "PISHARP_MODEL" })
                start.Environment.Remove(name);
            start.Environment["OPENAI_API_KEY"] = "unrelated-openai-key";
            start.Environment["PISHARP_AGENT_DIR"] = agentDirectory;
            using var process = System.Diagnostics.Process.Start(start)!;
            try
            {
                var stdout = process.StandardOutput.ReadToEndAsync();
                var stderr = process.StandardError.ReadToEndAsync();
                await process.WaitForExitAsync(timeout.Token);
                var request = await server.WaitAsync(timeout.Token);
                Assert.Equal(0, process.ExitCode);
                Assert.Equal("PROVIDER_FLOW_OK\n", await stdout);
                Assert.Equal("", await stderr);
                Assert.StartsWith("/v1/chat/completions", request.path);
                Assert.Equal("Bearer fixture-only-key", request.authorization);
                Assert.DoesNotContain("unrelated-openai-key", request.authorization!);
                Assert.Contains("provider workflow", request.body);
                Assert.Contains("fixture-model", request.body);
            }
            finally
            {
                if (!process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(CancellationToken.None); }
            }
        }
        finally
        {
            timeout.Cancel();
            listener.Stop();
            Directory.Delete(root, true);
        }
    }

    private static HttpListener StartLoopbackListener(out int port)
    {
        for (var attempt = 0; attempt < 10; attempt++)
        {
            using var reservation = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
            reservation.Start();
            port = ((IPEndPoint)reservation.LocalEndpoint).Port;
            reservation.Stop();
            var listener = new HttpListener();
            listener.Prefixes.Add($"http://127.0.0.1:{port}/");
            try
            {
                listener.Start();
                return listener;
            }
            catch (HttpListenerException)
            {
                try { listener.Close(); } catch (HttpListenerException) { }
            }
        }
        throw new InvalidOperationException("Could not reserve a loopback port for the provider fixture.");
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
