using System.Diagnostics;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using PiSharp.Cli;

namespace PiSharp.Tests;

public sealed class RpcIdleQueueProcessTests
{
    [Fact]
    public async Task IdleSteerAndFollowUpAreQueuedReportedAndClearedInRpcProcess()
    {
        if (!OperatingSystem.IsLinux()) return;
        var root = Path.Combine(Path.GetTempPath(), "pisharp-rpc-idle-queue-" + Guid.NewGuid().ToString("N"));
        var agentDirectory = Path.Combine(root, "agent");
        Directory.CreateDirectory(agentDirectory);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
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
            foreach (var argument in new[] { "--mode", "rpc", "--offline", "--no-session" })
                start.ArgumentList.Add(argument);
            foreach (var name in new[] { "OPENAI_API_KEY", "PISHARP_API_KEY", "PISHARP_BASE_URL", "PISHARP_MODEL",
                "PISHARP_AUTH_PATH", "PISHARP_MODELS_PATH", "PISHARP_SETTINGS_PATH", "PISHARP_PROVIDER" })
                start.Environment.Remove(name);
            start.Environment["PISHARP_AGENT_DIR"] = agentDirectory;
            process = Process.Start(start)!;
            var stderr = process.StandardError.ReadToEndAsync();
            var lines = new List<string>();

            await WriteCommandAsync(process, new { id = "steer", type = "steer", message = "change direction" }, timeout.Token);
            using var steer = await ReadResponseAsync(process, lines, "steer", timeout.Token);
            Assert.True(steer.RootElement.GetProperty("success").GetBoolean());
            Assert.Equal("queued", steer.RootElement.GetProperty("data").GetProperty("disposition").GetString());
            AssertQueueUpdatePrecedesResponse(lines, "steer", ["change direction"], []);

            await WriteCommandAsync(process, new { id = "follow", type = "follow_up", message = "do this later" }, timeout.Token);
            using var follow = await ReadResponseAsync(process, lines, "follow", timeout.Token);
            Assert.True(follow.RootElement.GetProperty("success").GetBoolean());
            Assert.Equal("queued", follow.RootElement.GetProperty("data").GetProperty("disposition").GetString());
            AssertQueueUpdatePrecedesResponse(lines, "follow", ["change direction"], ["do this later"]);

            await WriteCommandAsync(process, new { id = "state", type = "get_state" }, timeout.Token);
            using var state = await ReadResponseAsync(process, lines, "state", timeout.Token);
            Assert.Equal(2, state.RootElement.GetProperty("data").GetProperty("pendingMessageCount").GetInt32());

            await WriteCommandAsync(process, new { id = "clear", type = "clear_queue" }, timeout.Token);
            using var clear = await ReadResponseAsync(process, lines, "clear", timeout.Token);
            Assert.True(clear.RootElement.GetProperty("success").GetBoolean());
            Assert.Equal(["change direction"], clear.RootElement.GetProperty("data").GetProperty("steering")
                .EnumerateArray().Select(item => item.GetString()));
            Assert.Equal(["do this later"], clear.RootElement.GetProperty("data").GetProperty("followUp")
                .EnumerateArray().Select(item => item.GetString()));
            AssertQueueUpdatePrecedesResponse(lines, "clear", [], []);

            await WriteCommandAsync(process, new { id = "empty-state", type = "get_state" }, timeout.Token);
            using var emptyState = await ReadResponseAsync(process, lines, "empty-state", timeout.Token);
            Assert.Equal(0, emptyState.RootElement.GetProperty("data").GetProperty("pendingMessageCount").GetInt32());

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
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task IdleQueuedSteeringAndFollowUpReachTheSameProviderRequestsAsPi()
    {
        if (!OperatingSystem.IsLinux()) return;
        var root = Path.Combine(Path.GetTempPath(), "pisharp-rpc-idle-queue-provider-" + Guid.NewGuid().ToString("N"));
        var agentDirectory = Path.Combine(root, "agent");
        Directory.CreateDirectory(agentDirectory);
        using var listener = StartLoopbackListener(out var port);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var stopProvider = new CancellationTokenSource();
        var requests = new ConcurrentQueue<JsonElement>();
        Process? process = null;
        try
        {
            await File.WriteAllTextAsync(Path.Combine(agentDirectory, "models.json"), JsonSerializer.Serialize(new
            {
                providers = new
                {
                    fixture = new
                    {
                        baseUrl = $"http://127.0.0.1:{port}/v1",
                        apiKeyEnv = "PISHARP_QUEUE_KEY",
                        models = new[] { new { id = "rpc-queue-fixture", api = "openai-completions" } }
                    }
                }
            }));
            var provider = Task.Run(async () =>
            {
                var requestNumber = 0;
                while (!stopProvider.IsCancellationRequested)
                {
                    HttpListenerContext context;
                    try { context = await listener.GetContextAsync().WaitAsync(stopProvider.Token); }
                    catch (OperationCanceledException) { break; }
                    using var reader = new StreamReader(context.Request.InputStream);
                    requests.Enqueue(JsonDocument.Parse(await reader.ReadToEndAsync(stopProvider.Token)).RootElement.Clone());
                    var id = "rpc-queue-" + ++requestNumber;
                    context.Response.ContentType = "text/event-stream";
                    await using (var writer = new StreamWriter(context.Response.OutputStream))
                    {
                        var partial = new
                        {
                            id,
                            @object = "chat.completion.chunk",
                            created = 1,
                            model = "rpc-queue-fixture",
                            choices = new[] { new { index = 0, delta = new { role = "assistant", content = "reply" + requestNumber }, finish_reason = (string?)null } }
                        };
                        var complete = new
                        {
                            id,
                            @object = "chat.completion.chunk",
                            created = 1,
                            model = "rpc-queue-fixture",
                            choices = new[] { new { index = 0, delta = new { }, finish_reason = "stop" } }
                        };
                        await writer.WriteAsync($"data: {JsonSerializer.Serialize(partial)}\n\n");
                        await writer.WriteAsync($"data: {JsonSerializer.Serialize(complete)}\n\n");
                        await writer.WriteAsync("data: [DONE]\n\n");
                        await writer.FlushAsync(stopProvider.Token);
                    }
                    context.Response.Close();
                }
            }, CancellationToken.None);

            var start = new ProcessStartInfo("dotnet")
            {
                WorkingDirectory = root,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            start.ArgumentList.Add(typeof(CliArguments).Assembly.Location);
            foreach (var argument in new[] { "--mode", "rpc", "--provider", "fixture", "--model", "rpc-queue-fixture",
                "--offline", "--no-session", "--no-tools" })
                start.ArgumentList.Add(argument);
            foreach (var name in new[] { "OPENAI_API_KEY", "PISHARP_API_KEY", "PISHARP_BASE_URL", "PISHARP_MODEL",
                "PISHARP_AUTH_PATH", "PISHARP_MODELS_PATH", "PISHARP_SETTINGS_PATH", "PISHARP_PROVIDER", "PISHARP_QUEUE_KEY" })
                start.Environment.Remove(name);
            start.Environment["PISHARP_AGENT_DIR"] = agentDirectory;
            start.Environment["PISHARP_QUEUE_KEY"] = "fixture-only-key";
            process = Process.Start(start)!;
            var stderr = process.StandardError.ReadToEndAsync();
            var lines = new List<string>();

            await WriteCommandAsync(process, new { id = "steer", type = "steer", message = "steer-old" }, deadline.Token);
            using var steer = await ReadResponseAsync(process, lines, "steer", deadline.Token);
            await WriteCommandAsync(process, new { id = "follow", type = "follow_up", message = "follow-old" }, deadline.Token);
            using var follow = await ReadResponseAsync(process, lines, "follow", deadline.Token);
            await WriteCommandAsync(process, new { id = "state", type = "get_state" }, deadline.Token);
            using var state = await ReadResponseAsync(process, lines, "state", deadline.Token);
            Assert.Equal(2, state.RootElement.GetProperty("data").GetProperty("pendingMessageCount").GetInt32());

            await WriteCommandAsync(process, new { id = "prompt", type = "prompt", message = "base" }, deadline.Token);
            using var prompt = await ReadResponseAsync(process, lines, "prompt", deadline.Token);
            Assert.Equal("started", prompt.RootElement.GetProperty("data").GetProperty("disposition").GetString());
            await ReadUntilAsync(process, lines, record => record.GetProperty("type").GetString() == "agent_settled", deadline.Token);
            stopProvider.Cancel();
            await provider.WaitAsync(deadline.Token);

            var payloads = requests.ToArray();
            Assert.Equal(2, payloads.Length);
            Assert.Equal(["base", "steer-old"], UserTexts(payloads[0]));
            Assert.Equal(["base", "steer-old", "follow-old"], UserTexts(payloads[1]));

            process.StandardInput.Close();
            await process.WaitForExitAsync(deadline.Token);
            Assert.Equal(0, process.ExitCode);
            Assert.Equal(string.Empty, await stderr.WaitAsync(deadline.Token));
        }
        finally
        {
            stopProvider.Cancel();
            listener.Close();
            if (process is { HasExited: false })
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync();
            }
            process?.Dispose();
            Directory.Delete(root, recursive: true);
        }
    }

    private static void AssertQueueUpdatePrecedesResponse(IReadOnlyList<string> lines, string id,
        string[] steering, string[] followUp)
    {
        var responseIndex = lines.ToList().FindIndex(line => line.Contains($"\"id\":\"{id}\"", StringComparison.Ordinal));
        Assert.True(responseIndex >= 0);
        var expectedSteering = JsonSerializer.Serialize(steering);
        var expectedFollowUp = JsonSerializer.Serialize(followUp);
        var queueIndex = lines.ToList().FindIndex(line => line.Contains("\"type\":\"queue_update\"", StringComparison.Ordinal) &&
            line.Contains($"\"steering\":{expectedSteering}", StringComparison.Ordinal) &&
            line.Contains($"\"followUp\":{expectedFollowUp}", StringComparison.Ordinal));
        Assert.True(queueIndex >= 0 && queueIndex < responseIndex,
            $"Expected queue_update before {id} response. Records: {string.Join(Environment.NewLine, lines)}");
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

    private static async Task WriteCommandAsync(Process process, object command, CancellationToken cancellationToken)
    {
        await process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(command).AsMemory(), cancellationToken);
        await process.StandardInput.FlushAsync(cancellationToken);
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

    private static string[] UserTexts(JsonElement request) => request.GetProperty("messages").EnumerateArray()
        .Where(message => message.GetProperty("role").GetString() == "user")
        .Select(message => message.GetProperty("content") is { ValueKind: JsonValueKind.String } content
            ? content.GetString() ?? string.Empty
            : string.Concat(message.GetProperty("content").EnumerateArray().Where(part =>
                    part.TryGetProperty("type", out var type) && type.GetString() == "text")
                .Select(part => part.GetProperty("text").GetString())))
        .ToArray();

    private static HttpListener StartLoopbackListener(out int port)
    {
        for (var attempt = 0; attempt < 10; attempt++)
        {
            using var reservation = new TcpListener(IPAddress.Loopback, 0);
            reservation.Start();
            var candidatePort = ((IPEndPoint)reservation.LocalEndpoint).Port;
            reservation.Stop();

            var listener = new HttpListener();
            listener.Prefixes.Add($"http://127.0.0.1:{candidatePort}/");
            try
            {
                listener.Start();
                port = candidatePort;
                return listener;
            }
            catch (HttpListenerException)
            {
                listener.Close();
                if (attempt == 9) throw;
            }
        }

        throw new InvalidOperationException("Could not reserve a loopback port for the provider fixture.");
    }
}
