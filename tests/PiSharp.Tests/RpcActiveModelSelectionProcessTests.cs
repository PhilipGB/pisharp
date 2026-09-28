using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using PiSharp.Cli;

namespace PiSharp.Tests;

public sealed class RpcActiveModelSelectionProcessTests
{
    [Fact]
    public async Task RpcModelSelectionAndCycleDuringBlockedToolTurnChangeTheNextProviderRequest()
    {
        if (!OperatingSystem.IsLinux()) return;
        var root = Path.Combine(Path.GetTempPath(), "pisharp-rpc-active-model-" + Guid.NewGuid().ToString("N"));
        var agentDirectory = Path.Combine(root, "agent");
        Directory.CreateDirectory(agentDirectory);
        var sessionPath = Path.Combine(root, "active-model.session.json");
        var fifo = Path.Combine(root, "model-switch.fifo");
        var fifoProcess = Process.Start(new ProcessStartInfo("mkfifo") { ArgumentList = { fifo } })!;
        await fifoProcess.WaitForExitAsync();
        Assert.Equal(0, fifoProcess.ExitCode);

        var listener = StartLoopbackListener(out var port);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        Process? process = null;
        var server = Task.Run(async () =>
        {
            var first = await listener.GetContextAsync().WaitAsync(timeout.Token);
            using (var request = await JsonDocument.ParseAsync(first.Request.InputStream, cancellationToken: timeout.Token))
            {
                Assert.Equal("model-three", request.RootElement.GetProperty("model").GetString());
                Assert.Equal("low", request.RootElement.GetProperty("reasoning_effort").GetString());
            }
            await WriteSseResponseAsync(first, "model-three-tool-call", "model-three",
                new
                {
                    role = "assistant",
                    tool_calls = new[]
                    {
                        new
                        {
                            index = 0,
                            id = "call-blocking-bash",
                            type = "function",
                            function = new { name = "bash", arguments = "{\"command\":\"cat model-switch.fifo\"}" }
                        }
                    }
                }, "tool_calls");

            var second = await listener.GetContextAsync().WaitAsync(timeout.Token);
            using (var request = await JsonDocument.ParseAsync(second.Request.InputStream, cancellationToken: timeout.Token))
            {
                Assert.Equal("model-one", request.RootElement.GetProperty("model").GetString());
                Assert.Equal("high", request.RootElement.GetProperty("reasoning_effort").GetString());
            }
            await WriteSseResponseAsync(second, "model-one-final", "model-one",
                new { role = "assistant", content = "switched" }, "stop");

            var compact = await listener.GetContextAsync().WaitAsync(timeout.Token);
            using (var request = await JsonDocument.ParseAsync(compact.Request.InputStream, cancellationToken: timeout.Token))
                Assert.Equal("model-one", request.RootElement.GetProperty("model").GetString());
            compact.Response.StatusCode = (int)HttpStatusCode.OK;
            compact.Response.ContentType = "application/json";
            await using (var writer = new StreamWriter(compact.Response.OutputStream))
            {
                await writer.WriteAsync("""{"id":"model-one-compaction","object":"chat.completion","created":1,"model":"model-one","choices":[{"index":0,"message":{"role":"assistant","content":"compacted current model"},"finish_reason":"stop"}],"usage":{"prompt_tokens":12,"completion_tokens":4,"total_tokens":16}}""");
                await writer.FlushAsync();
            }
            compact.Response.Close();
        }, timeout.Token);

        try
        {
            await File.WriteAllTextAsync(Path.Combine(agentDirectory, "settings.json"), """
                {"defaultThinkingLevel":"low","modelThinkingLevels":{"fixture/model-one":"high","fixture/model-two":"minimal"},"compaction":{"keepRecentTokens":20000,"modelOverrides":{"fixture/model-one":{"keepRecentTokens":1},"fixture/model-two":{"keepRecentTokens":2}}}}
                """);
            await File.WriteAllTextAsync(Path.Combine(agentDirectory, "models.json"), JsonSerializer.Serialize(new
            {
                providers = new
                {
                    fixture = new
                    {
                        baseUrl = $"http://127.0.0.1:{port}/v1",
                        apiKeyEnv = "PISHARP_FIXTURE_KEY",
                        models = new[]
                        {
                            new { id = "model-one", reasoning = true },
                            new { id = "model-two", reasoning = true },
                            new { id = "model-three", reasoning = true }
                        }
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
            foreach (var argument in new[] { "--mode", "rpc", "--provider", "fixture", "--model", "model-three",
                "--models", "fixture/model-three,fixture/model-one", "--tools", "bash", "--offline", "--session", sessionPath })
                start.ArgumentList.Add(argument);
            foreach (var name in new[] { "OPENAI_API_KEY", "PISHARP_API_KEY", "PISHARP_BASE_URL", "PISHARP_MODEL",
                "PISHARP_AUTH_PATH", "PISHARP_MODELS_PATH", "PISHARP_SETTINGS_PATH", "PISHARP_FIXTURE_KEY" })
                start.Environment.Remove(name);
            start.Environment["PISHARP_AGENT_DIR"] = agentDirectory;
            start.Environment["PISHARP_FIXTURE_KEY"] = "fixture-only-key";
            process = Process.Start(start)!;
            var stderr = process.StandardError.ReadToEndAsync();

            await WriteCommandAsync(process, new { id = "active-prompt", type = "prompt", message = "wait for release" }, timeout.Token);
            var lines = new List<string>();
            await ReadUntilAsync(process, lines, rootElement => rootElement.GetProperty("type").GetString() == "tool_execution_start", timeout.Token);

            await WriteCommandAsync(process, new { id = "available-while-busy", type = "get_available_models" }, timeout.Token);
            await ReadUntilAsync(process, lines, rootElement => IsResponse(rootElement, "available-while-busy"), timeout.Token);
            await WriteCommandAsync(process, new
            {
                id = "select-while-busy",
                type = "set_model",
                provider = "fixture",
                modelId = "model-two"
            }, timeout.Token);
            await ReadUntilAsync(process, lines, rootElement => IsResponse(rootElement, "select-while-busy"), timeout.Token);
            await WriteCommandAsync(process, new { id = "cycle-while-busy", type = "cycle_model" }, timeout.Token);
            await ReadUntilAsync(process, lines, rootElement => IsResponse(rootElement, "cycle-while-busy"), timeout.Token);

            var fifoWriter = Task.Run(async () =>
            {
                await using var stream = new FileStream(fifo, FileMode.Open, FileAccess.Write, FileShare.Read, 4096,
                    FileOptions.Asynchronous);
                await stream.WriteAsync(Encoding.UTF8.GetBytes("released"), timeout.Token);
            }, timeout.Token);
            await fifoWriter.WaitAsync(timeout.Token);
            await ReadUntilAsync(process, lines, rootElement => rootElement.GetProperty("type").GetString() == "agent_settled", timeout.Token);
            await WriteCommandAsync(process, new { id = "compact-current-model", type = "compact" }, timeout.Token);
            await ReadUntilAsync(process, lines, rootElement => IsResponse(rootElement, "compact-current-model"), timeout.Token);
            process.StandardInput.Close();
            await process.WaitForExitAsync(timeout.Token);
            await server;

            Assert.Equal(0, process.ExitCode);
            Assert.Equal(string.Empty, await stderr.WaitAsync(timeout.Token));
            using var available = JsonDocument.Parse(Assert.Single(lines, line => IsResponseLine(line, "available-while-busy")));
            Assert.True(available.RootElement.GetProperty("success").GetBoolean());
            var availableModels = available.RootElement.GetProperty("data").GetProperty("models").EnumerateArray().ToArray();
            Assert.Equal(3, availableModels.Length);
            Assert.Contains(availableModels,
                model => model.GetProperty("id").GetString() == "model-two");
            using var selected = JsonDocument.Parse(Assert.Single(lines, line => IsResponseLine(line, "select-while-busy")));
            Assert.True(selected.RootElement.GetProperty("success").GetBoolean());
            Assert.Equal("model-two", selected.RootElement.GetProperty("data").GetProperty("id").GetString());
            var selectedThinkingEvent = lines.FindIndex(line => line.Contains(
                "\"type\":\"thinking_level_changed\",\"level\":\"minimal\"", StringComparison.Ordinal));
            var selectedResponse = lines.FindIndex(line => IsResponseLine(line, "select-while-busy"));
            Assert.True(selectedThinkingEvent >= 0 && selectedThinkingEvent < selectedResponse);
            using var cycled = JsonDocument.Parse(Assert.Single(lines, line => IsResponseLine(line, "cycle-while-busy")));
            Assert.True(cycled.RootElement.GetProperty("success").GetBoolean());
            Assert.Equal("model-one", cycled.RootElement.GetProperty("data").GetProperty("model").GetProperty("id").GetString());
            Assert.Equal("high", cycled.RootElement.GetProperty("data").GetProperty("thinkingLevel").GetString());
            Assert.True(cycled.RootElement.GetProperty("data").GetProperty("isScoped").GetBoolean());
            var cycledThinkingEvent = lines.FindIndex(line => line.Contains(
                "\"type\":\"thinking_level_changed\",\"level\":\"high\"", StringComparison.Ordinal));
            var cycleResponse = lines.FindIndex(line => IsResponseLine(line, "cycle-while-busy"));
            Assert.True(cycledThinkingEvent >= 0 && cycledThinkingEvent < cycleResponse);
            using var compacted = JsonDocument.Parse(Assert.Single(lines, line => IsResponseLine(line, "compact-current-model")));
            Assert.True(compacted.RootElement.GetProperty("success").GetBoolean(), compacted.RootElement.GetRawText());
            Assert.Contains("compacted current model", compacted.RootElement.GetProperty("data").GetProperty("summary").GetString());
            using var session = JsonDocument.Parse(await File.ReadAllTextAsync(sessionPath, timeout.Token));
            var entries = session.RootElement.GetProperty("Entries").EnumerateArray().ToArray();
            var modelChanges = entries.Select((entry, index) => (entry, index))
                .Where(item => item.entry.GetProperty("Type").GetString() == "model_change").ToArray();
            Assert.Equal(["model-two", "model-one"], modelChanges.Select(item =>
                item.entry.GetProperty("Payload").GetProperty("model").GetString()));
            Assert.All(modelChanges, item => Assert.Equal("fixture", item.entry.GetProperty("Payload").GetProperty("provider").GetString()));
            var assistantResponseIndex = Array.FindIndex(entries, entry => entry.GetProperty("Type").GetString() == "chat" &&
                entry.GetProperty("Payload").GetProperty("Message").GetProperty("role").GetString() == "assistant");
            var toolOutcomeIndex = Array.FindIndex(entries, entry => entry.GetProperty("Type").GetString() == "tool_outcome");
            Assert.True(assistantResponseIndex >= 0 && modelChanges[0].index > assistantResponseIndex &&
                modelChanges[1].index > modelChanges[0].index && modelChanges[1].index < toolOutcomeIndex);
        }
        finally
        {
            timeout.Cancel();
            listener.Close();
            try { await server; }
            catch (Exception error) when (error is OperationCanceledException or HttpListenerException or ObjectDisposedException) { }
            if (process is { HasExited: false })
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync();
            }
            process?.Dispose();
            fifoProcess.Dispose();
            Directory.Delete(root, recursive: true);
        }
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
        throw new EndOfStreamException("The RPC process exited before the expected record was written.");
    }

    private static bool IsResponse(JsonElement record, string id) =>
        record.GetProperty("type").GetString() == "response" &&
        record.TryGetProperty("id", out var responseId) && responseId.GetString() == id;

    private static bool IsResponseLine(string line, string id)
    {
        using var record = JsonDocument.Parse(line);
        return IsResponse(record.RootElement, id);
    }

    private static async Task WriteCommandAsync(Process process, object command, CancellationToken cancellationToken)
    {
        await process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(command).AsMemory(), cancellationToken);
        await process.StandardInput.FlushAsync(cancellationToken);
    }

    private static async Task WriteSseResponseAsync(HttpListenerContext context, string id, string model,
        object delta, string finishReason)
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
                model,
                choices = new[] { new { index = 0, delta, finish_reason = (string?)null } }
            };
            var complete = new
            {
                id,
                @object = "chat.completion.chunk",
                created = 1,
                model,
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
