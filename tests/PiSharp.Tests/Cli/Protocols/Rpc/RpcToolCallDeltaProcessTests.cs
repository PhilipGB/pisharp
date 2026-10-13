using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using PiSharp.Cli;

namespace PiSharp.Tests;

public sealed class RpcToolCallDeltaProcessTests
{
    [Fact]
    public async Task RpcProcessProjectsEachOpenAiToolArgumentFragmentInArrivalOrder()
    {
        if (!OperatingSystem.IsLinux()) return;
        var root = Path.Combine(Path.GetTempPath(), "pisharp-rpc-tool-deltas-" + Guid.NewGuid().ToString("N"));
        var agentDirectory = Path.Combine(root, "agent");
        Directory.CreateDirectory(agentDirectory);
        await File.WriteAllTextAsync(Path.Combine(root, "fixture.txt"), "tool result");
        using var listener = StartLoopbackListener(out var port);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        var releaseSecondFragment = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstFragmentObserved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Process? process = null;
        var provider = Task.Run(async () =>
        {
            var first = await listener.GetContextAsync().WaitAsync(timeout.Token);
            first.Response.ContentType = "text/event-stream";
            first.Response.SendChunked = true;
            await using (var writer = new StreamWriter(first.Response.OutputStream))
            {
                await WriteChunkAsync(writer, ToolChunk(new
                {
                    role = "assistant",
                    tool_calls = new[]
                    {
                        new
                        {
                            index = 0,
                            id = "call-read",
                            type = "function",
                            function = new { name = "read", arguments = "{\"path\":" }
                        }
                    }
                }, null));
                await releaseSecondFragment.Task.WaitAsync(timeout.Token);
                await WriteChunkAsync(writer, ToolChunk(new
                {
                    tool_calls = new[]
                    {
                        new { index = 0, function = new { arguments = "\"fixture.txt\"}" } }
                    }
                }, null));
                await WriteChunkAsync(writer, ToolChunk(new { }, "tool_calls"));
                await writer.WriteAsync("data: [DONE]\n\n");
                await writer.FlushAsync(timeout.Token);
            }
            first.Response.Close();

            var second = await listener.GetContextAsync().WaitAsync(timeout.Token);
            using (var body = await JsonDocument.ParseAsync(second.Request.InputStream, cancellationToken: timeout.Token))
            {
                Assert.Contains(body.RootElement.GetProperty("messages").EnumerateArray(), message =>
                    message.GetProperty("role").GetString() == "tool" &&
                    message.GetProperty("tool_call_id").GetString() == "call-read" &&
                    message.GetProperty("content").GetString()!.Contains("tool result", StringComparison.Ordinal));
            }
            await WriteTextResponseAsync(second, "finished", timeout.Token);
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
                        models = new[] { new { id = "rpc-tool-delta-fixture", api = "openai-completions" } }
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
            foreach (var argument in new[] { "--mode", "rpc", "--provider", "fixture", "--model", "rpc-tool-delta-fixture", "--offline", "--no-session" })
                start.ArgumentList.Add(argument);
            foreach (var name in new[] { "OPENAI_API_KEY", "PISHARP_API_KEY", "PISHARP_BASE_URL", "PISHARP_MODEL",
                "PISHARP_AUTH_PATH", "PISHARP_MODELS_PATH", "PISHARP_SETTINGS_PATH", "PISHARP_FIXTURE_KEY" })
                start.Environment.Remove(name);
            start.Environment["PISHARP_AGENT_DIR"] = agentDirectory;
            start.Environment["PISHARP_FIXTURE_KEY"] = "fixture-only-key";
            process = Process.Start(start)!;
            var stderr = process.StandardError.ReadToEndAsync();
            await process.StandardInput.WriteLineAsync("{\"id\":\"tool-delta-prompt\",\"type\":\"prompt\",\"message\":\"read fixture\"}");
            await process.StandardInput.FlushAsync(timeout.Token);
            var lines = new List<string>();
            while (await process.StandardOutput.ReadLineAsync(timeout.Token) is { } line)
            {
                lines.Add(line);
                using var record = JsonDocument.Parse(line);
                if (record.RootElement.GetProperty("type").GetString() == "message_update" &&
                    record.RootElement.GetProperty("assistantMessageEvent").TryGetProperty("delta", out var delta) &&
                    delta.GetString() == "{\"path\":")
                {
                    firstFragmentObserved.TrySetResult();
                    releaseSecondFragment.TrySetResult();
                }
                if (record.RootElement.GetProperty("type").GetString() == "agent_settled") break;
            }
            process.StandardInput.Close();
            await process.WaitForExitAsync(timeout.Token);
            await provider;
            await firstFragmentObserved.Task.WaitAsync(timeout.Token);

            Assert.Equal(0, process.ExitCode);
            Assert.Equal(string.Empty, await stderr.WaitAsync(timeout.Token));
            var updates = new List<JsonElement>();
            var recordTypes = new List<string>();
            foreach (var outputLine in lines)
            {
                using var record = JsonDocument.Parse(outputLine);
                var recordType = record.RootElement.GetProperty("type").GetString()!;
                recordTypes.Add(recordType);
                if (recordType == "message_update" &&
                    record.RootElement.TryGetProperty("assistantMessageEvent", out var item) &&
                    item.GetProperty("type").GetString()!.StartsWith("toolcall_", StringComparison.Ordinal))
                    updates.Add(item.Clone());
            }
            Assert.Equal(["toolcall_start", "toolcall_delta", "toolcall_delta", "toolcall_end"],
                updates.Select(item => item.GetProperty("type").GetString()));
            Assert.Equal(["{\"path\":", "\"fixture.txt\"}"], updates
                .Where(item => item.GetProperty("type").GetString() == "toolcall_delta")
                .Select(item => item.GetProperty("delta").GetString()));
            Assert.Equal(0, updates[0].GetProperty("contentIndex").GetInt32());
            Assert.Equal("call-read", updates[0].GetProperty("id").GetString());
            Assert.Equal("read", updates[0].GetProperty("toolName").GetString());
            Assert.Equal("fixture.txt", updates[3].GetProperty("toolCall").GetProperty("arguments")
                .GetProperty("path").GetString());
            Assert.True(recordTypes.IndexOf("message_start") < recordTypes.IndexOf("message_update"));
            Assert.True(recordTypes.FindIndex(type => type == "tool_execution_start") >
                lines.FindIndex(outputLine => outputLine.Contains("toolcall_end", StringComparison.Ordinal)));
        }
        finally
        {
            releaseSecondFragment.TrySetResult();
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
        using var reservation = new TcpListener(IPAddress.Loopback, 0);
        reservation.Start();
        port = ((IPEndPoint)reservation.LocalEndpoint).Port;
        reservation.Stop();
        var listener = new HttpListener();
        listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        listener.Start();
        return listener;
    }

    private static object ToolChunk(object delta, string? finishReason) => new
    {
        id = "chatcmpl-tool-delta",
        @object = "chat.completion.chunk",
        created = 1,
        model = "rpc-tool-delta-fixture",
        choices = new[] { new { index = 0, delta, finish_reason = finishReason } }
    };

    private static async Task WriteChunkAsync(StreamWriter writer, object chunk)
    {
        await writer.WriteAsync($"data: {JsonSerializer.Serialize(chunk)}\n\n");
        await writer.FlushAsync();
    }

    private static async Task WriteTextResponseAsync(HttpListenerContext context, string text, CancellationToken cancellationToken)
    {
        context.Response.ContentType = "text/event-stream";
        await using var writer = new StreamWriter(context.Response.OutputStream);
        await WriteChunkAsync(writer, new
        {
            id = "chatcmpl-tool-delta-result",
            @object = "chat.completion.chunk",
            created = 1,
            model = "rpc-tool-delta-fixture",
            choices = new[] { new { index = 0, delta = new { role = "assistant", content = text }, finish_reason = (string?)null } }
        });
        await WriteChunkAsync(writer, new
        {
            id = "chatcmpl-tool-delta-result",
            @object = "chat.completion.chunk",
            created = 1,
            model = "rpc-tool-delta-fixture",
            choices = new[] { new { index = 0, delta = new { }, finish_reason = "stop" } }
        });
        await writer.WriteAsync("data: [DONE]\n\n");
        await writer.FlushAsync(cancellationToken);
        context.Response.Close();
    }
}
