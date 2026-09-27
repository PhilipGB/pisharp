using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using PiSharp.Cli;
using PiSharp.Runtime.Sessions;

namespace PiSharp.Tests;

public sealed class RpcInterruptedSessionProcessTests
{
    [Fact]
    public async Task AbortedPartialAssistantIsVisibleInSessionSnapshotsAndNextProviderRequest()
    {
        if (!OperatingSystem.IsLinux()) return;
        var root = Path.Combine(Path.GetTempPath(), "pisharp-rpc-interrupted-" + Guid.NewGuid().ToString("N"));
        var agentDirectory = Path.Combine(root, "agent");
        var sessionDirectory = Path.Combine(root, "sessions");
        Directory.CreateDirectory(agentDirectory);
        var listener = StartLoopbackListener(out var port);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var releaseInterruptedResponse = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var continuationRequest = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        Process? process = null;
        var provider = Task.Run(async () =>
        {
            var interrupted = await listener.GetContextAsync().WaitAsync(timeout.Token);
            using (var reader = new StreamReader(interrupted.Request.InputStream))
                _ = await reader.ReadToEndAsync(timeout.Token);
            interrupted.Response.StatusCode = (int)HttpStatusCode.OK;
            interrupted.Response.ContentType = "text/event-stream";
            interrupted.Response.SendChunked = true;
            await using (var writer = new StreamWriter(interrupted.Response.OutputStream))
            {
                await WriteChunkAsync(writer, "chatcmpl-interrupted", "aborted partial");
                await releaseInterruptedResponse.Task.WaitAsync(timeout.Token);
            }
            try { interrupted.Response.Close(); }
            catch (HttpListenerException) { }

            var continuation = await listener.GetContextAsync().WaitAsync(timeout.Token);
            using (var reader = new StreamReader(continuation.Request.InputStream))
                continuationRequest.TrySetResult(await reader.ReadToEndAsync(timeout.Token));
            await WriteCompleteResponseAsync(continuation, "chatcmpl-continuation", "continued reply", timeout.Token);
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
                        models = new[] { new { id = "rpc-interrupted-fixture", api = "openai-completions" } }
                    }
                }
            }));
            var store = new ConversationStore(root, sessionDirectory);
            var seeded = new ConversationSession(root, "rpc-interrupted-fixture",
                $"http://127.0.0.1:{port}/v1", "fixture");
            seeded.Append(new Microsoft.Extensions.AI.ChatMessage(Microsoft.Extensions.AI.ChatRole.User, "seed prompt"));
            seeded.Append(new Microsoft.Extensions.AI.ChatMessage(Microsoft.Extensions.AI.ChatRole.Assistant, "seed answer"));
            var sessionPath = store.NewPath(seeded);
            await store.SaveAsync(seeded, sessionPath, timeout.Token);
            var start = new ProcessStartInfo("dotnet")
            {
                WorkingDirectory = root,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            start.ArgumentList.Add(typeof(CliArguments).Assembly.Location);
            foreach (var argument in new[] { "--mode", "rpc", "--provider", "fixture", "--model",
                "rpc-interrupted-fixture", "--offline", "--session", sessionPath, "--session-dir", sessionDirectory })
                start.ArgumentList.Add(argument);
            foreach (var name in new[] { "OPENAI_API_KEY", "PISHARP_API_KEY", "PISHARP_BASE_URL", "PISHARP_MODEL",
                "PISHARP_AUTH_PATH", "PISHARP_MODELS_PATH", "PISHARP_SETTINGS_PATH", "PISHARP_SESSION_DIR", "PISHARP_FIXTURE_KEY" })
                start.Environment.Remove(name);
            start.Environment["PISHARP_AGENT_DIR"] = agentDirectory;
            start.Environment["PISHARP_SESSION_DIR"] = sessionDirectory;
            start.Environment["PISHARP_FIXTURE_KEY"] = "fixture-only-key";
            process = Process.Start(start)!;
            var stderr = process.StandardError.ReadToEndAsync();

            await WriteAsync(process, "{\"id\":\"interrupted-prompt\",\"type\":\"prompt\",\"message\":\"start then abort\"}", timeout.Token);
            await ReadUntilAsync(process, record => record.TryGetProperty("type", out var type) &&
                type.GetString() == "message_update" &&
                record.TryGetProperty("assistantMessageEvent", out var item) &&
                item.TryGetProperty("delta", out var delta) && delta.GetString() == "aborted partial", timeout.Token);

            await WriteAsync(process, "{\"id\":\"abort\",\"type\":\"abort\"}", timeout.Token);
            var abortOrSettled = await ReadUntilAsync(process, record =>
                record.TryGetProperty("type", out var type) && type.GetString() == "agent_settled" ||
                record.TryGetProperty("id", out var id) && id.GetString() == "abort", timeout.Token);
            var settled = abortOrSettled.TryGetProperty("type", out var settledType) &&
                settledType.GetString() == "agent_settled";
            var abort = settled
                ? await ReadUntilAsync(process, record => record.TryGetProperty("id", out var id) &&
                    id.GetString() == "abort", timeout.Token)
                : abortOrSettled;
            Assert.True(abort.GetProperty("success").GetBoolean());
            releaseInterruptedResponse.TrySetResult();
            if (!settled)
                await ReadUntilAsync(process, record => record.TryGetProperty("type", out var type) &&
                    type.GetString() == "agent_settled", timeout.Token);

            await WriteAsync(process, "{\"id\":\"entries\",\"type\":\"get_entries\"}", timeout.Token);
            var entriesResponse = await ReadUntilAsync(process, record => record.TryGetProperty("id", out var id) &&
                id.GetString() == "entries", timeout.Token);
            var sessionEntries = entriesResponse.GetProperty("data").GetProperty("entries").EnumerateArray().ToArray();
            var abortedEntries = sessionEntries.Where(entry => entry.TryGetProperty("type", out var type) &&
                type.GetString() == "message" && entry.TryGetProperty("message", out var message) &&
                message.TryGetProperty("stopReason", out var reason) && reason.GetString() == "aborted").ToArray();
            Assert.True(abortedEntries.Length == 1,
                "No unique aborted session message was returned: " + string.Join("\n", sessionEntries.Select(entry => entry.GetRawText())));
            var abortedEntry = abortedEntries[0];
            Assert.Contains("aborted partial", abortedEntry.GetProperty("message").GetProperty("content").GetRawText(),
                StringComparison.Ordinal);
            Assert.Equal("openai-completions", abortedEntry.GetProperty("message").GetProperty("api").GetString());

            await WriteAsync(process, "{\"id\":\"messages\",\"type\":\"get_messages\"}", timeout.Token);
            var messagesResponse = await ReadUntilAsync(process, record => record.TryGetProperty("id", out var id) &&
                id.GetString() == "messages", timeout.Token);
            var abortedMessage = Assert.Single(messagesResponse.GetProperty("data").GetProperty("messages").EnumerateArray(),
                message => message.GetProperty("role").GetString() == "assistant" &&
                    message.GetProperty("stopReason").GetString() == "aborted");
            Assert.Contains("aborted partial", abortedMessage.GetProperty("content").GetRawText(), StringComparison.Ordinal);

            await WriteAsync(process, "{\"id\":\"continue\",\"type\":\"prompt\",\"message\":\"continue safely\"}", timeout.Token);
            await ReadUntilAsync(process, record => record.TryGetProperty("type", out var type) &&
                type.GetString() == "agent_settled", timeout.Token);
            var request = await continuationRequest.Task.WaitAsync(timeout.Token);
            Assert.Contains("aborted partial", request, StringComparison.Ordinal);

            process.StandardInput.Close();
            await process.WaitForExitAsync(timeout.Token);
            await provider;
            Assert.Equal(0, process.ExitCode);
            Assert.Equal("", await stderr.WaitAsync(timeout.Token));

            var persisted = await store.LoadAsync(sessionPath, timeout.Token);
            Assert.Contains(persisted.Tree.Entries, entry => entry.Type == "interrupted");
            Assert.Contains(persisted.ContextMessages(), message => message.Text == "aborted partial");
        }
        finally
        {
            releaseInterruptedResponse.TrySetResult();
            timeout.Cancel();
            listener.Close();
            try { await provider; }
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

    private static async Task<JsonElement> ReadUntilAsync(Process process, Func<JsonElement, bool> predicate,
        CancellationToken cancellationToken)
    {
        while (await process.StandardOutput.ReadLineAsync(cancellationToken) is { } line)
        {
            using var document = JsonDocument.Parse(line);
            var record = document.RootElement.Clone();
            if (predicate(record)) return record;
        }
        throw new InvalidOperationException("RPC process exited before the expected record.");
    }

    private static async Task WriteAsync(Process process, string line, CancellationToken cancellationToken)
    {
        await process.StandardInput.WriteLineAsync(line.AsMemory(), cancellationToken);
        await process.StandardInput.FlushAsync(cancellationToken);
    }

    private static async Task WriteChunkAsync(StreamWriter writer, string id, string text)
    {
        var chunk = new
        {
            id,
            @object = "chat.completion.chunk",
            created = 1,
            model = "rpc-interrupted-fixture",
            choices = new[] { new { index = 0, delta = new { role = "assistant", content = text }, finish_reason = (string?)null } }
        };
        await writer.WriteAsync($"data: {JsonSerializer.Serialize(chunk)}\n\n");
        await writer.FlushAsync();
    }

    private static async Task WriteCompleteResponseAsync(HttpListenerContext context, string id, string text,
        CancellationToken cancellationToken)
    {
        context.Response.StatusCode = (int)HttpStatusCode.OK;
        context.Response.ContentType = "text/event-stream";
        context.Response.SendChunked = true;
        await using var writer = new StreamWriter(context.Response.OutputStream);
        foreach (var chunk in new object[]
        {
            new
            {
                id,
                @object = "chat.completion.chunk",
                created = 1,
                model = "rpc-interrupted-fixture",
                choices = new[] { new { index = 0, delta = new { role = "assistant", content = text }, finish_reason = (string?)null } }
            },
            new
            {
                id,
                @object = "chat.completion.chunk",
                created = 1,
                model = "rpc-interrupted-fixture",
                choices = new[] { new { index = 0, delta = new { }, finish_reason = "stop" } }
            }
        })
        {
            await writer.WriteAsync($"data: {JsonSerializer.Serialize(chunk)}\n\n");
            await writer.FlushAsync(cancellationToken);
        }
        await writer.WriteAsync("data: [DONE]\n\n");
        await writer.FlushAsync(cancellationToken);
        context.Response.Close();
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
}
