using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using PiSharp.Cli;

namespace PiSharp.Tests;

public sealed class RpcClonePreflightProcessTests
{
    [Fact]
    public async Task MissingSourceSessionRejectsCloneBeforeCancellingActiveRun()
    {
        if (!OperatingSystem.IsLinux()) return;
        var root = Path.Combine(Path.GetTempPath(), "pisharp-rpc-clone-preflight-" + Guid.NewGuid().ToString("N"));
        var agentDirectory = Path.Combine(root, "agent");
        var sessionDirectory = Path.Combine(root, "sessions");
        Directory.CreateDirectory(agentDirectory);
        var listener = StartLoopbackListener(out var port);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var releaseResponse = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Process? process = null;
        var server = Task.Run(async () =>
        {
            var first = await listener.GetContextAsync().WaitAsync(timeout.Token);
            await WriteSseResponseAsync(first, "chatcmpl-clone-preflight-first", "first reply");

            var blocked = await listener.GetContextAsync().WaitAsync(timeout.Token);
            blocked.Response.StatusCode = (int)HttpStatusCode.OK;
            blocked.Response.ContentType = "text/event-stream";
            blocked.Response.SendChunked = true;
            await using var writer = new StreamWriter(blocked.Response.OutputStream);
            var partial = new
            {
                id = "chatcmpl-clone-preflight-blocked",
                @object = "chat.completion.chunk",
                created = 1,
                model = "rpc-clone-preflight-fixture",
                choices = new[] { new { index = 0, delta = new { role = "assistant", content = "still running" }, finish_reason = (string?)null } }
            };
            await writer.WriteAsync($"data: {JsonSerializer.Serialize(partial)}\n\n");
            await writer.FlushAsync(timeout.Token);
            await releaseResponse.Task.WaitAsync(timeout.Token);
            var complete = new
            {
                id = "chatcmpl-clone-preflight-blocked",
                @object = "chat.completion.chunk",
                created = 1,
                model = "rpc-clone-preflight-fixture",
                choices = new[] { new { index = 0, delta = new { role = "assistant", content = "second reply" }, finish_reason = (string?)null } }
            };
            var finish = new
            {
                id = "chatcmpl-clone-preflight-blocked",
                @object = "chat.completion.chunk",
                created = 1,
                model = "rpc-clone-preflight-fixture",
                choices = new[] { new { index = 0, delta = new { }, finish_reason = "stop" } }
            };
            try
            {
                await writer.WriteAsync($"data: {JsonSerializer.Serialize(complete)}\n\n");
                await writer.WriteAsync($"data: {JsonSerializer.Serialize(finish)}\n\n");
                await writer.WriteAsync("data: [DONE]\n\n");
                await writer.FlushAsync(timeout.Token);
                blocked.Response.Close();
            }
            catch (Exception error) when (error is HttpListenerException or IOException or ObjectDisposedException) { }
        }, timeout.Token);

        try
        {
            await File.WriteAllTextAsync(Path.Combine(agentDirectory, "models.json"), JsonSerializer.Serialize(new
            {
                providers = new
                {
                    fixture = new
                    {
                        baseUrl = $"http://127.0.0.1:{port}/v1",
                        apiKeyEnv = "PISHARP_FIXTURE_KEY",
                        models = new[] { new { id = "rpc-clone-preflight-fixture", api = "openai-completions" } }
                    }
                }
            }));
            var start = new ProcessStartInfo("dotnet")
            {
                WorkingDirectory = root,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            start.ArgumentList.Add(typeof(CliArguments).Assembly.Location);
            foreach (var argument in new[] { "--mode", "rpc", "--provider", "fixture", "--model", "rpc-clone-preflight-fixture",
                "--offline", "--session-dir", sessionDirectory })
                start.ArgumentList.Add(argument);
            foreach (var name in new[] { "OPENAI_API_KEY", "PISHARP_API_KEY", "PISHARP_BASE_URL", "PISHARP_MODEL",
                "PISHARP_AUTH_PATH", "PISHARP_MODELS_PATH", "PISHARP_SETTINGS_PATH", "PISHARP_SESSION_DIR", "PISHARP_FIXTURE_KEY" })
                start.Environment.Remove(name);
            start.Environment["PISHARP_AGENT_DIR"] = agentDirectory;
            start.Environment["PISHARP_FIXTURE_KEY"] = "fixture-only-key";
            process = Process.Start(start)!;
            var stderr = process.StandardError.ReadToEndAsync();
            var lines = new List<string>();

            async Task<JsonElement> ReadRecordAsync()
            {
                var line = await process.StandardOutput.ReadLineAsync(timeout.Token);
                Assert.NotNull(line);
                lines.Add(line!);
                using var record = JsonDocument.Parse(line!);
                return record.RootElement.Clone();
            }

            async Task<JsonElement> ReadResponseAsync(string id)
            {
                JsonElement response;
                do { response = await ReadRecordAsync(); }
                while (!response.TryGetProperty("id", out var responseId) || responseId.GetString() != id);
                return response;
            }

            await process.StandardInput.WriteLineAsync("{\"id\":\"first-prompt\",\"type\":\"prompt\",\"message\":\"seed prompt\"}");
            await process.StandardInput.FlushAsync(timeout.Token);
            JsonElement record;
            do { record = await ReadRecordAsync(); }
            while (record.GetProperty("type").GetString() != "agent_settled");
            await process.StandardInput.WriteLineAsync("{\"id\":\"state\",\"type\":\"get_state\"}");
            await process.StandardInput.FlushAsync(timeout.Token);
            var initialState = await ReadResponseAsync("state");
            var sourcePath = initialState.GetProperty("data").GetProperty("sessionFile").GetString();
            Assert.False(string.IsNullOrWhiteSpace(sourcePath));

            await process.StandardInput.WriteLineAsync("{\"id\":\"active-prompt\",\"type\":\"prompt\",\"message\":\"remain active\"}");
            await process.StandardInput.FlushAsync(timeout.Token);
            var partialSeen = false;
            while (!partialSeen)
            {
                record = await ReadRecordAsync();
                partialSeen = record.GetProperty("type").GetString() == "message_update" &&
                    record.GetProperty("assistantMessageEvent").GetProperty("type").GetString() == "text_delta" &&
                    record.GetProperty("assistantMessageEvent").GetProperty("delta").GetString() == "still running";
            }

            var originalSession = await File.ReadAllBytesAsync(sourcePath!);
            File.Delete(sourcePath!);
            await process.StandardInput.WriteLineAsync("{\"id\":\"clone\",\"type\":\"clone\"}");
            await process.StandardInput.FlushAsync(timeout.Token);
            var cloneResponse = await ReadResponseAsync("clone");
            Assert.False(cloneResponse.GetProperty("success").GetBoolean());
            Assert.Equal("This session has not been saved yet. Send a message before cloning or forking it.",
                cloneResponse.GetProperty("error").GetString());
            await File.WriteAllBytesAsync(sourcePath!, originalSession);

            await process.StandardInput.WriteLineAsync("{\"id\":\"active-state\",\"type\":\"get_state\"}");
            await process.StandardInput.FlushAsync(timeout.Token);
            var state = await ReadResponseAsync("active-state");
            Assert.True(state.GetProperty("data").GetProperty("isStreaming").GetBoolean());
            Assert.Single(Directory.GetFiles(sessionDirectory, "*.session.json"));

            releaseResponse.TrySetResult();
            do { record = await ReadRecordAsync(); }
            while (record.GetProperty("type").GetString() != "agent_settled");
            process.StandardInput.Close();
            await process.WaitForExitAsync(timeout.Token);
            await server;

            Assert.Equal(0, process.ExitCode);
            Assert.Equal("", await stderr.WaitAsync(timeout.Token));
            Assert.Contains(lines, line => line.Contains("\"id\":\"clone\"", StringComparison.Ordinal) &&
                line.Contains("\"success\":false", StringComparison.Ordinal));
        }
        finally
        {
            releaseResponse.TrySetResult();
            timeout.Cancel();
            listener.Close();
            try { await server; }
            catch (Exception error) when (error is OperationCanceledException or HttpListenerException or ObjectDisposedException or IOException) { }
            if (process is { HasExited: false })
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync();
            }
            process?.Dispose();
            Directory.Delete(root, recursive: true);
        }
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

    private static async Task WriteSseResponseAsync(HttpListenerContext context, string id, string text)
    {
        context.Response.StatusCode = (int)HttpStatusCode.OK;
        context.Response.ContentType = "text/event-stream";
        await using (var writer = new StreamWriter(context.Response.OutputStream))
        {
            var chunk = new
            {
                id,
                @object = "chat.completion.chunk",
                created = 1,
                model = "rpc-clone-preflight-fixture",
                choices = new[] { new { index = 0, delta = new { role = "assistant", content = text }, finish_reason = (string?)null } }
            };
            var complete = new
            {
                id,
                @object = "chat.completion.chunk",
                created = 1,
                model = "rpc-clone-preflight-fixture",
                choices = new[] { new { index = 0, delta = new { }, finish_reason = "stop" } }
            };
            await writer.WriteAsync($"data: {JsonSerializer.Serialize(chunk)}\n\n");
            await writer.WriteAsync($"data: {JsonSerializer.Serialize(complete)}\n\n");
            await writer.WriteAsync("data: [DONE]\n\n");
            await writer.FlushAsync();
        }
        context.Response.Close();
    }
}
