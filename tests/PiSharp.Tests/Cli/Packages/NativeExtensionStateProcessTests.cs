using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using PiSharp.Cli;
using PiSharp.Tests.TestInfrastructure;

namespace PiSharp.Tests;

[Collection("Managed npm subprocesses")]
public sealed class NativeExtensionStateProcessTests
{
    [Fact]
    public async Task PackedExtensionStateSurvivesFreshRpcProcessAndStaysOutOfProviderContext()
    {
        if (!OperatingSystem.IsLinux()) return;
        await using var fixture = new ManagedNpmFixture();
        fixture.Create();
        var sessionDirectory = Path.Combine(fixture.Root, "sessions");
        Directory.CreateDirectory(sessionDirectory);
        using var listener = StartLoopbackListener(out var port);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(35));
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
            await writer.WriteAsync("data: {\"id\":\"native-state-fixture\",\"object\":\"chat.completion.chunk\",\"created\":1,\"model\":\"native-state-fixture\",\"choices\":[{\"index\":0,\"delta\":{\"role\":\"assistant\",\"content\":\"provider accepted state-free context\"},\"finish_reason\":null}]}\n\n");
            await writer.WriteAsync("data: {\"id\":\"native-state-fixture\",\"object\":\"chat.completion.chunk\",\"created\":1,\"model\":\"native-state-fixture\",\"choices\":[{\"index\":0,\"delta\":{},\"finish_reason\":\"stop\"}]}\n\n");
            await writer.WriteAsync("data: [DONE]\n\n");
            await writer.FlushAsync(timeout.Token);
            request.Response.Close();
        }, timeout.Token);

        Process? first = null;
        Process? resumed = null;
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
                        models = new[] { new { id = "native-state-fixture", api = "openai-completions" } }
                    }
                }
            }));
            var v1 = await ResourcePackageCommandProcessTests.PackExternalNativeFixtureAsync(
                Path.Combine(fixture.Root, "native-state-v1"));
            var v2 = await ResourcePackageCommandProcessTests.PackExternalNativeFixtureAsync(
                Path.Combine(fixture.Root, "native-state-v2"), revision: "v2", version: "2.0.0",
                dependencyVersion: "2.0.0");
            var installed = await RunCliAsync(fixture, "install", v1);
            Assert.Equal(0, installed.ExitCode);
            Assert.Contains("Installed PiSharp-native extension PiSharp.TestExtensions.NativeFixture 1.0.0",
                installed.Output);

            first = StartRpcProcess(fixture, sessionDirectory, continueSession: false);
            var firstErrors = first.StandardError.ReadToEndAsync();
            Assert.Equal("v1 state saved", await RunExtensionCommandAsync(first, "set-state-1",
                "/native-fixture-state-set SECRET_STATE_MARKER", timeout.Token));
            Assert.Equal("v1 state saved", await RunExtensionCommandAsync(first, "set-state-2",
                "/native-fixture-state-set SECRET_STATE_MARKER", timeout.Token));
            Assert.Equal("SECRET_STATE_MARKER|SECRET_STATE_MARKER",
                await RunExtensionCommandAsync(first, "show-state", "/native-fixture-state-show", timeout.Token));
            Assert.Equal("rpc:False", await RunExtensionCommandAsync(first, "mode", "/native-fixture-mode",
                timeout.Token));
            Assert.Equal("native fixture UI selection cancelled",
                await RunExtensionCommandAsync(first, "headless-ui", "/native-fixture-ui", timeout.Token));
            await RunModelPromptAsync(first, "provider-turn", "ordinary model request", timeout.Token);
            first.StandardInput.Close();
            await first.WaitForExitAsync(timeout.Token);
            var firstErrorText = await firstErrors.WaitAsync(timeout.Token);
            await provider.WaitAsync(timeout.Token);

            Assert.Equal(0, first.ExitCode);
            Assert.Contains("native-fixture-session-start-v1:Startup:", firstErrorText);
            Assert.Contains("native-fixture-session-shutdown-v1:Quit", firstErrorText);
            Assert.NotNull(providerRequest);
            Assert.DoesNotContain("SECRET_STATE_MARKER", providerRequest, StringComparison.Ordinal);
            var persistedSession = Assert.Single(Directory.GetFiles(sessionDirectory, "*.session.json"));
            var persistedJson = await File.ReadAllTextAsync(persistedSession);
            Assert.Contains("\"Type\":\"extension_state\"", persistedJson, StringComparison.Ordinal);
            Assert.Contains("SECRET_STATE_MARKER", persistedJson, StringComparison.Ordinal);

            var updated = await RunCliAsync(fixture, "update", "--extension", v2);
            Assert.Equal(0, updated.ExitCode);
            Assert.Contains("Updated PiSharp-native extension PiSharp.TestExtensions.NativeFixture 2.0.0",
                updated.Output);

            resumed = StartRpcProcess(fixture, sessionDirectory, continueSession: true);
            var resumedErrors = resumed.StandardError.ReadToEndAsync();
            Assert.Equal("SECRET_STATE_MARKER|SECRET_STATE_MARKER",
                await RunExtensionCommandAsync(resumed, "resumed-state", "/native-fixture-state-show", timeout.Token));
            await SendRpcCommandAsync(resumed, "new-session", "new_session", timeout.Token);
            Assert.Equal("no native fixture state", await RunExtensionCommandAsync(resumed, "new-state",
                "/native-fixture-state-show", timeout.Token));
            resumed.StandardInput.Close();
            await resumed.WaitForExitAsync(timeout.Token);
            var resumedErrorText = await resumedErrors.WaitAsync(timeout.Token);
            Assert.Equal(0, resumed.ExitCode);
            Assert.Contains("native-fixture-session-start-v2:Resume:SECRET_STATE_MARKER|SECRET_STATE_MARKER",
                resumedErrorText);
            Assert.Contains("native-fixture-session-shutdown-v2:New", resumedErrorText);
            Assert.Contains("native-fixture-session-start-v2:New:", resumedErrorText);
            Assert.Contains("native-fixture-session-shutdown-v2:Quit", resumedErrorText);
        }
        finally
        {
            timeout.Cancel();
            listener.Close();
            foreach (var process in new[] { first, resumed })
            {
                if (process is { HasExited: false })
                {
                    process.Kill(entireProcessTree: true);
                    await process.WaitForExitAsync();
                }
                process?.Dispose();
            }
            try { await provider; }
            catch (Exception error) when (error is OperationCanceledException or HttpListenerException or ObjectDisposedException) { }
        }
    }

    private static Process StartRpcProcess(ManagedNpmFixture fixture, string sessionDirectory,
        bool continueSession)
    {
        var start = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = fixture.ProjectDirectory,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        start.ArgumentList.Add(typeof(CliArguments).Assembly.Location);
        foreach (var argument in new[] { "--mode", "rpc", "--provider", "fixture", "--model",
                     "native-state-fixture", "--offline", "--no-tools", "--session-dir", sessionDirectory })
            start.ArgumentList.Add(argument);
        if (continueSession) start.ArgumentList.Add("--continue");
        foreach (var name in new[] { "OPENAI_API_KEY", "PISHARP_API_KEY", "PISHARP_BASE_URL", "PISHARP_MODEL",
                     "PISHARP_AUTH_PATH", "PISHARP_MODELS_PATH", "PISHARP_SETTINGS_PATH", "PISHARP_FIXTURE_KEY" })
            start.Environment.Remove(name);
        start.Environment["PISHARP_AGENT_DIR"] = fixture.AgentDirectory;
        start.Environment["PISHARP_SESSION_DIR"] = sessionDirectory;
        start.Environment["PISHARP_FIXTURE_KEY"] = "fixture-only-key";
        return Process.Start(start) ?? throw new IOException("Could not start native extension state RPC fixture.");
    }

    private static async Task<string> RunExtensionCommandAsync(Process process, string id, string message,
        CancellationToken cancellationToken)
    {
        await process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(new { id, type = "prompt", message })
            .AsMemory(), cancellationToken);
        await process.StandardInput.FlushAsync(cancellationToken);
        while (await process.StandardOutput.ReadLineAsync(cancellationToken) is { } line)
        {
            using var document = JsonDocument.Parse(line);
            var record = document.RootElement;
            if (record.TryGetProperty("type", out var type) && type.GetString() == "event" &&
                record.TryGetProperty("data", out var data) && data.TryGetProperty("Type", out var eventType) &&
                eventType.GetString() == "extension_command_output" &&
                data.TryGetProperty("OperationId", out var operationId) && operationId.GetString() == id)
                return data.GetProperty("Text").GetString() ?? "";
            if (record.TryGetProperty("id", out var responseId) && responseId.GetString() == id &&
                record.TryGetProperty("type", out type) && type.GetString() == "response")
            {
                Assert.True(record.GetProperty("success").GetBoolean(), line);
                Assert.Equal("handled", record.GetProperty("data").GetProperty("disposition").GetString());
                throw new InvalidOperationException($"Extension command {id} returned no output event.");
            }
            if (record.TryGetProperty("type", out type) && type.GetString() == "extension_error")
                throw new InvalidOperationException(record.GetProperty("error").GetString());
        }
        throw new EndOfStreamException($"RPC process closed before extension command {id} completed.");
    }

    private static async Task SendRpcCommandAsync(Process process, string id, string type,
        CancellationToken cancellationToken)
    {
        await process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(new { id, type }).AsMemory(),
            cancellationToken);
        await process.StandardInput.FlushAsync(cancellationToken);
        while (await process.StandardOutput.ReadLineAsync(cancellationToken) is { } line)
        {
            using var document = JsonDocument.Parse(line);
            var record = document.RootElement;
            if (!record.TryGetProperty("id", out var responseId) || responseId.GetString() != id ||
                !record.TryGetProperty("type", out var responseType) || responseType.GetString() != "response")
                continue;
            Assert.True(record.GetProperty("success").GetBoolean(), line);
            Assert.Equal(type, record.GetProperty("command").GetString());
            return;
        }
        throw new EndOfStreamException($"RPC process closed before command {id} completed.");
    }

    private static async Task RunModelPromptAsync(Process process, string id, string message,
        CancellationToken cancellationToken)
    {
        await process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(new { id, type = "prompt", message })
            .AsMemory(), cancellationToken);
        await process.StandardInput.FlushAsync(cancellationToken);
        var started = false;
        while (await process.StandardOutput.ReadLineAsync(cancellationToken) is { } line)
        {
            using var document = JsonDocument.Parse(line);
            var record = document.RootElement;
            if (record.TryGetProperty("id", out var responseId) && responseId.GetString() == id &&
                record.TryGetProperty("type", out var type) && type.GetString() == "response")
            {
                Assert.True(record.GetProperty("success").GetBoolean(), line);
                Assert.Equal("started", record.GetProperty("data").GetProperty("disposition").GetString());
                started = true;
            }
            if (record.TryGetProperty("type", out type) && type.GetString() == "agent_settled") break;
        }
        Assert.True(started, "The ordinary prompt did not start a provider run.");
    }

    private static async Task<(int ExitCode, string Output, string Error)> RunCliAsync(
        ManagedNpmFixture fixture, params string[] arguments)
    {
        var start = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = fixture.ProjectDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        start.ArgumentList.Add(typeof(CliArguments).Assembly.Location);
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        start.Environment["PISHARP_AGENT_DIR"] = fixture.AgentDirectory;
        start.Environment["PISHARP_SESSION_DIR"] = Path.Combine(fixture.Root, "sessions");
        start.Environment["npm_config_cache"] = fixture.NpmCacheDirectory;
        start.Environment["npm_config_offline"] = "true";
        start.Environment.Remove("PISHARP_SETTINGS_PATH");
        using var process = Process.Start(start) ?? throw new IOException("Could not start PiSharp package command.");
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));
        return (process.ExitCode, await output, await error);
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
