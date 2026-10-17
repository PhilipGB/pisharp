using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using PiSharp.Cli;
using PiSharp.Tests.TestInfrastructure;

namespace PiSharp.Tests;

[Collection("Managed npm subprocesses")]
public sealed class NativeExtensionControlsProcessTests
{
    [Fact]
    public async Task ExternalPackageControlsDriveTheNextProviderRequest()
    {
        if (!OperatingSystem.IsLinux()) return;
        await using var fixture = new ManagedNpmFixture();
        fixture.Create();
        var sessionDirectory = Path.Combine(fixture.Root, "sessions");
        Directory.CreateDirectory(sessionDirectory);
        await File.WriteAllTextAsync(Path.Combine(fixture.AgentDirectory, "settings.json"),
            "{\"defaultProvider\":\"fixture\",\"defaultModel\":\"native-state-fixture\",\"defaultThinkingLevel\":\"low\"}");
        using var listener = StartLoopbackListener(out var port);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        string? providerRequest = null;
        var provider = Task.Run(async () =>
        {
            var request = await listener.GetContextAsync().WaitAsync(timeout.Token);
            using (var reader = new StreamReader(request.Request.InputStream))
                providerRequest = await reader.ReadToEndAsync(timeout.Token);
            request.Response.StatusCode = (int)HttpStatusCode.OK;
            request.Response.ContentType = "text/event-stream";
            request.Response.SendChunked = true;
            await using var writer = new StreamWriter(request.Response.OutputStream);
            await writer.WriteAsync("data: {\"type\":\"response.created\",\"response\":{\"id\":\"native-controls-run\",\"object\":\"response\",\"created_at\":1,\"model\":\"native-controls-b\",\"status\":\"in_progress\",\"output\":[]}}\n\n");
            await writer.WriteAsync("data: {\"type\":\"response.output_text.delta\",\"item_id\":\"native-controls-message\",\"output_index\":0,\"content_index\":0,\"delta\":\"controls accepted\"}\n\n");
            await writer.WriteAsync("data: {\"type\":\"response.completed\",\"response\":{\"id\":\"native-controls-run\",\"object\":\"response\",\"created_at\":1,\"model\":\"native-controls-b\",\"status\":\"completed\",\"output\":[]}}\n\n");
            await writer.FlushAsync(timeout.Token);
            request.Response.Close();
        }, timeout.Token);

        Process? process = null;
        Process? fresh = null;
        try
        {
            await File.WriteAllTextAsync(Path.Combine(fixture.AgentDirectory, "models.json"), JsonSerializer.Serialize(new
            {
                providers = new
                {
                    fixture = new
                    {
                        baseUrl = $"http://127.0.0.1:{port}/v1",
                        apiKeyEnv = "PISHARP_FIXTURE_KEY",
                        models = new[]
                        {
                            new { id = "native-state-fixture", api = "openai-responses", reasoning = true },
                            new { id = "native-controls-b", api = "openai-responses", reasoning = true }
                        }
                    },
                    locked = new
                    {
                        baseUrl = $"http://127.0.0.1:{port}/v1",
                        apiKeyEnv = "PISHARP_LOCKED_KEY",
                        models = new[] { new { id = "locked-model", api = "openai-responses", reasoning = true } }
                    }
                }
            }));
            var packagePath = await ResourcePackageCommandProcessTests.PackExternalNativeFixtureAsync(
                Path.Combine(fixture.Root, "native-controls-package"));
            var installed = await NativeExtensionStateProcessTests.RunCliAsync(fixture, "install", packagePath);
            Assert.Equal(0, installed.ExitCode);

            process = NativeExtensionStateProcessTests.StartRpcProcess(fixture, sessionDirectory,
                continueSession: false, noTools: false);
            var errors = process.StandardError.ReadToEndAsync();
            Assert.Equal("native_fixture_tool", await NativeExtensionStateProcessTests.RunExtensionCommandAsync(
                process, "set-tools", "/native-fixture-set-active-tools native_fixture_tool", timeout.Token));
            Assert.Equal("Selected", await NativeExtensionStateProcessTests.RunExtensionCommandAsync(process,
                "set-model", "/native-fixture-select-model fixture native-controls-b", timeout.Token));
            Assert.StartsWith("UnknownModel:", await NativeExtensionStateProcessTests.RunExtensionCommandAsync(process,
                "unknown-model", "/native-fixture-select-model fixture does-not-exist", timeout.Token));
            Assert.StartsWith("AuthenticationUnavailable:", await NativeExtensionStateProcessTests.RunExtensionCommandAsync(process,
                "locked-model", "/native-fixture-select-model locked locked-model", timeout.Token));
            Assert.Equal("High", await NativeExtensionStateProcessTests.RunExtensionCommandAsync(process,
                "set-thinking", "/native-fixture-set-thinking High", timeout.Token));

            await NativeExtensionStateProcessTests.RunModelPromptAsync(process, "controlled-turn",
                "native-fixture-structured:inspect the selected controls", timeout.Token);
            await provider.WaitAsync(timeout.Token);
            Assert.NotNull(providerRequest);
            using var requestBody = JsonDocument.Parse(providerRequest!);
            var root = requestBody.RootElement;
            Assert.Equal("native-controls-b", root.GetProperty("model").GetString());
            Assert.Equal("high", root.GetProperty("reasoning").GetProperty("effort").GetString());
            Assert.Contains("native_fixture_tool", providerRequest, StringComparison.Ordinal);
            Assert.Contains("native-fixture-v1-structured-prompt-options", providerRequest, StringComparison.Ordinal);

            await NativeExtensionStateProcessTests.RunExtensionCommandAsync(process, "shutdown",
                "/native-fixture-shutdown", timeout.Token);
            await process.WaitForExitAsync(timeout.Token);
            Assert.Equal(0, process.ExitCode);
            Assert.DoesNotContain("Unhandled exception", await errors.WaitAsync(timeout.Token), StringComparison.Ordinal);

            fresh = NativeExtensionStateProcessTests.StartRpcProcess(fixture, sessionDirectory,
                continueSession: false, noTools: false, model: null, provider: null);
            var freshErrors = fresh.StandardError.ReadToEndAsync();
            string freshResult;
            try
            {
                freshResult = await NativeExtensionStateProcessTests.RunExtensionCommandAsync(
                    fresh, "fresh-defaults", "/native-fixture-controls", timeout.Token);
            }
            catch (EndOfStreamException error) when (fresh.HasExited)
            {
                throw new InvalidOperationException("Fresh process failed before the extension command: " +
                    await freshErrors.WaitAsync(timeout.Token), error);
            }
            using var freshSnapshot = JsonDocument.Parse(freshResult);
            Assert.Equal("native-state-fixture", freshSnapshot.RootElement.GetProperty("model")
                .GetProperty("ModelId").GetString());
            Assert.Equal("Low", freshSnapshot.RootElement.GetProperty("thinking").GetString());
            await NativeExtensionStateProcessTests.RunExtensionCommandAsync(fresh, "fresh-shutdown",
                "/native-fixture-shutdown", timeout.Token);
            await fresh.WaitForExitAsync(timeout.Token);
            Assert.Equal(0, fresh.ExitCode);
            Assert.DoesNotContain("Unhandled exception", await freshErrors.WaitAsync(timeout.Token), StringComparison.Ordinal);
        }
        finally
        {
            foreach (var current in new[] { process, fresh })
            {
                if (current is { HasExited: false })
                {
                    current.Kill(entireProcessTree: true);
                    await current.WaitForExitAsync();
                }
                current?.Dispose();
            }
            try { await provider; }
            catch (Exception error) when (error is OperationCanceledException or HttpListenerException or ObjectDisposedException) { }
        }
    }

    private static HttpListener StartLoopbackListener(out int port)
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
