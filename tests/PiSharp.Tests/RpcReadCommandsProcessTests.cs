using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Microsoft.Extensions.AI;
using PiSharp.Cli;
using PiSharp.Runtime.Sessions;

namespace PiSharp.Tests;

public sealed class RpcReadCommandsProcessTests
{
    [Fact]
    public async Task GetMessagesReturnsCompactedContextAndAppliedEditsInRpcProcess()
    {
        if (!OperatingSystem.IsLinux()) return;
        var root = Path.Combine(Path.GetTempPath(), "pisharp-rpc-context-session-" + Guid.NewGuid().ToString("N"));
        var agentDirectory = Path.Combine(root, "agent");
        var sessionDirectory = Path.Combine(root, "sessions");
        Directory.CreateDirectory(agentDirectory);
        using var listener = StartLoopbackListener(out var port);
        var sessionPath = Path.Combine(sessionDirectory, "context.session.json");
        var session = new ConversationSession(root, "read-context-fixture",
            $"http://127.0.0.1:{port}/v1", "fixture");
        session.Append(new ChatMessage(ChatRole.User, "old question"));
        session.Append(new ChatMessage(ChatRole.Assistant, "old answer"));
        session.Append(new ChatMessage(ChatRole.User, "retained question"));
        session.Append(new ChatMessage(ChatRole.Assistant, "original answer"));
        var plan = Assert.IsType<ConversationSession.CompactionPlan>(session.PrepareCompaction());
        session.AppendCompaction(plan, "summary of old turn", tokensBefore: 1234);
        var assistantEntry = session.Tree.ActivePath()
            .Last(node => node.Type == "chat" && ConversationSession.RestoreEntry(node).Role == ChatRole.Assistant);
        session.Tree.Append("context_edit", JsonSerializer.SerializeToElement(new
        {
            targetId = assistantEntry.Id,
            replacement = new { content = "edited answer" }
        }));
        await new ConversationStore(root, sessionDirectory).SaveAsync(session, sessionPath);
        await File.WriteAllTextAsync(Path.Combine(agentDirectory, "models.json"), JsonSerializer.Serialize(new
        {
            providers = new
            {
                fixture = new
                {
                    baseUrl = $"http://127.0.0.1:{port}/v1",
                    apiKeyEnv = "PISHARP_FIXTURE_KEY",
                    models = new[] { new { id = "read-context-fixture", api = "openai-completions" } }
                }
            }
        }));

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        Process? process = null;
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
            foreach (var argument in new[] { "--mode", "rpc", "--provider", "fixture", "--model", "read-context-fixture",
                "--offline", "--session", sessionPath, "--session-dir", sessionDirectory })
                start.ArgumentList.Add(argument);
            start.Environment["PISHARP_FIXTURE_KEY"] = "fixture-only-key";
            start.Environment["PISHARP_AGENT_DIR"] = agentDirectory;
            foreach (var name in new[] { "OPENAI_API_KEY", "PISHARP_API_KEY", "PISHARP_BASE_URL", "PISHARP_MODEL",
                "PISHARP_AUTH_PATH", "PISHARP_MODELS_PATH", "PISHARP_SETTINGS_PATH", "PISHARP_PROVIDER" })
                start.Environment.Remove(name);
            process = Process.Start(start)!;
            var stderr = process.StandardError.ReadToEndAsync();
            var lines = new List<string>();

            await WriteCommandAsync(process, new { id = "context", type = "get_messages" }, timeout.Token);
            using var response = await ReadResponseAsync(process, lines, "context", timeout.Token);
            Assert.True(response.RootElement.GetProperty("success").GetBoolean());
            var messages = response.RootElement.GetProperty("data").GetProperty("messages");
            Assert.Equal("compactionSummary", messages[0].GetProperty("role").GetString());
            Assert.Equal("summary of old turn", messages[0].GetProperty("summary").GetString());
            Assert.Equal(1234, messages[0].GetProperty("tokensBefore").GetInt32());
            Assert.Equal("retained question", ReadMessageText(messages[1].GetProperty("content")));
            Assert.Equal("edited answer", ReadMessageText(messages[2].GetProperty("content")));
            Assert.DoesNotContain(messages.EnumerateArray(), message =>
                message.TryGetProperty("content", out var content) &&
                ReadMessageText(content) is "old question" or "original answer");

            process.StandardInput.Close();
            await process.WaitForExitAsync(timeout.Token);
            Assert.Equal(0, process.ExitCode);
            Assert.Equal(string.Empty, await stderr.WaitAsync(timeout.Token));
        }
        finally
        {
            if (process is { HasExited: false })
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync();
            }
            process?.Dispose();
            listener.Close();
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task SessionReadCommandsReturnOneSnapshotWhileProviderRequestIsBlocked()
    {
        if (!OperatingSystem.IsLinux()) return;
        var root = Path.Combine(Path.GetTempPath(), "pisharp-rpc-live-session-" + Guid.NewGuid().ToString("N"));
        var agentDirectory = Path.Combine(root, "agent");
        var sessionDirectory = Path.Combine(root, "sessions");
        Directory.CreateDirectory(agentDirectory);
        using var listener = StartLoopbackListener(out var port);
        var sessionPath = Path.Combine(sessionDirectory, "live.session.json");
        var session = new ConversationSession(root, "read-snapshot-fixture",
            $"http://127.0.0.1:{port}/v1", "fixture");
        session.Append(new ChatMessage(ChatRole.User, "previous question"));
        session.Append(new ChatMessage(ChatRole.Assistant, "previous answer"));
        await new ConversationStore(root, sessionDirectory).SaveAsync(session, sessionPath);
        await File.WriteAllTextAsync(Path.Combine(agentDirectory, "models.json"), JsonSerializer.Serialize(new
        {
            providers = new
            {
                fixture = new
                {
                    baseUrl = $"http://127.0.0.1:{port}/v1",
                    apiKeyEnv = "PISHARP_FIXTURE_KEY",
                    models = new[] { new { id = "read-snapshot-fixture", api = "openai-completions" } }
                }
            }
        }));

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var requestReceived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var partialResponseSent = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseResponse = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var server = Task.Run(async () =>
        {
            var request = await listener.GetContextAsync().WaitAsync(timeout.Token);
            using var body = await JsonDocument.ParseAsync(request.Request.InputStream, cancellationToken: timeout.Token);
            Assert.Contains(body.RootElement.GetProperty("messages").EnumerateArray(), message =>
                message.TryGetProperty("content", out var content) && content.GetString() == "current pending prompt");
            requestReceived.TrySetResult();
            await WriteSseResponseAsync(request, "live-read-final", partialResponseSent, releaseResponse.Task, timeout.Token);
        }, timeout.Token);

        Process? process = null;
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
            foreach (var argument in new[] { "--mode", "rpc", "--provider", "fixture", "--model", "read-snapshot-fixture",
                "--offline", "--session", sessionPath, "--session-dir", sessionDirectory })
                start.ArgumentList.Add(argument);
            start.Environment["PISHARP_FIXTURE_KEY"] = "fixture-only-key";
            start.Environment["PISHARP_AGENT_DIR"] = agentDirectory;
            foreach (var name in new[] { "OPENAI_API_KEY", "PISHARP_API_KEY", "PISHARP_BASE_URL", "PISHARP_MODEL",
                "PISHARP_AUTH_PATH", "PISHARP_MODELS_PATH", "PISHARP_SETTINGS_PATH", "PISHARP_PROVIDER" })
                start.Environment.Remove(name);
            process = Process.Start(start)!;
            var stderr = process.StandardError.ReadToEndAsync();
            var lines = new List<string>();

            await WriteCommandAsync(process, new { id = "blocked-prompt", type = "prompt", message = "current pending prompt" }, timeout.Token);
            using (var prompt = await ReadResponseAsync(process, lines, "blocked-prompt", timeout.Token))
            {
                Assert.True(prompt.RootElement.GetProperty("success").GetBoolean());
                Assert.Equal("started", prompt.RootElement.GetProperty("data").GetProperty("disposition").GetString());
            }
            await requestReceived.Task.WaitAsync(timeout.Token);
            await partialResponseSent.Task.WaitAsync(timeout.Token);
            await ReadUntilAsync(process, lines, record => record.GetProperty("type").GetString() == "message_update", timeout.Token);
            await WriteCommandAsync(process, new { id = "state-live", type = "get_state" }, timeout.Token);
            using (var state = await ReadResponseAsync(process, lines, "state-live", timeout.Token))
                Assert.True(state.RootElement.GetProperty("data").GetProperty("isStreaming").GetBoolean());

            var commandIds = new[] { "messages-live", "entries-live", "tree-live", "fork-messages-live", "last-assistant-live" };
            var commands = new object[]
            {
                new { id = commandIds[0], type = "get_messages" },
                new { id = commandIds[1], type = "get_entries" },
                new { id = commandIds[2], type = "get_tree" },
                new { id = commandIds[3], type = "get_fork_messages" },
                new { id = commandIds[4], type = "get_last_assistant_text" }
            };
            foreach (var command in commands) await WriteCommandAsync(process, command, timeout.Token);
            var responses = new Dictionary<string, JsonDocument>(StringComparer.Ordinal);
            try
            {
                foreach (var id in commandIds) responses.Add(id, await ReadResponseAsync(process, lines, id, timeout.Token));
                foreach (var id in commandIds) Assert.True(responses[id].RootElement.GetProperty("success").GetBoolean(), id);

                var messages = responses["messages-live"].RootElement.GetProperty("data").GetProperty("messages");
                Assert.Contains(messages.EnumerateArray(), message => message.GetProperty("role").GetString() == "user" &&
                    ReadMessageText(message.GetProperty("content")) == "current pending prompt");
                Assert.Contains(messages.EnumerateArray(), message => message.GetProperty("role").GetString() == "assistant" &&
                    ReadMessageText(message.GetProperty("content")) == "previous answer");
                var partialAssistant = Assert.Single(messages.EnumerateArray(), message =>
                    message.GetProperty("role").GetString() == "assistant" &&
                    ReadMessageText(message.GetProperty("content")) == "streamed fragment");
                Assert.Equal("pending", partialAssistant.GetProperty("stopReason").GetString());

                var entriesData = responses["entries-live"].RootElement.GetProperty("data");
                var entries = entriesData.GetProperty("entries");
                Assert.Contains(entries.EnumerateArray(), entry => entry.GetProperty("type").GetString() == "message" &&
                    entry.GetProperty("message").GetProperty("role").GetString() == "user" &&
                    entry.GetProperty("message").GetProperty("content").GetString() == "current pending prompt");
                var leafId = entriesData.GetProperty("leafId").GetString();
                Assert.Equal(entries[entries.GetArrayLength() - 1].GetProperty("id").GetString(), leafId);

                var treeData = responses["tree-live"].RootElement.GetProperty("data");
                Assert.Equal(leafId, treeData.GetProperty("leafId").GetString());
                Assert.True(TreeContains(treeData.GetProperty("tree"), leafId!));

                var forkMessages = responses["fork-messages-live"].RootElement.GetProperty("data").GetProperty("messages");
                Assert.Contains(forkMessages.EnumerateArray(), message => message.GetProperty("text").GetString() == "current pending prompt");
                Assert.Equal("streamed fragment", responses["last-assistant-live"].RootElement.GetProperty("data")
                    .GetProperty("text").GetString());
            }
            finally
            {
                foreach (var response in responses.Values) response.Dispose();
            }

            releaseResponse.TrySetResult();
            await ReadUntilAsync(process, lines, record => record.GetProperty("type").GetString() == "agent_settled", timeout.Token);
            process.StandardInput.Close();
            await process.WaitForExitAsync(timeout.Token);
            await server;
            Assert.Equal(0, process.ExitCode);
            Assert.Equal(string.Empty, await stderr.WaitAsync(timeout.Token));
        }
        finally
        {
            timeout.Cancel();
            listener.Close();
            releaseResponse.TrySetResult();
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

    private static bool TreeContains(JsonElement nodes, string id)
    {
        foreach (var node in nodes.EnumerateArray())
        {
            if (node.GetProperty("entry").GetProperty("id").GetString() == id ||
                TreeContains(node.GetProperty("children"), id)) return true;
        }
        return false;
    }

    private static async Task<JsonDocument> ReadResponseAsync(Process process, ICollection<string> lines, string id,
        CancellationToken cancellationToken)
    {
        while (await process.StandardOutput.ReadLineAsync(cancellationToken) is { } line)
        {
            lines.Add(line);
            using var record = JsonDocument.Parse(line);
            if (record.RootElement.GetProperty("type").GetString() == "response" &&
                record.RootElement.TryGetProperty("id", out var responseId) && responseId.GetString() == id)
                return JsonDocument.Parse(line);
        }
        throw new EndOfStreamException($"RPC process exited before response {id}.");
    }

    private static async Task ReadUntilAsync(Process process, ICollection<string> lines,
        Func<JsonElement, bool> predicate, CancellationToken cancellationToken)
    {
        while (await process.StandardOutput.ReadLineAsync(cancellationToken) is { } line)
        {
            lines.Add(line);
            using var record = JsonDocument.Parse(line);
            if (predicate(record.RootElement)) return;
        }
        throw new EndOfStreamException("RPC process exited before the expected event.");
    }

    private static async Task WriteCommandAsync(Process process, object command, CancellationToken cancellationToken)
    {
        await process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(command).AsMemory(), cancellationToken);
        await process.StandardInput.FlushAsync(cancellationToken);
    }

