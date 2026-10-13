using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using PiSharp.Cli;

namespace PiSharp.Tests;

public sealed class RpcActiveToolForkProcessTests
{
    [Fact]
    public async Task ForkDuringBlockedToolTurnDoesNotAppendTheAbortedTurnToTheReplacementSession()
    {
        if (!OperatingSystem.IsLinux()) return;
        var root = Path.Combine(Path.GetTempPath(), "pisharp-rpc-active-tool-fork-" + Guid.NewGuid().ToString("N"));
        var agentDirectory = Path.Combine(root, "agent");
        Directory.CreateDirectory(agentDirectory);
        var toolStarted = Path.Combine(root, "tool-started");
        var releaseTool = Path.Combine(root, "release-tool");
        var toolFinished = Path.Combine(root, "tool-finished");
        var listener = StartLoopbackListener(out var port);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        Process? process = null;
        var server = Task.Run(async () =>
        {
            var first = await listener.GetContextAsync().WaitAsync(timeout.Token);
            await WriteSseResponseAsync(first, "active-tool-fork-first", "first reply");

            var second = await listener.GetContextAsync().WaitAsync(timeout.Token);
            await WriteSseResponseAsync(second, "active-tool-fork-tool", new
            {
                role = "assistant",
                tool_calls = new[]
                {
                    new
                    {
                        index = 0,
                        id = "call-blocking-bash",
                        type = "function",
                        function = new
                        {
                            name = "bash",
                            arguments = JsonSerializer.Serialize(new
                            {
                                command = $"touch {ProcessTestHelpers.ShellQuote(toolStarted)}; while [ ! -e {ProcessTestHelpers.ShellQuote(releaseTool)} ]; do sleep 0.02; done; touch {ProcessTestHelpers.ShellQuote(toolFinished)}"
                            })
                        }
                    }
                }
            }, "tool_calls");
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
                        models = new[] { new { id = "active-tool-fork-fixture", api = "openai-completions" } }
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
            foreach (var argument in new[] { "--mode", "rpc", "--provider", "fixture", "--model", "active-tool-fork-fixture",
                         "--tools", "bash", "--offline", "--no-session" })
                start.ArgumentList.Add(argument);
            foreach (var name in new[] { "OPENAI_API_KEY", "PISHARP_API_KEY", "PISHARP_BASE_URL", "PISHARP_MODEL",
                         "PISHARP_AUTH_PATH", "PISHARP_MODELS_PATH", "PISHARP_SETTINGS_PATH", "PISHARP_SESSION_DIR", "PISHARP_FIXTURE_KEY" })
                start.Environment.Remove(name);
            start.Environment["PISHARP_AGENT_DIR"] = agentDirectory;
            start.Environment["PISHARP_FIXTURE_KEY"] = "fixture-only-key";
            process = Process.Start(start)!;
            var stderr = process.StandardError.ReadToEndAsync();
            var lines = new List<string>();

            await WriteCommandAsync(process, new { id = "seed-prompt", type = "prompt", message = "seed prompt" }, timeout.Token);
            await ReadUntilAsync(process, lines, record => record.GetProperty("type").GetString() == "agent_settled", timeout.Token);
            var idle = false;
            for (var attempt = 0; attempt < 100 && !idle; attempt++)
            {
                var id = $"idle-{attempt}";
                await WriteCommandAsync(process, new { id, type = "get_state" }, timeout.Token);
                using var state = await ReadResponseAsync(process, lines, id, timeout.Token);
                idle = !state.RootElement.GetProperty("data").GetProperty("isStreaming").GetBoolean();
                if (!idle) await Task.Delay(5, timeout.Token);
            }
            Assert.True(idle);

            await WriteCommandAsync(process, new { id = "forkable", type = "get_fork_messages" }, timeout.Token);
            using var forkable = await ReadResponseAsync(process, lines, "forkable", timeout.Token);
            Assert.True(forkable.RootElement.GetProperty("success").GetBoolean());
            var selectedEntryId = Assert.Single(forkable.RootElement.GetProperty("data").GetProperty("messages").EnumerateArray())
                .GetProperty("entryId").GetString();
            Assert.False(string.IsNullOrWhiteSpace(selectedEntryId));

            await WriteCommandAsync(process, new { id = "active-tool-prompt", type = "prompt", message = "block in Bash" }, timeout.Token);
            await ReadUntilAsync(process, lines, record => record.GetProperty("type").GetString() == "tool_execution_start", timeout.Token);
            await ProcessTestHelpers.WaitForFileAsync(toolStarted, timeout.Token);

            var forkStartIndex = lines.Count;
            await WriteCommandAsync(process, new { id = "fork-active-tool", type = "fork", entryId = selectedEntryId }, timeout.Token);
            using var fork = await ReadResponseAsync(process, lines, "fork-active-tool", timeout.Token);
            Assert.True(fork.RootElement.GetProperty("success").GetBoolean());
            Assert.Equal("seed prompt", fork.RootElement.GetProperty("data").GetProperty("text").GetString());
            Assert.False(fork.RootElement.GetProperty("data").GetProperty("cancelled").GetBoolean());

            await WriteCommandAsync(process, new { id = "replacement-messages", type = "get_messages" }, timeout.Token);
            using var messages = await ReadResponseAsync(process, lines, "replacement-messages", timeout.Token);
            Assert.Empty(messages.RootElement.GetProperty("data").GetProperty("messages").EnumerateArray());

            process.StandardInput.Close();
            await process.WaitForExitAsync(timeout.Token);
            await server;

            Assert.Equal(0, process.ExitCode);
            Assert.Equal(string.Empty, await stderr.WaitAsync(timeout.Token));
            Assert.False(File.Exists(toolFinished));
            using var records = JsonDocument.Parse("[" + string.Join(',', lines) + "]");
            var forkEvents = records.RootElement.EnumerateArray().Skip(forkStartIndex).ToArray();
            var agentEndIndex = Array.FindIndex(forkEvents, record => record.GetProperty("type").GetString() == "agent_end");
            var settledIndex = Array.FindIndex(forkEvents, record => record.GetProperty("type").GetString() == "agent_settled");
            var responseIndex = Array.FindIndex(forkEvents, record =>
                record.TryGetProperty("id", out var id) && id.GetString() == "fork-active-tool");
            Assert.True(agentEndIndex >= 0 && settledIndex > agentEndIndex && responseIndex > settledIndex);
            var abortedMessages = forkEvents[agentEndIndex].GetProperty("messages").EnumerateArray().ToArray();
            Assert.Equal("aborted", abortedMessages[^1].GetProperty("stopReason").GetString());
        }
        finally
        {
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
        throw new EndOfStreamException("The RPC process exited before returning the expected response.");
    }

    private static async Task ReadUntilAsync(Process process, ICollection<string> lines, Func<JsonElement, bool> predicate,
        CancellationToken cancellationToken)
    {
        while (await process.StandardOutput.ReadLineAsync(cancellationToken) is { } line)
        {
            lines.Add(line);
            using var record = JsonDocument.Parse(line);
            if (predicate(record.RootElement)) return;
        }
        throw new EndOfStreamException("The RPC process exited before writing the expected event.");
    }

    private static async Task WriteCommandAsync(Process process, object command, CancellationToken cancellationToken)
    {
        await process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(command).AsMemory(), cancellationToken);
        await process.StandardInput.FlushAsync(cancellationToken);
    }

    private static async Task WriteSseResponseAsync(HttpListenerContext context, string id, string text) =>
        await WriteSseResponseAsync(context, id, new { role = "assistant", content = text }, "stop");

    private static async Task WriteSseResponseAsync(HttpListenerContext context, string id, object delta, string finishReason)
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
                model = "active-tool-fork-fixture",
                choices = new[] { new { index = 0, delta, finish_reason = (string?)null } }
            };
            var complete = new
            {
                id,
                @object = "chat.completion.chunk",
                created = 1,
                model = "active-tool-fork-fixture",
                choices = new[] { new { index = 0, delta = new { }, finish_reason = finishReason } }
            };
            await writer.WriteAsync($"data: {JsonSerializer.Serialize(chunk)}\n\n");
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
