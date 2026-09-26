using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using PiSharp.Cli;

namespace PiSharp.Tests;

public sealed class RpcRetryProcessTests
{
    [Fact]
    public async Task RpcProcessAbortsAgentRetryAndPersistsRetrySetting()
    {
        if (!OperatingSystem.IsLinux()) return;
        var root = Path.Combine(Path.GetTempPath(), "pisharp-rpc-retry-" + Guid.NewGuid().ToString("N"));
        var agentDirectory = Path.Combine(root, "agent");
        Directory.CreateDirectory(agentDirectory);
        var listener = StartLoopbackListener(out var port);
        await File.WriteAllTextAsync(Path.Combine(agentDirectory, "models.json"), JsonSerializer.Serialize(new
        {
            providers = new
            {
                fixture = new
                {
                    baseUrl = $"http://127.0.0.1:{port}/v1",
                    apiKeyEnv = "PISHARP_FIXTURE_KEY",
                    models = new[] { new { id = "rpc-retry-fixture", api = "openai-completions" } }
                }
            }
        }));
        var settingsPath = Path.Combine(agentDirectory, "settings.json");
        await File.WriteAllTextAsync(settingsPath, """
            {"retry":{"enabled":true,"maxRetries":2,"baseDelayMs":30000,"maxAgentDelayMs":30000}}
            """);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        Process? process = null;
        var provider = Task.Run(async () =>
        {
            var context = await listener.GetContextAsync().WaitAsync(timeout.Token);
            context.Response.StatusCode = (int)HttpStatusCode.BadRequest;
            context.Response.ContentType = "application/json";
            await using (var writer = new StreamWriter(context.Response.OutputStream))
            {
                await writer.WriteAsync("{\"error\":{\"message\":\"overloaded service unavailable\",\"type\":\"server_error\"}}");
                await writer.FlushAsync(timeout.Token);
            }
            context.Response.Close();
        }, timeout.Token);

        try
        {
            var start = new ProcessStartInfo("dotnet")
            {
                WorkingDirectory = root,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            start.ArgumentList.Add(typeof(CliArguments).Assembly.Location);
            foreach (var argument in new[] { "--mode", "rpc", "--provider", "fixture", "--model", "rpc-retry-fixture",
                "--offline", "--no-session" })
                start.ArgumentList.Add(argument);
            foreach (var name in new[] { "OPENAI_API_KEY", "PISHARP_API_KEY", "PISHARP_BASE_URL", "PISHARP_MODEL",
                "PISHARP_AUTH_PATH", "PISHARP_MODELS_PATH", "PISHARP_SETTINGS_PATH", "PISHARP_FIXTURE_KEY" })
                start.Environment.Remove(name);
            start.Environment["PISHARP_AGENT_DIR"] = agentDirectory;
            start.Environment["PISHARP_FIXTURE_KEY"] = "fixture-only-key";
            process = Process.Start(start)!;
            var stderr = process.StandardError.ReadToEndAsync();
            await WriteAsync(process, "{\"id\":\"prompt\",\"type\":\"prompt\",\"message\":\"hello\"}", timeout.Token);

            var lines = new List<string>();
            var retryStarted = false;
            var abortAcknowledged = false;
            var settingAcknowledged = false;
            var noopAbortAcknowledged = false;
            while (!noopAbortAcknowledged && await process.StandardOutput.ReadLineAsync(timeout.Token) is { } line)
            {
                lines.Add(line);
                using var record = JsonDocument.Parse(line);
                var rootRecord = record.RootElement;
                if (rootRecord.TryGetProperty("type", out var type) && type.GetString() == "auto_retry_start" && !retryStarted)
                {
                    retryStarted = true;
                    await WriteAsync(process, "{\"id\":\"abort\",\"type\":\"abort_retry\"}", timeout.Token);
                }
                if (rootRecord.TryGetProperty("id", out var id))
                {
                    if (id.GetString() == "abort") abortAcknowledged = rootRecord.GetProperty("success").GetBoolean();
                    if (id.GetString() == "prompt") Assert.True(rootRecord.GetProperty("success").GetBoolean());
                }
                if (rootRecord.TryGetProperty("type", out type) && type.GetString() == "agent_settled")
                {
                    await WriteAsync(process,
                        "{\"id\":\"disable\",\"type\":\"set_auto_retry\",\"enabled\":false}", timeout.Token);
                }
                if (rootRecord.TryGetProperty("id", out id) && id.GetString() == "disable")
                {
                    settingAcknowledged = rootRecord.GetProperty("success").GetBoolean();
                    await WriteAsync(process, "{\"id\":\"noop-abort\",\"type\":\"abort_retry\"}", timeout.Token);
                }
                if (rootRecord.TryGetProperty("id", out id) && id.GetString() == "noop-abort")
                    noopAbortAcknowledged = rootRecord.GetProperty("success").GetBoolean();
            }

            process.StandardInput.Close();
            await process.WaitForExitAsync(timeout.Token);
            await provider;

            Assert.Equal(0, process.ExitCode);
            Assert.Equal("", await stderr.WaitAsync(timeout.Token));
            Assert.True(retryStarted);
            Assert.True(abortAcknowledged);
            Assert.True(settingAcknowledged);
            Assert.True(noopAbortAcknowledged);
            var eventTypes = lines.Select(line => JsonDocument.Parse(line)).ToArray();
            try
            {
                var retryStart = Assert.Single(eventTypes, item =>
                    item.RootElement.TryGetProperty("type", out var type) && type.GetString() == "auto_retry_start");
                Assert.Equal(1, retryStart.RootElement.GetProperty("attempt").GetInt32());
                Assert.Equal(2, retryStart.RootElement.GetProperty("maxAttempts").GetInt32());
                Assert.Equal(30_000, retryStart.RootElement.GetProperty("delayMs").GetInt64());
                Assert.Contains("overloaded", retryStart.RootElement.GetProperty("errorMessage").GetString(),
                    StringComparison.OrdinalIgnoreCase);
                var agentEnd = Assert.Single(eventTypes, item =>
                    item.RootElement.TryGetProperty("type", out var type) && type.GetString() == "agent_end");
                Assert.True(agentEnd.RootElement.GetProperty("willRetry").GetBoolean());
                var retryEnd = Assert.Single(eventTypes, item =>
                    item.RootElement.TryGetProperty("type", out var type) && type.GetString() == "auto_retry_end");
                Assert.False(retryEnd.RootElement.GetProperty("success").GetBoolean());
                Assert.Equal("Retry cancelled", retryEnd.RootElement.GetProperty("finalError").GetString());
                var settledIndex = Array.FindIndex(eventTypes, item =>
                    item.RootElement.TryGetProperty("type", out var type) && type.GetString() == "agent_settled");
                Assert.True(settledIndex > Array.IndexOf(eventTypes, retryEnd));
            }
            finally { foreach (var record in eventTypes) record.Dispose(); }

            using var settings = JsonDocument.Parse(await File.ReadAllTextAsync(settingsPath));
            Assert.False(settings.RootElement.GetProperty("retry").GetProperty("enabled").GetBoolean());
            Assert.Equal(2, settings.RootElement.GetProperty("retry").GetProperty("maxRetries").GetInt32());
        }
        finally
        {
            timeout.Cancel();
            listener.Close();
            try { await provider; }
            catch (Exception error) when (error is OperationCanceledException or HttpListenerException or ObjectDisposedException) { }
            if (process is { HasExited: false })
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync();
            }
            process?.Dispose();
            Directory.Delete(root, recursive: true);
        }
    }

    private static async Task WriteAsync(Process process, string line, CancellationToken cancellationToken)
    {
        await process.StandardInput.WriteLineAsync(line.AsMemory(), cancellationToken);
        await process.StandardInput.FlushAsync(cancellationToken);
    }

    private static HttpListener StartLoopbackListener(out int port)
    {
        for (var attempt = 0; attempt < 10; attempt++)
        {
            using var reservation = new TcpListener(IPAddress.Loopback, 0);
            reservation.Start();
            var candidate = ((IPEndPoint)reservation.LocalEndpoint).Port;
            reservation.Stop();
            var listener = new HttpListener();
            listener.Prefixes.Add($"http://127.0.0.1:{candidate}/");
            try
            {
                listener.Start();
                port = candidate;
                return listener;
            }
            catch (HttpListenerException) { listener.Close(); }
        }
        throw new InvalidOperationException("Could not reserve a loopback port for the RPC process test.");
    }
}
