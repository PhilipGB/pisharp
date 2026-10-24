using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using PiSharp.Cli;
using PiSharp.Runtime.Resources;
using PiSharp.Runtime.Sessions;

namespace PiSharp.Tests;

public sealed class RpcSessionReplacementProcessTests
{
    [Theory]
    [InlineData(false, true, "target-project")]
    [InlineData(true, false, "global-project")]
    public async Task RpcSessionSwitchRebindsProjectWorkingDirectoryBeforeNextToolCall(
        bool sourceTrusted, bool targetTrusted, string expectedShellPrefix)
    {
        if (!OperatingSystem.IsLinux()) return;
        var root = Path.Combine(Path.GetTempPath(), "pisharp-rpc-cross-project-switch-" + Guid.NewGuid().ToString("N"));
        var sourceCwd = Path.Combine(root, "source");
        var targetCwd = Path.Combine(root, "target");
        var agentDirectory = Path.Combine(root, "agent");
        Directory.CreateDirectory(sourceCwd);
        Directory.CreateDirectory(targetCwd);
        Directory.CreateDirectory(agentDirectory);
        await File.WriteAllTextAsync(Path.Combine(sourceCwd, "marker.txt"), "source-project-marker");
        await File.WriteAllTextAsync(Path.Combine(targetCwd, "marker.txt"), "target-project-marker");
        var sourcePi = Path.Combine(sourceCwd, ".pi");
        var targetPi = Path.Combine(targetCwd, ".pi");
        Directory.CreateDirectory(sourcePi);
        Directory.CreateDirectory(targetPi);
        await File.WriteAllTextAsync(Path.Combine(agentDirectory, "settings.json"),
            "{\"shellCommandPrefix\":\"export PISHARP_PREFIX=global-project\"}");
        await File.WriteAllTextAsync(Path.Combine(sourcePi, "settings.json"),
            "{\"shellCommandPrefix\":\"export PISHARP_PREFIX=source-project\"}");
        await File.WriteAllTextAsync(Path.Combine(sourcePi, "APPEND_SYSTEM.md"), "SOURCE_PROJECT_PRIVATE_CONTEXT_MARKER");
        await File.WriteAllTextAsync(Path.Combine(targetPi, "settings.json"),
            "{\"shellCommandPrefix\":\"export PISHARP_PREFIX=target-project\"}");
        await File.WriteAllTextAsync(Path.Combine(targetPi, "APPEND_SYSTEM.md"), "TARGET_PROJECT_PRIVATE_CONTEXT_MARKER");
        var projectTrust = new ProjectTrust(agentDirectory);
        await projectTrust.SetAsync(sourceCwd, sourceTrusted);
        await projectTrust.SetAsync(targetCwd, targetTrusted);

        using var listener = StartLoopbackListener(out var port);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        Process? process = null;
        var provider = Task.Run(async () =>
        {
            var toolCallContext = await listener.GetContextAsync().WaitAsync(timeout.Token);
            using (var request = await JsonDocument.ParseAsync(toolCallContext.Request.InputStream,
                cancellationToken: timeout.Token))
            {
                var system = string.Join("\n", request.RootElement.GetProperty("messages").EnumerateArray()
                    .Where(message => message.GetProperty("role").GetString() == "system")
                    .Select(message => message.GetProperty("content").ToString()));
                if (targetTrusted)
                    Assert.Contains("TARGET_PROJECT_PRIVATE_CONTEXT_MARKER", system, StringComparison.Ordinal);
                else
                    Assert.DoesNotContain("TARGET_PROJECT_PRIVATE_CONTEXT_MARKER", system, StringComparison.Ordinal);
                Assert.DoesNotContain("SOURCE_PROJECT_PRIVATE_CONTEXT_MARKER", system, StringComparison.Ordinal);
            }
            await WriteSseToolCallResponseAsync(toolCallContext, "rpc-cross-project-tool-call", "rpc-cross-project-fixture",
                "bash", """{"command":"printf 'target-prefix=%s\\n' \"$PISHARP_PREFIX\"; cat marker.txt"}""", "call-target-prefix");

            var continuationContext = await listener.GetContextAsync().WaitAsync(timeout.Token);
            using (var request = await JsonDocument.ParseAsync(continuationContext.Request.InputStream,
                cancellationToken: timeout.Token))
            {
                var messages = request.RootElement.GetProperty("messages").EnumerateArray().ToArray();
                var system = string.Join("\n", messages.Where(message => message.GetProperty("role").GetString() == "system")
                    .Select(message => message.GetProperty("content").ToString()));
                if (targetTrusted)
                    Assert.Contains("TARGET_PROJECT_PRIVATE_CONTEXT_MARKER", system, StringComparison.Ordinal);
                else
                    Assert.DoesNotContain("TARGET_PROJECT_PRIVATE_CONTEXT_MARKER", system, StringComparison.Ordinal);
                Assert.DoesNotContain("SOURCE_PROJECT_PRIVATE_CONTEXT_MARKER", system, StringComparison.Ordinal);
                var toolResult = Assert.Single(messages, message => message.GetProperty("role").GetString() == "tool");
                Assert.Equal("call-target-prefix", toolResult.GetProperty("tool_call_id").GetString());
                var content = toolResult.GetProperty("content").GetString();
                Assert.Contains($"target-prefix={expectedShellPrefix}", content, StringComparison.Ordinal);
                Assert.DoesNotContain("target-prefix=source-project", content, StringComparison.Ordinal);
                if (!targetTrusted)
                    Assert.DoesNotContain("target-prefix=target-project", content, StringComparison.Ordinal);
                Assert.Contains("target-project-marker", content, StringComparison.Ordinal);
                Assert.DoesNotContain("source-project-marker", content, StringComparison.Ordinal);
            }

            await WriteSseResponseAsync(continuationContext, "rpc-cross-project-finished", "read from target", "rpc-cross-project-fixture");
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
                        models = new[] { new { id = "rpc-cross-project-fixture", api = "openai-completions" } }
                    }
                }
            }));

            var sourceSession = new ConversationSession(sourceCwd, "rpc-cross-project-fixture",
                $"http://127.0.0.1:{port}/v1", "fixture");
            var targetSession = new ConversationSession(targetCwd, "rpc-cross-project-fixture",
                $"http://127.0.0.1:{port}/v1", "fixture");
            targetSession.Append(new Microsoft.Extensions.AI.ChatMessage(
                Microsoft.Extensions.AI.ChatRole.User, "existing target history"));
            var sourcePath = Path.Combine(sourceCwd, "source.session.json");
            var targetPath = Path.Combine(targetCwd, "target.session.json");
            await File.WriteAllTextAsync(sourcePath, sourceSession.ToJson());
            await File.WriteAllTextAsync(targetPath, targetSession.ToJson());

            var start = new ProcessStartInfo("dotnet")
            {
                WorkingDirectory = sourceCwd,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            start.ArgumentList.Add(typeof(CliArguments).Assembly.Location);
            foreach (var argument in new[] { "--mode", "rpc", "--provider", "fixture", "--model",
                "rpc-cross-project-fixture", "--offline", "--session", sourcePath })
                start.ArgumentList.Add(argument);
            foreach (var name in new[] { "OPENAI_API_KEY", "PISHARP_API_KEY", "PISHARP_BASE_URL", "PISHARP_MODEL",
                "PISHARP_AUTH_PATH", "PISHARP_MODELS_PATH", "PISHARP_SETTINGS_PATH", "PISHARP_SESSION_DIR", "PISHARP_FIXTURE_KEY" })
                start.Environment.Remove(name);
            start.Environment["PISHARP_AGENT_DIR"] = agentDirectory;
            start.Environment["PISHARP_FIXTURE_KEY"] = "fixture-only-key";
            process = Process.Start(start)!;
            var stderr = process.StandardError.ReadToEndAsync();

            await process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(new
            {
                id = "cross-project-switch",
                type = "switch_session",
                sessionPath = targetPath
            }));
            await process.StandardInput.FlushAsync(timeout.Token);
            var switched = false;
            while (!switched)
            {
                var line = await process.StandardOutput.ReadLineAsync(timeout.Token);
                Assert.NotNull(line);
                using var record = JsonDocument.Parse(line!);
                if (!record.RootElement.TryGetProperty("id", out var id) || id.GetString() != "cross-project-switch") continue;
                Assert.True(record.RootElement.GetProperty("success").GetBoolean(), line);
                switched = true;
            }

            await process.StandardInput.WriteLineAsync(
                "{\"id\":\"cross-project-prompt\",\"type\":\"prompt\",\"message\":\"read marker.txt\"}");
            await process.StandardInput.FlushAsync(timeout.Token);
            var settled = false;
            while (!settled)
            {
                var line = await process.StandardOutput.ReadLineAsync(timeout.Token);
                Assert.NotNull(line);
                using var record = JsonDocument.Parse(line!);
                settled = record.RootElement.GetProperty("type").GetString() == "agent_settled";
            }

            process.StandardInput.Close();
            await process.WaitForExitAsync(timeout.Token);
            await provider;
            Assert.Equal(0, process.ExitCode);
            Assert.Equal(string.Empty, await stderr.WaitAsync(timeout.Token));
        }
        finally
        {
            timeout.Cancel();
            listener.Close();
            try { await provider; }
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

    [Fact]
    public async Task RpcValidSwitchSettlesBlockedProviderRunBeforeReplacingSession()
    {
        if (!OperatingSystem.IsLinux()) return;
        var root = Path.Combine(Path.GetTempPath(), "pisharp-rpc-active-switch-" + Guid.NewGuid().ToString("N"));
        var agentDirectory = Path.Combine(root, "agent");
        Directory.CreateDirectory(agentDirectory);
        using var listener = StartLoopbackListener(out var port);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var releaseActiveResponse = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Process? process = null;
        var provider = Task.Run(async () =>
        {
            var active = await listener.GetContextAsync().WaitAsync(timeout.Token);
            active.Response.StatusCode = (int)HttpStatusCode.OK;
            active.Response.ContentType = "text/event-stream";
            active.Response.SendChunked = true;
            await using (var writer = new StreamWriter(active.Response.OutputStream))
            {
                var partial = new
                {
                    id = "chatcmpl-rpc-active-switch",
                    @object = "chat.completion.chunk",
                    created = 1,
                    model = "rpc-switch-fixture",
                    choices = new[] { new { index = 0, delta = new { role = "assistant", content = "source partial" }, finish_reason = (string?)null } }
                };
                await writer.WriteAsync($"data: {JsonSerializer.Serialize(partial)}\n\n");
                await writer.FlushAsync(timeout.Token);
                await releaseActiveResponse.Task.WaitAsync(timeout.Token);
            }
            active.Response.Close();

            var destination = await listener.GetContextAsync().WaitAsync(timeout.Token);
            using (var request = await JsonDocument.ParseAsync(destination.Request.InputStream,
                cancellationToken: timeout.Token))
            {
                var messages = request.RootElement.GetProperty("messages").EnumerateArray().ToArray();
                var allContent = string.Join("\n", messages.Select(message => message.GetProperty("content").ToString()));
                Assert.Contains("destination history", allContent, StringComparison.Ordinal);
                Assert.Contains("continue on destination", allContent, StringComparison.Ordinal);
                Assert.DoesNotContain("source partial", allContent, StringComparison.Ordinal);
                Assert.DoesNotContain("source active prompt", allContent, StringComparison.Ordinal);
            }
            await WriteSseResponseAsync(destination, "chatcmpl-rpc-destination", "destination reply", "rpc-switch-fixture");
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
                        models = new[] { new { id = "rpc-switch-fixture", api = "openai-completions" } }
                    }
                }
            }));
            var sourcePath = Path.Combine(root, "source.session.json");
            var targetPath = Path.Combine(root, "target.session.json");
            await File.WriteAllTextAsync(sourcePath, new ConversationSession(root, "rpc-switch-fixture",
                $"http://127.0.0.1:{port}/v1", "fixture").ToJson());
            var target = new ConversationSession(root, "rpc-switch-fixture", $"http://127.0.0.1:{port}/v1", "fixture");
            target.Append(new Microsoft.Extensions.AI.ChatMessage(Microsoft.Extensions.AI.ChatRole.User,
                "destination history"));
            await File.WriteAllTextAsync(targetPath, target.ToJson());

            var start = new ProcessStartInfo("dotnet")
            {
                WorkingDirectory = root,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            start.ArgumentList.Add(typeof(CliArguments).Assembly.Location);
            foreach (var argument in new[] { "--mode", "rpc", "--provider", "fixture", "--model", "rpc-switch-fixture", "--offline", "--session", sourcePath })
                start.ArgumentList.Add(argument);
            foreach (var name in new[] { "OPENAI_API_KEY", "PISHARP_API_KEY", "PISHARP_BASE_URL", "PISHARP_MODEL",
                "PISHARP_AUTH_PATH", "PISHARP_MODELS_PATH", "PISHARP_SETTINGS_PATH", "PISHARP_SESSION_DIR", "PISHARP_FIXTURE_KEY" })
                start.Environment.Remove(name);
            start.Environment["PISHARP_AGENT_DIR"] = agentDirectory;
            start.Environment["PISHARP_FIXTURE_KEY"] = "fixture-only-key";
            process = Process.Start(start)!;
            var stderr = process.StandardError.ReadToEndAsync();
            var lines = new List<string>();

            async Task<JsonElement> ReadRecordAsync()
            {
                var line = await process.StandardOutput.ReadLineAsync(timeout.Token);
                Assert.NotNull(line);
                lines.Add(line!);
                using var record = JsonDocument.Parse(line!);
                return record.RootElement.Clone();
            }

            await process.StandardInput.WriteLineAsync("{\"id\":\"source-prompt\",\"type\":\"prompt\",\"message\":\"source active prompt\"}");
            await process.StandardInput.FlushAsync(timeout.Token);
            var sawPartial = false;
            while (!sawPartial)
            {
                var record = await ReadRecordAsync();
                sawPartial = record.GetProperty("type").GetString() == "message_update" &&
                    record.GetProperty("assistantMessageEvent").GetProperty("type").GetString() == "text_delta" &&
                    record.GetProperty("assistantMessageEvent").GetProperty("delta").GetString() == "source partial";
            }

            var switchStartIndex = lines.Count;
            await process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(new
            {
                id = "valid-active-switch",
                type = "switch_session",
                sessionPath = targetPath
            }));
            await process.StandardInput.FlushAsync(timeout.Token);
            JsonElement switchResponse;
            do { switchResponse = await ReadRecordAsync(); }
            while (!switchResponse.TryGetProperty("id", out var switchId) || switchId.GetString() != "valid-active-switch");
            Assert.True(switchResponse.GetProperty("success").GetBoolean());

            releaseActiveResponse.TrySetResult();
            await process.StandardInput.WriteLineAsync("{\"id\":\"destination-prompt\",\"type\":\"prompt\",\"message\":\"continue on destination\"}");
            await process.StandardInput.FlushAsync(timeout.Token);
            var settled = false;
            while (!settled)
                settled = (await ReadRecordAsync()).GetProperty("type").GetString() == "agent_settled";
            process.StandardInput.Close();
            await process.WaitForExitAsync(timeout.Token);
            await provider;

            Assert.Equal(0, process.ExitCode);
            Assert.Equal(string.Empty, await stderr.WaitAsync(timeout.Token));
            var switchEvents = lines.Skip(switchStartIndex).Select(line => JsonDocument.Parse(line)).ToArray();
            try
            {
                var types = switchEvents.Select(record => record.RootElement.GetProperty("type").GetString()).ToArray();
                var turnEndIndex = Array.FindIndex(switchEvents, record =>
                    record.RootElement.GetProperty("type").GetString() == "turn_end" &&
                    record.RootElement.GetProperty("message").GetProperty("stopReason").GetString() == "aborted");
                var agentEndIndex = Array.IndexOf(types, "agent_end");
                var agentSettledIndex = Array.IndexOf(types, "agent_settled");
                var switchResponseIndex = Array.FindIndex(switchEvents, record =>
                    record.RootElement.TryGetProperty("id", out var id) && id.GetString() == "valid-active-switch");
                Assert.True(turnEndIndex >= 0 && agentEndIndex > turnEndIndex &&
                    agentSettledIndex > agentEndIndex && switchResponseIndex > agentSettledIndex);
            }
            finally { foreach (var record in switchEvents) record.Dispose(); }
        }
        finally
        {
            releaseActiveResponse.TrySetResult();
            timeout.Cancel();
            listener.Close();
            try { await provider; }
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

    [Fact]
    public async Task ForkDuringBlockedRunSettlesBeforeTheResponseAndOmitsTheAbortedTurn()
    {
        if (!OperatingSystem.IsLinux()) return;
        var root = Path.Combine(Path.GetTempPath(), "pisharp-rpc-active-fork-" + Guid.NewGuid().ToString("N"));
        var agentDirectory = Path.Combine(root, "agent");
        Directory.CreateDirectory(agentDirectory);
        var listener = StartLoopbackListener(out var port);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        var releaseResponse = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Process? process = null;
        var server = Task.Run(async () =>
        {
            var firstContext = await listener.GetContextAsync().WaitAsync(timeout.Token);
            await WriteSseResponseAsync(firstContext, "chatcmpl-rpc-fork-first", "first reply");

            var activeContext = await listener.GetContextAsync().WaitAsync(timeout.Token);
            activeContext.Response.StatusCode = (int)HttpStatusCode.OK;
            activeContext.Response.ContentType = "text/event-stream";
            activeContext.Response.SendChunked = true;
            await using var writer = new StreamWriter(activeContext.Response.OutputStream);
            var partial = new
            {
                id = "chatcmpl-rpc-fork-active",
                @object = "chat.completion.chunk",
                created = 1,
                model = "rpc-fork-fixture",
                choices = new[] { new { index = 0, delta = new { role = "assistant", content = "interrupted text" }, finish_reason = (string?)null } }
            };
            await writer.WriteAsync($"data: {JsonSerializer.Serialize(partial)}\n\n");
            await writer.FlushAsync(timeout.Token);
            await releaseResponse.Task.WaitAsync(timeout.Token);
            activeContext.Response.Close();
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
                        models = new[] { new { id = "rpc-fork-fixture", api = "openai-completions" } }
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
            foreach (var argument in new[] { "--mode", "rpc", "--provider", "fixture", "--model", "rpc-fork-fixture", "--offline", "--no-session" })
                start.ArgumentList.Add(argument);
            foreach (var name in new[] { "OPENAI_API_KEY", "PISHARP_API_KEY", "PISHARP_BASE_URL", "PISHARP_MODEL",
                "PISHARP_AUTH_PATH", "PISHARP_MODELS_PATH", "PISHARP_SETTINGS_PATH", "PISHARP_SESSION_DIR", "PISHARP_FIXTURE_KEY" })
                start.Environment.Remove(name);
            start.Environment["PISHARP_AGENT_DIR"] = agentDirectory;
            start.Environment["PISHARP_FIXTURE_KEY"] = "fixture-only-key";
            process = Process.Start(start)!;
            var stderr = process.StandardError.ReadToEndAsync();
            var lines = new List<string>();

            async Task<JsonElement> ReadRecordAsync()
            {
                var line = await process.StandardOutput.ReadLineAsync(timeout.Token);
                Assert.NotNull(line);
                lines.Add(line!);
                using var record = JsonDocument.Parse(line!);
                return record.RootElement.Clone();
            }

            await process.StandardInput.WriteLineAsync("{\"id\":\"first-prompt\",\"type\":\"prompt\",\"message\":\"seed prompt\"}");
            await process.StandardInput.FlushAsync(timeout.Token);
            JsonElement firstEvent;
            do { firstEvent = await ReadRecordAsync(); }
            while (firstEvent.GetProperty("type").GetString() != "agent_settled");

            var idle = false;
            for (var attempt = 0; attempt < 100 && !idle; attempt++)
            {
                var stateId = $"idle-{attempt}";
                await process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(new { id = stateId, type = "get_state" }));
                await process.StandardInput.FlushAsync(timeout.Token);
                JsonElement state;
                do { state = await ReadRecordAsync(); }
                while (!state.TryGetProperty("id", out var id) || id.GetString() != stateId);
                idle = !state.GetProperty("data").GetProperty("isStreaming").GetBoolean();
                if (!idle) await Task.Delay(5, timeout.Token);
            }
            Assert.True(idle);

            await process.StandardInput.WriteLineAsync("{\"id\":\"forkable\",\"type\":\"get_fork_messages\"}");
            await process.StandardInput.FlushAsync(timeout.Token);
            JsonElement forkable;
            do { forkable = await ReadRecordAsync(); }
            while (!forkable.TryGetProperty("id", out var forkableId) || forkableId.GetString() != "forkable");
            Assert.True(forkable.GetProperty("success").GetBoolean());
            var selectedEntryId = Assert.Single(forkable.GetProperty("data").GetProperty("messages").EnumerateArray())
                .GetProperty("entryId").GetString();
            Assert.False(string.IsNullOrWhiteSpace(selectedEntryId));

            await process.StandardInput.WriteLineAsync("{\"id\":\"active-prompt\",\"type\":\"prompt\",\"message\":\"block for fork\"}");
            await process.StandardInput.FlushAsync(timeout.Token);
            var partialSeen = false;
            while (!partialSeen)
            {
                var record = await ReadRecordAsync();
                partialSeen = record.GetProperty("type").GetString() == "message_update" &&
                    record.GetProperty("assistantMessageEvent").GetProperty("type").GetString() == "text_delta" &&
                    record.GetProperty("assistantMessageEvent").GetProperty("delta").GetString() == "interrupted text";
            }

            var forkStartIndex = lines.Count;
            await process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(new
            {
                id = "fork-active",
                type = "fork",
                entryId = selectedEntryId
            }));
            await process.StandardInput.FlushAsync(timeout.Token);
            JsonElement forkResponse;
            do { forkResponse = await ReadRecordAsync(); }
            while (!forkResponse.TryGetProperty("id", out var forkId) || forkId.GetString() != "fork-active");
            Assert.True(forkResponse.GetProperty("success").GetBoolean());
            Assert.Equal("seed prompt", forkResponse.GetProperty("data").GetProperty("text").GetString());
            Assert.False(forkResponse.GetProperty("data").GetProperty("cancelled").GetBoolean());

            releaseResponse.TrySetResult();
            await process.StandardInput.WriteLineAsync("{\"id\":\"fork-messages\",\"type\":\"get_messages\"}");
            await process.StandardInput.FlushAsync(timeout.Token);
            JsonElement messagesResponse;
            do { messagesResponse = await ReadRecordAsync(); }
            while (!messagesResponse.TryGetProperty("id", out var messagesId) || messagesId.GetString() != "fork-messages");
            process.StandardInput.Close();
            await process.WaitForExitAsync(timeout.Token);
            await server;

            Assert.Equal(0, process.ExitCode);
            Assert.Equal("", await stderr.WaitAsync(timeout.Token));
            var records = lines.Select(line => JsonDocument.Parse(line)).ToArray();
            try
            {
                var forkEvents = records.Skip(forkStartIndex).ToArray();
                var turnEndIndex = Array.FindIndex(forkEvents, record =>
                    record.RootElement.GetProperty("type").GetString() == "turn_end" &&
                    record.RootElement.GetProperty("message").GetProperty("stopReason").GetString() == "aborted");
                var agentEndIndex = Array.FindIndex(forkEvents, record =>
                    record.RootElement.GetProperty("type").GetString() == "agent_end");
                var settledIndex = Array.FindIndex(forkEvents, record =>
                    record.RootElement.GetProperty("type").GetString() == "agent_settled");
                var responseIndex = Array.FindIndex(forkEvents, record =>
                    record.RootElement.TryGetProperty("id", out var id) && id.GetString() == "fork-active");
                Assert.True(turnEndIndex >= 0 && agentEndIndex > turnEndIndex && settledIndex > agentEndIndex && responseIndex > settledIndex);
                var aborted = forkEvents[turnEndIndex].RootElement.GetProperty("message");
                Assert.Contains("interrupted text", aborted.GetProperty("content")[0].GetProperty("text").GetString());
                Assert.False(forkEvents[agentEndIndex].RootElement.GetProperty("willRetry").GetBoolean());
                Assert.Equal(0, messagesResponse.GetProperty("data").GetProperty("messages").GetArrayLength());
            }
            finally { foreach (var record in records) record.Dispose(); }
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

    [Fact]
    public async Task InvalidSwitchTargetDoesNotCancelAnActiveRun()
    {
        if (!OperatingSystem.IsLinux()) return;
        var root = Path.Combine(Path.GetTempPath(), "pisharp-rpc-invalid-switch-" + Guid.NewGuid().ToString("N"));
        var agentDirectory = Path.Combine(root, "agent");
        var invalidSessionPath = Path.Combine(root, "invalid.jsonl");
        var unknownProviderSessionPath = Path.Combine(root, "unknown-provider.session.json");
        Directory.CreateDirectory(agentDirectory);
        await File.WriteAllTextAsync(invalidSessionPath, "{invalid session entry}\n");
        await File.WriteAllTextAsync(unknownProviderSessionPath,
            new ConversationSession(root, "rpc-switch-fixture", null, "missing-provider").ToJson());
        var listener = StartLoopbackListener(out var port);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var releaseResponse = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Process? process = null;
        var server = Task.Run(async () =>
        {
            var context = await listener.GetContextAsync().WaitAsync(timeout.Token);
            context.Response.StatusCode = (int)HttpStatusCode.OK;
            context.Response.ContentType = "text/event-stream";
            context.Response.SendChunked = true;
            await using var writer = new StreamWriter(context.Response.OutputStream);
            var partial = new
            {
                id = "chatcmpl-rpc-invalid-switch",
                @object = "chat.completion.chunk",
                created = 1,
                model = "rpc-switch-fixture",
                choices = new[] { new { index = 0, delta = new { role = "assistant", content = "still running" }, finish_reason = (string?)null } }
            };
            await writer.WriteAsync($"data: {JsonSerializer.Serialize(partial)}\n\n");
            await writer.FlushAsync(timeout.Token);
            await releaseResponse.Task.WaitAsync(timeout.Token);
            var complete = new
            {
                id = "chatcmpl-rpc-invalid-switch",
                @object = "chat.completion.chunk",
                created = 1,
                model = "rpc-switch-fixture",
                choices = new[] { new { index = 0, delta = new { }, finish_reason = "stop" } }
            };
            await writer.WriteAsync($"data: {JsonSerializer.Serialize(complete)}\n\n");
            await writer.WriteAsync("data: [DONE]\n\n");
            await writer.FlushAsync(timeout.Token);
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
                        models = new[] { new { id = "rpc-switch-fixture", api = "openai-completions" } }
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
            foreach (var argument in new[] { "--mode", "rpc", "--provider", "fixture", "--model", "rpc-switch-fixture", "--offline", "--no-session" })
                start.ArgumentList.Add(argument);
            foreach (var name in new[] { "OPENAI_API_KEY", "PISHARP_API_KEY", "PISHARP_BASE_URL", "PISHARP_MODEL",
                "PISHARP_AUTH_PATH", "PISHARP_MODELS_PATH", "PISHARP_SETTINGS_PATH", "PISHARP_FIXTURE_KEY" })
                start.Environment.Remove(name);
            start.Environment["PISHARP_AGENT_DIR"] = agentDirectory;
            start.Environment["PISHARP_FIXTURE_KEY"] = "fixture-only-key";
            process = Process.Start(start)!;
            var stderr = process.StandardError.ReadToEndAsync();
            await process.StandardInput.WriteLineAsync("{\"id\":\"prompt\",\"type\":\"prompt\",\"message\":\"continue while switch fails\"}");
            await process.StandardInput.FlushAsync(timeout.Token);

            var lines = new List<string>();
            var sawPartial = false;
            while (!sawPartial)
            {
                var line = await process.StandardOutput.ReadLineAsync(timeout.Token);
                Assert.NotNull(line);
                lines.Add(line!);
                using var record = JsonDocument.Parse(line!);
                var rootElement = record.RootElement;
                sawPartial = rootElement.GetProperty("type").GetString() == "message_update" &&
                    rootElement.GetProperty("assistantMessageEvent").GetProperty("type").GetString() == "text_delta" &&
                    rootElement.GetProperty("assistantMessageEvent").GetProperty("delta").GetString() == "still running";
            }

            await process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(new
            {
                id = "invalid-switch",
                type = "switch_session",
                sessionPath = invalidSessionPath
            }));
            await process.StandardInput.FlushAsync(timeout.Token);
            var switchResponded = false;
            while (!switchResponded)
            {
                var line = await process.StandardOutput.ReadLineAsync(timeout.Token);
                Assert.NotNull(line);
                lines.Add(line!);
                using var record = JsonDocument.Parse(line!);
                switchResponded = record.RootElement.TryGetProperty("id", out var id) && id.GetString() == "invalid-switch";
                if (switchResponded) Assert.False(record.RootElement.GetProperty("success").GetBoolean());
            }

            await process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(new
            {
                id = "unknown-provider-switch",
                type = "switch_session",
                sessionPath = unknownProviderSessionPath
            }));
            await process.StandardInput.FlushAsync(timeout.Token);
            var providerSwitchResponded = false;
            while (!providerSwitchResponded)
            {
                var line = await process.StandardOutput.ReadLineAsync(timeout.Token);
                Assert.NotNull(line);
                lines.Add(line!);
                using var record = JsonDocument.Parse(line!);
                providerSwitchResponded = record.RootElement.TryGetProperty("id", out var id) &&
                    id.GetString() == "unknown-provider-switch";
                if (providerSwitchResponded) Assert.False(record.RootElement.GetProperty("success").GetBoolean());
            }

            Assert.DoesNotContain(lines, line =>
            {
                using var record = JsonDocument.Parse(line);
                var type = record.RootElement.GetProperty("type").GetString();
                return type is "agent_end" or "agent_settled" or "turn_end";
            });

            releaseResponse.TrySetResult();
            var settled = false;
            while (!settled)
            {
                var line = await process.StandardOutput.ReadLineAsync(timeout.Token);
                Assert.NotNull(line);
                lines.Add(line!);
                using var record = JsonDocument.Parse(line!);
                settled = record.RootElement.GetProperty("type").GetString() == "agent_settled";
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
                var responseIndex = Array.FindIndex(records, record =>
                    record.RootElement.TryGetProperty("id", out var id) && id.GetString() == "invalid-switch");
                var turnEndIndex = Array.IndexOf(types, "turn_end");
                var agentEndIndex = Array.IndexOf(types, "agent_end");
                var settledIndex = Array.IndexOf(types, "agent_settled");
                Assert.True(responseIndex >= 0 && turnEndIndex > responseIndex && agentEndIndex > turnEndIndex && settledIndex > agentEndIndex);
                Assert.False(string.IsNullOrEmpty(records[responseIndex].RootElement.GetProperty("error").GetString()));
                var assistantEnd = Assert.Single(records, record =>
                    record.RootElement.GetProperty("type").GetString() == "message_end" &&
                    record.RootElement.GetProperty("message").GetProperty("role").GetString() == "assistant");
                var assistant = assistantEnd.RootElement.GetProperty("message");
                Assert.NotEqual("aborted", assistant.GetProperty("stopReason").GetString());
                Assert.Contains("still running", assistant.GetProperty("content")[0].GetProperty("text").GetString());
                Assert.True(JsonElement.DeepEquals(assistant, records[turnEndIndex].RootElement.GetProperty("message")));
            }
            finally { foreach (var record in records) record.Dispose(); }
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

    private static async Task WriteSseResponseAsync(HttpListenerContext context, string id, string text,
        string model = "rpc-fork-fixture")
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
                choices = new[] { new { index = 0, delta = new { role = "assistant", content = text }, finish_reason = (string?)null } }
            };
            var complete = new
            {
                id,
                @object = "chat.completion.chunk",
                created = 1,
                model,
                choices = new[] { new { index = 0, delta = new { }, finish_reason = "stop" } }
            };
            await writer.WriteAsync($"data: {JsonSerializer.Serialize(chunk)}\n\n");
            await writer.WriteAsync($"data: {JsonSerializer.Serialize(complete)}\n\n");
            await writer.WriteAsync("data: [DONE]\n\n");
            await writer.FlushAsync();
        }
        context.Response.Close();
    }

    private static async Task WriteSseToolCallResponseAsync(HttpListenerContext context, string id, string model,
        string toolName = "read", string arguments = """{"path":"marker.txt"}""",
        string callId = "call-read-target")
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
                choices = new[]
                {
                    new
                    {
                        index = 0,
                        delta = new
                        {
                            role = "assistant",
                            tool_calls = new[]
                            {
                                new
                                {
                                    index = 0,
                                    id = callId,
                                    type = "function",
                                    function = new { name = toolName, arguments }
                                }
                            }
                        },
                        finish_reason = "tool_calls"
                    }
                }
            };
            await writer.WriteAsync($"data: {JsonSerializer.Serialize(chunk)}\n\n");
            await writer.WriteAsync("data: [DONE]\n\n");
            await writer.FlushAsync();
        }
        context.Response.Close();
    }
}