    private static string ReadMessageText(JsonElement content)
    {
        if (content.ValueKind == JsonValueKind.String) return content.GetString() ?? "";
        if (content.ValueKind != JsonValueKind.Array) return "";
        return string.Concat(content.EnumerateArray()
            .Where(part => part.TryGetProperty("type", out var type) && type.GetString() == "text")
            .Select(part => part.TryGetProperty("text", out var text) ? text.GetString() : ""));
    }

    private static async Task WriteSseResponseAsync(HttpListenerContext context, string id,
        TaskCompletionSource partialResponseSent, Task releaseResponse, CancellationToken cancellationToken)
    {
        context.Response.StatusCode = (int)HttpStatusCode.OK;
        context.Response.ContentType = "text/event-stream";
        await using (var writer = new StreamWriter(context.Response.OutputStream))
        {
            var partial = new
            {
                id,
                @object = "chat.completion.chunk",
                created = 1,
                model = "read-snapshot-fixture",
                choices = new[] { new { index = 0, delta = new { role = "assistant", content = "streamed fragment" }, finish_reason = (string?)null } }
            };
            var complete = new
            {
                id,
                @object = "chat.completion.chunk",
                created = 1,
                model = "read-snapshot-fixture",
                choices = new[] { new { index = 0, delta = new { }, finish_reason = "stop" } }
            };
            await writer.WriteAsync($"data: {JsonSerializer.Serialize(partial)}\n\n");
            await writer.FlushAsync(cancellationToken);
            partialResponseSent.TrySetResult();
            await releaseResponse.WaitAsync(cancellationToken);
            await writer.WriteAsync($"data: {JsonSerializer.Serialize(complete)}\n\n");
            await writer.WriteAsync("data: [DONE]\n\n");
            await writer.FlushAsync();
        }
        context.Response.Close();
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
