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
    public async Task ProcessKillAfterCompactionSaveReloadsOneCommittedEntry()
    {
        if (!OperatingSystem.IsLinux()) return;
        var root = Path.Combine(Path.GetTempPath(), "pisharp-crash-compaction-" + Guid.NewGuid().ToString("N"));
        var workingDirectory = Path.Combine(root, "project");
        var sessionPath = Path.Combine(root, "sessions", "compaction.session.json");
        Directory.CreateDirectory(workingDirectory);
        Directory.CreateDirectory(Path.GetDirectoryName(sessionPath)!);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var helperPath = ResolveSessionResumeHelperPath();
        Process? child = null;
        try
        {
            child = StartSessionResumeHelper(helperPath, "pause-after-compaction-save", workingDirectory, sessionPath);
            var output = child.StandardOutput.ReadToEndAsync();
            var error = child.StandardError.ReadToEndAsync();
            await ProcessTestHelpers.WaitForFileAsync(sessionPath + ".compaction-ready", timeout.Token);
            child.Kill(entireProcessTree: true);
            await child.WaitForExitAsync(timeout.Token);
            _ = await output.WaitAsync(timeout.Token);
            _ = await error.WaitAsync(timeout.Token);

            var store = new ConversationStore(workingDirectory, Path.GetDirectoryName(sessionPath));
            var recovered = await store.LoadAsync(sessionPath, timeout.Token);
            var compaction = Assert.Single(recovered.Tree.ActivePath(), node => node.Type == "compaction");
            Assert.Contains("deterministic response", compaction.Payload.GetProperty("summary").GetString(),
                StringComparison.Ordinal);
            Assert.Contains(recovered.ContextMessages(), message =>
                message.Text.Contains("deterministic response", StringComparison.Ordinal));
            Assert.Equal(4, recovered.ActiveMessages().Count);
            Assert.Single(ConversationSession.Parse(recovered.ToJson()).Tree.ActivePath(), node => node.Type == "compaction");
        }
        finally
        {
            if (child is { HasExited: false })
            {
                child.Kill(entireProcessTree: true);
                await child.WaitForExitAsync();
            }
            child?.Dispose();
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ChildProcessKillAfterToolIntentBeforeInvocationDoesNotRunOrReplayTool()
    {
        if (!OperatingSystem.IsLinux()) return;
        var root = Path.Combine(Path.GetTempPath(), "pisharp-crash-before-tool-" + Guid.NewGuid().ToString("N"));
        var workingDirectory = Path.Combine(root, "project");
        var sessionPath = Path.Combine(root, "sessions", "before-tool.session.json");
        Directory.CreateDirectory(workingDirectory);
        Directory.CreateDirectory(Path.GetDirectoryName(sessionPath)!);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var helperPath = ResolveSessionResumeHelperPath();
        Process? child = null;
        try
        {
            child = StartSessionResumeHelper(helperPath, "pause-after-intent", workingDirectory, sessionPath);
            var output = child.StandardOutput.ReadToEndAsync();
            var error = child.StandardError.ReadToEndAsync();
            await ProcessTestHelpers.WaitForFileAsync(sessionPath + ".intent-ready", timeout.Token);
            Assert.False(File.Exists(Path.Combine(workingDirectory, "tool-result.txt")));
            child.Kill(entireProcessTree: true);
            await child.WaitForExitAsync(timeout.Token);
            _ = await output.WaitAsync(timeout.Token);
            _ = await error.WaitAsync(timeout.Token);

            var store = new ConversationStore(workingDirectory, Path.GetDirectoryName(sessionPath));
            var interrupted = await store.LoadAsync(sessionPath, timeout.Token);
            Assert.Contains(interrupted.Tree.ActivePath(), node => node.Type == "tool_intent");
            Assert.DoesNotContain(interrupted.Tree.ActivePath(), node => node.Type == "tool_outcome");

            using var recovered = StartSessionResumeHelper(helperPath, "recover-no-replay", workingDirectory, sessionPath);
            var recoveredOutput = recovered.StandardOutput.ReadToEndAsync();
            var recoveredError = recovered.StandardError.ReadToEndAsync();
            await recovered.WaitForExitAsync(timeout.Token);

            Assert.Equal(0, recovered.ExitCode);
            Assert.Equal(string.Empty, await recoveredError.WaitAsync(timeout.Token));
            Assert.Contains("recovered-no-replay", await recoveredOutput.WaitAsync(timeout.Token), StringComparison.Ordinal);
            Assert.False(File.Exists(Path.Combine(workingDirectory, "tool-result.txt")));
            var final = await store.LoadAsync(sessionPath, timeout.Token);
            Assert.Contains(final.Tree.ActivePath(), node => node.Type == "run_recovered");
        }
        finally
        {
            if (child is { HasExited: false })
            {
                child.Kill(entireProcessTree: true);
                await child.WaitForExitAsync();
            }
            child?.Dispose();
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

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

    [Theory]
    [InlineData("openai-responses")]
    [InlineData("openai-completions")]
    [InlineData("anthropic-messages")]
    public async Task CrashAfterToolSideEffectBeforeOutcomeCheckpointRecoversWithoutReplayingTool(string api)
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
        var baseUrl = api == "anthropic-messages" ? $"http://127.0.0.1:{port}" : $"http://127.0.0.1:{port}/v1";
        await File.WriteAllTextAsync(Path.Combine(agentDirectory, "models.json"), JsonSerializer.Serialize(new
        {
            providers = new
            {
                fixture = new
                {
                    baseUrl,
                    apiKeyEnv = "PISHARP_FIXTURE_KEY",
                    models = new[] { new { id = "crash-window-fixture", api } }
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
            await WriteProviderToolCallResponseAsync(first, api, "call-crash-window", command);

            var second = await listener.GetContextAsync().WaitAsync(timeout.Token);
            using (var reader = new StreamReader(second.Request.InputStream))
                continuationRequest.TrySetResult(await reader.ReadToEndAsync(timeout.Token));
            await WriteProviderTextResponseAsync(second, api, "resumed safely");
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
                AssertProviderRecoveryPair(request.RootElement, api, "call-crash-window", "No result provided");
                Assert.Contains("Outcome UNKNOWN for bash", request.RootElement.GetRawText(), StringComparison.Ordinal);
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

    [Theory]
    [InlineData("openai-responses")]
    [InlineData("openai-completions")]
    [InlineData("anthropic-messages")]
    public async Task ReopenedCompletedToolHistoryContinuesOnPhysicalProviderWithoutReplayingTool(string api)
    {
        if (!OperatingSystem.IsLinux()) return;
        var root = Path.Combine(Path.GetTempPath(), "pisharp-session-provider-restart-" + Guid.NewGuid().ToString("N"));
        var agentDirectory = Path.Combine(root, "agent");
        Directory.CreateDirectory(agentDirectory);
        var sessionPath = Path.Combine(root, "reopened.session.jsonl");
        var sideEffectPath = Path.Combine(root, "side-effects");
        await File.WriteAllTextAsync(sideEffectPath, "x");
        using var reservation = new TcpListener(IPAddress.Loopback, 0);
        reservation.Start();
        var port = ((IPEndPoint)reservation.LocalEndpoint).Port;
        reservation.Stop();
        var baseUrl = api == "anthropic-messages" ? $"http://127.0.0.1:{port}" : $"http://127.0.0.1:{port}/v1";
        await File.WriteAllTextAsync(Path.Combine(agentDirectory, "models.json"), JsonSerializer.Serialize(new
        {
            providers = new
            {
                fixture = new
                {
                    baseUrl,
                    apiKeyEnv = "PISHARP_FIXTURE_KEY",
                    models = new[] { new { id = "restart-history-fixture", api } }
                }
            }
        }));

        const string callId = "call-restart-history";
        var command = $"printf x >> {ProcessTestHelpers.ShellQuote(sideEffectPath)}";
        var records = new[]
        {
            JsonSerializer.Serialize(new { type = "session", version = 3, id = "restart-history", timestamp = "2026-10-24T12:00:00Z", cwd = root }),
            JsonSerializer.Serialize(new { type = "message", id = "prior-user", parentId = (string?)null, timestamp = "2026-10-24T12:00:01Z", message = new { role = "user", content = "run a command", timestamp = 1792843201000L } }),
            JsonSerializer.Serialize(new { type = "message", id = "prior-call", parentId = "prior-user", timestamp = "2026-10-24T12:00:02Z", message = new { role = "assistant", content = new[] { new { type = "toolCall", id = callId, name = "bash", arguments = new { command } } }, provider = "fixture", model = "restart-history-fixture", api, timestamp = 1792843202000L } }),
            JsonSerializer.Serialize(new { type = "message", id = "prior-result", parentId = "prior-call", timestamp = "2026-10-24T12:00:03Z", message = new { role = "toolResult", toolCallId = callId, toolName = "bash", content = new[] { new { type = "text", text = "x" } }, timestamp = 1792843203000L } })
        };
        await File.WriteAllTextAsync(sessionPath, string.Join('\n', records) + "\n");

        using var listener = new HttpListener();
        listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        listener.Start();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        var capturedRequest = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var server = Task.Run(async () =>
        {
            var context = await listener.GetContextAsync().WaitAsync(timeout.Token);
            var expectedPath = api switch
            {
                "openai-responses" => "/v1/responses",
                "openai-completions" => "/v1/chat/completions",
                "anthropic-messages" => "/v1/messages",
                _ => throw new ArgumentOutOfRangeException(nameof(api))
            };
            Assert.Equal(expectedPath, context.Request.Url?.AbsolutePath);
            using (var reader = new StreamReader(context.Request.InputStream))
                capturedRequest.TrySetResult(await reader.ReadToEndAsync(timeout.Token));
            await WriteProviderTextResponseAsync(context, api, "continued after reload");
        }, timeout.Token);

        Process? process = null;
        try
        {
            process = StartCli(root, agentDirectory, true, "--mode", "rpc", "--session", sessionPath,
                "--session-dir", Path.Combine(root, "sessions"),
                "--tools", "bash", "--offline");
            var error = process.StandardError.ReadToEndAsync();
            var lines = new List<string>();
            await WriteRpcCommandAsync(process, new { id = "restart-history-prompt", type = "prompt", message = "continue" }, timeout.Token);
            try
            {
                await ReadRpcUntilAsync(process, lines, record => record.GetProperty("type").GetString() == "agent_settled", timeout.Token);
            }
            catch (EndOfStreamException errorEvent)
            {
                throw new InvalidOperationException($"RPC ended before provider continuation. stderr: {await error}", errorEvent);
            }

            using (var request = JsonDocument.Parse(await capturedRequest.Task.WaitAsync(timeout.Token)))
            {
                AssertProviderRecoveryPair(request.RootElement, api, callId, "x", command);
                var body = request.RootElement.GetRawText();
                var priorPromptIndex = body.IndexOf("run a command", StringComparison.Ordinal);
                var callIndex = body.IndexOf(callId, StringComparison.Ordinal);
                var continuationIndex = body.LastIndexOf("continue", StringComparison.Ordinal);
                Assert.True(priorPromptIndex >= 0 && priorPromptIndex < callIndex,
                    "The reopened provider request omitted or reordered the prior user prompt.");
                Assert.True(continuationIndex > callIndex,
                    "The new user prompt must follow the restored tool call in provider history.");
                Assert.DoesNotContain("Outcome UNKNOWN", body, StringComparison.Ordinal);
            }

            process.StandardInput.Close();
            await process.WaitForExitAsync(timeout.Token);
            await server;
            Assert.Equal(0, process.ExitCode);
            Assert.Equal(string.Empty, await error.WaitAsync(timeout.Token));
            Assert.Equal("x", await File.ReadAllTextAsync(sideEffectPath, timeout.Token));
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
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
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

    private static string ReadMessageText(JsonElement content)
    {
        if (content.ValueKind == JsonValueKind.String) return content.GetString() ?? "";
        if (content.ValueKind != JsonValueKind.Array) return "";
        return string.Concat(content.EnumerateArray()
            .Where(part => part.TryGetProperty("type", out var type) && type.GetString() == "text")
            .Select(part => part.TryGetProperty("text", out var text) ? text.GetString() : ""));
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

    private static void AssertProviderRecoveryPair(JsonElement request, string api, string callId, string expectedResult,
        string? expectedCommand = null)
    {
        if (api == "openai-responses")
        {
            var input = request.GetProperty("input").EnumerateArray().ToArray();
            var callIndex = Array.FindIndex(input, item => item.TryGetProperty("type", out var type) &&
                type.GetString() == "function_call" && item.GetProperty("call_id").GetString() == callId);
            Assert.True(callIndex >= 0, "The Responses request omitted the persisted function call.");
            Assert.Equal("bash", input[callIndex].GetProperty("name").GetString());
            AssertToolCallArguments(input[callIndex].GetProperty("arguments").GetString(), expectedCommand);
            var result = Assert.Single(input.Skip(callIndex + 1), item =>
                item.TryGetProperty("type", out var type) && type.GetString() == "function_call_output" &&
                item.GetProperty("call_id").GetString() == callId);
            Assert.Contains(expectedResult, result.GetProperty("output").GetString(), StringComparison.Ordinal);
            return;
        }

        var messages = request.GetProperty("messages").EnumerateArray().ToArray();
        if (api == "openai-completions")
        {
            var callIndex = Array.FindIndex(messages, message => message.GetProperty("role").GetString() == "assistant" &&
                message.TryGetProperty("tool_calls", out var calls) && calls.EnumerateArray().Any(call =>
                    call.GetProperty("id").GetString() == callId));
            Assert.True(callIndex >= 0, "The Chat Completions request omitted the persisted tool call.");
            var toolCall = messages[callIndex].GetProperty("tool_calls").EnumerateArray()
                .Single(call => call.GetProperty("id").GetString() == callId);
            Assert.Equal("bash", toolCall.GetProperty("function").GetProperty("name").GetString());
            AssertToolCallArguments(toolCall.GetProperty("function").GetProperty("arguments").GetString(), expectedCommand);
            var result = Assert.Single(messages.Skip(callIndex + 1), message =>
                message.GetProperty("role").GetString() == "tool" &&
                message.GetProperty("tool_call_id").GetString() == callId);
            Assert.Contains(expectedResult, result.GetProperty("content").GetString(), StringComparison.Ordinal);
            return;
        }

        var anthropicCall = messages.SelectMany((message, index) => message.GetProperty("content").ValueKind == JsonValueKind.Array
                ? message.GetProperty("content").EnumerateArray().Select(block => (message, block, index))
                : [])
            .FirstOrDefault(item => item.message.GetProperty("role").GetString() == "assistant" &&
                item.block.TryGetProperty("type", out var type) && type.GetString() == "tool_use" &&
                item.block.GetProperty("id").GetString() == callId);
        Assert.NotEqual(default, anthropicCall);
        Assert.Equal("bash", anthropicCall.block.GetProperty("name").GetString());
        if (expectedCommand is not null)
            Assert.Equal(expectedCommand, anthropicCall.block.GetProperty("input").GetProperty("command").GetString());
        var anthropicResult = messages.Skip(anthropicCall.index + 1).SelectMany(message =>
                message.GetProperty("content").ValueKind == JsonValueKind.Array
                    ? message.GetProperty("content").EnumerateArray().Select(block => (message, block))
                    : [])
            .Single(item => item.message.GetProperty("role").GetString() == "user" &&
                item.block.TryGetProperty("type", out var type) && type.GetString() == "tool_result" &&
                item.block.GetProperty("tool_use_id").GetString() == callId);
        Assert.Contains(expectedResult, anthropicResult.block.GetProperty("content").GetString(), StringComparison.Ordinal);
        if (expectedResult == "No result provided")
            Assert.True(anthropicResult.block.GetProperty("is_error").GetBoolean(),
                "Pinned Pi marks a missing result for an orphaned tool call as an error.");
    }

    private static void AssertToolCallArguments(string? arguments, string? expectedCommand)
    {
        if (expectedCommand is null) return;
        using var parsed = JsonDocument.Parse(arguments ?? throw new InvalidDataException("Tool arguments were omitted."));
        Assert.Equal(expectedCommand, parsed.RootElement.GetProperty("command").GetString());
    }

    private static Task WriteProviderToolCallResponseAsync(HttpListenerContext context, string api, string callId,
        string command)
    {
        var arguments = JsonSerializer.Serialize(new { command });
        var events = api switch
        {
            "openai-responses" => new[]
            {
                DataEvent(new { type = "response.created", response = new { id = "resp_crash_tool", @object = "response", created_at = 1, model = "crash-window-fixture", status = "in_progress", output = Array.Empty<object>() } }),
                DataEvent(new { type = "response.output_item.added", output_index = 0, item = new { id = "fc_crash_tool", type = "function_call", call_id = callId, name = "bash", arguments = "" } }),
                DataEvent(new { type = "response.function_call_arguments.done", output_index = 0, item_id = "fc_crash_tool", arguments }),
                DataEvent(new { type = "response.output_item.done", output_index = 0, item = new { id = "fc_crash_tool", type = "function_call", call_id = callId, name = "bash", arguments } }),
                DataEvent(new { type = "response.completed", response = new { id = "resp_crash_tool", @object = "response", created_at = 1, model = "crash-window-fixture", status = "completed", output = Array.Empty<object>() } }),
                "data: [DONE]\n\n"
            },
            "openai-completions" => new[]
            {
                DataEvent(new { id = "chatcmpl_crash_tool", @object = "chat.completion.chunk", created = 1, model = "crash-window-fixture", choices = new[] { new { index = 0, delta = new { role = "assistant", tool_calls = new[] { new { index = 0, id = callId, type = "function", function = new { name = "bash", arguments } } } }, finish_reason = (string?)null } } }),
                DataEvent(new { id = "chatcmpl_crash_tool", @object = "chat.completion.chunk", created = 1, model = "crash-window-fixture", choices = new[] { new { index = 0, delta = new { }, finish_reason = "tool_calls" } } }),
                "data: [DONE]\n\n"
            },
            "anthropic-messages" => new[]
            {
                AnthropicEvent("message_start", new { type = "message_start", message = new { id = "msg_crash_tool", type = "message", role = "assistant", model = "crash-window-fixture", content = Array.Empty<object>(), stop_reason = (string?)null, stop_sequence = (string?)null, usage = new { input_tokens = 1, output_tokens = 0 } } }),
                AnthropicEvent("content_block_start", new { type = "content_block_start", index = 0, content_block = new { type = "tool_use", id = callId, name = "bash", input = new { } } }),
                AnthropicEvent("content_block_delta", new { type = "content_block_delta", index = 0, delta = new { type = "input_json_delta", partial_json = arguments } }),
                AnthropicEvent("content_block_stop", new { type = "content_block_stop", index = 0 }),
                AnthropicEvent("message_delta", new { type = "message_delta", delta = new { stop_reason = "tool_use", stop_sequence = (string?)null }, usage = new { output_tokens = 1 } }),
                AnthropicEvent("message_stop", new { type = "message_stop" })
            },
            _ => throw new ArgumentOutOfRangeException(nameof(api), api, "Unsupported provider API.")
        };
        return WriteEventStreamAsync(context, events);
    }

    private static Task WriteProviderTextResponseAsync(HttpListenerContext context, string api, string text)
    {
        var events = api switch
        {
            "openai-responses" => new[]
            {
                DataEvent(new { type = "response.created", response = new { id = "resp_crash_recovered", @object = "response", created_at = 1, model = "crash-window-fixture", status = "in_progress", output = Array.Empty<object>() } }),
                DataEvent(new { type = "response.output_text.delta", item_id = "msg_recovered", output_index = 0, content_index = 0, delta = text }),
                DataEvent(new { type = "response.completed", response = new { id = "resp_crash_recovered", @object = "response", created_at = 1, model = "crash-window-fixture", status = "completed", output = Array.Empty<object>() } }),
                "data: [DONE]\n\n"
            },
            "openai-completions" => new[]
            {
                DataEvent(new { id = "chatcmpl_crash_recovered", @object = "chat.completion.chunk", created = 1, model = "crash-window-fixture", choices = new[] { new { index = 0, delta = new { role = "assistant", content = text }, finish_reason = (string?)null } } }),
                DataEvent(new { id = "chatcmpl_crash_recovered", @object = "chat.completion.chunk", created = 1, model = "crash-window-fixture", choices = new[] { new { index = 0, delta = new { }, finish_reason = "stop" } } }),
                "data: [DONE]\n\n"
            },
            "anthropic-messages" => new[]
            {
                AnthropicEvent("message_start", new { type = "message_start", message = new { id = "msg_crash_recovered", type = "message", role = "assistant", model = "crash-window-fixture", content = Array.Empty<object>(), stop_reason = (string?)null, stop_sequence = (string?)null, usage = new { input_tokens = 1, output_tokens = 0 } } }),
                AnthropicEvent("content_block_start", new { type = "content_block_start", index = 0, content_block = new { type = "text", text = "" } }),
                AnthropicEvent("content_block_delta", new { type = "content_block_delta", index = 0, delta = new { type = "text_delta", text } }),
                AnthropicEvent("content_block_stop", new { type = "content_block_stop", index = 0 }),
                AnthropicEvent("message_delta", new { type = "message_delta", delta = new { stop_reason = "end_turn", stop_sequence = (string?)null }, usage = new { output_tokens = 1 } }),
                AnthropicEvent("message_stop", new { type = "message_stop" })
            },
            _ => throw new ArgumentOutOfRangeException(nameof(api), api, "Unsupported provider API.")
        };
        return WriteEventStreamAsync(context, events);
    }

    private static string DataEvent(object value) => $"data: {JsonSerializer.Serialize(value)}\n\n";

    private static string AnthropicEvent(string name, object value) =>
        $"event: {name}\ndata: {JsonSerializer.Serialize(value)}\n\n";

    private static async Task WriteEventStreamAsync(HttpListenerContext context, IEnumerable<string> events)
    {
        context.Response.StatusCode = (int)HttpStatusCode.OK;
        context.Response.ContentType = "text/event-stream";
        await using (var writer = new StreamWriter(context.Response.OutputStream))
        {
            foreach (var item in events) await writer.WriteAsync(item);
            await writer.FlushAsync();
        }
        context.Response.Close();
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

    private static string ResolveSessionResumeHelperPath()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "PiSharp.slnx")))
            directory = directory.Parent;
        if (directory is null) throw new DirectoryNotFoundException("Could not locate repository root.");
#if DEBUG
        const string configuration = "Debug";
#else
        const string configuration = "Release";
#endif
        return Path.Combine(directory.FullName, "tests", "fixtures", "SessionResumeProcess", "bin",
            configuration, "net10.0", "SessionResumeProcess.dll");
    }

    private static Process StartSessionResumeHelper(string helperPath, string mode, string workingDirectory,
        string sessionPath)
    {
        var start = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        start.ArgumentList.Add(helperPath);
        start.ArgumentList.Add(mode);
        start.ArgumentList.Add(workingDirectory);
        start.ArgumentList.Add(sessionPath);
        return Process.Start(start) ?? throw new IOException("Could not start session recovery helper.");
    }
}
