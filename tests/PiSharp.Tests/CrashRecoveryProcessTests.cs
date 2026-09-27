using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using PiSharp.Cli;
using PiSharp.Runtime.Sessions;

namespace PiSharp.Tests;

public sealed class CrashRecoveryProcessTests
{
    [Fact]
    public async Task KilledCliKeepsToolCheckpointAndNeverClaimsTurnCompleted()
    {
        if (!OperatingSystem.IsLinux()) return;
        var root = Path.Combine(Path.GetTempPath(), "pisharp-crash-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        using var reservation = new TcpListener(IPAddress.Loopback, 0);
        reservation.Start();
        var port = ((IPEndPoint)reservation.LocalEndpoint).Port;
        reservation.Stop();
        using var listener = new HttpListener();
        listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        listener.Start();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var sessionPath = Path.Combine(root, "record.session.json");
        var server = Task.Run(async () =>
        {
            var first = await listener.GetContextAsync().WaitAsync(timeout.Token);
            first.Response.ContentType = "text/event-stream";
            await using (var writer = new StreamWriter(first.Response.OutputStream))
            {
                var chunk = new
                {
                    id = "chatcmpl-1",
                    @object = "chat.completion.chunk",
                    created = 1,
                    model = "fixture",
                    choices = new[] { new { index = 0,
                        delta = new { role = "assistant", tool_calls = new[] { new { index = 0, id = "call-1", type = "function",
                            function = new { name = "write", arguments = "{\"path\":\"result.txt\",\"content\":\"made\"}" } } } },
                        finish_reason = (string?)null } }
                };
                await writer.WriteAsync($"data: {JsonSerializer.Serialize(chunk)}\n\n");
                await writer.WriteAsync("data: {\"id\":\"chatcmpl-1\",\"object\":\"chat.completion.chunk\",\"created\":1,\"model\":\"fixture\",\"choices\":[{\"index\":0,\"delta\":{},\"finish_reason\":\"tool_calls\"}]}\n\n");
                await writer.WriteAsync("data: [DONE]\n\n");
                await writer.FlushAsync(timeout.Token);
            }
            first.Response.Close();
            var second = await listener.GetContextAsync().WaitAsync(timeout.Token);
            // Keep the model request in-flight until SIGKILL; the assistant never completed.
            await Task.Delay(Timeout.InfiniteTimeSpan, timeout.Token);
            second.Response.Close();
        });
        try
        {
            var start = new ProcessStartInfo("dotnet")
            {
                WorkingDirectory = root,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            start.ArgumentList.Add(typeof(CliArguments).Assembly.Location);
            start.ArgumentList.Add("--session"); start.ArgumentList.Add(sessionPath);
            start.ArgumentList.Add("--print"); start.ArgumentList.Add("Create result.txt");
            start.Environment["PISHARP_BASE_URL"] = $"http://127.0.0.1:{port}/v1";
            start.Environment["PISHARP_MODEL"] = "fixture";
            start.Environment["PISHARP_API_KEY"] = "not-needed";
            start.Environment["PISHARP_AGENT_DIR"] = Path.Combine(root, "empty-agent");
            start.Environment.Remove("OPENAI_API_KEY");
            using var process = Process.Start(start);
            Assert.NotNull(process);
            var output = process.StandardOutput.ReadToEndAsync();
            var error = process.StandardError.ReadToEndAsync();
            try
            {
                while (true)
                {
                    timeout.Token.ThrowIfCancellationRequested();
                    if (File.Exists(sessionPath))
                    {
                        try
                        {
                            var snapshot = ConversationSession.Parse(await File.ReadAllTextAsync(sessionPath, timeout.Token));
                            if (snapshot.Tree.ActivePath().Any(node => node.Type == "tool_outcome")) break;
                        }
                        catch (IOException) { }
                    }
                    if (process.HasExited) throw new InvalidOperationException("CLI exited before tool checkpoint: " + await error);
                    await Task.Delay(25, timeout.Token);
                }
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync(timeout.Token);
                _ = await output; _ = await error;
                Assert.Equal("made", await File.ReadAllTextAsync(Path.Combine(root, "result.txt"), timeout.Token));
                var store = new ConversationStore(root, root);
                var recovered = await store.LoadAsync(sessionPath, timeout.Token);
                Assert.DoesNotContain(recovered.Tree.ActivePath(), node => node.Type == "run_finished");
                Assert.True(recovered.RecoverIncomplete());
                Assert.Contains("No tool outcome is unknown", recovered.ActiveMessages().Last().Text);
                Assert.Equal("made", await File.ReadAllTextAsync(Path.Combine(root, "result.txt"), timeout.Token));
            }
            finally
            {
                if (!process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(CancellationToken.None); }
            }
        }
        finally
        {
            timeout.Cancel();
            try { await server; } catch (Exception e) when (e is OperationCanceledException or HttpListenerException or ObjectDisposedException) { }
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task CrashAfterToolSideEffectBeforeOutcomeCheckpointRecoversWithoutReplayingTool()
    {
        if (!OperatingSystem.IsLinux()) return;
        var root = Path.Combine(Path.GetTempPath(), "pisharp-crash-tool-outcome-" + Guid.NewGuid().ToString("N"));
        var agentDirectory = Path.Combine(root, "agent");
        Directory.CreateDirectory(agentDirectory);
        var sessionPath = Path.Combine(root, "recovery.session.json");
        var startedPath = Path.Combine(root, "tool-started");
        var sideEffectPath = Path.Combine(root, "side-effects");
        using var reservation = new TcpListener(IPAddress.Loopback, 0);
        reservation.Start();
        var port = ((IPEndPoint)reservation.LocalEndpoint).Port;
        reservation.Stop();
        await File.WriteAllTextAsync(Path.Combine(agentDirectory, "models.json"), JsonSerializer.Serialize(new
        {
            providers = new
            {
                fixture = new
                {
                    baseUrl = $"http://127.0.0.1:{port}/v1",
                    apiKeyEnv = "PISHARP_FIXTURE_KEY",
                    models = new[] { new { id = "crash-window-fixture", api = "openai-completions" } }
                }
            }
        }));
        using var listener = new HttpListener();
        listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        listener.Start();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        Process? interrupted = null;
        Process? recovered = null;
        var continuationRequest = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var crashCommand = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var server = Task.Run(async () =>
        {
            var first = await listener.GetContextAsync().WaitAsync(timeout.Token);
            var command = await crashCommand.Task.WaitAsync(timeout.Token);
            await WriteSseResponseAsync(first, "crash-window-tool", new
            {
                role = "assistant",
                tool_calls = new[]
                {
                    new
                    {
                        index = 0,
                        id = "call-crash-window",
                        type = "function",
                        function = new { name = "bash", arguments = JsonSerializer.Serialize(new { command }) }
                    }
                }
            }, "tool_calls");

            var second = await listener.GetContextAsync().WaitAsync(timeout.Token);
            using (var reader = new StreamReader(second.Request.InputStream))
                continuationRequest.TrySetResult(await reader.ReadToEndAsync(timeout.Token));
            await WriteSseResponseAsync(second, "crash-window-recovered", new { role = "assistant", content = "resumed safely" }, "stop");
        }, timeout.Token);

        try
        {
            interrupted = StartCli(root, agentDirectory, false, "--provider", "fixture", "--model", "crash-window-fixture",
                "--session", sessionPath, "--print", "run blocked command", "--tools", "bash", "--offline");
            var firstOutput = interrupted.StandardOutput.ReadToEndAsync();
            var firstError = interrupted.StandardError.ReadToEndAsync();
            crashCommand.TrySetResult($"touch {ProcessTestHelpers.ShellQuote(startedPath)}; printf x >> {ProcessTestHelpers.ShellQuote(sideEffectPath)}; kill -9 {interrupted.Id}");
            try { await ProcessTestHelpers.WaitForFileAsync(startedPath, timeout.Token); }
            catch (TimeoutException)
            {
                if (interrupted is { HasExited: false })
                {
                    interrupted.Kill(entireProcessTree: true);
                    await interrupted.WaitForExitAsync(timeout.Token);
                }
                var sessionText = File.Exists(sessionPath) ? await File.ReadAllTextAsync(sessionPath, timeout.Token) : "missing";
                throw new InvalidOperationException($"Tool did not start. server={server.Status}; session={sessionText}; stdout: {await firstOutput}; stderr: {await firstError}");
            }

            var before = ConversationSession.Parse(await File.ReadAllTextAsync(sessionPath, timeout.Token));
            var originalRunId = before.Tree.ActivePath().Last(node => node.Type == "run_started")
                .Payload.GetProperty("runId").GetString();
            Assert.Contains(before.Tree.ActivePath(), node => node.Type == "tool_intent" &&
                node.Payload.GetProperty("name").GetString() == "bash");
            Assert.DoesNotContain(before.Tree.ActivePath(), node => node.Type == "tool_outcome");

            await interrupted.WaitForExitAsync(timeout.Token);
            _ = await firstOutput;
            _ = await firstError;
            Assert.NotEqual(0, interrupted.ExitCode);
            Assert.Equal("x", await File.ReadAllTextAsync(sideEffectPath, timeout.Token));
            var afterCrash = ConversationSession.Parse(await File.ReadAllTextAsync(sessionPath, timeout.Token));
            Assert.DoesNotContain(afterCrash.Tree.ActivePath(), node => node.Type == "tool_outcome");
            Assert.DoesNotContain(afterCrash.Tree.ActivePath(), node => node.Type == "run_finished");

            recovered = StartCli(root, agentDirectory, true, "--provider", "fixture", "--model", "crash-window-fixture",
                "--mode", "rpc", "--session", sessionPath, "--tools", "bash", "--offline");
            var recoveryError = recovered.StandardError.ReadToEndAsync();
            var lines = new List<string>();
            await WriteRpcCommandAsync(recovered, new { id = "recovery-state", type = "get_state" }, timeout.Token);
            await ReadRpcUntilAsync(recovered, lines, record =>
                record.GetProperty("type").GetString() == "response" &&
                record.TryGetProperty("id", out var id) && id.GetString() == "recovery-state", timeout.Token);

            var reopened = ConversationSession.Parse(await File.ReadAllTextAsync(sessionPath, timeout.Token));
            var activePath = reopened.Tree.ActivePath();
            Assert.Contains(activePath, node => node.Type == "run_recovered" &&
                node.Payload.GetProperty("runId").GetString() == originalRunId &&
                node.Payload.GetProperty("unknownOperations").EnumerateArray().Any(name => name.GetString() == "bash"));
            Assert.DoesNotContain(activePath, node => node.Type == "run_finished" &&
                node.Payload.GetProperty("runId").GetString() == originalRunId);

            await WriteRpcCommandAsync(recovered, new { id = "after-crash", type = "prompt", message = "continue" }, timeout.Token);
            await ReadRpcUntilAsync(recovered, lines, record => record.GetProperty("type").GetString() == "agent_settled", timeout.Token);
            using (var request = JsonDocument.Parse(await continuationRequest.Task.WaitAsync(timeout.Token)))
            {
                var messages = request.RootElement.GetProperty("messages").EnumerateArray().ToArray();
                var assistantIndex = Array.FindIndex(messages, message => message.GetProperty("role").GetString() == "assistant" &&
                    message.TryGetProperty("tool_calls", out var calls) && calls.EnumerateArray().Any(call =>
                        call.GetProperty("id").GetString() == "call-crash-window"));
                Assert.True(assistantIndex >= 0, "The recovered provider request omitted the persisted assistant tool call.");
                Assert.True(assistantIndex + 1 < messages.Length, "The recovered provider request ended after an incomplete tool call.");
                var toolResult = messages[assistantIndex + 1];
                Assert.Equal("tool", toolResult.GetProperty("role").GetString());
                Assert.Equal("call-crash-window", toolResult.GetProperty("tool_call_id").GetString());
                Assert.Contains("No result provided", toolResult.GetProperty("content").GetString(), StringComparison.Ordinal);
                Assert.Contains(messages, message => message.GetProperty("role").GetString() == "user" &&
                    message.TryGetProperty("content", out var content) && content.GetString()?.Contains(
                        "Outcome UNKNOWN for bash", StringComparison.Ordinal) == true);
            }
            var idle = false;
            for (var attempt = 0; attempt < 100 && !idle; attempt++)
            {
                var id = $"recovered-idle-{attempt}";
                await WriteRpcCommandAsync(recovered, new { id, type = "get_state" }, timeout.Token);
                using var state = await ReadRpcResponseAsync(recovered, lines, id, timeout.Token);
                idle = !state.RootElement.GetProperty("data").GetProperty("isStreaming").GetBoolean();
                if (!idle) await Task.Delay(5, timeout.Token);
            }
            Assert.True(idle);
            await WriteRpcCommandAsync(recovered, new { id = "recovered-messages", type = "get_messages" }, timeout.Token);
            using var recoveredMessages = await ReadRpcResponseAsync(recovered, lines, "recovered-messages", timeout.Token);
            var messageTexts = new List<string>();
            foreach (var message in recoveredMessages.RootElement.GetProperty("data").GetProperty("messages").EnumerateArray())
            {
                if (!message.TryGetProperty("content", out var content)) continue;
                if (content.ValueKind == JsonValueKind.String) messageTexts.Add(content.GetString() ?? "");
                else if (content.ValueKind == JsonValueKind.Array)
                    foreach (var part in content.EnumerateArray())
                        if (part.TryGetProperty("text", out var text)) messageTexts.Add(text.GetString() ?? "");
            }
            Assert.Contains(messageTexts, text => text.Contains("Outcome UNKNOWN for bash", StringComparison.Ordinal));
            Assert.DoesNotContain(messageTexts, text => text.Contains("No result provided", StringComparison.Ordinal));
            var recoveredSession = ConversationSession.Parse(await File.ReadAllTextAsync(sessionPath, timeout.Token));
            Assert.DoesNotContain(recoveredSession.Tree.ActivePath(), node => node.Type == "tool_outcome");

            recovered.StandardInput.Close();
            await recovered.WaitForExitAsync(timeout.Token);
            await server;
            Assert.Equal(0, recovered.ExitCode);
            Assert.Equal(string.Empty, await recoveryError.WaitAsync(timeout.Token));
            Assert.Equal("x", await File.ReadAllTextAsync(sideEffectPath, timeout.Token));
        }
        finally
        {
            timeout.Cancel();
            listener.Close();
            try { await server; }
            catch (Exception error) when (error is OperationCanceledException or HttpListenerException or ObjectDisposedException or IOException) { }
            foreach (var process in new[] { interrupted, recovered })
            {
                if (process is { HasExited: false })
                {
                    process.Kill(entireProcessTree: true);
                    await process.WaitForExitAsync();
                }
                process?.Dispose();
            }
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ConcurrentProcessSaveWaitsForTheSessionLeaseAndPersistsTheToolOutcome()
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS()) return;
        var root = Path.Combine(Path.GetTempPath(), "pisharp-session-lease-" + Guid.NewGuid().ToString("N"));
        var agentDirectory = Path.Combine(root, "agent");
        Directory.CreateDirectory(agentDirectory);
        var sessionPath = Path.Combine(root, "lease.session.json");
        var startedPath = Path.Combine(root, "tool-started");
        var releasePath = Path.Combine(root, "release-tool");
        var sideEffectPath = Path.Combine(root, "side-effects");
        using var reservation = new TcpListener(IPAddress.Loopback, 0);
        reservation.Start();
        var port = ((IPEndPoint)reservation.LocalEndpoint).Port;
        reservation.Stop();
        await File.WriteAllTextAsync(Path.Combine(agentDirectory, "models.json"), JsonSerializer.Serialize(new
        {
            providers = new
            {
                fixture = new
                {
                    baseUrl = $"http://127.0.0.1:{port}/v1",
                    apiKeyEnv = "PISHARP_FIXTURE_KEY",
                    models = new[] { new { id = "session-lease-fixture", api = "openai-completions" } }
                }
            }
        }));
        using var listener = new HttpListener();
        listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        listener.Start();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        Process? process = null;
        var server = Task.Run(async () =>
        {
            var first = await listener.GetContextAsync().WaitAsync(timeout.Token);
            var command = $"touch {ProcessTestHelpers.ShellQuote(startedPath)}; while [ ! -e {ProcessTestHelpers.ShellQuote(releasePath)} ]; do sleep 0.02; done; printf x >> {ProcessTestHelpers.ShellQuote(sideEffectPath)}";
            await WriteSseResponseAsync(first, "session-lease-tool", new
            {
                role = "assistant",
                tool_calls = new[]
                {
                    new
                    {
                        index = 0,
                        id = "call-session-lease",
                        type = "function",
                        function = new { name = "bash", arguments = JsonSerializer.Serialize(new { command }) }
                    }
                }
            }, "tool_calls");
            var second = await listener.GetContextAsync().WaitAsync(timeout.Token);
            await WriteSseResponseAsync(second, "session-lease-final", new { role = "assistant", content = "done" }, "stop");
        }, timeout.Token);

        try
        {
            process = StartCli(root, agentDirectory, false, "--provider", "fixture", "--model", "session-lease-fixture",
                "--session", sessionPath, "--print", "run blocked command", "--tools", "bash", "--offline");
            var output = process.StandardOutput.ReadToEndAsync();
            var error = process.StandardError.ReadToEndAsync();
            await ProcessTestHelpers.WaitForFileAsync(startedPath, timeout.Token);
            var macOs = OperatingSystem.IsMacOS();
            var lease = new FileStream(sessionPath + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite,
                macOs ? FileShare.None : FileShare.ReadWrite);
            if (!macOs) lease.Lock(0, 1);
            try
            {
                var inProgress = ConversationSession.Parse(await File.ReadAllTextAsync(sessionPath, timeout.Token));
                Assert.Contains(inProgress.Tree.ActivePath(), node => node.Type == "tool_intent");
                Assert.DoesNotContain(inProgress.Tree.ActivePath(), node => node.Type == "tool_outcome");
                await File.WriteAllTextAsync(releasePath, "continue", timeout.Token);
                await ProcessTestHelpers.WaitForFileAsync(sideEffectPath, timeout.Token);
                await Task.Delay(200, timeout.Token);
                Assert.False(process.HasExited, "The session writer exited while another process held its lock.");
                var stillInProgress = ConversationSession.Parse(await File.ReadAllTextAsync(sessionPath, timeout.Token));
                Assert.DoesNotContain(stillInProgress.Tree.ActivePath(), node => node.Type == "tool_outcome");
            }
            finally
            {
                if (!macOs) lease.Unlock(0, 1);
                await lease.DisposeAsync();
            }

            await process.WaitForExitAsync(timeout.Token);
            await server;
            Assert.Equal(0, process.ExitCode);
            Assert.Equal(string.Empty, await error.WaitAsync(timeout.Token));
            Assert.Contains("done", await output.WaitAsync(timeout.Token), StringComparison.Ordinal);
            Assert.Equal("x", await File.ReadAllTextAsync(sideEffectPath, timeout.Token));
            var completed = ConversationSession.Parse(await File.ReadAllTextAsync(sessionPath, timeout.Token));
            Assert.Contains(completed.Tree.ActivePath(), node => node.Type == "tool_outcome");
            Assert.Contains(completed.Tree.ActivePath(), node => node.Type == "run_finished" &&
                node.Payload.GetProperty("completed").ValueKind == System.Text.Json.JsonValueKind.True);
        }
        finally
        {
            timeout.Cancel();
            listener.Close();
            try { await server; }
            catch (Exception exception) when (exception is OperationCanceledException or HttpListenerException or ObjectDisposedException or IOException) { }
            if (process is { HasExited: false })
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync();
            }
            process?.Dispose();
            Directory.Delete(root, recursive: true);
        }
    }

    private static Process StartCli(string root, string agentDirectory, bool redirectStandardInput, params string[] arguments)
    {
        var start = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = root,
            RedirectStandardInput = redirectStandardInput,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        start.ArgumentList.Add(typeof(CliArguments).Assembly.Location);
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        start.Environment["PISHARP_FIXTURE_KEY"] = "fixture-only-key";
        start.Environment["PISHARP_AGENT_DIR"] = agentDirectory;
        foreach (var name in new[] { "OPENAI_API_KEY", "PISHARP_API_KEY", "PISHARP_BASE_URL", "PISHARP_MODEL",
                     "PISHARP_AUTH_PATH", "PISHARP_MODELS_PATH", "PISHARP_SETTINGS_PATH", "PISHARP_PROVIDER" })
            start.Environment.Remove(name);
        return Process.Start(start)!;
    }

    private static async Task WriteRpcCommandAsync(Process process, object command, CancellationToken cancellationToken)
    {
        await process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(command).AsMemory(), cancellationToken);
        await process.StandardInput.FlushAsync(cancellationToken);
    }

    private static async Task<JsonDocument> ReadRpcResponseAsync(Process process, ICollection<string> lines, string id,
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
        throw new EndOfStreamException("RPC process exited before the expected recovery response.");
    }

    private static async Task ReadRpcUntilAsync(Process process, ICollection<string> lines, Func<JsonElement, bool> predicate,
        CancellationToken cancellationToken)
    {
        while (await process.StandardOutput.ReadLineAsync(cancellationToken) is { } line)
        {
            lines.Add(line);
            using var record = JsonDocument.Parse(line);
            if (predicate(record.RootElement)) return;
        }
        throw new EndOfStreamException("RPC process exited before the expected recovery event.");
    }

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
                model = "crash-window-fixture",
                choices = new[] { new { index = 0, delta, finish_reason = (string?)null } }
            };
            var complete = new
            {
                id,
                @object = "chat.completion.chunk",
                created = 1,
                model = "crash-window-fixture",
                choices = new[] { new { index = 0, delta = new { }, finish_reason = finishReason } }
            };
            await writer.WriteAsync($"data: {JsonSerializer.Serialize(chunk)}\n\n");
            await writer.WriteAsync($"data: {JsonSerializer.Serialize(complete)}\n\n");
            await writer.WriteAsync("data: [DONE]\n\n");
            await writer.FlushAsync();
        }
        context.Response.Close();
    }
}
