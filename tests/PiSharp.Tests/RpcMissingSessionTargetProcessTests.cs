using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using PiSharp.Cli;
using PiSharp.Runtime.Sessions;

namespace PiSharp.Tests;

public sealed class RpcMissingSessionTargetProcessTests
{
    [Fact]
    public async Task MissingTargetCreatesLazyPiJsonlSessionAndSettlesCurrentRunBeforeResponse()
    {
        if (!OperatingSystem.IsLinux()) return;
        var root = Path.Combine(Path.GetTempPath(), "pisharp-rpc-missing-session-" + Guid.NewGuid().ToString("N"));
        var agentDirectory = Path.Combine(root, "agent");
        var sessionDirectory = Path.Combine(root, "sessions");
        var targetPath = Path.Combine(root, "created-session.jsonl");
        Directory.CreateDirectory(agentDirectory);
        var listener = StartLoopbackListener(out var port);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        var releaseFirstResponse = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Process? process = null;
        var server = Task.Run(async () =>
        {
            var first = await listener.GetContextAsync().WaitAsync(timeout.Token);
            first.Response.StatusCode = (int)HttpStatusCode.OK;
            first.Response.ContentType = "text/event-stream";
            first.Response.SendChunked = true;
            await using (var writer = new StreamWriter(first.Response.OutputStream))
            {
                var partial = new
                {
                    id = "chatcmpl-rpc-session-old",
                    @object = "chat.completion.chunk",
                    created = 1,
                    model = "rpc-session-fixture",
                    choices = new[] { new { index = 0, delta = new { role = "assistant", content = "outgoing partial" }, finish_reason = (string?)null } }
                };
                await writer.WriteAsync($"data: {JsonSerializer.Serialize(partial)}\n\n");
                await writer.FlushAsync(timeout.Token);
                await releaseFirstResponse.Task.WaitAsync(timeout.Token);
            }
            first.Response.Close();

            var second = await listener.GetContextAsync().WaitAsync(timeout.Token);
            await WriteSseResponseAsync(second, "chatcmpl-rpc-session-new", "new session reply");
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
                        models = new[] { new { id = "rpc-session-fixture", api = "openai-completions" } }
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
            foreach (var argument in new[] { "--mode", "rpc", "--provider", "fixture", "--model", "rpc-session-fixture",
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

            await process.StandardInput.WriteLineAsync("{\"id\":\"old-prompt\",\"type\":\"prompt\",\"message\":\"finish this old turn\"}");
            await process.StandardInput.FlushAsync(timeout.Token);
            var partialSeen = false;
            while (!partialSeen)
            {
                var record = await ReadRecordAsync();
                partialSeen = record.GetProperty("type").GetString() == "message_update" &&
                    record.GetProperty("assistantMessageEvent").GetProperty("type").GetString() == "text_delta" &&
                    record.GetProperty("assistantMessageEvent").GetProperty("delta").GetString() == "outgoing partial";
            }

            var switchStartIndex = lines.Count;
            await process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(new
            {
                id = "switch-missing",
                type = "switch_session",
                sessionPath = targetPath
            }));
            await process.StandardInput.FlushAsync(timeout.Token);
            JsonElement switchResponse;
            do { switchResponse = await ReadRecordAsync(); }
            while (!switchResponse.TryGetProperty("id", out var switchId) || switchId.GetString() != "switch-missing");
            Assert.True(switchResponse.GetProperty("success").GetBoolean());
            Assert.False(switchResponse.GetProperty("data").GetProperty("cancelled").GetBoolean());
            Assert.False(File.Exists(targetPath));

            await process.StandardInput.WriteLineAsync("{\"id\":\"state\",\"type\":\"get_state\"}");
            await process.StandardInput.FlushAsync(timeout.Token);
            JsonElement state;
            do { state = await ReadRecordAsync(); }
            while (!state.TryGetProperty("id", out var stateId) || stateId.GetString() != "state");
            var sessionId = state.GetProperty("data").GetProperty("sessionId").GetString();
            Assert.Equal(targetPath, state.GetProperty("data").GetProperty("sessionFile").GetString());

            releaseFirstResponse.TrySetResult();
            await process.StandardInput.WriteLineAsync("{\"id\":\"new-prompt\",\"type\":\"prompt\",\"message\":\"write the new session\"}");
            await process.StandardInput.FlushAsync(timeout.Token);
            var settled = false;
            while (!settled)
            {
                var record = await ReadRecordAsync();
                settled = record.GetProperty("type").GetString() == "agent_settled";
            }
            var idle = false;
            for (var attempt = 0; attempt < 100 && !idle; attempt++)
            {
                var stateId = $"new-idle-{attempt}";
                await process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(new { id = stateId, type = "get_state" }));
                await process.StandardInput.FlushAsync(timeout.Token);
                JsonElement currentState;
                do { currentState = await ReadRecordAsync(); }
                while (!currentState.TryGetProperty("id", out var currentStateId) || currentStateId.GetString() != stateId);
                idle = !currentState.GetProperty("data").GetProperty("isStreaming").GetBoolean();
                if (idle) Assert.Equal(2, currentState.GetProperty("data").GetProperty("messageCount").GetInt32());
                else await Task.Delay(5, timeout.Token);
            }
            Assert.True(idle);
            process.StandardInput.Close();
            await process.WaitForExitAsync(timeout.Token);
            await server;

            Assert.Equal(0, process.ExitCode);
            Assert.Equal("", await stderr.WaitAsync(timeout.Token));
            Assert.True(File.Exists(targetPath));
            var serialized = await File.ReadAllTextAsync(targetPath);
            Assert.Contains("\"type\":\"session\"", serialized.Split('\n')[0]);
            var imported = PiJsonlSessionInterchange.Import(serialized);
            Assert.Equal(sessionId, imported.Id);
            Assert.Equal(root, imported.WorkingDirectory);
            var activeMessages = imported.ActiveMessages().Select(message => message.Text).ToArray();
            Assert.True(new[] { "write the new session", "new session reply" }.SequenceEqual(activeMessages),
                $"Expected persisted prompt and reply. Active messages: {JsonSerializer.Serialize(activeMessages)}. " +
                $"Session JSONL: {serialized}{Environment.NewLine}RPC records: {string.Join(Environment.NewLine, lines)}");

            var records = lines.Select(line => JsonDocument.Parse(line)).ToArray();
            try
            {
                var replacementEvents = records.Skip(switchStartIndex).ToArray();
                var turnEndIndex = Array.FindIndex(replacementEvents, record =>
                    record.RootElement.GetProperty("type").GetString() == "turn_end" &&
                    record.RootElement.GetProperty("message").GetProperty("stopReason").GetString() == "aborted");
                var agentEndIndex = Array.FindIndex(replacementEvents, record =>
                    record.RootElement.GetProperty("type").GetString() == "agent_end");
                var settledIndex = Array.FindIndex(replacementEvents, record =>
                    record.RootElement.GetProperty("type").GetString() == "agent_settled");
                var responseIndex = Array.FindIndex(replacementEvents, record =>
                    record.RootElement.TryGetProperty("id", out var id) && id.GetString() == "switch-missing");
                Assert.True(turnEndIndex >= 0 && agentEndIndex > turnEndIndex && settledIndex > agentEndIndex && responseIndex > settledIndex);
                Assert.False(replacementEvents[agentEndIndex].RootElement.GetProperty("willRetry").GetBoolean());
            }
            finally { foreach (var record in records) record.Dispose(); }
        }
        finally
        {
            releaseFirstResponse.TrySetResult();
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
                model = "rpc-session-fixture",
                choices = new[] { new { index = 0, delta = new { role = "assistant", content = text }, finish_reason = (string?)null } }
            };
            var complete = new
            {
                id,
                @object = "chat.completion.chunk",
                created = 1,
                model = "rpc-session-fixture",
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
