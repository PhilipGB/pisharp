using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using PiSharp.Cli;

namespace PiSharp.Tests;

public sealed class RpcAgentLifecycleProcessTests
{
    [Fact]
    public async Task RpcProcessEmitsPiAgentEndWithCanonicalMessagesBeforeSettlement()
    {
        if (!OperatingSystem.IsLinux()) return;
        var root = Path.Combine(Path.GetTempPath(), "pisharp-rpc-agent-end-" + Guid.NewGuid().ToString("N"));
        var agentDirectory = Path.Combine(root, "agent");
        Directory.CreateDirectory(agentDirectory);
        var listener = StartLoopbackListener(out var port);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        Process? process = null;
        var server = Task.Run(async () =>
        {
            var context = await listener.GetContextAsync().WaitAsync(timeout.Token);
            context.Response.StatusCode = (int)HttpStatusCode.OK;
            context.Response.ContentType = "text/event-stream";
            await using (var writer = new StreamWriter(context.Response.OutputStream))
            {
                var chunk = new
                {
                    id = "chatcmpl-rpc-end",
                    @object = "chat.completion.chunk",
                    created = 1,
                    model = "rpc-event-fixture",
                    choices = new[] { new { index = 0, delta = new { role = "assistant", content = "reply" }, finish_reason = (string?)null } }
                };
                var complete = new
                {
                    id = "chatcmpl-rpc-end",
                    @object = "chat.completion.chunk",
                    created = 1,
                    model = "rpc-event-fixture",
                    choices = new[] { new { index = 0, delta = new { }, finish_reason = "stop" } }
                };
                await writer.WriteAsync($"data: {JsonSerializer.Serialize(chunk)}\n\n");
                await writer.WriteAsync($"data: {JsonSerializer.Serialize(complete)}\n\n");
                await writer.WriteAsync("data: [DONE]\n\n");
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
                        models = new[] { new { id = "rpc-event-fixture", api = "openai-completions" } }
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
            foreach (var argument in new[] { "--mode", "rpc", "--provider", "fixture", "--model", "rpc-event-fixture", "--offline", "--no-session" })
                start.ArgumentList.Add(argument);
            foreach (var name in new[] { "OPENAI_API_KEY", "PISHARP_API_KEY", "PISHARP_BASE_URL", "PISHARP_MODEL",
                "PISHARP_AUTH_PATH", "PISHARP_MODELS_PATH", "PISHARP_SETTINGS_PATH", "PISHARP_FIXTURE_KEY" })
                start.Environment.Remove(name);
            start.Environment["PISHARP_AGENT_DIR"] = agentDirectory;
            start.Environment["PISHARP_FIXTURE_KEY"] = "fixture-only-key";
            process = Process.Start(start)!;
            var stderr = process.StandardError.ReadToEndAsync();
            await process.StandardInput.WriteLineAsync("{\"id\":\"prompt-1\",\"type\":\"prompt\",\"message\":\"hello\"}");
            await process.StandardInput.FlushAsync(timeout.Token);
            var lines = new List<string>();
            while (await process.StandardOutput.ReadLineAsync(timeout.Token) is { } line)
            {
                lines.Add(line);
                using var record = JsonDocument.Parse(line);
                if (record.RootElement.GetProperty("type").GetString() == "agent_settled") break;
            }
            process.StandardInput.Close();
            await process.WaitForExitAsync(timeout.Token);
            await server;

            Assert.Equal(0, process.ExitCode);
            Assert.Equal("", await stderr.WaitAsync(timeout.Token));
            Assert.All(lines, line => JsonDocument.Parse(line).Dispose());
            using var response = JsonDocument.Parse(Assert.Single(lines, line => line.Contains("\"id\":\"prompt-1\"", StringComparison.Ordinal)));
            Assert.True(response.RootElement.GetProperty("success").GetBoolean());
            Assert.Equal("started", response.RootElement.GetProperty("data").GetProperty("disposition").GetString());
            var eventTypes = lines.Select(line => JsonDocument.Parse(line)).ToArray();
            try
            {
                var startIndex = Array.FindIndex(eventTypes, record => record.RootElement.GetProperty("type").GetString() == "agent_start");
                var turnStartIndex = Array.FindIndex(eventTypes, record => record.RootElement.GetProperty("type").GetString() == "turn_start");
                var turnEndIndex = Array.FindIndex(eventTypes, record => record.RootElement.GetProperty("type").GetString() == "turn_end");
                var endIndex = Array.FindIndex(eventTypes, record => record.RootElement.GetProperty("type").GetString() == "agent_end");
                var settledIndex = Array.FindIndex(eventTypes, record => record.RootElement.GetProperty("type").GetString() == "agent_settled");
                Assert.True(startIndex >= 0 && turnStartIndex > startIndex && turnEndIndex > turnStartIndex &&
                    endIndex > turnEndIndex && settledIndex > endIndex);
                Assert.Single(eventTypes, record => record.RootElement.GetProperty("type").GetString() == "agent_start");
                Assert.Single(eventTypes, record => record.RootElement.GetProperty("type").GetString() == "turn_start");
                Assert.Single(eventTypes, record => record.RootElement.GetProperty("type").GetString() == "turn_end");
                var userMessageStart = Assert.Single(eventTypes, record => record.RootElement.GetProperty("type").GetString() == "message_start" &&
                    record.RootElement.GetProperty("message").GetProperty("role").GetString() == "user");
                var userMessageEnd = Assert.Single(eventTypes, record => record.RootElement.GetProperty("type").GetString() == "message_end" &&
                    record.RootElement.GetProperty("message").GetProperty("role").GetString() == "user");
                var assistantMessageStartIndex = Array.FindIndex(eventTypes, record => record.RootElement.GetProperty("type").GetString() == "message_start" &&
                    record.RootElement.GetProperty("message").GetProperty("role").GetString() == "assistant");
                var assistantMessageEndIndex = Array.FindIndex(eventTypes, record => record.RootElement.GetProperty("type").GetString() == "message_end" &&
                    record.RootElement.GetProperty("message").GetProperty("role").GetString() == "assistant");
                var textUpdateIndex = Array.FindIndex(eventTypes, record => record.RootElement.GetProperty("type").GetString() == "message_update" &&
                    record.RootElement.GetProperty("assistantMessageEvent").GetProperty("type").GetString() == "text_delta");
                Assert.True(turnStartIndex < Array.IndexOf(eventTypes, userMessageStart) &&
                    Array.IndexOf(eventTypes, userMessageStart) < Array.IndexOf(eventTypes, userMessageEnd) &&
                    Array.IndexOf(eventTypes, userMessageEnd) < assistantMessageStartIndex &&
                    assistantMessageStartIndex < textUpdateIndex && textUpdateIndex < assistantMessageEndIndex &&
                    assistantMessageEndIndex < turnEndIndex);
                var agentEnd = eventTypes[endIndex].RootElement;
                Assert.Equal(["type", "messages", "willRetry"], agentEnd.EnumerateObject().Select(property => property.Name));
                Assert.False(agentEnd.GetProperty("willRetry").GetBoolean());
                var messages = agentEnd.GetProperty("messages");
                Assert.Equal(2, messages.GetArrayLength());
                Assert.Equal("user", messages[0].GetProperty("role").GetString());
                Assert.Equal("hello", messages[0].GetProperty("content").GetString());
                Assert.Equal("assistant", messages[1].GetProperty("role").GetString());
                Assert.Equal("reply", messages[1].GetProperty("content")[0].GetProperty("text").GetString());
                Assert.Equal("openai-completions", messages[1].GetProperty("api").GetString());
                Assert.Equal("fixture", messages[1].GetProperty("provider").GetString());
                Assert.Equal("rpc-event-fixture", messages[1].GetProperty("model").GetString());
                Assert.Equal("stop", messages[1].GetProperty("stopReason").GetString());
                Assert.True(JsonElement.DeepEquals(userMessageEnd.RootElement.GetProperty("message"), messages[0]));
                Assert.True(JsonElement.DeepEquals(eventTypes[assistantMessageEndIndex].RootElement.GetProperty("message"), messages[1]));
            }
            finally { foreach (var record in eventTypes) record.Dispose(); }
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
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task RpcProcessProjectsToolCallAndToolResultMessageEvents()
    {
        if (!OperatingSystem.IsLinux()) return;
        var root = Path.Combine(Path.GetTempPath(), "pisharp-rpc-tool-events-" + Guid.NewGuid().ToString("N"));
        var agentDirectory = Path.Combine(root, "agent");
        Directory.CreateDirectory(agentDirectory);
        await File.WriteAllTextAsync(Path.Combine(root, "fixture.txt"), "process tool result");
        var listener = StartLoopbackListener(out var port);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        Process? process = null;
        var server = Task.Run(async () =>
        {
            var first = await listener.GetContextAsync().WaitAsync(timeout.Token);
            await WriteSseResponseAsync(first, "chatcmpl-tool-call", "rpc-event-fixture",
                new
                {
                    role = "assistant",
                    tool_calls = new[]
                    {
                        new
                        {
                            index = 0,
                            id = "call-read",
                            type = "function",
                            function = new { name = "read", arguments = "{\"path\":\"fixture.txt\"}" }
                        }
                    }
                }, "tool_calls");

            var second = await listener.GetContextAsync().WaitAsync(timeout.Token);
            using (var request = await JsonDocument.ParseAsync(second.Request.InputStream, cancellationToken: timeout.Token))
            {
                Assert.Contains(request.RootElement.GetProperty("messages").EnumerateArray(), message =>
                    message.GetProperty("role").GetString() == "tool" &&
                    message.GetProperty("tool_call_id").GetString() == "call-read" &&
                    message.GetProperty("content").GetString()!.Contains("process tool result", StringComparison.Ordinal));
            }
            await WriteSseResponseAsync(second, "chatcmpl-tool-result", "rpc-event-fixture",
                new { role = "assistant", content = "finished" }, "stop");
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
                        models = new[] { new { id = "rpc-event-fixture", api = "openai-completions" } }
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
            foreach (var argument in new[] { "--mode", "rpc", "--provider", "fixture", "--model", "rpc-event-fixture", "--offline", "--no-session" })
                start.ArgumentList.Add(argument);
            foreach (var name in new[] { "OPENAI_API_KEY", "PISHARP_API_KEY", "PISHARP_BASE_URL", "PISHARP_MODEL",
                "PISHARP_AUTH_PATH", "PISHARP_MODELS_PATH", "PISHARP_SETTINGS_PATH", "PISHARP_FIXTURE_KEY" })
                start.Environment.Remove(name);
            start.Environment["PISHARP_AGENT_DIR"] = agentDirectory;
            start.Environment["PISHARP_FIXTURE_KEY"] = "fixture-only-key";
            process = Process.Start(start)!;
            var stderr = process.StandardError.ReadToEndAsync();
            await process.StandardInput.WriteLineAsync("{\"id\":\"tool-prompt\",\"type\":\"prompt\",\"message\":\"read fixture\"}");
            await process.StandardInput.FlushAsync(timeout.Token);
            var lines = new List<string>();
            while (await process.StandardOutput.ReadLineAsync(timeout.Token) is { } line)
            {
                lines.Add(line);
                using var record = JsonDocument.Parse(line);
                if (record.RootElement.GetProperty("type").GetString() == "agent_settled") break;
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
                var callStart = Array.FindIndex(records, record => record.RootElement.GetProperty("type").GetString() == "message_update" &&
                    record.RootElement.GetProperty("assistantMessageEvent").GetProperty("type").GetString() == "toolcall_start");
                var callEnd = Array.FindIndex(records, record => record.RootElement.GetProperty("type").GetString() == "message_update" &&
                    record.RootElement.GetProperty("assistantMessageEvent").GetProperty("type").GetString() == "toolcall_end");
                var toolStart = Array.IndexOf(types, "tool_execution_start");
                var toolEnd = Array.IndexOf(types, "tool_execution_end");
                var toolMessageStart = Array.FindIndex(records, record => record.RootElement.GetProperty("type").GetString() == "message_start" &&
                    record.RootElement.GetProperty("message").GetProperty("role").GetString() == "toolResult");
                var toolMessageEnd = Array.FindIndex(records, record => record.RootElement.GetProperty("type").GetString() == "message_end" &&
                    record.RootElement.GetProperty("message").GetProperty("role").GetString() == "toolResult");
                var turnEnds = Array.FindAll(types, type => type == "turn_end").Length;
                Assert.True(callStart >= 0 && callEnd > callStart && toolStart > callEnd && toolEnd > toolStart &&
                    toolMessageStart > toolEnd && toolMessageEnd > toolMessageStart && turnEnds == 2);
                Assert.Equal(["type", "contentIndex", "id", "toolName"], records[callStart].RootElement
                    .GetProperty("assistantMessageEvent").EnumerateObject().Select(property => property.Name));
                Assert.Equal("toolCall", records[callEnd].RootElement.GetProperty("assistantMessageEvent")
                    .GetProperty("toolCall").GetProperty("type").GetString());
                var toolResultEndMessage = records[toolMessageEnd].RootElement.GetProperty("message");
                Assert.Equal("call-read", toolResultEndMessage.GetProperty("toolCallId").GetString());
                var firstTurnEnd = records[Array.IndexOf(types, "turn_end")].RootElement;
                Assert.True(JsonElement.DeepEquals(toolResultEndMessage, firstTurnEnd.GetProperty("toolResults")[0]));
                var agentEnd = records[Array.IndexOf(types, "agent_end")].RootElement;
                var messages = agentEnd.GetProperty("messages");
                Assert.Equal(4, messages.GetArrayLength());
                var assistantMessageEnds = records.Where(record => record.RootElement.GetProperty("type").GetString() == "message_end" &&
                        record.RootElement.GetProperty("message").GetProperty("role").GetString() == "assistant")
                    .Select(record => record.RootElement.GetProperty("message")).ToArray();
                Assert.Equal(2, assistantMessageEnds.Length);
                Assert.True(JsonElement.DeepEquals(assistantMessageEnds[0], messages[1]));
                Assert.True(JsonElement.DeepEquals(toolResultEndMessage, messages[2]));
                Assert.True(JsonElement.DeepEquals(assistantMessageEnds[1], messages[3]));
                Assert.True(JsonElement.DeepEquals(assistantMessageEnds[0], firstTurnEnd.GetProperty("message")));
                var secondTurnEnd = records[Array.LastIndexOf(types, "turn_end")].RootElement;
                Assert.True(JsonElement.DeepEquals(assistantMessageEnds[1], secondTurnEnd.GetProperty("message")));
                Assert.Equal("process tool result", messages[2].GetProperty("content")[0]
                    .GetProperty("text").GetString());
            }
            finally { foreach (var record in records) record.Dispose(); }
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
            Directory.Delete(root, recursive: true);
        }
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
