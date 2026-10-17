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
    public async Task ExternalPackageReadsFlagsAndSettingsAndPersistsSessionMetadataAcrossGracefulShutdown()
    {
        if (!OperatingSystem.IsLinux()) return;
        await using var fixture = new ManagedNpmFixture();
        fixture.Create();
        var sessionDirectory = Path.Combine(fixture.Root, "sessions");
        Directory.CreateDirectory(sessionDirectory);
        var projectConfig = Path.Combine(fixture.ProjectDirectory, ".pi");
        Directory.CreateDirectory(projectConfig);
        await File.WriteAllTextAsync(Path.Combine(fixture.AgentDirectory, "settings.json"),
            "{\"shellCommandPrefix\":\"global-prefix\"}");
        await File.WriteAllTextAsync(Path.Combine(projectConfig, "settings.json"),
            "{\"shellCommandPrefix\":\"project-prefix\"}");
        await File.WriteAllTextAsync(Path.Combine(fixture.AgentDirectory, "models.json"),
            "{\"providers\":{\"fixture\":{\"baseUrl\":\"http://127.0.0.1:1/v1\",\"apiKeyEnv\":\"PISHARP_FIXTURE_KEY\",\"models\":[{\"id\":\"native-state-fixture\",\"api\":\"openai-completions\"}]}}}");
        var lifecyclePath = Path.Combine(fixture.Root, "lifecycle.log");
        var packagePath = await ResourcePackageCommandProcessTests.PackExternalNativeFixtureAsync(
            Path.Combine(fixture.Root, "native-controls-package"));
        var installed = await RunCliAsync(fixture, "install", packagePath);
        Assert.Equal(0, installed.ExitCode);

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        Process? first = null;
        Process? resumed = null;
        try
        {
            var extensionArguments = new[]
            {
                "--approve", "--native-fixture-enabled", "--native-fixture-label", "process-label"
            };
            first = StartRpcProcess(fixture, sessionDirectory, continueSession: false,
                noTools: false, lifecyclePath: lifecyclePath, extraArguments: ["--approve"]);
            var firstErrors = first.StandardError.ReadToEndAsync();
            var snapshot = JsonDocument.Parse(await RunExtensionCommandAsync(first, "snapshot-1",
                "/native-fixture-controls", timeout.Token));
            Assert.Equal("project-prefix", snapshot.RootElement.GetProperty("shellCommandPrefix").GetString());
            Assert.False(snapshot.RootElement.GetProperty("enabled").GetBoolean());
            Assert.Equal("default-label", snapshot.RootElement.GetProperty("label").GetString());
            Assert.Contains("native-fixture-controls", snapshot.RootElement.GetProperty("commands")
                .EnumerateArray().Select(value => value.GetString()));
            Assert.Contains("/model", snapshot.RootElement.GetProperty("commands")
                .EnumerateArray().Select(value => value.GetString()));
            Assert.Contains(snapshot.RootElement.GetProperty("configuredTools").EnumerateArray(), tool =>
                tool.GetProperty("Name").GetString() == "native_fixture_tool");
            Assert.Equal("NotNeeded", await RunExtensionCommandAsync(first, "compact-idle",
                "/native-fixture-compact", timeout.Token));

            Assert.Equal("metadata-control-session", await RunExtensionCommandAsync(first, "session-name",
                "/native-fixture-set-session-name metadata-control-session", timeout.Token));
            var entryId = await RunExtensionCommandAsync(first, "label-set",
                "/native-fixture-label-entry set persistent-label", timeout.Token);
            Assert.False(string.IsNullOrWhiteSpace(entryId));
            await RunExtensionCommandAsync(first, "shutdown", "/native-fixture-shutdown", timeout.Token);
            await first.WaitForExitAsync(timeout.Token);
            Assert.Equal(0, first.ExitCode);
            var firstErrorText = await firstErrors.WaitAsync(timeout.Token);
            Assert.Contains("native-fixture-session-shutdown-v1:Quit", firstErrorText);
            Assert.Equal(1, (await File.ReadAllLinesAsync(lifecyclePath)).Count(line =>
                line == "native-fixture-session-shutdown-v1:Quit"));

            var savedPath = Assert.Single(Directory.GetFiles(sessionDirectory, "*.session.json"));
            var savedSession = await File.ReadAllTextAsync(savedPath);
            Assert.Contains("metadata-control-session", savedSession, StringComparison.Ordinal);
            Assert.Contains("persistent-label", savedSession, StringComparison.Ordinal);

            resumed = StartRpcProcess(fixture, sessionDirectory, continueSession: true,
                noTools: false, lifecyclePath: lifecyclePath, extraArguments: extensionArguments);
            var resumedErrors = resumed.StandardError.ReadToEndAsync();
            using var resumedSnapshot = JsonDocument.Parse(await RunExtensionCommandAsync(resumed, "snapshot-2",
                "/native-fixture-controls", timeout.Token));
            Assert.Equal("metadata-control-session", resumedSnapshot.RootElement.GetProperty("sessionName").GetString());
            Assert.True(resumedSnapshot.RootElement.GetProperty("enabled").GetBoolean());
            Assert.Equal("process-label", resumedSnapshot.RootElement.GetProperty("label").GetString());
            Assert.Equal("label cleared", await RunExtensionCommandAsync(resumed, "label-clear",
                $"/native-fixture-label-entry clear {entryId}", timeout.Token));
            await RunExtensionCommandAsync(resumed, "shutdown-2", "/native-fixture-shutdown", timeout.Token);
            await resumed.WaitForExitAsync(timeout.Token);
            Assert.Equal(0, resumed.ExitCode);
            Assert.Contains("native-fixture-session-start-v1:Resume", await resumedErrors.WaitAsync(timeout.Token));

            using var unknownFlag = StartRpcProcess(fixture, sessionDirectory, continueSession: false,
                extraArguments: ["--approve", "--not-a-registered-extension-flag"]);
            var unknownError = unknownFlag.StandardError.ReadToEndAsync();
            await unknownFlag.WaitForExitAsync(timeout.Token);
            Assert.NotEqual(0, unknownFlag.ExitCode);
            Assert.Contains("Unknown option: --not-a-registered-extension-flag", await unknownError.WaitAsync(timeout.Token));
        }
        finally
        {
            foreach (var process in new[] { first, resumed })
            {
                if (process is { HasExited: false })
                {
                    process.Kill(entireProcessTree: true);
                    await process.WaitForExitAsync();
                }
                process?.Dispose();
            }
        }
    }

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

            resumed = StartRpcProcess(fixture, sessionDirectory, continueSession: true,
                extraArguments: ["--native-fixture-v2-enabled", "--native-fixture-v2-label", "v2-label"]);
            var resumedErrors = resumed.StandardError.ReadToEndAsync();
            Assert.Equal("SECRET_STATE_MARKER|SECRET_STATE_MARKER",
                await RunExtensionCommandAsync(resumed, "resumed-state", "/native-fixture-state-show", timeout.Token));
            using (var controlSnapshot = JsonDocument.Parse(await RunExtensionCommandAsync(resumed,
                "resumed-controls", "/native-fixture-controls", timeout.Token)))
            {
                Assert.Equal("v2", controlSnapshot.RootElement.GetProperty("revision").GetString());
                Assert.True(controlSnapshot.RootElement.GetProperty("enabled").GetBoolean());
                Assert.Equal("v2-label", controlSnapshot.RootElement.GetProperty("label").GetString());
            }
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

    internal static Process StartRpcProcess(ManagedNpmFixture fixture, string sessionDirectory,
        bool continueSession, bool noTools = true, string? lifecyclePath = null,
        string? model = "native-state-fixture", string? provider = "fixture", params string[] extraArguments)
    {
        var start = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = fixture.ProjectDirectory,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        start.ArgumentList.Add(typeof(CliArguments).Assembly.Location);
        foreach (var argument in new[] { "--mode", "rpc" })
            start.ArgumentList.Add(argument);
        if (provider is not null)
        {
            start.ArgumentList.Add("--provider");
            start.ArgumentList.Add(provider);
        }
        if (model is not null)
        {
            start.ArgumentList.Add("--model");
            start.ArgumentList.Add(model);
        }
        foreach (var argument in new[] { "--offline", "--session-dir", sessionDirectory })
            start.ArgumentList.Add(argument);
        if (noTools) start.ArgumentList.Add("--no-tools");
        if (continueSession) start.ArgumentList.Add("--continue");
        foreach (var argument in extraArguments) start.ArgumentList.Add(argument);
        foreach (var name in new[] { "OPENAI_API_KEY", "PISHARP_API_KEY", "PISHARP_BASE_URL", "PISHARP_MODEL",
                     "PISHARP_AUTH_PATH", "PISHARP_MODELS_PATH", "PISHARP_SETTINGS_PATH", "PISHARP_FIXTURE_KEY" })
            start.Environment.Remove(name);
        start.Environment["PISHARP_AGENT_DIR"] = fixture.AgentDirectory;
        start.Environment["PISHARP_SESSION_DIR"] = sessionDirectory;
        start.Environment["PISHARP_FIXTURE_KEY"] = "fixture-only-key";
        if (lifecyclePath is not null) start.Environment["NATIVE_FIXTURE_LIFECYCLE_PATH"] = lifecyclePath;
        return Process.Start(start) ?? throw new IOException("Could not start native extension state RPC fixture.");
    }

    internal static async Task<string> RunExtensionCommandAsync(Process process, string id, string message,
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

    internal static async Task RunModelPromptAsync(Process process, string id, string message,
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

    internal static async Task<(int ExitCode, string Output, string Error)> RunCliAsync(
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
