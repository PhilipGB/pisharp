using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using PiSharp.Cli;
using PiSharp.ResourceDiscoveryExtension;

namespace PiSharp.Tests;

public sealed class RpcExtensionContextProcessTests
{
    [Fact]
    public async Task ExtensionToolReceivesLiveProjectContextAndLifecycleCallbacksInOrder()
    {
        if (!OperatingSystem.IsLinux()) return;
        var root = Path.Combine(Path.GetTempPath(), "pisharp-rpc-extension-context-" + Guid.NewGuid().ToString("N"));
        var agentDirectory = Path.Combine(root, "agent");
        var tracePath = Path.Combine(root, "extension-trace.log");
        Directory.CreateDirectory(agentDirectory);
        using var listener = StartLoopbackListener(out var port);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        string? toolResult = null;
        var server = Task.Run(async () =>
        {
            var first = await listener.GetContextAsync().WaitAsync(timeout.Token);
            await WriteToolCallAsync(first, timeout.Token);

            var second = await listener.GetContextAsync().WaitAsync(timeout.Token);
            using (var request = await JsonDocument.ParseAsync(second.Request.InputStream, cancellationToken: timeout.Token))
            {
                var toolMessage = Assert.Single(request.RootElement.GetProperty("messages").EnumerateArray(), message =>
                    message.GetProperty("role").GetString() == "tool" &&
                    message.GetProperty("tool_call_id").GetString() == "call-extension-context");
                toolResult = toolMessage.GetProperty("content").GetString();
            }
            await WriteAssistantReplyAsync(second, timeout.Token);
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
                        models = new[] { new { id = "rpc-extension-context-fixture", api = "openai-completions" } }
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
                "--mode", "rpc", "--provider", "fixture", "--model", "rpc-extension-context-fixture",
                "--offline", "--no-session", "--approve", "--no-builtin-tools", "--extension",
                typeof(ExtensionContextFixture).Assembly.Location
            }) start.ArgumentList.Add(argument);
            foreach (var name in new[] { "OPENAI_API_KEY", "PISHARP_API_KEY", "PISHARP_BASE_URL", "PISHARP_MODEL",
                "PISHARP_AUTH_PATH", "PISHARP_MODELS_PATH", "PISHARP_SETTINGS_PATH", "PISHARP_FIXTURE_KEY",
                "PISHARP_TEST_EXTENSION_CONTEXT", "PISHARP_TEST_EXTENSION_CONTEXT_TRACE" })
                start.Environment.Remove(name);
            start.Environment["PISHARP_AGENT_DIR"] = agentDirectory;
            start.Environment["PISHARP_FIXTURE_KEY"] = "fixture-only-key";
            start.Environment["PISHARP_TEST_EXTENSION_CONTEXT"] = "true";
            start.Environment["PISHARP_TEST_EXTENSION_CONTEXT_TRACE"] = tracePath;
            process = Process.Start(start)!;
            var stderr = process.StandardError.ReadToEndAsync();
            await process.StandardInput.WriteLineAsync("{\"id\":\"context-prompt\",\"type\":\"prompt\",\"message\":\"inspect context\"}");
            await process.StandardInput.FlushAsync(timeout.Token);

            var output = new List<JsonElement>();
            while (await process.StandardOutput.ReadLineAsync(timeout.Token) is { } line)
            {
                using var document = JsonDocument.Parse(line);
                var record = document.RootElement.Clone();
                output.Add(record);
                if (record.GetProperty("type").GetString() == "extension_ui_request" &&
                    record.GetProperty("method").GetString() != "notify")
                    await RespondToUiRequestAsync(process, record, timeout.Token);
                if (record.GetProperty("type").GetString() == "agent_settled") break;
            }
            process.StandardInput.Close();
            await process.WaitForExitAsync(timeout.Token);
            await server.WaitAsync(timeout.Token);
            _ = await stderr.WaitAsync(timeout.Token);

            Assert.Equal(0, process.ExitCode);
            Assert.Equal(4, output.Count(item => item.GetProperty("type").GetString() == "extension_ui_request" &&
                item.GetProperty("method").GetString() is "select" or "confirm" or "input" or "editor"));
            Assert.Contains(output, item => item.GetProperty("type").GetString() == "extension_ui_request" &&
                item.GetProperty("method").GetString() == "notify" &&
                item.GetProperty("message").GetString() == "extension context notification");
            Assert.Contains(output, item => item.GetProperty("type").GetString() == "tool_execution_update" &&
                item.GetProperty("partialResult").GetProperty("content")[0].GetProperty("text").GetString() ==
                    "extension context progress");
            Assert.NotNull(toolResult);
            using var result = JsonDocument.Parse(toolResult);
            var stateJson = result.RootElement.ValueKind == JsonValueKind.String
                ? result.RootElement.GetString()! : toolResult;
            using var stateDocument = JsonDocument.Parse(stateJson);
            var state = stateDocument.RootElement;
            Assert.Equal(Path.GetFullPath(root), state.GetProperty("workingDirectory").GetString());
            Assert.Equal("rpc", state.GetProperty("mode").GetString());
            Assert.False(state.GetProperty("hasUserInterface").GetBoolean());
            Assert.Equal("South", state.GetProperty("selected").GetString());
            Assert.True(state.GetProperty("confirmed").GetBoolean());
            Assert.Equal("label", state.GetProperty("label").GetString());
            Assert.Equal("line one\nline two", state.GetProperty("edited").GetString());
            Assert.True(state.GetProperty("isProjectTrusted").GetBoolean());
            Assert.False(state.GetProperty("isIdle").GetBoolean());
            Assert.Equal("rpc-extension-context-fixture", state.GetProperty("currentModelId").GetString());
            Assert.Equal("fixture", state.GetProperty("currentProviderId").GetString());
            Assert.False(string.IsNullOrWhiteSpace(state.GetProperty("sessionId").GetString()));
            Assert.Contains("extension_context_probe", state.GetProperty("activeToolNames").EnumerateArray()
                .Select(item => item.GetString()));

            var trace = await File.ReadAllLinesAsync(tracePath);
            Assert.Equal(new[] { "event:tool_execution_started:rpc-extension-context-fixture",
                "event:tool_execution_update:rpc-extension-context-fixture", "state:" + stateJson,
                "event:tool_execution_finished:rpc-extension-context-fixture" }, trace.Where(line => line.StartsWith("event:", StringComparison.Ordinal) ||
                    line.StartsWith("state:", StringComparison.Ordinal)));
            Assert.Contains("resource:startup", trace);
        }
        finally
        {
            timeout.Cancel();
            listener.Close();
            if (process is { HasExited: false })
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync();
            }
            process?.Dispose();
            try { await server; }
            catch (Exception error) when (error is OperationCanceledException or HttpListenerException or ObjectDisposedException) { }
            Directory.Delete(root, recursive: true);
        }
    }

    private static async Task RespondToUiRequestAsync(Process process, JsonElement request,
        CancellationToken cancellationToken)
    {
        var method = request.GetProperty("method").GetString();
        var id = request.GetProperty("id").GetString();
        object response = method switch
        {
            "select" => new { type = "extension_ui_response", id, value = "South" },
            "confirm" => new { type = "extension_ui_response", id, confirmed = true },
            "input" => new { type = "extension_ui_response", id, value = "label" },
            "editor" => new { type = "extension_ui_response", id, value = "line one\nline two" },
            _ => throw new InvalidOperationException($"Unexpected RPC extension UI request method '{method}'.")
        };
        await process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(response));
        await process.StandardInput.FlushAsync(cancellationToken);
    }

    private static async Task WriteToolCallAsync(HttpListenerContext context, CancellationToken cancellationToken)
    {
        context.Response.ContentType = "text/event-stream";
        await using var writer = new StreamWriter(context.Response.OutputStream);
        var chunk = new
        {
            id = "chatcmpl-extension-context-tool",
            @object = "chat.completion.chunk",
            created = 1,
            model = "rpc-extension-context-fixture",
            choices = new[] { new { index = 0,
                delta = new { role = "assistant", tool_calls = new[] { new { index = 0, id = "call-extension-context",
                    type = "function", function = new { name = "extension_context_probe", arguments = "{}" } } } },
                finish_reason = (string?)null } }
        };
        await writer.WriteAsync($"data: {JsonSerializer.Serialize(chunk)}\n\n");
        await writer.WriteAsync("data: {\"id\":\"chatcmpl-extension-context-tool\",\"object\":\"chat.completion.chunk\",\"created\":1,\"model\":\"rpc-extension-context-fixture\",\"choices\":[{\"index\":0,\"delta\":{},\"finish_reason\":\"tool_calls\"}]}\n\n");
        await writer.WriteAsync("data: [DONE]\n\n");
        await writer.FlushAsync(cancellationToken);
        context.Response.Close();
    }

    private static async Task WriteAssistantReplyAsync(HttpListenerContext context, CancellationToken cancellationToken)
    {
        context.Response.ContentType = "text/event-stream";
        await using var writer = new StreamWriter(context.Response.OutputStream);
        var chunk = new
        {
            id = "chatcmpl-extension-context-result",
            @object = "chat.completion.chunk",
            created = 1,
            model = "rpc-extension-context-fixture",
            choices = new[] { new { index = 0, delta = new { role = "assistant", content = "context captured" }, finish_reason = (string?)null } }
        };
        await writer.WriteAsync($"data: {JsonSerializer.Serialize(chunk)}\n\n");
        await writer.WriteAsync("data: {\"id\":\"chatcmpl-extension-context-result\",\"object\":\"chat.completion.chunk\",\"created\":1,\"model\":\"rpc-extension-context-fixture\",\"choices\":[{\"index\":0,\"delta\":{},\"finish_reason\":\"stop\"}]}\n\n");
        await writer.WriteAsync("data: [DONE]\n\n");
        await writer.FlushAsync(cancellationToken);
        context.Response.Close();
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
}
