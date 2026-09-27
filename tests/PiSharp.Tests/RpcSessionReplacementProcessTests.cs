using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using PiSharp.Cli;

namespace PiSharp.Tests;

public sealed class RpcSessionReplacementProcessTests
{
    [Fact]
    public async Task ForkDuringBlockedRunSettlesBeforeTheResponseAndOmitsTheAbortedTurn()
    {
        if (!OperatingSystem.IsLinux()) return;
        var root = Path.Combine(Path.GetTempPath(), "pisharp-rpc-active-fork-" + Guid.NewGuid().ToString("N"));
        var agentDirectory = Path.Combine(root, "agent");
        Directory.CreateDirectory(agentDirectory);
        var listener = StartLoopbackListener(out var port);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        var releaseResponse = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Process? process = null;
        var server = Task.Run(async () =>
        {
            var firstContext = await listener.GetContextAsync().WaitAsync(timeout.Token);
            await WriteSseResponseAsync(firstContext, "chatcmpl-rpc-fork-first", "first reply");

            var activeContext = await listener.GetContextAsync().WaitAsync(timeout.Token);
            activeContext.Response.StatusCode = (int)HttpStatusCode.OK;
            activeContext.Response.ContentType = "text/event-stream";
            activeContext.Response.SendChunked = true;
            await using var writer = new StreamWriter(activeContext.Response.OutputStream);
            var partial = new
            {
                id = "chatcmpl-rpc-fork-active",
                @object = "chat.completion.chunk",
                created = 1,
                model = "rpc-fork-fixture",
                choices = new[] { new { index = 0, delta = new { role = "assistant", content = "interrupted text" }, finish_reason = (string?)null } }
            };
            await writer.WriteAsync($"data: {JsonSerializer.Serialize(partial)}\n\n");
            await writer.FlushAsync(timeout.Token);
            await releaseResponse.Task.WaitAsync(timeout.Token);
            activeContext.Response.Close();
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
                        models = new[] { new { id = "rpc-fork-fixture", api = "openai-completions" } }
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
            foreach (var argument in new[] { "--mode", "rpc", "--provider", "fixture", "--model", "rpc-fork-fixture", "--offline", "--no-session" })
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

            await process.StandardInput.WriteLineAsync("{\"id\":\"first-prompt\",\"type\":\"prompt\",\"message\":\"seed prompt\"}");
            await process.StandardInput.FlushAsync(timeout.Token);
            JsonElement firstEvent;
            do { firstEvent = await ReadRecordAsync(); }
            while (firstEvent.GetProperty("type").GetString() != "agent_settled");

            await process.StandardInput.WriteLineAsync("{\"id\":\"forkable\",\"type\":\"get_fork_messages\"}");
            await process.StandardInput.FlushAsync(timeout.Token);
            JsonElement forkable;
            do { forkable = await ReadRecordAsync(); }
            while (!forkable.TryGetProperty("id", out var forkableId) || forkableId.GetString() != "forkable");
            Assert.True(forkable.GetProperty("success").GetBoolean());
            var selectedEntryId = Assert.Single(forkable.GetProperty("data").GetProperty("messages").EnumerateArray())
                .GetProperty("entryId").GetString();
            Assert.False(string.IsNullOrWhiteSpace(selectedEntryId));

            await process.StandardInput.WriteLineAsync("{\"id\":\"active-prompt\",\"type\":\"prompt\",\"message\":\"block for fork\"}");
            await process.StandardInput.FlushAsync(timeout.Token);
            var partialSeen = false;
            while (!partialSeen)
            {
                var record = await ReadRecordAsync();
                partialSeen = record.GetProperty("type").GetString() == "message_update" &&
                    record.GetProperty("assistantMessageEvent").GetProperty("type").GetString() == "text_delta" &&
                    record.GetProperty("assistantMessageEvent").GetProperty("delta").GetString() == "interrupted text";
            }

            var forkStartIndex = lines.Count;
            await process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(new
            {
                id = "fork-active",
                type = "fork",
                entryId = selectedEntryId
            }));
            await process.StandardInput.FlushAsync(timeout.Token);
            JsonElement forkResponse;
            do { forkResponse = await ReadRecordAsync(); }
            while (!forkResponse.TryGetProperty("id", out var forkId) || forkId.GetString() != "fork-active");
            Assert.True(forkResponse.GetProperty("success").GetBoolean());
            Assert.Equal("seed prompt", forkResponse.GetProperty("data").GetProperty("text").GetString());
            Assert.False(forkResponse.GetProperty("data").GetProperty("cancelled").GetBoolean());

            releaseResponse.TrySetResult();
            await process.StandardInput.WriteLineAsync("{\"id\":\"fork-messages\",\"type\":\"get_messages\"}");
            await process.StandardInput.FlushAsync(timeout.Token);
            JsonElement messagesResponse;
            do { messagesResponse = await ReadRecordAsync(); }
            while (!messagesResponse.TryGetProperty("id", out var messagesId) || messagesId.GetString() != "fork-messages");
            process.StandardInput.Close();
            await process.WaitForExitAsync(timeout.Token);
            await server;

            Assert.Equal(0, process.ExitCode);
            Assert.Equal("", await stderr.WaitAsync(timeout.Token));
            var records = lines.Select(line => JsonDocument.Parse(line)).ToArray();
            try
            {
                var forkEvents = records.Skip(forkStartIndex).ToArray();
                var turnEndIndex = Array.FindIndex(forkEvents, record =>
                    record.RootElement.GetProperty("type").GetString() == "turn_end" &&
                    record.RootElement.GetProperty("message").GetProperty("stopReason").GetString() == "aborted");
                var agentEndIndex = Array.FindIndex(forkEvents, record =>
                    record.RootElement.GetProperty("type").GetString() == "agent_end");
                var settledIndex = Array.FindIndex(forkEvents, record =>
                    record.RootElement.GetProperty("type").GetString() == "agent_settled");
                var responseIndex = Array.FindIndex(forkEvents, record =>
                    record.RootElement.TryGetProperty("id", out var id) && id.GetString() == "fork-active");
                Assert.True(turnEndIndex >= 0 && agentEndIndex > turnEndIndex && settledIndex > agentEndIndex && responseIndex > settledIndex);
                var aborted = forkEvents[turnEndIndex].RootElement.GetProperty("message");
                Assert.Contains("interrupted text", aborted.GetProperty("content")[0].GetProperty("text").GetString());
                Assert.False(forkEvents[agentEndIndex].RootElement.GetProperty("willRetry").GetBoolean());
                Assert.Equal(0, messagesResponse.GetProperty("data").GetProperty("messages").GetArrayLength());
            }
            finally { foreach (var record in records) record.Dispose(); }
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

    [Fact]
    public async Task InvalidSwitchTargetDoesNotCancelAnActiveRun()
    {
        if (!OperatingSystem.IsLinux()) return;
        var root = Path.Combine(Path.GetTempPath(), "pisharp-rpc-invalid-switch-" + Guid.NewGuid().ToString("N"));
        var agentDirectory = Path.Combine(root, "agent");
        var invalidSessionPath = Path.Combine(root, "invalid.jsonl");
        Directory.CreateDirectory(agentDirectory);
        await File.WriteAllTextAsync(invalidSessionPath, "{invalid session entry}\n");
        var listener = StartLoopbackListener(out var port);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var releaseResponse = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Process? process = null;
        var server = Task.Run(async () =>
        {
            var context = await listener.GetContextAsync().WaitAsync(timeout.Token);
            context.Response.StatusCode = (int)HttpStatusCode.OK;
            context.Response.ContentType = "text/event-stream";
            context.Response.SendChunked = true;
            await using var writer = new StreamWriter(context.Response.OutputStream);
            var partial = new
            {
                id = "chatcmpl-rpc-invalid-switch",
                @object = "chat.completion.chunk",
                created = 1,
                model = "rpc-switch-fixture",
                choices = new[] { new { index = 0, delta = new { role = "assistant", content = "still running" }, finish_reason = (string?)null } }
            };
            await writer.WriteAsync($"data: {JsonSerializer.Serialize(partial)}\n\n");
            await writer.FlushAsync(timeout.Token);
            await releaseResponse.Task.WaitAsync(timeout.Token);
            var complete = new
            {
                id = "chatcmpl-rpc-invalid-switch",
                @object = "chat.completion.chunk",
                created = 1,
                model = "rpc-switch-fixture",
                choices = new[] { new { index = 0, delta = new { }, finish_reason = "stop" } }
            };
            await writer.WriteAsync($"data: {JsonSerializer.Serialize(complete)}\n\n");
            await writer.WriteAsync("data: [DONE]\n\n");
            await writer.FlushAsync(timeout.Token);
            context.Response.Close();
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
                        models = new[] { new { id = "rpc-switch-fixture", api = "openai-completions" } }
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
            foreach (var argument in new[] { "--mode", "rpc", "--provider", "fixture", "--model", "rpc-switch-fixture", "--offline", "--no-session" })
                start.ArgumentList.Add(argument);
            foreach (var name in new[] { "OPENAI_API_KEY", "PISHARP_API_KEY", "PISHARP_BASE_URL", "PISHARP_MODEL",
                "PISHARP_AUTH_PATH", "PISHARP_MODELS_PATH", "PISHARP_SETTINGS_PATH", "PISHARP_FIXTURE_KEY" })
                start.Environment.Remove(name);
            start.Environment["PISHARP_AGENT_DIR"] = agentDirectory;
            start.Environment["PISHARP_FIXTURE_KEY"] = "fixture-only-key";
            process = Process.Start(start)!;
            var stderr = process.StandardError.ReadToEndAsync();
            await process.StandardInput.WriteLineAsync("{\"id\":\"prompt\",\"type\":\"prompt\",\"message\":\"continue while switch fails\"}");
            await process.StandardInput.FlushAsync(timeout.Token);

            var lines = new List<string>();
            var sawPartial = false;
            while (!sawPartial)
            {
                var line = await process.StandardOutput.ReadLineAsync(timeout.Token);
                Assert.NotNull(line);
                lines.Add(line!);
                using var record = JsonDocument.Parse(line!);
                var rootElement = record.RootElement;
                sawPartial = rootElement.GetProperty("type").GetString() == "message_update" &&
                    rootElement.GetProperty("assistantMessageEvent").GetProperty("type").GetString() == "text_delta" &&
                    rootElement.GetProperty("assistantMessageEvent").GetProperty("delta").GetString() == "still running";
            }

            await process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(new
            {
                id = "invalid-switch",
                type = "switch_session",
                sessionPath = invalidSessionPath
            }));
            await process.StandardInput.FlushAsync(timeout.Token);
            var switchResponded = false;
            while (!switchResponded)
            {
                var line = await process.StandardOutput.ReadLineAsync(timeout.Token);
                Assert.NotNull(line);
                lines.Add(line!);
                using var record = JsonDocument.Parse(line!);
                switchResponded = record.RootElement.TryGetProperty("id", out var id) && id.GetString() == "invalid-switch";
                if (switchResponded) Assert.False(record.RootElement.GetProperty("success").GetBoolean());
            }

            Assert.DoesNotContain(lines, line =>
            {
                using var record = JsonDocument.Parse(line);
                var type = record.RootElement.GetProperty("type").GetString();
                return type is "agent_end" or "agent_settled" or "turn_end";
            });

            releaseResponse.TrySetResult();
            var settled = false;
            while (!settled)
            {
                var line = await process.StandardOutput.ReadLineAsync(timeout.Token);
                Assert.NotNull(line);
                lines.Add(line!);
                using var record = JsonDocument.Parse(line!);
                settled = record.RootElement.GetProperty("type").GetString() == "agent_settled";
            }
            process.StandardInput.Close();
            await process.WaitForExitAsync(timeout.Token);
            await server;

            Assert.Equal(0, process.ExitCode);
            Assert.Equal("", await stderr.WaitAsync(timeout.Token));
            var records = lines.Select(line => JsonDocument.Parse(line)).ToArray();
            try
            {
                var types = records.Select(record => record.RootElement.GetProperty("type").GetString()).ToArray();
                var responseIndex = Array.FindIndex(records, record =>
                    record.RootElement.TryGetProperty("id", out var id) && id.GetString() == "invalid-switch");
                var turnEndIndex = Array.IndexOf(types, "turn_end");
                var agentEndIndex = Array.IndexOf(types, "agent_end");
                var settledIndex = Array.IndexOf(types, "agent_settled");
                Assert.True(responseIndex >= 0 && turnEndIndex > responseIndex && agentEndIndex > turnEndIndex && settledIndex > agentEndIndex);
                Assert.False(string.IsNullOrEmpty(records[responseIndex].RootElement.GetProperty("error").GetString()));
                var assistantEnd = Assert.Single(records, record =>
                    record.RootElement.GetProperty("type").GetString() == "message_end" &&
                    record.RootElement.GetProperty("message").GetProperty("role").GetString() == "assistant");
                var assistant = assistantEnd.RootElement.GetProperty("message");
                Assert.NotEqual("aborted", assistant.GetProperty("stopReason").GetString());
                Assert.Contains("still running", assistant.GetProperty("content")[0].GetProperty("text").GetString());
                Assert.True(JsonElement.DeepEquals(assistant, records[turnEndIndex].RootElement.GetProperty("message")));
            }
            finally { foreach (var record in records) record.Dispose(); }
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
                model = "rpc-fork-fixture",
                choices = new[] { new { index = 0, delta = new { role = "assistant", content = text }, finish_reason = (string?)null } }
            };
            var complete = new
            {
                id,
                @object = "chat.completion.chunk",
                created = 1,
                model = "rpc-fork-fixture",
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
