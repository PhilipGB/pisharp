using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using PiSharp.Cli;
using PiSharp.Runtime.Sessions;

namespace PiSharp.Tests;

public sealed class RpcExportHtmlProcessTests
{
    [Fact]
    public async Task ExportHtmlDuringBlockedRunWritesTheAcceptedSessionSnapshot()
    {
        if (!OperatingSystem.IsLinux()) return;
        var root = Path.Combine(Path.GetTempPath(), "pisharp-rpc-export-active-" + Guid.NewGuid().ToString("N"));
        var agentDirectory = Path.Combine(root, "agent");
        Directory.CreateDirectory(agentDirectory);
        using var listener = StartLoopbackListener(out var port);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var releaseResponse = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var partialResponseSent = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var server = Task.Run(async () =>
        {
            var blocked = await listener.GetContextAsync().WaitAsync(timeout.Token);
            await WriteBlockedSseResponseAsync(blocked, partialResponseSent, releaseResponse.Task, timeout.Token);
        }, timeout.Token);

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
                        apiKeyEnv = "PISHARP_FIXTURE_KEY",
                        models = new[] { new { id = "rpc-export-fixture", api = "openai-completions" } }
                    }
                }
            }));
            var sessionPath = Path.Combine(root, "active.session.json");
            var session = new ConversationSession(root, "rpc-export-fixture", $"http://127.0.0.1:{port}/v1", "fixture");
            session.Append(new Microsoft.Extensions.AI.ChatMessage(Microsoft.Extensions.AI.ChatRole.User, "saved prompt"));
            session.Append(new Microsoft.Extensions.AI.ChatMessage(Microsoft.Extensions.AI.ChatRole.Assistant, "seed response"));
            await new ConversationStore(root, Path.Combine(root, "sessions")).SaveAsync(session, sessionPath, timeout.Token);
            var start = new ProcessStartInfo("dotnet")
            {
                WorkingDirectory = root,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            start.ArgumentList.Add(typeof(CliArguments).Assembly.Location);
            foreach (var argument in new[] { "--mode", "rpc", "--provider", "fixture", "--model", "rpc-export-fixture",
                "--offline", "--session", sessionPath })
                start.ArgumentList.Add(argument);
            foreach (var name in new[] { "OPENAI_API_KEY", "PISHARP_API_KEY", "PISHARP_BASE_URL", "PISHARP_MODEL",
                "PISHARP_AUTH_PATH", "PISHARP_MODELS_PATH", "PISHARP_SETTINGS_PATH", "PISHARP_PROVIDER", "PISHARP_FIXTURE_KEY" })
                start.Environment.Remove(name);
            start.Environment["PISHARP_AGENT_DIR"] = agentDirectory;
            start.Environment["PISHARP_FIXTURE_KEY"] = "fixture-only-key";
            process = Process.Start(start)!;
            var stderr = process.StandardError.ReadToEndAsync();
            var lines = new List<string>();

            await WriteCommandAsync(process, new { id = "active", type = "prompt", message = "accepted while active" }, timeout.Token);
            using (var response = await ReadResponseAsync(process, lines, "active", timeout.Token))
                Assert.True(response.RootElement.GetProperty("success").GetBoolean());
            await partialResponseSent.Task.WaitAsync(timeout.Token);
            await ReadUntilAsync(process, lines, record => record.GetProperty("type").GetString() == "message_update" &&
                record.GetProperty("assistantMessageEvent").GetProperty("type").GetString() == "text_delta" &&
                record.GetProperty("assistantMessageEvent").GetProperty("delta").GetString() == "partial export text", timeout.Token);

            var exportPath = Path.Combine(root, "active-export.html");
            await WriteCommandAsync(process, new { id = "export-active", type = "export_html", outputPath = exportPath }, timeout.Token);
            using (var response = await ReadResponseAsync(process, lines, "export-active", timeout.Token))
            {
                Assert.True(response.RootElement.GetProperty("success").GetBoolean());
                Assert.Equal(Path.GetFullPath(exportPath), response.RootElement.GetProperty("data").GetProperty("path").GetString());
            }

            var html = await File.ReadAllTextAsync(exportPath, timeout.Token);
            Assert.Contains("saved prompt", html);
            Assert.Contains("seed response", html);
            Assert.Contains("accepted while active", html);
            Assert.DoesNotContain("partial export text", html);
            Assert.False(string.IsNullOrWhiteSpace(await File.ReadAllTextAsync(sessionPath, timeout.Token)));
            if (OperatingSystem.IsLinux())
                Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite,
                    File.GetUnixFileMode(exportPath) & (UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead));

            await WriteCommandAsync(process, new { id = "state-active", type = "get_state" }, timeout.Token);
            using (var state = await ReadResponseAsync(process, lines, "state-active", timeout.Token))
                Assert.True(state.RootElement.GetProperty("data").GetProperty("isStreaming").GetBoolean());

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

    private static async Task WriteBlockedSseResponseAsync(HttpListenerContext context,
        TaskCompletionSource partialResponseSent, Task releaseResponse, CancellationToken cancellationToken)
    {
        context.Response.StatusCode = (int)HttpStatusCode.OK;
        context.Response.ContentType = "text/event-stream";
        await using var writer = new StreamWriter(context.Response.OutputStream);
        var partial = new
        {
            id = "active-answer",
            @object = "chat.completion.chunk",
            created = 1,
            model = "rpc-export-fixture",
            choices = new[] { new { index = 0, delta = new { role = "assistant", content = "partial export text" }, finish_reason = (string?)null } }
        };
        var complete = new
        {
            id = "active-answer",
            @object = "chat.completion.chunk",
            created = 1,
            model = "rpc-export-fixture",
            choices = new[] { new { index = 0, delta = new { }, finish_reason = "stop" } }
        };
        await writer.WriteAsync($"data: {JsonSerializer.Serialize(partial)}\n\n");
        await writer.FlushAsync(cancellationToken);
        partialResponseSent.TrySetResult();
        await releaseResponse.WaitAsync(cancellationToken);
        await writer.WriteAsync($"data: {JsonSerializer.Serialize(complete)}\n\n");
        await writer.WriteAsync("data: [DONE]\n\n");
        await writer.FlushAsync(cancellationToken);
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
