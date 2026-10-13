using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Threading.Channels;
using PiSharp.Cli;

namespace PiSharp.Tests;

public sealed class RpcExtensionCommandProcessTests
{
    [Fact]
    public async Task ExtensionCommandsRunDuringProviderWorkAndReturnHandledOnSuccessOrFailure()
    {
        if (!OperatingSystem.IsLinux()) return;
        var root = Path.Combine(Path.GetTempPath(), "pisharp-rpc-extension-command-" + Guid.NewGuid().ToString("N"));
        var agentDirectory = Path.Combine(root, "agent");
        Directory.CreateDirectory(agentDirectory);
        using var listener = StartLoopbackListener(out var port);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        var providerStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseProvider = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Process? process = null;
        Task? outputReader = null;
        var records = new List<JsonElement>();
        var output = Channel.CreateUnbounded<JsonElement>();
        var provider = Task.Run(async () =>
        {
            var request = await listener.GetContextAsync().WaitAsync(timeout.Token);
            Assert.Equal("/v1/chat/completions", request.Request.Url?.AbsolutePath);
            using var reader = new StreamReader(request.Request.InputStream);
            await reader.ReadToEndAsync(timeout.Token);
            providerStarted.TrySetResult();
            await releaseProvider.Task.WaitAsync(timeout.Token);
            request.Response.ContentType = "text/event-stream";
            request.Response.SendChunked = true;
            await using var writer = new StreamWriter(request.Response.OutputStream);
            await writer.WriteAsync("data: {\"id\":\"rpc-extension\",\"object\":\"chat.completion.chunk\",\"created\":1,\"model\":\"rpc-extension-command-fixture\",\"choices\":[{\"index\":0,\"delta\":{\"role\":\"assistant\",\"content\":\"provider done\"},\"finish_reason\":null}]}\n\n");
            await writer.WriteAsync("data: {\"id\":\"rpc-extension\",\"object\":\"chat.completion.chunk\",\"created\":1,\"model\":\"rpc-extension-command-fixture\",\"choices\":[{\"index\":0,\"delta\":{},\"finish_reason\":\"stop\"}]}\n\n");
            await writer.WriteAsync("data: [DONE]\n\n");
            await writer.FlushAsync(timeout.Token);
            request.Response.Close();
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
                        models = new[] { new { id = "rpc-extension-command-fixture", api = "openai-completions" } }
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
            foreach (var argument in new[]
            {
                "--mode", "rpc", "--provider", "fixture", "--model", "rpc-extension-command-fixture",
                "--offline", "--no-session", "--no-tools", "--no-extensions", "--extension",
                typeof(FixtureExtension).Assembly.Location
            }) start.ArgumentList.Add(argument);
            foreach (var name in new[] { "OPENAI_API_KEY", "PISHARP_API_KEY", "PISHARP_BASE_URL", "PISHARP_MODEL",
                "PISHARP_AUTH_PATH", "PISHARP_MODELS_PATH", "PISHARP_SETTINGS_PATH", "PISHARP_FIXTURE_KEY" })
                start.Environment.Remove(name);
            start.Environment["PISHARP_AGENT_DIR"] = agentDirectory;
            start.Environment["PISHARP_FIXTURE_KEY"] = "fixture-only-key";
            process = Process.Start(start)!;
            var stderr = process.StandardError.ReadToEndAsync();
            outputReader = Task.Run(async () =>
            {
                try
                {
                    while (await process.StandardOutput.ReadLineAsync(timeout.Token) is { } line)
                    {
                        using var document = JsonDocument.Parse(line);
                        await output.Writer.WriteAsync(document.RootElement.Clone(), timeout.Token);
                    }
                }
                finally { output.Writer.TryComplete(); }
            }, timeout.Token);

            await process.StandardInput.WriteLineAsync("{\"id\":\"start\",\"type\":\"prompt\",\"message\":\"start provider run\"}");
            await process.StandardInput.FlushAsync(timeout.Token);
            var startResponse = await ReadUntilAsync(IsResponse("start"));
            Assert.True(startResponse.GetProperty("success").GetBoolean());
            Assert.Equal("started", startResponse.GetProperty("data").GetProperty("disposition").GetString());
            await providerStarted.Task.WaitAsync(timeout.Token);

            await process.StandardInput.WriteLineAsync("{\"id\":\"busy-command\",\"type\":\"prompt\",\"message\":\"/fixture busy argument\",\"images\":[{\"type\":\"image\",\"data\":\"AA==\",\"mimeType\":\"image/png\"}]}");
            await process.StandardInput.FlushAsync(timeout.Token);
            var commandResponse = await ReadUntilAsync(IsResponse("busy-command"));
            Assert.True(commandResponse.GetProperty("success").GetBoolean());
            Assert.Equal("handled", commandResponse.GetProperty("data").GetProperty("disposition").GetString());

            releaseProvider.TrySetResult();
            await ReadUntilAsync(item => item.GetProperty("type").GetString() == "agent_settled");

            await process.StandardInput.WriteLineAsync("{\"id\":\"failed-command\",\"type\":\"prompt\",\"message\":\"/explode\"}");
            await process.StandardInput.FlushAsync(timeout.Token);
            var failedCommandResponse = await ReadUntilAsync(IsResponse("failed-command"));
            Assert.True(failedCommandResponse.GetProperty("success").GetBoolean());
            Assert.Equal("handled", failedCommandResponse.GetProperty("data").GetProperty("disposition").GetString());

            process.StandardInput.Close();
            await process.WaitForExitAsync(timeout.Token);
            await outputReader.WaitAsync(timeout.Token);
            await provider.WaitAsync(timeout.Token);

            Assert.Equal(0, process.ExitCode);
            Assert.Equal(string.Empty, await stderr.WaitAsync(timeout.Token));
            var resultEvent = Assert.Single(records, item => item.GetProperty("type").GetString() == "event" &&
                item.GetProperty("data").GetProperty("Type").GetString() == "extension_command_output");
            var resultData = resultEvent.GetProperty("data");
            Assert.Equal("fixture", resultData.GetProperty("Tool").GetString());
            Assert.Equal("extension: busy argument", resultData.GetProperty("Text").GetString());
            Assert.Equal("busy-command", resultData.GetProperty("OperationId").GetString());

            var extensionError = Assert.Single(records, item => item.GetProperty("type").GetString() == "extension_error");
            Assert.Equal("command:explode", extensionError.GetProperty("extensionPath").GetString());
            Assert.Equal("command", extensionError.GetProperty("event").GetString());
            Assert.Equal("fixture command failed", extensionError.GetProperty("error").GetString());

            Assert.Single(records, item => item.GetProperty("type").GetString() == "agent_start");
            Assert.DoesNotContain(records, item => item.GetProperty("type").GetString() == "queue_update");
            Assert.True(Array.IndexOf(records.ToArray(), resultEvent) < Array.FindIndex(records.ToArray(), record => IsResponse("busy-command")(record)));
            Assert.True(Array.IndexOf(records.ToArray(), extensionError) < Array.FindIndex(records.ToArray(), record => IsResponse("failed-command")(record)));
        }
        finally
        {
            releaseProvider.TrySetResult();
            timeout.Cancel();
            listener.Close();
            if (process is { HasExited: false })
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync();
            }
            if (outputReader is not null)
            {
                try { await outputReader; }
                catch (Exception error) when (error is OperationCanceledException or ObjectDisposedException) { }
            }
            try { await provider; }
            catch (Exception error) when (error is OperationCanceledException or HttpListenerException or ObjectDisposedException) { }
            process?.Dispose();
            Directory.Delete(root, recursive: true);
        }

        async Task<JsonElement> ReadUntilAsync(Func<JsonElement, bool> predicate)
        {
            while (await output.Reader.WaitToReadAsync(timeout.Token))
            {
                while (output.Reader.TryRead(out var record))
                {
                    records.Add(record);
                    if (predicate(record)) return record;
                }
            }
            throw new InvalidOperationException("RPC process exited before emitting the expected record.");
        }
    }

    private static Func<JsonElement, bool> IsResponse(string id) => record =>
        record.GetProperty("type").GetString() == "response" &&
        record.TryGetProperty("id", out var responseId) && responseId.GetString() == id;

    private static HttpListener StartLoopbackListener(out int port)
    {
        using var reservation = new TcpListener(IPAddress.Loopback, 0);
        reservation.Start();
        port = ((IPEndPoint)reservation.LocalEndpoint).Port;
        reservation.Stop();
        var listener = new HttpListener();
        listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        listener.Start();
        return listener;
    }
}
