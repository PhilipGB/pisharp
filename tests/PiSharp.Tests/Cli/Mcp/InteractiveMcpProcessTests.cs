using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using PiSharp.Cli;

namespace PiSharp.Tests;

public sealed class InteractiveMcpProcessTests
{
    [Fact]
    public async Task ManagementActionReloadsServerAndConversationContinues()
    {
        if (!OperatingSystem.IsLinux() || !File.Exists("/usr/bin/script")) return;

        var root = Path.Combine(Path.GetTempPath(), "pisharp-interactive-mcp-process-" + Guid.NewGuid().ToString("N"));
        var agent = Path.Combine(root, "agent");
        Directory.CreateDirectory(agent);
        using var portReservation = new TcpListener(IPAddress.Loopback, 0);
        portReservation.Start();
        var port = ((IPEndPoint)portReservation.LocalEndpoint).Port;
        portReservation.Stop();
        using var listener = new HttpListener();
        listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        listener.Start();

        var fixture = Path.Combine(AppContext.BaseDirectory, "TestInfrastructure", "Fixtures", "mcp_fixture.py");
        var mcpProcessIdPath = Path.Combine(root, "mcp-process-id.txt");
        await File.WriteAllTextAsync(Path.Combine(agent, "mcp.json"), System.Text.Json.JsonSerializer.Serialize(new
        {
            mcpServers = new Dictionary<string, object>
            {
                ["managed"] = new
                {
                    command = "python3",
                    args = new[] { fixture },
                    exposure = "direct",
                    env = new Dictionary<string, string> { ["MCP_FIXTURE_PID_PATH"] = mcpProcessIdPath }
                }
            }
        }));
        var models = """
            {"providers":{"fixture":{"baseUrl":"http://127.0.0.1:PORT/v1","apiKeyEnv":"PISHARP_FIXTURE_KEY","models":[{"id":"fixture-model","api":"openai-responses","reasoning":true}]}}}
            """.Replace("PORT", port.ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal);
        await File.WriteAllTextAsync(Path.Combine(agent, "models.json"), models);

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        var providerRequest = Task.Run(async () =>
        {
            var request = await listener.GetContextAsync().WaitAsync(timeout.Token);
            Assert.Equal("/v1/responses", request.Request.Url?.AbsolutePath);
            Assert.Equal("Bearer mcp-management-fixture-key", request.Request.Headers["Authorization"]);
            using var reader = new StreamReader(request.Request.InputStream);
            var body = await reader.ReadToEndAsync(timeout.Token);
            Assert.Contains("continue after MCP management", body, StringComparison.Ordinal);
            request.Response.StatusCode = 200;
            request.Response.ContentType = "text/event-stream";
            request.Response.KeepAlive = false;
            await using var writer = new StreamWriter(request.Response.OutputStream);
            await writer.WriteAsync("data: {\"type\":\"response.created\",\"response\":{\"id\":\"resp_mcp_management\",\"object\":\"response\",\"created_at\":1,\"model\":\"fixture-model\",\"status\":\"in_progress\",\"output\":[]}}\n\n");
            await writer.WriteAsync("data: {\"type\":\"response.output_text.delta\",\"item_id\":\"msg_mcp_management\",\"output_index\":0,\"content_index\":0,\"delta\":\"FOLLOWUP_AFTER_MCP_RELOAD_OK\"}\n\n");
            await writer.WriteAsync("data: {\"type\":\"response.completed\",\"response\":{\"id\":\"resp_mcp_management\",\"object\":\"response\",\"created_at\":1,\"model\":\"fixture-model\",\"status\":\"completed\",\"output\":[]}}\n\n");
            await writer.WriteAsync("data: [DONE]\n\n");
            await writer.FlushAsync();
            request.Response.Close();
        });

        Process? process = null;
        try
        {
            var start = new ProcessStartInfo("/usr/bin/script")
            {
                WorkingDirectory = root,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            start.ArgumentList.Add("-q");
            start.ArgumentList.Add("-e");
            start.ArgumentList.Add("-c");
            start.ArgumentList.Add($"stty rows 24 cols 80; exec dotnet {ProcessTestHelpers.ShellQuote(typeof(CliArguments).Assembly.Location)} --provider fixture --model fixture-model --no-session --offline");
            start.ArgumentList.Add("/dev/null");
            start.Environment["PISHARP_AGENT_DIR"] = agent;
            start.Environment["PISHARP_FIXTURE_KEY"] = "mcp-management-fixture-key";
            foreach (var name in new[] { "OPENAI_API_KEY", "PISHARP_API_KEY", "PISHARP_BASE_URL", "PISHARP_MODEL", "PISHARP_AUTH_PATH", "PISHARP_MODELS_PATH" })
                start.Environment.Remove(name);
            process = Process.Start(start);
            Assert.NotNull(process);

            var output = new StringBuilder();
            var outputGate = new object();
            var inputReady = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var connectedStatus = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var disabledNotice = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var reloadNotice = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var disabledStatus = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var followUpSettled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var drain = Task.Run(async () =>
            {
                var buffer = new char[2048];
                while (true)
                {
                    var count = await process.StandardOutput.ReadAsync(buffer);
                    if (count == 0) break;
                    lock (outputGate)
                    {
                        output.Append(buffer, 0, count);
                        var captured = output.ToString();
                        if (captured.Contains("\u001b[?2004h", StringComparison.Ordinal)) inputReady.TrySetResult();
                        if (captured.Contains("managed: connected (direct, global, 2 tools, resources)", StringComparison.Ordinal))
                            connectedStatus.TrySetResult();
                        if (captured.Contains("Disabled MCP server managed.", StringComparison.Ordinal))
                            disabledNotice.TrySetResult();
                        if (captured.Contains("Reloaded MCP servers and interactive resources.", StringComparison.Ordinal))
                            reloadNotice.TrySetResult();
                        if (captured.Contains("managed: disabled (direct, global, 0 tools)", StringComparison.Ordinal))
                            disabledStatus.TrySetResult();
                        var frames = TerminalOutputFrameReader.Read(captured, rows: 24, columns: 80);
                        if (frames.Count > 0)
                        {
                            var frame = frames[^1].Screen;
                            if (frame.Contains("FOLLOWUP_AFTER_MCP_RELOAD_OK", StringComparison.Ordinal) &&
                                !frame.Contains("Enter steers · follow-up queues · Escape aborts", StringComparison.Ordinal) &&
                                !frame.Contains("Working", StringComparison.Ordinal)) followUpSettled.TrySetResult();
                        }
                    }
                }
                lock (outputGate) return output.ToString();
            });
            var stderr = process.StandardError.ReadToEndAsync();

            async Task WaitForOutputAsync(Task task, string label)
            {
                try { await task.WaitAsync(timeout.Token); }
                catch (OperationCanceledException error)
                {
                    string captured;
                    lock (outputGate) captured = output.ToString();
                    throw new TimeoutException($"Timed out waiting for {label}.\n{captured[^Math.Min(captured.Length, 5000)..]}", error);
                }
            }

            await WaitForOutputAsync(inputReady.Task, "interactive input");
            await process.StandardInput.WriteAsync("/mcp status\r");
            await process.StandardInput.FlushAsync();
            await WaitForOutputAsync(connectedStatus.Task, "connected MCP status");
            await process.StandardInput.WriteAsync("/mcp disable managed\r");
            await process.StandardInput.FlushAsync();
            await WaitForOutputAsync(disabledNotice.Task, "MCP disable result");
            await process.StandardInput.WriteAsync("/mcp reload\r");
            await process.StandardInput.FlushAsync();
            await WaitForOutputAsync(reloadNotice.Task, "MCP reload result");
            await process.StandardInput.WriteAsync("/mcp status\r");
            await process.StandardInput.FlushAsync();
            await WaitForOutputAsync(disabledStatus.Task, "disabled MCP status after reload");
            await process.StandardInput.WriteAsync("continue after MCP management\r");
            await process.StandardInput.FlushAsync();
            await WaitForOutputAsync(followUpSettled.Task, "settled follow-up model response");
            await providerRequest.WaitAsync(timeout.Token);
            await process.StandardInput.WriteAsync("/quit\r");
            await process.StandardInput.FlushAsync();
            process.StandardInput.Close();
            await process.WaitForExitAsync(timeout.Token);
            var capturedOutput = await drain;
            Assert.Equal(0, process.ExitCode);
            Assert.DoesNotContain("Session error:", capturedOutput, StringComparison.Ordinal);
            Assert.DoesNotContain("Agent error:", await stderr, StringComparison.Ordinal);
            Assert.True(File.Exists(mcpProcessIdPath));
            ProcessTestHelpers.WaitForLinuxProcessExit(ProcessTestHelpers.ReadLinuxProcessId(mcpProcessIdPath));
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
            try { await providerRequest; }
            catch (Exception) { }
            Directory.Delete(root, recursive: true);
        }
    }
}
