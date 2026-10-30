using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Microsoft.Extensions.AI;
using PiSharp.Cli;
using PiSharp.Runtime.Sessions;

namespace PiSharp.Tests;

public sealed class RpcCompactionProcessTests
{
    [Fact]
    public async Task RpcProcessAbortCancelsAnInFlightCompactionAndReportsItsLifecycle()
    {
        if (!OperatingSystem.IsLinux()) return;
        var root = Path.Combine(Path.GetTempPath(), "pisharp-rpc-compact-abort-" + Guid.NewGuid().ToString("N"));
        var agentDirectory = Path.Combine(root, "agent");
        var sessionDirectory = Path.Combine(root, "sessions");
        Directory.CreateDirectory(agentDirectory);
        var listener = StartLoopbackListener(out var port);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var requestReceived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Process? process = null;
        var provider = Task.Run(async () =>
        {
            var context = await listener.GetContextAsync().WaitAsync(timeout.Token);
            using (var reader = new StreamReader(context.Request.InputStream))
                _ = await reader.ReadToEndAsync(timeout.Token);
            requestReceived.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, timeout.Token);
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
                        models = new[] { new { id = "rpc-compact-abort-fixture", api = "openai-completions" } }
                    }
                }
            }));
            await File.WriteAllTextAsync(Path.Combine(agentDirectory, "settings.json"),
                "{\"compaction\":{\"keepRecentTokens\":1}}");
            var conversation = new ConversationSession(root, "rpc-compact-abort-fixture",
                $"http://127.0.0.1:{port}/v1", "fixture");
            conversation.Append(new ChatMessage(ChatRole.User, "first question"));
            conversation.Append(new ChatMessage(ChatRole.Assistant, "first answer"));
            conversation.Append(new ChatMessage(ChatRole.User, "latest question"));
            var store = new ConversationStore(root, sessionDirectory);
            var sessionPath = store.NewPath(conversation);
            await store.SaveAsync(conversation, sessionPath);

            var start = new ProcessStartInfo("dotnet")
            {
                WorkingDirectory = root,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            start.ArgumentList.Add(typeof(CliArguments).Assembly.Location);
            foreach (var argument in new[] { "--mode", "rpc", "--provider", "fixture", "--model",
                "rpc-compact-abort-fixture", "--offline", "--session", sessionPath, "--session-dir", sessionDirectory })
                start.ArgumentList.Add(argument);
            foreach (var name in new[] { "OPENAI_API_KEY", "PISHARP_API_KEY", "PISHARP_BASE_URL", "PISHARP_MODEL",
                "PISHARP_AUTH_PATH", "PISHARP_MODELS_PATH", "PISHARP_SETTINGS_PATH", "PISHARP_FIXTURE_KEY" })
                start.Environment.Remove(name);
            start.Environment["PISHARP_AGENT_DIR"] = agentDirectory;
            start.Environment["PISHARP_SESSION_DIR"] = sessionDirectory;
            start.Environment["PISHARP_FIXTURE_KEY"] = "fixture-only-key";
            process = Process.Start(start)!;
            var stderr = process.StandardError.ReadToEndAsync();
            await WriteAsync(process, "{\"id\":\"compact\",\"type\":\"compact\"}", timeout.Token);

            var lines = new List<string>();
            while (await process.StandardOutput.ReadLineAsync(timeout.Token) is { } line)
            {
                lines.Add(line);
                using var record = JsonDocument.Parse(line);
                if (record.RootElement.TryGetProperty("type", out var type) && type.GetString() == "compaction_start") break;
            }
            await requestReceived.Task.WaitAsync(timeout.Token);
            await WriteAsync(process, "{\"id\":\"state\",\"type\":\"get_state\"}", timeout.Token);
            while (await process.StandardOutput.ReadLineAsync(timeout.Token) is { } line)
            {
                lines.Add(line);
                using var record = JsonDocument.Parse(line);
                if (record.RootElement.TryGetProperty("id", out var id) && id.GetString() == "state")
                {
                    Assert.True(record.RootElement.GetProperty("data").GetProperty("isCompacting").GetBoolean());
                    break;
                }
            }
            await WriteAsync(process, "{\"id\":\"abort\",\"type\":\"abort\"}", timeout.Token);
            while (await process.StandardOutput.ReadLineAsync(timeout.Token) is { } line)
            {
                lines.Add(line);
                using var record = JsonDocument.Parse(line);
                if (record.RootElement.TryGetProperty("id", out var id) && id.GetString() == "abort") break;
            }
            process.StandardInput.Close();
            await process.WaitForExitAsync(timeout.Token);

            Assert.Equal(0, process.ExitCode);
            Assert.Equal("", await stderr.WaitAsync(timeout.Token));
            var records = lines.Select(line => JsonDocument.Parse(line)).ToArray();
            try
            {
                var end = Assert.Single(records, record => record.RootElement.GetProperty("type").GetString() == "compaction_end");
                Assert.True(end.RootElement.GetProperty("aborted").GetBoolean());
                Assert.False(end.RootElement.TryGetProperty("result", out _));
                Assert.False(end.RootElement.TryGetProperty("errorMessage", out _));
                var compact = Assert.Single(records, record => record.RootElement.TryGetProperty("id", out var id) &&
                    id.GetString() == "compact");
                Assert.False(compact.RootElement.GetProperty("success").GetBoolean());
                var abort = Assert.Single(records, record => record.RootElement.TryGetProperty("id", out var id) &&
                    id.GetString() == "abort");
                Assert.True(abort.RootElement.GetProperty("success").GetBoolean());
                Assert.True(Array.IndexOf(records, end) < Array.IndexOf(records, compact) &&
                    Array.IndexOf(records, compact) < Array.IndexOf(records, abort));
            }
            finally { foreach (var record in records) record.Dispose(); }
        }
        finally
        {
            timeout.Cancel();
            listener.Close();
            try { await provider; }
            catch (Exception exception) when (exception is OperationCanceledException or HttpListenerException or ObjectDisposedException) { }
            if (process is { HasExited: false })
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync();
            }
            process?.Dispose();
            Directory.Delete(root, recursive: true);
        }
    }

    [Theory]
    [InlineData("complete")]
    [InlineData("abort")]
    [InlineData("eof")]
    public async Task RpcProcessSerializesOverlappingCompactionsAndSettlesEachAcceptedRequestOnce(string settlement)
    {
        if (!OperatingSystem.IsLinux()) return;
        var root = Path.Combine(Path.GetTempPath(), "pisharp-rpc-overlapping-compact-" + Guid.NewGuid().ToString("N"));
        var agentDirectory = Path.Combine(root, "agent");
        var sessionDirectory = Path.Combine(root, "sessions");
        Directory.CreateDirectory(agentDirectory);
        var listener = StartLoopbackListener(out var port);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var requestReceived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseProvider = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Process? process = null;
        var provider = Task.Run(() => ServeCompactionRequestsAsync(listener, requestReceived, releaseProvider, timeout.Token), timeout.Token);

        try
        {
            const string modelId = "rpc-overlapping-compact-fixture";
            await File.WriteAllTextAsync(Path.Combine(agentDirectory, "models.json"), JsonSerializer.Serialize(new
            {
                providers = new
                {
                    fixture = new
                    {
                        baseUrl = $"http://127.0.0.1:{port}/v1",
                        apiKeyEnv = "PISHARP_FIXTURE_KEY",
                        models = new[] { new { id = modelId, api = "openai-completions" } }
                    }
                }
            }));
            await File.WriteAllTextAsync(Path.Combine(agentDirectory, "settings.json"),
                "{\"compaction\":{\"keepRecentTokens\":1}}");
            var conversation = new ConversationSession(root, modelId, $"http://127.0.0.1:{port}/v1", "fixture");
            conversation.Append(new ChatMessage(ChatRole.User, "first question"));
            conversation.Append(new ChatMessage(ChatRole.Assistant, "first answer"));
            conversation.Append(new ChatMessage(ChatRole.User, "latest question"));
            var store = new ConversationStore(root, sessionDirectory);
            var sessionPath = store.NewPath(conversation);
            await store.SaveAsync(conversation, sessionPath);

            var start = new ProcessStartInfo("dotnet")
            {
                WorkingDirectory = root,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            start.ArgumentList.Add(typeof(CliArguments).Assembly.Location);
            foreach (var argument in new[] { "--mode", "rpc", "--provider", "fixture", "--model", modelId,
                "--offline", "--session", sessionPath, "--session-dir", sessionDirectory })
                start.ArgumentList.Add(argument);
            foreach (var name in new[] { "OPENAI_API_KEY", "PISHARP_API_KEY", "PISHARP_BASE_URL", "PISHARP_MODEL",
                "PISHARP_AUTH_PATH", "PISHARP_MODELS_PATH", "PISHARP_SETTINGS_PATH", "PISHARP_FIXTURE_KEY" })
                start.Environment.Remove(name);
            start.Environment["PISHARP_AGENT_DIR"] = agentDirectory;
            start.Environment["PISHARP_SESSION_DIR"] = sessionDirectory;
            start.Environment["PISHARP_FIXTURE_KEY"] = "fixture-only-key";
            process = Process.Start(start)!;
            var stderr = process.StandardError.ReadToEndAsync();
            var lines = new List<string>();

            await WriteAsync(process, "{\"id\":\"compact-a\",\"type\":\"compact\"}", timeout.Token);
            await ReadUntilAsync(process, lines, record =>
                record.TryGetProperty("type", out var type) && type.GetString() == "compaction_start", timeout.Token);
            await requestReceived.Task.WaitAsync(timeout.Token);

            // The state response proves the RPC loop accepted B while A is held in the provider call.
            await WriteAsync(process, "{\"id\":\"compact-b\",\"type\":\"compact\"}", timeout.Token);
            await WriteAsync(process, "{\"id\":\"state\",\"type\":\"get_state\"}", timeout.Token);
            await ReadUntilAsync(process, lines, record =>
                record.TryGetProperty("id", out var id) && id.GetString() == "state", timeout.Token);
            using (var state = JsonDocument.Parse(Assert.Single(lines, line => IsResponseLine(line, "state"))))
                Assert.True(state.RootElement.GetProperty("data").GetProperty("isCompacting").GetBoolean());

            if (settlement == "complete")
            {
                releaseProvider.TrySetResult();
                await ReadUntilAsync(process, lines, record => HasCompactResponses(lines, "compact-a", "compact-b"), timeout.Token);
            }
            else if (settlement == "abort")
            {
                await WriteAsync(process, "{\"id\":\"abort\",\"type\":\"abort\"}", timeout.Token);
                await ReadUntilAsync(process, lines, record => HasCompactResponses(lines, "compact-a", "compact-b") &&
                    IsResponse(record, "abort"), timeout.Token);
            }

            if (settlement == "eof")
            {
                process.StandardInput.Close();
                while (await process.StandardOutput.ReadLineAsync(timeout.Token) is { } line) lines.Add(line);
            }
            else
            {
                process.StandardInput.Close();
                while (!process.HasExited && await process.StandardOutput.ReadLineAsync(timeout.Token) is { } line)
                    lines.Add(line);
            }
            await process.WaitForExitAsync(timeout.Token);

            Assert.Equal(0, process.ExitCode);
            Assert.Equal("", await stderr.WaitAsync(timeout.Token));
            var responsesA = lines.Where(line => IsCompactResponseLine(line, "compact-a")).ToArray();
            var responsesB = lines.Where(line => IsCompactResponseLine(line, "compact-b")).ToArray();
            Assert.Single(responsesA);
            Assert.Single(responsesB);
            using var responseA = JsonDocument.Parse(responsesA[0]);
            using var responseB = JsonDocument.Parse(responsesB[0]);

            if (settlement == "complete")
            {
                Assert.True(responseA.RootElement.GetProperty("success").GetBoolean());
                Assert.False(responseB.RootElement.GetProperty("success").GetBoolean());
                Assert.Equal("Already compacted", responseB.RootElement.GetProperty("error").GetString());
            }
            else
            {
                Assert.False(responseA.RootElement.GetProperty("success").GetBoolean());
                Assert.False(responseB.RootElement.GetProperty("success").GetBoolean());
            }

            if (settlement == "abort")
            {
                var abortLine = Assert.Single(lines, line => IsResponseLine(line, "abort"));
                using var abortResponse = JsonDocument.Parse(abortLine);
                Assert.True(abortResponse.RootElement.GetProperty("success").GetBoolean());
                Assert.True(lines.IndexOf(responsesA[0]) < lines.IndexOf(abortLine));
                Assert.True(lines.IndexOf(responsesB[0]) < lines.IndexOf(abortLine));
            }
            else
            {
                Assert.DoesNotContain(lines, line => IsResponseLine(line, "abort"));
            }
        }
        finally
        {
            releaseProvider.TrySetResult();
            if (process is { HasExited: false })
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync();
            }
            timeout.Cancel();
            try { await provider.WaitAsync(TimeSpan.FromSeconds(5)); }
            catch (Exception exception) when (exception is OperationCanceledException or HttpListenerException or
                                               ObjectDisposedException or TimeoutException or IOException)
            { }
            listener.Close();
            process?.Dispose();
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task RpcProcessUsesPiSplitTurnCompactionAndCustomInstructionsForHistoryOnly()
    {
        if (!OperatingSystem.IsLinux()) return;
        var root = Path.Combine(Path.GetTempPath(), "pisharp-rpc-compact-" + Guid.NewGuid().ToString("N"));
        var agentDirectory = Path.Combine(root, "agent");
        var sessionDirectory = Path.Combine(root, "sessions");
        Directory.CreateDirectory(agentDirectory);
        var listener = StartLoopbackListener(out var port);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        Process? process = null;
        var providerBodies = new List<string>();
        var provider = Task.Run(async () =>
        {
            for (var index = 0; index < 2; index++)
            {
                var context = await listener.GetContextAsync().WaitAsync(timeout.Token);
                using (var reader = new StreamReader(context.Request.InputStream))
                    providerBodies.Add(await reader.ReadToEndAsync(timeout.Token));
                context.Response.StatusCode = (int)HttpStatusCode.OK;
                context.Response.ContentType = "text/event-stream";
                await using (var writer = new StreamWriter(context.Response.OutputStream))
                {
                    var id = index == 0 ? "compact-history" : "compact-turn";
                    var text = index == 0 ? "history summary from provider" : "turn summary from provider";
                    var promptTokens = index == 0 ? 14 : 16;
                    var completionTokens = index == 0 ? 4 : 3;
                    var contentChunk = JsonSerializer.Serialize(new
                    {
                        id,
                        @object = "chat.completion.chunk",
                        created = 1,
                        model = "rpc-compact-fixture",
                        choices = new[] { new { index = 0, delta = new { role = "assistant", content = text }, finish_reason = (string?)null } }
                    });
                    var finishChunk = JsonSerializer.Serialize(new
                    {
                        id,
                        @object = "chat.completion.chunk",
                        created = 1,
                        model = "rpc-compact-fixture",
                        choices = new[] { new { index = 0, delta = new { }, finish_reason = "stop" } },
                        usage = new
                        {
                            prompt_tokens = promptTokens,
                            completion_tokens = completionTokens,
                            total_tokens = promptTokens + completionTokens
                        }
                    });
                    await writer.WriteAsync($"data: {contentChunk}\n\n");
                    await writer.WriteAsync($"data: {finishChunk}\n\n");
                    await writer.WriteAsync("data: [DONE]\n\n");
                    await writer.FlushAsync(timeout.Token);
                }
                context.Response.Close();
            }
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
                        models = new[] { new { id = "rpc-compact-fixture", api = "openai-completions" } }
                    }
                }
            }));
            var conversation = new ConversationSession(root, "rpc-compact-fixture",
                $"http://127.0.0.1:{port}/v1", "fixture");
            conversation.Append(new ChatMessage(ChatRole.User, "first question"));
            conversation.Append(new ChatMessage(ChatRole.Assistant, "first answer"));
            conversation.Append(new ChatMessage(ChatRole.User, "latest question"));
            conversation.Append(new ChatMessage(ChatRole.Assistant,
                [new FunctionCallContent("read-current", "read", new Dictionary<string, object?> { ["path"] = "current.txt" })]));
            conversation.Append(new ChatMessage(ChatRole.Tool, [new FunctionResultContent("read-current", "current file contents")]));
            var interrupted = conversation.Tree.Append("interrupted", JsonSerializer.SerializeToElement(new
            {
                prompt = "latest question",
                partialAssistantText = "partial work on current.txt",
                terminalType = "turn_interrupted",
                stopReason = "aborted",
                errorMessage = "Request was aborted"
            }));
            var store = new ConversationStore(root, sessionDirectory);
            var sessionPath = store.NewPath(conversation);
            await store.SaveAsync(conversation, sessionPath);
            await File.WriteAllTextAsync(Path.Combine(agentDirectory, "settings.json"),
                "{\"compaction\":{\"keepRecentTokens\":1}}");

            var start = new ProcessStartInfo("dotnet")
            {
                WorkingDirectory = root,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            start.ArgumentList.Add(typeof(CliArguments).Assembly.Location);
            foreach (var argument in new[] { "--mode", "rpc", "--provider", "fixture", "--model",
                "rpc-compact-fixture", "--offline", "--session", sessionPath, "--session-dir", sessionDirectory })
                start.ArgumentList.Add(argument);
            foreach (var name in new[] { "OPENAI_API_KEY", "PISHARP_API_KEY", "PISHARP_BASE_URL", "PISHARP_MODEL",
                "PISHARP_AUTH_PATH", "PISHARP_MODELS_PATH", "PISHARP_SETTINGS_PATH", "PISHARP_FIXTURE_KEY" })
                start.Environment.Remove(name);
            start.Environment["PISHARP_AGENT_DIR"] = agentDirectory;
            start.Environment["PISHARP_SESSION_DIR"] = sessionDirectory;
            start.Environment["PISHARP_FIXTURE_KEY"] = "fixture-only-key";
            start.Environment["PISHARP_CONTEXT_WINDOW_TOKENS"] = "32768";
            process = Process.Start(start)!;
            var error = process.StandardError.ReadToEndAsync();
            await process.StandardInput.WriteLineAsync("{\"id\":\"compact\",\"type\":\"compact\",\"customInstructions\":\"preserve decisions\"}");
            var outputLines = new List<string>();
            while (await process.StandardOutput.ReadLineAsync().WaitAsync(timeout.Token) is { } line)
            {
                outputLines.Add(line);
                if (line.Contains("\"id\":\"compact\"", StringComparison.Ordinal)) break;
            }
            process.StandardInput.Close();
            await process.WaitForExitAsync(timeout.Token);
            await provider;

            Assert.Equal(0, process.ExitCode);
            Assert.Equal("", await error.WaitAsync(timeout.Token));
            Assert.Equal(2, providerBodies.Count);
            Assert.Contains("preserve decisions", providerBodies[0], StringComparison.Ordinal);
            Assert.Contains("first question", providerBodies[0], StringComparison.Ordinal);
            Assert.Contains("## Goal", providerBodies[0], StringComparison.Ordinal);
            Assert.DoesNotContain("preserve decisions", providerBodies[1], StringComparison.Ordinal);
            Assert.Contains("Original Request", providerBodies[1], StringComparison.Ordinal);
            Assert.Contains("current.txt", providerBodies[1], StringComparison.Ordinal);
            var trailingOutput = await process.StandardOutput.ReadToEndAsync(timeout.Token);
            outputLines.AddRange(trailingOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries));
            var records = outputLines.Select(line => JsonDocument.Parse(line)).ToArray();
            try
            {
                var response = Assert.Single(records, record => record.RootElement.TryGetProperty("id", out var id) &&
                    id.GetString() == "compact");
                Assert.True(response.RootElement.GetProperty("success").GetBoolean());
                var data = response.RootElement.GetProperty("data");
                Assert.Equal("history summary from provider\n\n---\n\n**Turn Context (split turn):**\n\nturn summary from provider\n\n<read-files>\ncurrent.txt\n</read-files>",
                    data.GetProperty("summary").GetString());
                Assert.Equal(interrupted.Id, data.GetProperty("firstKeptEntryId").GetString());
                Assert.True(data.GetProperty("tokensBefore").GetInt32() > 0);
                Assert.True(data.GetProperty("estimatedTokensAfter").GetInt32() > 0);
                Assert.True(data.TryGetProperty("usage", out var summaryUsage), data.GetRawText());
                Assert.Equal(30, summaryUsage.GetProperty("input").GetInt64());
                Assert.Equal(0, summaryUsage.GetProperty("cacheRead").GetInt64());
                Assert.Equal(0, summaryUsage.GetProperty("cacheWrite").GetInt64());
                Assert.Equal(7, summaryUsage.GetProperty("output").GetInt64());
                Assert.Equal(37, summaryUsage.GetProperty("totalTokens").GetInt64());
                Assert.True(data.GetProperty("details").ValueKind == JsonValueKind.Object);
                Assert.Equal("current.txt", Assert.Single(data.GetProperty("details").GetProperty("readFiles").EnumerateArray()).GetString());
                Assert.Empty(data.GetProperty("details").GetProperty("modifiedFiles").EnumerateArray());
                var persisted = await store.LoadAsync(sessionPath, timeout.Token);
                var persistedCompaction = Assert.Single(persisted.Tree.Entries, entry => entry.Type == "compaction");
                Assert.Equal(interrupted.Id, persistedCompaction.Payload.GetProperty("firstKeptEntryId").GetString());
                Assert.Contains(persisted.ContextMessages(), message => message.Text == "partial work on current.txt");
                Assert.Equal(data.GetProperty("tokensBefore").GetInt32(),
                    persistedCompaction.Payload.GetProperty("tokensBefore").GetInt32());
                Assert.True(JsonElement.DeepEquals(data.GetProperty("details"),
                    persistedCompaction.Payload.GetProperty("details")));
                var compactionStart = Assert.Single(records, record =>
                    record.RootElement.GetProperty("type").GetString() == "compaction_start");
                var compactionEnd = Assert.Single(records, record =>
                    record.RootElement.GetProperty("type").GetString() == "compaction_end");
                Assert.Equal("manual", compactionStart.RootElement.GetProperty("reason").GetString());
                Assert.Equal("manual", compactionEnd.RootElement.GetProperty("reason").GetString());
                Assert.True(JsonElement.DeepEquals(data, compactionEnd.RootElement.GetProperty("result")));
                Assert.True(Array.IndexOf(records, compactionStart) < Array.IndexOf(records, compactionEnd) &&
                    Array.IndexOf(records, compactionEnd) < Array.IndexOf(records, response));
            }
            finally { foreach (var record in records) record.Dispose(); }
        }
        finally
        {
            timeout.Cancel();
            listener.Close();
            try { await provider; }
            catch (Exception exception) when (exception is OperationCanceledException or HttpListenerException or ObjectDisposedException) { }
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
        using var socket = new TcpListener(IPAddress.Loopback, 0);
        socket.Start();
        port = ((IPEndPoint)socket.LocalEndpoint).Port;
        socket.Stop();
        var listener = new HttpListener();
        listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        listener.Start();
        return listener;
    }

    private static async Task WriteAsync(Process process, string line, CancellationToken cancellationToken)
    {
        await process.StandardInput.WriteLineAsync(line.AsMemory(), cancellationToken);
        await process.StandardInput.FlushAsync(cancellationToken);
    }

    private static async Task ReadUntilAsync(Process process, List<string> lines, Func<JsonElement, bool> predicate,
        CancellationToken cancellationToken)
    {
        while (await process.StandardOutput.ReadLineAsync(cancellationToken) is { } line)
        {
            lines.Add(line);
            using var record = JsonDocument.Parse(line);
            if (predicate(record.RootElement)) return;
        }
        throw new EndOfStreamException("RPC process exited before the expected response was observed.");
    }

    private static async Task ServeCompactionRequestsAsync(HttpListener listener,
        TaskCompletionSource firstRequestReceived, TaskCompletionSource releaseFirstResponse,
        CancellationToken cancellationToken)
    {
        var requestNumber = 0;
        while (!cancellationToken.IsCancellationRequested)
        {
            HttpListenerContext context;
            try { context = await listener.GetContextAsync().WaitAsync(cancellationToken); }
            catch (Exception error) when (error is OperationCanceledException or HttpListenerException or ObjectDisposedException)
            { return; }

            using (var reader = new StreamReader(context.Request.InputStream))
                _ = await reader.ReadToEndAsync(cancellationToken);
            var index = Interlocked.Increment(ref requestNumber);
            firstRequestReceived.TrySetResult();
            if (index == 1)
            {
                try { await releaseFirstResponse.Task.WaitAsync(cancellationToken); }
                catch (OperationCanceledException) { return; }
            }

            var id = $"compact-{index}";
            var contentChunk = JsonSerializer.Serialize(new
            {
                id,
                @object = "chat.completion.chunk",
                created = 1,
                model = "rpc-overlapping-compact-fixture",
                choices = new[] { new { index = 0, delta = new { role = "assistant", content = $"summary-{index}" }, finish_reason = (string?)null } }
            });
            var finishChunk = JsonSerializer.Serialize(new
            {
                id,
                @object = "chat.completion.chunk",
                created = 1,
                model = "rpc-overlapping-compact-fixture",
                choices = new[] { new { index = 0, delta = new { }, finish_reason = "stop" } },
                usage = new { prompt_tokens = 12, completion_tokens = 3, total_tokens = 15 }
            });
            context.Response.StatusCode = (int)HttpStatusCode.OK;
            context.Response.ContentType = "text/event-stream";
            try
            {
                await using var writer = new StreamWriter(context.Response.OutputStream);
                await writer.WriteAsync($"data: {contentChunk}\n\ndata: {finishChunk}\n\ndata: [DONE]\n\n");
                await writer.FlushAsync(cancellationToken);
                context.Response.Close();
            }
            catch (Exception error) when (error is HttpListenerException or IOException or ObjectDisposedException or
                                           OperationCanceledException)
            {
                try { context.Response.Abort(); } catch (ObjectDisposedException) { }
            }
        }
    }

    private static bool HasCompactResponses(List<string> lines, string firstId, string secondId) =>
        lines.Any(line => IsCompactResponseLine(line, firstId)) &&
        lines.Any(line => IsCompactResponseLine(line, secondId));

    private static bool IsCompactResponseLine(string line, string id)
    {
        using var record = JsonDocument.Parse(line);
        return IsResponse(record.RootElement, id) &&
            record.RootElement.TryGetProperty("command", out var command) && command.GetString() == "compact";
    }

    private static bool IsResponseLine(string line, string id)
    {
        using var record = JsonDocument.Parse(line);
        return IsResponse(record.RootElement, id);
    }

    private static bool IsResponse(JsonElement record, string id) =>
        record.TryGetProperty("id", out var responseId) && responseId.GetString() == id &&
        record.TryGetProperty("type", out var type) && type.GetString() == "response";
}
