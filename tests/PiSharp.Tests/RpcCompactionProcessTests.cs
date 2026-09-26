using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Microsoft.Extensions.AI;
using PiSharp.Cli;
using PiSharp.Runtime.Sessions;

namespace PiSharp.Tests;

public sealed class RpcCompactionProcessTests
{
    [Fact]
    public async Task RpcProcessAbortCancelsAnInFlightCompactionAndReportsItsLifecycle()
    {
        if (!OperatingSystem.IsLinux()) return;
        var root = Path.Combine(Path.GetTempPath(), "pisharp-rpc-compact-abort-" + Guid.NewGuid().ToString("N"));
        var agentDirectory = Path.Combine(root, "agent");
        var sessionDirectory = Path.Combine(root, "sessions");
        Directory.CreateDirectory(agentDirectory);
        var listener = StartLoopbackListener(out var port);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var requestReceived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Process? process = null;
        var provider = Task.Run(async () =>
        {
            var context = await listener.GetContextAsync().WaitAsync(timeout.Token);
            using (var reader = new StreamReader(context.Request.InputStream))
                _ = await reader.ReadToEndAsync(timeout.Token);
            requestReceived.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, timeout.Token);
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
                        models = new[] { new { id = "rpc-compact-abort-fixture", api = "openai-completions" } }
                    }
                }
            }));
            var conversation = new ConversationSession(root, "rpc-compact-abort-fixture",
                $"http://127.0.0.1:{port}/v1", "fixture");
            conversation.Append(new ChatMessage(ChatRole.User, "first question"));
            conversation.Append(new ChatMessage(ChatRole.Assistant, "first answer"));
            conversation.Append(new ChatMessage(ChatRole.User, "latest question"));
            var store = new ConversationStore(root, sessionDirectory);
            var sessionPath = store.NewPath(conversation);
            await store.SaveAsync(conversation, sessionPath);

            var start = new ProcessStartInfo("dotnet")
            {
                WorkingDirectory = root,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            start.ArgumentList.Add(typeof(CliArguments).Assembly.Location);
            foreach (var argument in new[] { "--mode", "rpc", "--provider", "fixture", "--model",
                "rpc-compact-abort-fixture", "--offline", "--session", sessionPath, "--session-dir", sessionDirectory })
                start.ArgumentList.Add(argument);
            foreach (var name in new[] { "OPENAI_API_KEY", "PISHARP_API_KEY", "PISHARP_BASE_URL", "PISHARP_MODEL",
                "PISHARP_AUTH_PATH", "PISHARP_MODELS_PATH", "PISHARP_SETTINGS_PATH", "PISHARP_FIXTURE_KEY" })
                start.Environment.Remove(name);
            start.Environment["PISHARP_AGENT_DIR"] = agentDirectory;
            start.Environment["PISHARP_SESSION_DIR"] = sessionDirectory;
            start.Environment["PISHARP_FIXTURE_KEY"] = "fixture-only-key";
            process = Process.Start(start)!;
            var stderr = process.StandardError.ReadToEndAsync();
            await WriteAsync(process, "{\"id\":\"compact\",\"type\":\"compact\"}", timeout.Token);

            var lines = new List<string>();
            while (await process.StandardOutput.ReadLineAsync(timeout.Token) is { } line)
            {
                lines.Add(line);
                using var record = JsonDocument.Parse(line);
                if (record.RootElement.TryGetProperty("type", out var type) && type.GetString() == "compaction_start") break;
            }
            await requestReceived.Task.WaitAsync(timeout.Token);
            await WriteAsync(process, "{\"id\":\"state\",\"type\":\"get_state\"}", timeout.Token);
            while (await process.StandardOutput.ReadLineAsync(timeout.Token) is { } line)
            {
                lines.Add(line);
                using var record = JsonDocument.Parse(line);
                if (record.RootElement.TryGetProperty("id", out var id) && id.GetString() == "state")
                {
                    Assert.True(record.RootElement.GetProperty("data").GetProperty("isCompacting").GetBoolean());
                    break;
                }
            }
            await WriteAsync(process, "{\"id\":\"abort\",\"type\":\"abort\"}", timeout.Token);
            while (await process.StandardOutput.ReadLineAsync(timeout.Token) is { } line)
            {
                lines.Add(line);
                using var record = JsonDocument.Parse(line);
                if (record.RootElement.TryGetProperty("id", out var id) && id.GetString() == "abort") break;
            }
            process.StandardInput.Close();
            await process.WaitForExitAsync(timeout.Token);

            Assert.Equal(0, process.ExitCode);
            Assert.Equal("", await stderr.WaitAsync(timeout.Token));
            var records = lines.Select(line => JsonDocument.Parse(line)).ToArray();
            try
            {
                var end = Assert.Single(records, record => record.RootElement.GetProperty("type").GetString() == "compaction_end");
                Assert.True(end.RootElement.GetProperty("aborted").GetBoolean());
                Assert.False(end.RootElement.TryGetProperty("result", out _));
                Assert.False(end.RootElement.TryGetProperty("errorMessage", out _));
                var compact = Assert.Single(records, record => record.RootElement.TryGetProperty("id", out var id) &&
                    id.GetString() == "compact");
                Assert.False(compact.RootElement.GetProperty("success").GetBoolean());
                var abort = Assert.Single(records, record => record.RootElement.TryGetProperty("id", out var id) &&
                    id.GetString() == "abort");
                Assert.True(abort.RootElement.GetProperty("success").GetBoolean());
                Assert.True(Array.IndexOf(records, end) < Array.IndexOf(records, compact) &&
                    Array.IndexOf(records, compact) < Array.IndexOf(records, abort));
            }
            finally { foreach (var record in records) record.Dispose(); }
        }
        finally
        {
            timeout.Cancel();
            listener.Close();
            try { await provider; }
            catch (Exception exception) when (exception is OperationCanceledException or HttpListenerException or ObjectDisposedException) { }
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
    public async Task RpcProcessReturnsPiCompactionResultAndUsesCustomInstructions()
    {
        if (!OperatingSystem.IsLinux()) return;
        var root = Path.Combine(Path.GetTempPath(), "pisharp-rpc-compact-" + Guid.NewGuid().ToString("N"));
        var agentDirectory = Path.Combine(root, "agent");
        var sessionDirectory = Path.Combine(root, "sessions");
        Directory.CreateDirectory(agentDirectory);
        var listener = StartLoopbackListener(out var port);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        Process? process = null;
        string? providerBody = null;
        var provider = Task.Run(async () =>
        {
            var context = await listener.GetContextAsync().WaitAsync(timeout.Token);
            using (var reader = new StreamReader(context.Request.InputStream))
                providerBody = await reader.ReadToEndAsync(timeout.Token);
            context.Response.StatusCode = (int)HttpStatusCode.OK;
            context.Response.ContentType = "application/json";
            await using (var writer = new StreamWriter(context.Response.OutputStream))
            {
                await writer.WriteAsync("""
                    {"id":"compact-response","object":"chat.completion","created":1,"model":"rpc-compact-fixture","choices":[{"index":0,"message":{"role":"assistant","content":"summary from provider"},"finish_reason":"stop"}],"usage":{"prompt_tokens":14,"completion_tokens":4,"total_tokens":18}}
                    """);
                await writer.FlushAsync(timeout.Token);
            }
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
                        models = new[] { new { id = "rpc-compact-fixture", api = "openai-completions" } }
                    }
                }
            }));
            var conversation = new ConversationSession(root, "rpc-compact-fixture",
                $"http://127.0.0.1:{port}/v1", "fixture");
            conversation.Append(new ChatMessage(ChatRole.User, "first question"));
            conversation.Append(new ChatMessage(ChatRole.Assistant, "first answer"));
            conversation.Append(new ChatMessage(ChatRole.User, "latest question"));
            var store = new ConversationStore(root, sessionDirectory);
            var sessionPath = store.NewPath(conversation);
            await store.SaveAsync(conversation, sessionPath);

            var start = new ProcessStartInfo("dotnet")
            {
                WorkingDirectory = root,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            start.ArgumentList.Add(typeof(CliArguments).Assembly.Location);
            foreach (var argument in new[] { "--mode", "rpc", "--provider", "fixture", "--model",
                "rpc-compact-fixture", "--offline", "--session", sessionPath, "--session-dir", sessionDirectory })
                start.ArgumentList.Add(argument);
            foreach (var name in new[] { "OPENAI_API_KEY", "PISHARP_API_KEY", "PISHARP_BASE_URL", "PISHARP_MODEL",
                "PISHARP_AUTH_PATH", "PISHARP_MODELS_PATH", "PISHARP_SETTINGS_PATH", "PISHARP_FIXTURE_KEY" })
                start.Environment.Remove(name);
            start.Environment["PISHARP_AGENT_DIR"] = agentDirectory;
            start.Environment["PISHARP_SESSION_DIR"] = sessionDirectory;
            start.Environment["PISHARP_FIXTURE_KEY"] = "fixture-only-key";
            process = Process.Start(start)!;
            var output = process.StandardOutput.ReadToEndAsync();
            var error = process.StandardError.ReadToEndAsync();
            await process.StandardInput.WriteLineAsync("{\"id\":\"compact\",\"type\":\"compact\",\"customInstructions\":\"preserve decisions\"}");
            process.StandardInput.Close();
            await process.WaitForExitAsync(timeout.Token);
            await provider;

            Assert.Equal(0, process.ExitCode);
            Assert.Equal("", await error.WaitAsync(timeout.Token));
            Assert.Contains("preserve decisions", providerBody, StringComparison.Ordinal);
            var records = (await output.WaitAsync(timeout.Token)).Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Select(line => JsonDocument.Parse(line)).ToArray();
            try
            {
                var response = Assert.Single(records, record => record.RootElement.TryGetProperty("id", out var id) &&
                    id.GetString() == "compact");
                Assert.True(response.RootElement.GetProperty("success").GetBoolean());
                var data = response.RootElement.GetProperty("data");
                Assert.Equal("summary from provider", data.GetProperty("summary").GetString());
                Assert.False(string.IsNullOrWhiteSpace(data.GetProperty("firstKeptEntryId").GetString()));
                Assert.True(data.GetProperty("tokensBefore").GetInt32() > 0);
                Assert.True(data.GetProperty("estimatedTokensAfter").GetInt32() > 0);
                Assert.Equal(14, data.GetProperty("usage").GetProperty("input").GetInt64());
                Assert.Equal(4, data.GetProperty("usage").GetProperty("output").GetInt64());
                Assert.Equal(18, data.GetProperty("usage").GetProperty("totalTokens").GetInt64());
                Assert.True(data.GetProperty("details").ValueKind == JsonValueKind.Object);
                Assert.Empty(data.GetProperty("details").GetProperty("readFiles").EnumerateArray());
                Assert.Empty(data.GetProperty("details").GetProperty("modifiedFiles").EnumerateArray());
                var persisted = await store.LoadAsync(sessionPath, timeout.Token);
                var persistedCompaction = Assert.Single(persisted.Tree.Entries, entry => entry.Type == "compaction");
                Assert.Equal(data.GetProperty("tokensBefore").GetInt32(),
                    persistedCompaction.Payload.GetProperty("tokensBefore").GetInt32());
                Assert.True(JsonElement.DeepEquals(data.GetProperty("details"),
                    persistedCompaction.Payload.GetProperty("details")));
                var compactionStart = Assert.Single(records, record =>
                    record.RootElement.GetProperty("type").GetString() == "compaction_start");
                var compactionEnd = Assert.Single(records, record =>
                    record.RootElement.GetProperty("type").GetString() == "compaction_end");
                Assert.Equal("manual", compactionStart.RootElement.GetProperty("reason").GetString());
                Assert.Equal("manual", compactionEnd.RootElement.GetProperty("reason").GetString());
                Assert.True(JsonElement.DeepEquals(data, compactionEnd.RootElement.GetProperty("result")));
                Assert.True(Array.IndexOf(records, compactionStart) < Array.IndexOf(records, compactionEnd) &&
                    Array.IndexOf(records, compactionEnd) < Array.IndexOf(records, response));
            }
            finally { foreach (var record in records) record.Dispose(); }
        }
        finally
        {
            timeout.Cancel();
            listener.Close();
            try { await provider; }
            catch (Exception exception) when (exception is OperationCanceledException or HttpListenerException or ObjectDisposedException) { }
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
        using var socket = new TcpListener(IPAddress.Loopback, 0);
        socket.Start();
        port = ((IPEndPoint)socket.LocalEndpoint).Port;
        socket.Stop();
        var listener = new HttpListener();
        listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        listener.Start();
        return listener;
    }

    private static async Task WriteAsync(Process process, string line, CancellationToken cancellationToken)
    {
        await process.StandardInput.WriteLineAsync(line.AsMemory(), cancellationToken);
        await process.StandardInput.FlushAsync(cancellationToken);
    }
}
