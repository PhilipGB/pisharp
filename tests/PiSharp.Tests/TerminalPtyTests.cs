using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using PiSharp.Cli;
using SkiaSharp;

namespace PiSharp.Tests;

public sealed class TerminalPtyTests
{
    [Fact]
    public async Task TrustSelectorSavesParentDecisionWithoutChangingTheCurrentSession()
    {
        if (!OperatingSystem.IsLinux() || !File.Exists("/usr/bin/script")) return;
        var root = Path.Combine(Path.GetTempPath(), "pisharp-trust-selector-pty-" + Guid.NewGuid().ToString("N"));
        var agent = Path.Combine(root, "agent");
        var parent = Path.Combine(root, "parent");
        var cwd = Path.Combine(parent, "project");
        Directory.CreateDirectory(agent);
        Directory.CreateDirectory(Path.Combine(cwd, ".pi"));
        await File.WriteAllTextAsync(Path.Combine(cwd, ".pi", "settings.json"), "{}");
        var trustStore = new PiSharp.Runtime.Resources.ProjectTrust(agent);
        await trustStore.SetAsync(parent, true);
        await trustStore.SetAsync(cwd, false);

        Process? process = null;
        try
        {
            var start = new ProcessStartInfo("/usr/bin/script")
            {
                WorkingDirectory = cwd,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            start.ArgumentList.Add("-q");
            start.ArgumentList.Add("-e");
            start.ArgumentList.Add("-c");
            start.ArgumentList.Add($"stty rows 24 cols 100; exec dotnet {ShellQuote(typeof(CliArguments).Assembly.Location)} --local --offline --no-session --no-tools");
            start.ArgumentList.Add("/dev/null");
            start.Environment["PISHARP_AGENT_DIR"] = agent;
            process = Process.Start(start);
            Assert.NotNull(process);

            var output = new StringBuilder();
            var outputLock = new object();
            var inputReady = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var initialSelector = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var decisionSaved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var decisionSavedAgain = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var selectorAfterSave = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var stdout = Task.Run(async () =>
            {
                var buffer = new char[1024];
                while (true)
                {
                    var count = await process.StandardOutput.ReadAsync(buffer);
                    if (count == 0) break;
                    lock (outputLock)
                    {
                        output.Append(buffer, 0, count);
                        var captured = output.ToString();
                        if (captured.Contains("\u001b[?2004h", StringComparison.Ordinal)) inputReady.TrySetResult();
                        if (captured.Contains("Saved decision: untrusted (", StringComparison.Ordinal) &&
                            captured.Contains("Trust parent folder (", StringComparison.Ordinal))
                            initialSelector.TrySetResult();
                        const string savedStatus = "Saved trust decision: trusted. Restart PiSharp for this to take effect.";
                        if (captured.Contains(savedStatus, StringComparison.Ordinal))
                            decisionSaved.TrySetResult();
                        if (CountOccurrences(captured, savedStatus) >= 2) decisionSavedAgain.TrySetResult();
                        if (CountOccurrences(captured, "Current session: untrusted") >= 2 &&
                            captured.Contains("Saved decision: trusted (inherited from ", StringComparison.Ordinal))
                            selectorAfterSave.TrySetResult();
                    }
                }
                lock (outputLock) return output.ToString();
            });
            var stderr = process.StandardError.ReadToEndAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            await inputReady.Task.WaitAsync(timeout.Token);
            await process.StandardInput.WriteAsync("/trust\n");
            await process.StandardInput.FlushAsync();
            try { await initialSelector.Task.WaitAsync(timeout.Token); }
            catch (OperationCanceledException)
            {
                string tail;
                lock (outputLock) tail = new string(output.ToString().TakeLast(5000).ToArray());
                throw new InvalidOperationException($"Trust selector did not open. exited={process.HasExited}; stdout tail: {tail}");
            }
            await process.StandardInput.WriteAsync("\u001b[A\n");
            await process.StandardInput.FlushAsync();
            await decisionSaved.Task.WaitAsync(timeout.Token);
            await process.StandardInput.WriteAsync("/trust\n");
            await process.StandardInput.FlushAsync();
            await selectorAfterSave.Task.WaitAsync(timeout.Token);
            await process.StandardInput.WriteAsync("\n");
            await process.StandardInput.FlushAsync();
            await decisionSavedAgain.Task.WaitAsync(timeout.Token);
            await process.StandardInput.WriteAsync("/quit\n");
            await process.StandardInput.FlushAsync();
            process.StandardInput.Close();
            await process.WaitForExitAsync(timeout.Token);
            var capturedOutput = await stdout;

            Assert.Equal(0, process.ExitCode);
            Assert.Contains("Saved decision: untrusted (", capturedOutput);
            Assert.Contains("Current session: untrusted", capturedOutput);
            Assert.Contains("Saved decision: trusted (inherited from ", capturedOutput);
            Assert.Contains("Saved trust decision: trusted. Restart PiSharp for this to take effect.", capturedOutput);
            Assert.Contains("This project is not trusted.", capturedOutput);
            Assert.Contains("restart PiSharp.", capturedOutput);
            Assert.Contains("PiSharp can explain its own features and look up its docs. Ask how to use or extend PiSharp.", capturedOutput);
            Assert.Contains("Use /hotkeys for shortcut help and / for commands.", capturedOutput);
            Assert.True(capturedOutput.IndexOf("PiSharp can explain its own features and look up its docs. Ask how to use or extend PiSharp.", StringComparison.Ordinal) <
                capturedOutput.LastIndexOf("This project is not trusted.", StringComparison.Ordinal));
            Assert.DoesNotContain("Agent error:", capturedOutput);
            Assert.DoesNotContain("Exception:", await stderr);
            using var saved = System.Text.Json.JsonDocument.Parse(await File.ReadAllTextAsync(trustStore.PathOnDisk));
            Assert.Single(saved.RootElement.EnumerateObject());
            Assert.True(saved.RootElement.GetProperty(Path.GetFullPath(parent)).GetBoolean());
        }
        finally
        {
            if (process is { HasExited: false }) process.Kill(entireProcessTree: true);
            process?.Dispose();
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task BangCommandsUseTheConfiguredPrefixAndDoubleBangExcludesContext()
    {
        if (!OperatingSystem.IsLinux() || !File.Exists("/usr/bin/script")) return;
        var root = Path.Combine(Path.GetTempPath(), "pisharp-bang-bash-" + Guid.NewGuid().ToString("N"));
        var agent = Path.Combine(root, "agent");
        var sessionPath = Path.Combine(root, "bang.session.json");
        Directory.CreateDirectory(agent);
        await File.WriteAllTextAsync(Path.Combine(agent, "settings.json"),
            "{\"quietStartup\":true,\"shellCommandPrefix\":\"export PISHARP_BANG_PREFIX=prefix-ready\"}");
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
            start.ArgumentList.Add($"stty rows 24 cols 80; exec dotnet {ShellQuote(typeof(CliArguments).Assembly.Location)} --local --offline --session {ShellQuote(sessionPath)} --no-tools");
            start.ArgumentList.Add("/dev/null");
            foreach (var name in new[] { "OPENAI_API_KEY", "PISHARP_API_KEY", "PISHARP_BASE_URL", "PISHARP_MODEL", "PISHARP_SETTINGS_PATH" })
                start.Environment.Remove(name);
            start.Environment["PISHARP_AGENT_DIR"] = agent;
            process = Process.Start(start);
            Assert.NotNull(process);

            var output = new StringBuilder();
            var outputLock = new object();
            var idle = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var normalResult = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var excludedResult = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var stdout = Task.Run(async () =>
            {
                var buffer = new char[1024];
                while (true)
                {
                    var count = await process.StandardOutput.ReadAsync(buffer);
                    if (count == 0) break;
                    lock (outputLock)
                    {
                        output.Append(buffer, 0, count);
                        var current = output.ToString();
                        if (current.Contains("\u001b[?2026l", StringComparison.Ordinal)) idle.TrySetResult();
                        if (current.Contains("bang-result:prefix-ready", StringComparison.Ordinal)) normalResult.TrySetResult();
                        if (current.Contains("excluded-result:prefix-ready", StringComparison.Ordinal)) excludedResult.TrySetResult();
                    }
                }
                lock (outputLock) return output.ToString();
            });
            var stderr = process.StandardError.ReadToEndAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            await idle.Task.WaitAsync(timeout.Token);
            await process.StandardInput.WriteAsync("!printf '%s\\n' \"bang-result:$PISHARP_BANG_PREFIX\"\n");
            await process.StandardInput.FlushAsync();
            await normalResult.Task.WaitAsync(timeout.Token);
            await process.StandardInput.WriteAsync("!!printf '%s\\n' \"excluded-result:$PISHARP_BANG_PREFIX\"\n");
            await process.StandardInput.FlushAsync();
            await excludedResult.Task.WaitAsync(timeout.Token);
            await process.StandardInput.WriteAsync("/quit\n");
            await process.StandardInput.FlushAsync();
            process.StandardInput.Close();
            await process.WaitForExitAsync(timeout.Token);
            var captured = await stdout;
            _ = await stderr;

            Assert.Equal(0, process.ExitCode);
            Assert.Contains("bang-result:prefix-ready", captured);
            Assert.Contains("excluded-result:prefix-ready", captured);
            var session = PiSharp.Runtime.Sessions.ConversationSession.Parse(await File.ReadAllTextAsync(sessionPath));
            var executions = session.Tree.ActivePath().Where(entry => entry.Type == "bash_execution").ToArray();
            Assert.Equal(2, executions.Length);
            Assert.Equal("printf '%s\\n' \"bang-result:$PISHARP_BANG_PREFIX\"", executions[0].Payload.GetProperty("command").GetString());
            Assert.False(executions[0].Payload.GetProperty("excludeFromContext").GetBoolean());
            Assert.Equal("printf '%s\\n' \"excluded-result:$PISHARP_BANG_PREFIX\"", executions[1].Payload.GetProperty("command").GetString());
            Assert.True(executions[1].Payload.GetProperty("excludeFromContext").GetBoolean());
            Assert.Contains(session.ContextMessages(), message => message.Text.Contains("bang-result:prefix-ready", StringComparison.Ordinal));
            Assert.DoesNotContain(session.ContextMessages(), message => message.Text.Contains("excluded-result:prefix-ready", StringComparison.Ordinal));
        }
        finally
        {
            if (process is { HasExited: false }) process.Kill(entireProcessTree: true);
            process?.Dispose();
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ActiveRunStreamsExtensionToolThroughLinuxPtyAndRestoresTheTerminal()
    {
        if (!OperatingSystem.IsLinux() || !File.Exists("/usr/bin/script")) return;
        var root = Path.Combine(Path.GetTempPath(), "pisharp-active-pty-" + Guid.NewGuid().ToString("N"));
        var agent = Path.Combine(root, "agent");
        Directory.CreateDirectory(agent);
        using var listener = StartLoopbackListener(out var port);
        var requests = new List<string>();
        using var serverTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var firstRequestReceived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirstResponse = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var server = Task.Run(async () =>
        {
            for (var index = 0; index < 2; index++)
            {
                var request = await listener.GetContextAsync().WaitAsync(serverTimeout.Token);
                Assert.Equal("/v1/responses", request.Request.Url?.AbsolutePath);
                Assert.Equal("Bearer pty-fixture-key", request.Request.Headers["Authorization"]);
                using var reader = new StreamReader(request.Request.InputStream);
                requests.Add(await reader.ReadToEndAsync(serverTimeout.Token));
                if (index == 0)
                {
                    firstRequestReceived.TrySetResult();
                    await releaseFirstResponse.Task.WaitAsync(serverTimeout.Token);
                }
                request.Response.ContentType = "text/event-stream";
                request.Response.KeepAlive = false;
                await using var writer = new StreamWriter(request.Response.OutputStream);
                await writer.WriteAsync($"data: {{\"type\":\"response.created\",\"response\":{{\"id\":\"resp_{index}\",\"object\":\"response\",\"created_at\":1,\"model\":\"fixture-model\",\"status\":\"in_progress\",\"output\":[]}}}}\n\n");
                if (index == 0)
                {
                    await writer.WriteAsync("data: {\"type\":\"response.output_item.added\",\"output_index\":0,\"item\":{\"id\":\"fc_1\",\"type\":\"function_call\",\"call_id\":\"call_1\",\"name\":\"echo_ext\",\"arguments\":\"\"}}\n\n");
                    await writer.WriteAsync("data: {\"type\":\"response.function_call_arguments.done\",\"output_index\":0,\"item_id\":\"fc_1\",\"arguments\":\"{\\\"value\\\":\\\"pty\\\"}\"}\n\n");
                    await writer.WriteAsync("data: {\"type\":\"response.output_item.done\",\"output_index\":0,\"item\":{\"id\":\"fc_1\",\"type\":\"function_call\",\"call_id\":\"call_1\",\"name\":\"echo_ext\",\"arguments\":\"{\\\"value\\\":\\\"pty\\\"}\"}}\n\n");
                }
                else
                    await writer.WriteAsync("data: {\"type\":\"response.output_text.delta\",\"item_id\":\"msg_1\",\"output_index\":0,\"content_index\":0,\"delta\":\"PTY_REPLY_OK\"}\n\n");
                await writer.WriteAsync($"data: {{\"type\":\"response.completed\",\"response\":{{\"id\":\"resp_{index}\",\"object\":\"response\",\"created_at\":1,\"model\":\"fixture-model\",\"status\":\"completed\",\"output\":[]}}}}\n\n");
                await writer.WriteAsync("data: [DONE]\n\n");
                await writer.FlushAsync();
                request.Response.Close();
            }
        });

        Process? process = null;
        try
        {
            var models = """
                {"providers":{"fixture":{"baseUrl":"http://127.0.0.1:PORT/v1","apiKeyEnv":"PISHARP_FIXTURE_KEY","models":[{"id":"fixture-model","api":"openai-responses","reasoning":true}]}}}
                """.Replace("PORT", port.ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal);
            await File.WriteAllTextAsync(Path.Combine(agent, "models.json"), models);
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
            start.ArgumentList.Add($"stty rows 24 cols 80; exec dotnet {ShellQuote(typeof(CliArguments).Assembly.Location)} --provider fixture --model fixture-model --no-session --offline --extension {ShellQuote(typeof(FixtureExtension).Assembly.Location)}");
            start.ArgumentList.Add("/dev/null");
            start.Environment["PISHARP_AGENT_DIR"] = agent;
            start.Environment["PISHARP_FIXTURE_KEY"] = "pty-fixture-key";
            process = Process.Start(start);
            Assert.NotNull(process);

            var output = new StringBuilder();
            var idleReady = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var idleAfterReply = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var settingsScopeShown = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var steeringOptionsShown = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var steeringSaved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var settingsClosed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var firstSteeringQueued = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var bothSteeringQueued = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var drain = Task.Run(async () =>
            {
                var buffer = new char[1024];
                int count;
                while ((count = await process.StandardOutput.ReadAsync(buffer)) > 0)
                {
                    lock (output)
                    {
                        output.Append(buffer, 0, count);
                        var captured = output.ToString();
                        const string idleFrameEnd = "\u001b[?2026l";
                        if (captured.Contains(idleFrameEnd, StringComparison.Ordinal)) idleReady.TrySetResult();
                        if (captured.Contains("Settings scope", StringComparison.Ordinal)) settingsScopeShown.TrySetResult();
                        if (captured.Contains("Deliver all queued messages together", StringComparison.Ordinal))
                            steeringOptionsShown.TrySetResult();
                        if (captured.Contains("Saved user setting steeringMode = all.", StringComparison.Ordinal))
                            steeringSaved.TrySetResult();
                        if (captured.Contains("Settings closed.", StringComparison.Ordinal)) settingsClosed.TrySetResult();
                        const string queuedMessage = "Queued steering message.";
                        var frames = TerminalOutputFrameReader.Read(captured, rows: 24, columns: 80);
                        if (frames.Count > 0)
                        {
                            var frame = frames[^1].Screen;
                            var queuedCount = 0;
                            for (var position = 0;
                                (position = frame.IndexOf(queuedMessage, position, StringComparison.Ordinal)) >= 0;
                                position += queuedMessage.Length)
                                queuedCount++;
                            if (queuedCount >= 1) firstSteeringQueued.TrySetResult();
                            if (queuedCount >= 2) bothSteeringQueued.TrySetResult();

                            const string activeFooter = "Enter steers · follow-up queues · Escape aborts";
                            if (frame.Contains("PTY_REPLY_OK", StringComparison.Ordinal) &&
                                !frame.Contains(activeFooter, StringComparison.Ordinal))
                                idleAfterReply.TrySetResult();
                        }
                    }
                }
            });
            var stderr = process.StandardError.ReadToEndAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            await idleReady.Task.WaitAsync(timeout.Token);
            await process.StandardInput.WriteAsync("/settings\n");
            await process.StandardInput.FlushAsync();
            try
            {
                await settingsScopeShown.Task.WaitAsync(timeout.Token);
                await process.StandardInput.WriteAsync("\n");
                await process.StandardInput.FlushAsync();
                await process.StandardInput.WriteAsync("Steering mode\n");
                await process.StandardInput.FlushAsync();
                await steeringOptionsShown.Task.WaitAsync(timeout.Token);
                await process.StandardInput.WriteAsync("All together\n");
                await process.StandardInput.FlushAsync();
                await steeringSaved.Task.WaitAsync(timeout.Token);
                await process.StandardInput.WriteAsync("\u001b");
                await process.StandardInput.FlushAsync();
                await settingsClosed.Task.WaitAsync(timeout.Token);
                await process.StandardInput.WriteAsync("Use echo_ext with value pty.\n");
                await process.StandardInput.FlushAsync();
                await firstRequestReceived.Task.WaitAsync(timeout.Token);
                await process.StandardInput.WriteAsync("steering one\n");
                await process.StandardInput.FlushAsync();
                await firstSteeringQueued.Task.WaitAsync(timeout.Token);
                await process.StandardInput.WriteAsync("steering two\n");
                await process.StandardInput.FlushAsync();
                await bothSteeringQueued.Task.WaitAsync(timeout.Token);
                releaseFirstResponse.TrySetResult();
                await idleAfterReply.Task.WaitAsync(timeout.Token);
                await process.StandardInput.WriteAsync("/quit\n");
                await process.StandardInput.FlushAsync();
                process.StandardInput.Close();
                await process.WaitForExitAsync(timeout.Token);
            }
            catch (OperationCanceledException error)
            {
                process.Kill(entireProcessTree: true);
                await drain;
                string diagnostic;
                lock (output) diagnostic = output.ToString();
                throw new TimeoutException($"Active PTY failed to exit. stdout:\n{diagnostic}\nstderr:\n{await stderr}", error);
            }
            await drain;
            await server.WaitAsync(timeout.Token);

            string terminalOutput;
            lock (output) terminalOutput = output.ToString();
            Assert.Equal(0, process.ExitCode);
            Assert.Contains("extension call: pty", terminalOutput);
            Assert.Contains("extension result: pty / extension: pty", terminalOutput);
            Assert.Contains("PTY_REPLY_OK", terminalOutput);
            Assert.Contains("\u001b[?1049l", terminalOutput);
            Assert.Contains("function_call_output", requests[1]);
            Assert.Contains("extension: pty", requests[1]);
            Assert.Contains("steering one", requests[1]);
            Assert.Contains("steering two", requests[1]);
            Assert.True(requests[1].IndexOf("steering one", StringComparison.Ordinal) <
                requests[1].IndexOf("steering two", StringComparison.Ordinal));
            using var firstRequest = System.Text.Json.JsonDocument.Parse(requests[0]);
            Assert.Equal("medium", firstRequest.RootElement.GetProperty("reasoning").GetProperty("effort").GetString());
            Assert.DoesNotContain("Agent error:", terminalOutput);
            Assert.DoesNotContain("Unhandled exception", await stderr);
        }
        finally
        {
            if (process is { HasExited: false }) process.Kill(entireProcessTree: true);
            releaseFirstResponse.TrySetResult();
            await serverTimeout.CancelAsync();
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task SessionDiscoveryResumeAndCloneWorkThroughLinuxPty()
    {
        if (!OperatingSystem.IsLinux() || !File.Exists("/usr/bin/script")) return;
        var cwd = Path.Combine(Path.GetTempPath(), "pisharp-session-pty-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(cwd);
        try
        {
            var store = new PiSharp.Runtime.Sessions.ConversationStore(cwd, Path.Combine(cwd, "sessions"));
            var assembly = typeof(CliArguments).Assembly.Location;
            var start = new ProcessStartInfo("/usr/bin/script")
            {
                WorkingDirectory = cwd,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                ArgumentList = { "-q", "-e", "-c", $"stty rows 24 cols 80; dotnet '{assembly}' --local --session-dir '{cwd}/sessions' --no-tools", "/dev/null" }
            };
            using var process = Process.Start(start);
            Assert.NotNull(process);
            var output = new StringBuilder();
            var outputLock = new object();
            var inputReady = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var pickerOpened = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var outputChanged = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var stdout = Task.Run(async () =>
            {
                var buffer = new char[1024];
                while (true)
                {
                    var count = await process.StandardOutput.ReadAsync(buffer);
                    if (count == 0) break;
                    lock (outputLock)
                    {
                        output.Append(buffer, 0, count);
                        var captured = output.ToString();
                        if (captured.Contains("\u001b[?2004h", StringComparison.Ordinal)) inputReady.TrySetResult();
                        if (captured.Contains("Resume session", StringComparison.Ordinal)) pickerOpened.TrySetResult();
                        var changed = outputChanged;
                        outputChanged = new(TaskCreationOptions.RunContinuationsAsynchronously);
                        changed.TrySetResult();
                    }
                }
                lock (outputLock)
                {
                    outputChanged.TrySetResult();
                    return output.ToString();
                }
            });
            async Task WaitForOutputAsync(Func<string, bool> predicate, CancellationToken token)
            {
                while (true)
                {
                    Task changed;
                    lock (outputLock)
                    {
                        var captured = output.ToString();
                        if (predicate(captured)) return;
                        changed = outputChanged.Task;
                    }
                    await changed.WaitAsync(token);
                }
            }
            var stderr = process.StandardError.ReadToEndAsync();
            using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            await inputReady.Task.WaitAsync(limit.Token);
            await process.StandardInput.WriteAsync("/name first\n/new\n/name second\n/sessions\n/resume first\n/resume\n");
            await process.StandardInput.FlushAsync();
            await pickerOpened.Task.WaitAsync(limit.Token);
            var secondId = (await PiSharp.Runtime.Sessions.SessionCatalog.ListAsync(store))
                .Single(session => session.Name == "second").Id[..12];
            await process.StandardInput.WriteAsync("second\r");
            await process.StandardInput.FlushAsync();
            await WaitForOutputAsync(captured => captured.Contains($"Resumed {secondId}", StringComparison.Ordinal), limit.Token);
            await process.StandardInput.WriteAsync($"/session\n/export {cwd}/export.html\n/clone\n/session\n/quit\n");
            await process.StandardInput.FlushAsync();
            process.StandardInput.Close();
            try { await process.WaitForExitAsync(limit.Token); }
            catch (OperationCanceledException) { process.Kill(entireProcessTree: true); throw; }
            var capturedOutput = await stdout;
            var secondIds = (await PiSharp.Runtime.Sessions.SessionCatalog.ListAsync(store))
                .Where(session => session.Name == "second").Select(session => session.Id[..12]).ToHashSet();
            var resumedIds = System.Text.RegularExpressions.Regex.Matches(capturedOutput, @"Resumed ([a-f0-9]{12})")
                .Select(match => match.Groups[1].Value).ToArray();
            Assert.Equal(0, process.ExitCode);
            Assert.Contains("Resumed", capturedOutput);
            Assert.Contains("Resume session", capturedOutput);
            Assert.Contains(resumedIds, id => secondIds.Contains(id));
            Assert.Contains("clone:", capturedOutput);
            Assert.Contains("Exported private HTML", capturedOutput);
            Assert.Contains("PiSharp session", await File.ReadAllTextAsync(Path.Combine(cwd, "export.html")));
            Assert.Equal(3, Directory.EnumerateFiles(Path.Combine(cwd, "sessions"), "*.session.json").Count());
            Assert.DoesNotContain("Session error", capturedOutput);
            Assert.DoesNotContain("Agent error", await stderr);
        }
        finally { Directory.Delete(cwd, recursive: true); }
    }

    [Fact]
    public async Task PiJsonlImportConfirmsAndSwitchesTheActiveSessionThroughLinuxPty()
    {
        if (!OperatingSystem.IsLinux() || !File.Exists("/usr/bin/script")) return;
        var cwd = Path.Combine(Path.GetTempPath(), "pisharp-pi-import-pty-" + Guid.NewGuid().ToString("N"));
        var sourceCwd = Path.Combine(cwd, "source-project");
        var agent = Path.Combine(cwd, "agent");
        Directory.CreateDirectory(agent);
        Directory.CreateDirectory(sourceCwd);
        Process? process = null;
        try
        {
            const string importedId = "pi-import-pty";
            var sourcePath = Path.Combine(sourceCwd, "pi session with spaces.jsonl");
            var exportPath = Path.Combine(cwd, "round trip export.jsonl");
            var jsonl = $$$$"""
                {"type":"session","version":3,"id":"{{{{importedId}}}}","timestamp":"2025-01-01T00:00:00Z","cwd":"{{{{sourceCwd}}}}"}
                {"type":"message","id":"user-entry","parentId":null,"timestamp":"2025-01-01T00:00:01Z","message":{"role":"user","content":"imported context from Pi","timestamp":1735689601000}}
                """;
            await File.WriteAllTextAsync(sourcePath, jsonl);
            var store = new PiSharp.Runtime.Sessions.ConversationStore(cwd, Path.Combine(cwd, "sessions"));
            var start = new ProcessStartInfo("/usr/bin/script")
            {
                WorkingDirectory = cwd,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                ArgumentList = { "-q", "-e", "-c", $"stty rows 24 cols 180; exec dotnet {ShellQuote(typeof(CliArguments).Assembly.Location)} --local --offline --session-dir {ShellQuote(store.DirectoryPath)} --no-tools", "/dev/null" }
            };
            start.Environment["PISHARP_AGENT_DIR"] = agent;
            process = Process.Start(start);
            Assert.NotNull(process);
            var output = new StringBuilder();
            var promptReady = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var retryPromptReady = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var cancelledReady = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var importReady = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var exportReady = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            const string confirmationPrompt = "Type import to confirm:";
            var drain = Task.Run(async () =>
            {
                var buffer = new char[1024];
                int count;
                while ((count = await process.StandardOutput.ReadAsync(buffer)) > 0)
                {
                    lock (output)
                    {
                        output.Append(buffer, 0, count);
                        var captured = output.ToString();
                        var promptIndex = captured.LastIndexOf(confirmationPrompt, StringComparison.Ordinal);
                        var cancelledIndex = captured.LastIndexOf("Import cancelled.", StringComparison.Ordinal);
                        if (promptIndex >= 0) promptReady.TrySetResult();
                        if (cancelledIndex >= 0 && promptIndex > cancelledIndex) retryPromptReady.TrySetResult();
                        if (cancelledIndex >= 0) cancelledReady.TrySetResult();
                        if (captured.Contains($"Imported Pi session {importedId}", StringComparison.Ordinal)) importReady.TrySetResult();
                        if (captured.Contains("Exported private Pi JSONL to", StringComparison.Ordinal)) exportReady.TrySetResult();
                    }
                }
            });
            var stderr = process.StandardError.ReadToEndAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            await process.StandardInput.WriteAsync($"/import \"{sourcePath}\"\n");
            await process.StandardInput.FlushAsync();
            try
            {
                await promptReady.Task.WaitAsync(timeout.Token);
                await process.StandardInput.WriteAsync("cancel\n");
                await process.StandardInput.FlushAsync();
                await cancelledReady.Task.WaitAsync(timeout.Token);
                Assert.DoesNotContain(await PiSharp.Runtime.Sessions.SessionCatalog.ListAsync(store),
                    session => session.Id == importedId);
                await process.StandardInput.WriteAsync($"/import \"{sourcePath}\"\n");
                await retryPromptReady.Task.WaitAsync(timeout.Token);
                await process.StandardInput.WriteAsync("import\n");
                await process.StandardInput.FlushAsync();
                await importReady.Task.WaitAsync(timeout.Token);
                await process.StandardInput.WriteAsync($"/export-jsonl \"{exportPath}\"\n");
                await process.StandardInput.FlushAsync();
                await exportReady.Task.WaitAsync(timeout.Token);
                await process.StandardInput.WriteAsync("/session\n/quit\n");
                await process.StandardInput.FlushAsync();
                process.StandardInput.Close();
                await process.WaitForExitAsync(timeout.Token);
                await drain;
            }
            catch (OperationCanceledException error)
            {
                if (!process.HasExited) process.Kill(entireProcessTree: true);
                await drain;
                string diagnostic;
                lock (output) diagnostic = output.ToString();
                throw new TimeoutException($"Pi JSONL import PTY did not reach the expected state. stdout:\n{diagnostic}\nstderr:\n{await stderr}", error);
            }

            string terminalOutput;
            lock (output) terminalOutput = output.ToString();
            var importedStore = new PiSharp.Runtime.Sessions.ConversationStore(sourceCwd, store.DirectoryPath);
            var saved = Assert.Single(await PiSharp.Runtime.Sessions.SessionCatalog.ListAsync(importedStore),
                session => session.Id == importedId);
            var imported = await importedStore.LoadAsync(saved.Path);
            Assert.Equal(0, process.ExitCode);
            Assert.Equal(sourceCwd, imported.WorkingDirectory);
            Assert.Contains("Type import to confirm:", terminalOutput);
            Assert.Contains("Import cancelled.", terminalOutput);
            Assert.Contains("Exported private Pi JSONL to", terminalOutput);
            Assert.Contains($"Imported Pi session {importedId}", terminalOutput);
            Assert.Contains("› imported context from Pi", terminalOutput);
            Assert.Contains(importedId, terminalOutput);
            Assert.Contains("imported context from Pi", imported.ActiveMessages().Select(message => message.Text));
            var exported = PiSharp.Runtime.Sessions.PiJsonlSessionInterchange.Import(await File.ReadAllTextAsync(exportPath));
            Assert.Equal(importedId, exported.Id);
            Assert.Contains("imported context from Pi", exported.ActiveMessages().Select(message => message.Text));
            Assert.DoesNotContain("Session error:", terminalOutput);
            Assert.DoesNotContain("Agent error:", await stderr);

            var startupStore = new PiSharp.Runtime.Sessions.ConversationStore(sourceCwd, Path.Combine(cwd, "startup-sessions"));
            var startup = new ProcessStartInfo("/usr/bin/script")
            {
                WorkingDirectory = cwd,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                ArgumentList = { "-q", "-e", "-c", $"stty rows 24 cols 80; exec dotnet {ShellQuote(typeof(CliArguments).Assembly.Location)} --local --offline --session {ShellQuote(sourcePath)} --session-dir {ShellQuote(startupStore.DirectoryPath)} --no-tools", "/dev/null" }
            };
            startup.Environment["PISHARP_AGENT_DIR"] = agent;
            process = Process.Start(startup);
            Assert.NotNull(process);
            var startupOutput = process.StandardOutput.ReadToEndAsync();
            var startupError = process.StandardError.ReadToEndAsync();
            await process.StandardInput.WriteAsync("/session\n/quit\n");
            process.StandardInput.Close();
            await process.WaitForExitAsync(timeout.Token);
            var startupText = await startupOutput;
            var startupSession = Assert.Single(await PiSharp.Runtime.Sessions.SessionCatalog.ListAsync(startupStore),
                session => session.Id == importedId);
            Assert.Equal(sourceCwd, (await startupStore.LoadAsync(startupSession.Path)).WorkingDirectory);
            Assert.Equal(0, process.ExitCode);
            Assert.Contains(importedId, startupText);
            Assert.Contains("› imported context from Pi", startupText);
            Assert.DoesNotContain("Could not open session:", startupText);
            Assert.DoesNotContain("Agent error:", await startupError);
            Assert.Equal(importedId, (await startupStore.LoadAsync(startupSession.Path)).Id);
        }
        finally
        {
            if (process is { HasExited: false }) process.Kill(entireProcessTree: true);
            Directory.Delete(cwd, recursive: true);
        }
    }

    [Fact]
    public async Task ForkFromEarlierUserTurnPreservesSourceAndSeedsEditableDraft()
    {
        if (!OperatingSystem.IsLinux() || !File.Exists("/usr/bin/script")) return;
        var cwd = Path.Combine(Path.GetTempPath(), "pisharp-fork-pty-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(cwd);
        try
        {
            var store = new PiSharp.Runtime.Sessions.ConversationStore(cwd, Path.Combine(cwd, "sessions"));
            var original = new PiSharp.Runtime.Sessions.ConversationSession(cwd, ConnectionSettings.LocalModel,
                new Uri(ConnectionSettings.LocalEndpoint).ToString());
            original.Append(new Microsoft.Extensions.AI.ChatMessage(Microsoft.Extensions.AI.ChatRole.User, "draft to change"));
            var userId = original.Tree.HeadId!;
            original.Append(new Microsoft.Extensions.AI.ChatMessage(Microsoft.Extensions.AI.ChatRole.Assistant, "original answer"));
            var sourcePath = store.NewPath(original);
            await store.SaveAsync(original, sourcePath);
            var assembly = typeof(CliArguments).Assembly.Location;
            var start = new ProcessStartInfo("/usr/bin/script")
            {
                WorkingDirectory = cwd,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                ArgumentList = { "-q", "-e", "-c", $"dotnet '{assembly}' --local --continue --session-dir '{store.DirectoryPath}' --no-tools", "/dev/null" }
            };
            using var process = Process.Start(start);
            Assert.NotNull(process);
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            await process.StandardInput.WriteAsync($"/fork {userId[..12]}\n");
            await process.StandardInput.FlushAsync();
            using (var forkReady = new CancellationTokenSource(TimeSpan.FromSeconds(12)))
            {
                while (Directory.EnumerateFiles(store.DirectoryPath, "*.session.json").Count() < 2)
                    await Task.Delay(30, forkReady.Token);
            }
            await Task.Delay(150);
            await process.StandardInput.WriteAsync("\u0015/name forked\n/quit\n");
            process.StandardInput.Close();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(12));
            try { await process.WaitForExitAsync(timeout.Token); }
            catch (OperationCanceledException) { process.Kill(entireProcessTree: true); throw; }
            var output = await stdout;
            Assert.Equal(0, process.ExitCode);
            Assert.Contains("draft to change", output);
            Assert.Contains("Forked " + userId[..12], output);
            Assert.True(output.Contains("Name: forked", StringComparison.Ordinal), output);
            Assert.DoesNotContain("Session error:", output);
            Assert.DoesNotContain("Agent error:", await stderr);
            var source = await store.LoadAsync(sourcePath);
            Assert.Equal(["draft to change", "original answer"], source.ActiveMessages().Select(message => message.Text));
            var paths = Directory.EnumerateFiles(store.DirectoryPath, "*.session.json").Where(path => path != sourcePath).ToArray();
            var fork = await store.LoadAsync(Assert.Single(paths));
            Assert.Empty(fork.ActiveMessages());
            Assert.Equal("forked", fork.Name);
        }
        finally { Directory.Delete(cwd, recursive: true); }
    }

    [Fact]
    public async Task ForkPickerSearchesUserMessagesAndSeedsSelectedPromptThroughLinuxPty()
    {
        if (!OperatingSystem.IsLinux() || !File.Exists("/usr/bin/script")) return;
        var cwd = Path.Combine(Path.GetTempPath(), "pisharp-fork-picker-pty-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(cwd);
        try
        {
            var store = new PiSharp.Runtime.Sessions.ConversationStore(cwd, Path.Combine(cwd, "sessions"));
            var original = new PiSharp.Runtime.Sessions.ConversationSession(cwd, ConnectionSettings.LocalModel,
                new Uri(ConnectionSettings.LocalEndpoint).ToString());
            original.Append(new Microsoft.Extensions.AI.ChatMessage(Microsoft.Extensions.AI.ChatRole.User, "base context"));
            original.Append(new Microsoft.Extensions.AI.ChatMessage(Microsoft.Extensions.AI.ChatRole.Assistant, "base answer"));
            original.Append(new Microsoft.Extensions.AI.ChatMessage(Microsoft.Extensions.AI.ChatRole.User, "draft to change"));
            var selectedId = original.Tree.HeadId!;
            original.Append(new Microsoft.Extensions.AI.ChatMessage(Microsoft.Extensions.AI.ChatRole.Assistant, "later answer"));
            var sourcePath = store.NewPath(original);
            await store.SaveAsync(original, sourcePath);
            var assembly = typeof(CliArguments).Assembly.Location;
            var start = new ProcessStartInfo("/usr/bin/script")
            {
                WorkingDirectory = cwd,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                ArgumentList = { "-q", "-e", "-c", $"stty rows 24 cols 80; dotnet '{assembly}' --local --session '{sourcePath}' --session-dir '{store.DirectoryPath}' --no-tools", "/dev/null" }
            };
            using var process = Process.Start(start);
            Assert.NotNull(process);
            var outputBuilder = new System.Text.StringBuilder();
            var outputLock = new object();
            var promptReady = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var selectedIdPrefix = selectedId[..12];
            var stdout = Task.Run(async () =>
            {
                var buffer = new char[1024];
                while (true)
                {
                    var count = await process.StandardOutput.ReadAsync(buffer);
                    if (count == 0) break;
                    lock (outputLock)
                    {
                        outputBuilder.Append(buffer, 0, count);
                        var current = outputBuilder.ToString();
                        var forkStatus = current.LastIndexOf($"Forked {selectedIdPrefix}", StringComparison.Ordinal);
                        if (forkStatus >= 0 && current.IndexOf("draft to change", forkStatus, StringComparison.Ordinal) >= 0)
                            promptReady.TrySetResult();
                    }
                }
                lock (outputLock) return outputBuilder.ToString();
            });
            var stderr = process.StandardError.ReadToEndAsync();
            await process.StandardInput.WriteAsync("/fork\ndraft to change\n");
            await process.StandardInput.FlushAsync();
            await promptReady.Task.WaitAsync(TimeSpan.FromSeconds(12));
            await process.StandardInput.WriteAsync("\u0015/name forked\n/quit\n");
            await process.StandardInput.FlushAsync();
            process.StandardInput.Close();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(12));
            try { await process.WaitForExitAsync(timeout.Token); }
            catch (OperationCanceledException)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync();
                var diagnostic = await stdout;
                throw new TimeoutException($"Fork picker session did not close after /quit. Output tail:\n{new string(diagnostic.TakeLast(3000).ToArray())}");
            }
            var output = await stdout;
            Assert.Equal(0, process.ExitCode);
            Assert.Contains("Fork from message", output);
            Assert.Contains("draft to change", output);
            Assert.Contains("Forked " + selectedIdPrefix, output);
            Assert.Contains("Name: forked", output);
            Assert.DoesNotContain("Session error:", output);
            Assert.DoesNotContain("Agent error:", await stderr);
            var source = await store.LoadAsync(sourcePath);
            Assert.Equal(["base context", "base answer", "draft to change", "later answer"],
                source.ActiveMessages().Select(message => message.Text));
            var paths = Directory.EnumerateFiles(store.DirectoryPath, "*.session.json").Where(path => path != sourcePath).ToArray();
            var fork = await store.LoadAsync(Assert.Single(paths));
            Assert.Equal("forked", fork.Name);
            Assert.Equal(["base context", "base answer"], fork.ActiveMessages().Select(message => message.Text));
        }
        finally { Directory.Delete(cwd, recursive: true); }
    }

    [Fact]
    public async Task SettingsPickerEditsUserScopeThroughLinuxPty()
    {
        if (!OperatingSystem.IsLinux() || !File.Exists("/usr/bin/script")) return;
        var cwd = Path.Combine(Path.GetTempPath(), "pisharp-settings-picker-pty-" + Guid.NewGuid().ToString("N"));
        var agent = Path.Combine(cwd, "agent");
        Directory.CreateDirectory(agent);
        try
        {
            var settingsPath = Path.Combine(agent, "settings.json");
            await File.WriteAllTextAsync(settingsPath, "{\"compaction\":{\"reserveTokens\":2048}}\n");
            var store = new PiSharp.Runtime.Sessions.ConversationStore(cwd, Path.Combine(cwd, "sessions"));
            var assembly = typeof(CliArguments).Assembly.Location;
            var start = new ProcessStartInfo("/usr/bin/script")
            {
                WorkingDirectory = cwd,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                ArgumentList = { "-q", "-e", "-c", $"stty rows 24 cols 80; dotnet '{assembly}' --local --session-dir '{store.DirectoryPath}' --no-tools", "/dev/null" }
            };
            start.Environment["PISHARP_AGENT_DIR"] = agent;
            using var process = Process.Start(start);
            Assert.NotNull(process);
            var outputBuilder = new System.Text.StringBuilder();
            var outputLock = new object();
            var saved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var editorPrompt = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var externalEditorSaved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var themeSaved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var steeringSaved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var followUpSaved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var proxyPrompt = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var proxySaved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var providerTimeoutPrompt = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var idleTimeoutSaved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var providerTimeoutSaved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var providerRetryDelaySaved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var codeBlockIndentPrompt = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var codeBlockIndentSaved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var trueColorSaved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var skillCommandsSaved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var stdout = Task.Run(async () =>
            {
                var buffer = new char[1024];
                while (true)
                {
                    var count = await process.StandardOutput.ReadAsync(buffer);
                    if (count == 0) break;
                    lock (outputLock)
                    {
                        outputBuilder.Append(buffer, 0, count);
                        var current = outputBuilder.ToString();
                        if (current.Contains("Saved user setting hideThinkingBlock = true.", StringComparison.Ordinal)) saved.TrySetResult();
                        if (current.Contains("External editor command [", StringComparison.Ordinal)) editorPrompt.TrySetResult();
                        if (current.Contains("Saved user setting externalEditor = code --wait.", StringComparison.Ordinal)) externalEditorSaved.TrySetResult();
                        if (current.Contains("Saved user setting theme = light.", StringComparison.Ordinal)) themeSaved.TrySetResult();
                        if (current.Contains("Saved user setting steeringMode = all.", StringComparison.Ordinal)) steeringSaved.TrySetResult();
                        if (current.Contains("Saved user setting followUpMode = one-at-a-time.", StringComparison.Ordinal)) followUpSaved.TrySetResult();
                        if (current.Contains("HTTP proxy [", StringComparison.Ordinal)) proxyPrompt.TrySetResult();
                        if (current.Contains("Saved user setting httpProxy = (configured).", StringComparison.Ordinal)) proxySaved.TrySetResult();
                        if (current.Contains("Saved user setting httpIdleTimeoutMs = 0.", StringComparison.Ordinal)) idleTimeoutSaved.TrySetResult();
                        if (current.Contains("Provider request timeout in milliseconds [", StringComparison.Ordinal)) providerTimeoutPrompt.TrySetResult();
                        if (current.Contains("Saved user setting retry.provider.timeoutMs = 45000.", StringComparison.Ordinal)) providerTimeoutSaved.TrySetResult();
                        if (current.Contains("Saved user setting retry.provider.maxRetryDelayMs = 0.", StringComparison.Ordinal)) providerRetryDelaySaved.TrySetResult();
                        if (current.Contains("Literal code line prefix [", StringComparison.Ordinal)) codeBlockIndentPrompt.TrySetResult();
                        if (current.Contains("Saved user setting markdown.codeBlockIndent = " + " > " + ".", StringComparison.Ordinal)) codeBlockIndentSaved.TrySetResult();
                        if (current.Contains("Saved user setting terminal.trueColor = true.", StringComparison.Ordinal)) trueColorSaved.TrySetResult();
                        if (current.Contains("Saved user setting enableSkillCommands = false.", StringComparison.Ordinal)) skillCommandsSaved.TrySetResult();
                        if (current.Contains("Settings closed.", StringComparison.Ordinal)) closed.TrySetResult();
                    }
                }
                lock (outputLock) return outputBuilder.ToString();
            });
            var stderr = process.StandardError.ReadToEndAsync();
            await process.StandardInput.WriteAsync("/settings\n\nHide thinking\nEnabled\n");
            await process.StandardInput.FlushAsync();
            await saved.Task.WaitAsync(TimeSpan.FromSeconds(12));
            await process.StandardInput.WriteAsync("\u001b[A\n");
            await process.StandardInput.FlushAsync();
            await editorPrompt.Task.WaitAsync(TimeSpan.FromSeconds(12));
            await process.StandardInput.WriteAsync("code --wait\n");
            await process.StandardInput.FlushAsync();
            await externalEditorSaved.Task.WaitAsync(TimeSpan.FromSeconds(12));
            await process.StandardInput.WriteAsync("Theme\nlight\n");
            await process.StandardInput.FlushAsync();
            await themeSaved.Task.WaitAsync(TimeSpan.FromSeconds(12));
            await process.StandardInput.WriteAsync("Steering mode\nall\n");
            await process.StandardInput.FlushAsync();
            await steeringSaved.Task.WaitAsync(TimeSpan.FromSeconds(12));
            await process.StandardInput.WriteAsync("Follow-up mode\nOne at a time\n");
            await process.StandardInput.FlushAsync();
            await followUpSaved.Task.WaitAsync(TimeSpan.FromSeconds(12));
            await process.StandardInput.WriteAsync("HTTP proxy\n");
            await process.StandardInput.FlushAsync();
            await proxyPrompt.Task.WaitAsync(TimeSpan.FromSeconds(12));
            await process.StandardInput.WriteAsync("https://proxy.example:8443\n");
            await process.StandardInput.FlushAsync();
            await proxySaved.Task.WaitAsync(TimeSpan.FromSeconds(12));
            await process.StandardInput.WriteAsync("HTTP idle timeout\n");
            await process.StandardInput.FlushAsync();
            await process.StandardInput.WriteAsync("Disabled\n");
            await process.StandardInput.FlushAsync();
            await idleTimeoutSaved.Task.WaitAsync(TimeSpan.FromSeconds(12));
            await process.StandardInput.WriteAsync("Provider request timeout\n");
            await process.StandardInput.FlushAsync();
            await providerTimeoutPrompt.Task.WaitAsync(TimeSpan.FromSeconds(12));
            await process.StandardInput.WriteAsync("45000\n");
            await process.StandardInput.FlushAsync();
            await providerTimeoutSaved.Task.WaitAsync(TimeSpan.FromSeconds(12));
            await process.StandardInput.WriteAsync("Provider retry delay limit\nNo limit\n");
            await process.StandardInput.FlushAsync();
            await providerRetryDelaySaved.Task.WaitAsync(TimeSpan.FromSeconds(12));
            await process.StandardInput.WriteAsync("Code block indent\n");
            await process.StandardInput.FlushAsync();
            await codeBlockIndentPrompt.Task.WaitAsync(TimeSpan.FromSeconds(12));
            await process.StandardInput.WriteAsync(" > \n");
            await process.StandardInput.FlushAsync();
            await codeBlockIndentSaved.Task.WaitAsync(TimeSpan.FromSeconds(12));
            await process.StandardInput.WriteAsync("Terminal true color\nEnabled\n");
            await process.StandardInput.FlushAsync();
            await trueColorSaved.Task.WaitAsync(TimeSpan.FromSeconds(12));
            await process.StandardInput.WriteAsync("Skill commands\nDisabled\n");
            await process.StandardInput.FlushAsync();
            await skillCommandsSaved.Task.WaitAsync(TimeSpan.FromSeconds(12));
            await process.StandardInput.WriteAsync("\u001b");
            await process.StandardInput.FlushAsync();
            await closed.Task.WaitAsync(TimeSpan.FromSeconds(12));
            await process.StandardInput.WriteAsync("/quit\n");
            await process.StandardInput.FlushAsync();
            process.StandardInput.Close();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(12));
            try { await process.WaitForExitAsync(timeout.Token); }
            catch (OperationCanceledException) { process.Kill(entireProcessTree: true); throw; }
            var output = await stdout;
            Assert.Equal(0, process.ExitCode);
            Assert.Contains("Settings scope", output);
            Assert.Contains("Hide thinking", output);
            Assert.Contains("Saved user setting hideThinkingBlock = true", output);
            Assert.Contains("Saved user setting steeringMode = all", output);
            Assert.Contains("Saved user setting followUpMode = one-at-a-time", output);
            Assert.Contains("Saved user setting httpProxy = (configured)", output);
            Assert.DoesNotContain("Saved user setting httpProxy = https://proxy.example:8443", output);
            Assert.DoesNotContain("Agent error:", output);
            Assert.DoesNotContain("Exception:", await stderr);
            var settings = await PiSharp.Cli.UserSettings.LoadAsync(agent, _ => null);
            Assert.True(settings.HideThinkingBlock);
            Assert.Equal("light", settings.Theme);
            Assert.Equal("code --wait", settings.ExternalEditor);
            Assert.Equal(PiSharp.Runtime.Sessions.PromptDeliveryMode.All, settings.SteeringMode);
            Assert.Equal(PiSharp.Runtime.Sessions.PromptDeliveryMode.OneAtATime, settings.FollowUpMode);
            Assert.Equal("https://proxy.example:8443", settings.HttpProxy);
            Assert.Equal(0, settings.HttpIdleTimeoutMs);
            Assert.Equal(45_000, settings.Retry?.Provider?.TimeoutMs);
            Assert.Equal(0, settings.Retry?.Provider?.MaxRetryDelayMs);
            Assert.Equal(" > ", settings.MarkdownCodeBlockIndent);
            Assert.Equal("true", settings.TerminalTrueColor);
            Assert.True(settings.TerminalTrueColorOverride);
            Assert.False(settings.SkillCommandsEnabled);
            Assert.Equal(2048, settings.Compaction?.ReserveTokens);
        }
        finally { Directory.Delete(cwd, recursive: true); }
    }

    [Fact]
    public async Task ClipboardShortcutsPasteTextAndCopyLastAssistantThroughLinuxPty()
    {
        if (!OperatingSystem.IsLinux() || !File.Exists("/usr/bin/script")) return;
        var cwd = Path.Combine(Path.GetTempPath(), "pisharp-clipboard-pty-" + Guid.NewGuid().ToString("N"));
        var agent = Path.Combine(cwd, "agent");
        var bin = Path.Combine(cwd, "bin");
        Directory.CreateDirectory(agent);
        Directory.CreateDirectory(bin);
        try
        {
            var capture = Path.Combine(cwd, "clipboard.txt");
            var readCommand = Path.Combine(bin, "wl-paste");
            var writeCommand = Path.Combine(bin, "wl-copy");
            await File.WriteAllTextAsync(readCommand, "#!/bin/sh\nprintf 'clipboard paste text'\n");
            await File.WriteAllTextAsync(writeCommand, "#!/bin/sh\ncat > \"$PISHARP_CLIP_CAPTURE\"\n");
            var mode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
            File.SetUnixFileMode(readCommand, mode);
            File.SetUnixFileMode(writeCommand, mode);

            var store = new PiSharp.Runtime.Sessions.ConversationStore(cwd, Path.Combine(cwd, "sessions"));
            var conversation = new PiSharp.Runtime.Sessions.ConversationSession(cwd, ConnectionSettings.LocalModel,
                new Uri(ConnectionSettings.LocalEndpoint).ToString());
            conversation.Append(new Microsoft.Extensions.AI.ChatMessage(Microsoft.Extensions.AI.ChatRole.Assistant, "assistant response to copy"));
            var sessionPath = store.NewPath(conversation);
            await store.SaveAsync(conversation, sessionPath);
            var assembly = typeof(CliArguments).Assembly.Location;
            var start = new ProcessStartInfo("/usr/bin/script")
            {
                WorkingDirectory = cwd,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                ArgumentList = { "-q", "-e", "-c", $"stty rows 24 cols 80; dotnet '{assembly}' --local --session '{sessionPath}' --session-dir '{store.DirectoryPath}' --no-tools", "/dev/null" }
            };
            start.Environment["PISHARP_AGENT_DIR"] = agent;
            start.Environment["PISHARP_CLIP_CAPTURE"] = capture;
            start.Environment["WAYLAND_DISPLAY"] = "wayland-0";
            start.Environment["PATH"] = bin + Path.PathSeparator + Environment.GetEnvironmentVariable("PATH");
            using var process = Process.Start(start);
            Assert.NotNull(process);
            try
            {
                var outputBuilder = new System.Text.StringBuilder();
                var outputLock = new object();
                var inputReady = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var pasteRendered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var shortcutCopyCompleted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var slashCopyCompleted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var copyStatusEnd = 0;
                var stdout = Task.Run(async () =>
                {
                    var buffer = new char[1024];
                    while (true)
                    {
                        var count = await process.StandardOutput.ReadAsync(buffer);
                        if (count == 0) break;
                        lock (outputLock)
                        {
                            outputBuilder.Append(buffer, 0, count);
                            var current = outputBuilder.ToString();
                            if (current.Contains("\u001b[?2004h", StringComparison.Ordinal)) inputReady.TrySetResult();
                            if (current.Contains("clipboard paste text", StringComparison.Ordinal)) pasteRendered.TrySetResult();
                            var statusAt = current.IndexOf("Copied last agent message to clipboard", copyStatusEnd, StringComparison.Ordinal);
                            if (statusAt >= 0)
                            {
                                copyStatusEnd = statusAt + "Copied last agent message to clipboard".Length;
                                if (!shortcutCopyCompleted.Task.IsCompleted) shortcutCopyCompleted.TrySetResult();
                                else slashCopyCompleted.TrySetResult();
                            }
                        }
                    }
                    lock (outputLock) return outputBuilder.ToString();
                });
                var stderr = process.StandardError.ReadToEndAsync();
                using var readyTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(12));
                await inputReady.Task.WaitAsync(readyTimeout.Token);
                await process.StandardInput.WriteAsync("\u0016");
                await process.StandardInput.FlushAsync();
                try { await pasteRendered.Task.WaitAsync(TimeSpan.FromSeconds(12)); }
                catch (TimeoutException)
                {
                    try { process.Kill(entireProcessTree: true); }
                    catch (InvalidOperationException) { }
                    await process.WaitForExitAsync();
                    var partialOutput = await stdout;
                    var outputTail = new string(partialOutput.TakeLast(3000).ToArray());
                    throw new InvalidOperationException($"Clipboard paste was not rendered. stdout tail: {outputTail}; stderr: {await stderr}");
                }
                await process.StandardInput.WriteAsync("\u0015\u0018");
                await process.StandardInput.FlushAsync();
                await shortcutCopyCompleted.Task.WaitAsync(TimeSpan.FromSeconds(12));
                await process.StandardInput.WriteAsync("/copy\n");
                await process.StandardInput.FlushAsync();
                await slashCopyCompleted.Task.WaitAsync(TimeSpan.FromSeconds(12));
                await process.StandardInput.WriteAsync("/quit\n");
                await process.StandardInput.FlushAsync();
                process.StandardInput.Close();
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(12));
                try { await process.WaitForExitAsync(timeout.Token); }
                catch (OperationCanceledException) { process.Kill(entireProcessTree: true); throw; }
                var output = await stdout;

                Assert.Equal(0, process.ExitCode);
                Assert.Contains("clipboard paste text", output);
                Assert.Contains("Copied last agent message to clipboard", output);
                Assert.Equal("assistant response to copy", await File.ReadAllTextAsync(capture));
                Assert.DoesNotContain("Shortcut action failed:", output);
                Assert.DoesNotContain("Exception:", await stderr);
            }
            finally
            {
                if (!process.HasExited)
                {
                    try { process.Kill(entireProcessTree: true); }
                    catch (InvalidOperationException) { }
                    await process.WaitForExitAsync();
                }
            }
        }
        finally { Directory.Delete(cwd, recursive: true); }
    }

    [Fact]
    public async Task ClipboardImagePasteInsertsPrivateImagePathThroughLinuxPty()
    {
        if (!OperatingSystem.IsLinux() || !File.Exists("/usr/bin/script")) return;
        var cwd = Path.Combine(Path.GetTempPath(), "pisharp-clipboard-image-pty-" + Guid.NewGuid().ToString("N"));
        var agent = Path.Combine(cwd, "agent");
        var bin = Path.Combine(cwd, "bin");
        Directory.CreateDirectory(agent);
        Directory.CreateDirectory(bin);
        string? pastedImagePath = null;
        try
        {
            var png = CreatePng();
            var pngPath = Path.Combine(cwd, "fixture.png");
            await File.WriteAllBytesAsync(pngPath, png);
            var readCommand = Path.Combine(bin, "wl-paste");
            await File.WriteAllTextAsync(readCommand,
                $"#!/bin/sh\nif [ \"$1\" = \"--list-types\" ]; then printf 'image/png\\n'; else /bin/cat '{pngPath}'; fi\n");
            File.SetUnixFileMode(readCommand, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

            var assembly = typeof(CliArguments).Assembly.Location;
            var start = new ProcessStartInfo("/usr/bin/script")
            {
                WorkingDirectory = cwd,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                ArgumentList = { "-q", "-e", "-c", $"stty rows 24 cols 80; dotnet '{assembly}' --provider openai --model gpt-4o-mini --no-tools --no-session --offline", "/dev/null" }
            };
            foreach (var name in new[] { "OPENAI_API_KEY", "PISHARP_API_KEY", "PISHARP_BASE_URL", "PISHARP_AUTH_PATH", "PISHARP_MODELS_PATH" })
                start.Environment.Remove(name);
            start.Environment["PISHARP_AGENT_DIR"] = agent;
            start.Environment["WAYLAND_DISPLAY"] = "wayland-0";
            start.Environment["PATH"] = bin + Path.PathSeparator + Environment.GetEnvironmentVariable("PATH");
            using var process = Process.Start(start);
            Assert.NotNull(process);

            var outputBuilder = new System.Text.StringBuilder();
            var outputLock = new object();
            var inputReady = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var imagePathRendered = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            var stdout = Task.Run(async () =>
            {
                var buffer = new char[1024];
                while (true)
                {
                    var count = await process.StandardOutput.ReadAsync(buffer);
                    if (count == 0) break;
                    lock (outputLock)
                    {
                        outputBuilder.Append(buffer, 0, count);
                        var output = outputBuilder.ToString();
                        if (output.Contains("\u001b[?2004h", StringComparison.Ordinal)) inputReady.TrySetResult();
                        var match = System.Text.RegularExpressions.Regex.Match(output, @"pisharp-clipboard-[0-9a-f]{32}\.png");
                        if (match.Success)
                        {
                            pastedImagePath = Path.Combine(Path.GetTempPath(), match.Value);
                            imagePathRendered.TrySetResult(pastedImagePath);
                        }
                    }
                }
                lock (outputLock) return outputBuilder.ToString();
            });
            var stderr = process.StandardError.ReadToEndAsync();
            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(25));
                await inputReady.Task.WaitAsync(timeout.Token);
                await process.StandardInput.WriteAsync("\u0016");
                await process.StandardInput.FlushAsync();
                pastedImagePath = await imagePathRendered.Task.WaitAsync(timeout.Token);
                Assert.Equal(png, await File.ReadAllBytesAsync(pastedImagePath));
                if (!OperatingSystem.IsWindows())
                    Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(pastedImagePath));
                await process.StandardInput.WriteAsync("/quit\r");
                process.StandardInput.Close();
                await process.WaitForExitAsync(timeout.Token);
                var output = await stdout;

                Assert.Equal(0, process.ExitCode);
                Assert.Contains(Path.GetFileName(pastedImagePath), output);
                Assert.DoesNotContain("Shortcut action failed:", output);
                Assert.DoesNotContain("Error:", await stderr);
            }
            catch
            {
                if (!process.HasExited) process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync();
                await stdout;
                throw;
            }
        }
        finally
        {
            if (pastedImagePath is not null) File.Delete(pastedImagePath);
            Directory.Delete(cwd, recursive: true);
        }
    }

    [Fact]
    public async Task ExternalEditorSuspendsAndRestoresTheTerminalThroughLinuxPty()
    {
        if (!OperatingSystem.IsLinux() || !File.Exists("/usr/bin/script")) return;
        var cwd = Path.Combine(Path.GetTempPath(), "pisharp-external-editor-pty-" + Guid.NewGuid().ToString("N"));
        var agent = Path.Combine(cwd, "agent");
        Directory.CreateDirectory(agent);
        try
        {
            var editorScript = Path.Combine(cwd, "fake external editor.sh");
            await File.WriteAllTextAsync(editorScript, "#!/bin/sh\nprintf '/quit' > \"$1\"\n");
            File.SetUnixFileMode(editorScript, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            var store = new PiSharp.Runtime.Sessions.ConversationStore(cwd, Path.Combine(cwd, "sessions"));
            var assembly = typeof(CliArguments).Assembly.Location;
            var start = new ProcessStartInfo("/usr/bin/script")
            {
                WorkingDirectory = cwd,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                ArgumentList = { "-q", "-e", "-c", $"stty rows 24 cols 80; dotnet '{assembly}' --local --session-dir '{store.DirectoryPath}' --no-tools", "/dev/null" }
            };
            start.Environment["PISHARP_AGENT_DIR"] = agent;
            start.Environment["VISUAL"] = $"/bin/sh \"{editorScript}\"";
            using var process = Process.Start(start);
            Assert.NotNull(process);
            var output = new StringBuilder();
            var outputLock = new object();
            var inputReady = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var editorLaunched = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var restoredDraft = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var stdout = Task.Run(async () =>
            {
                var buffer = new char[1024];
                while (true)
                {
                    var count = await process.StandardOutput.ReadAsync(buffer);
                    if (count == 0) break;
                    lock (outputLock)
                    {
                        output.Append(buffer, 0, count);
                        var captured = output.ToString();
                        if (captured.Contains("\u001b[?2004h", StringComparison.Ordinal)) inputReady.TrySetResult();
                        if (captured.Contains("Launching external editor:", StringComparison.Ordinal)) editorLaunched.TrySetResult();
                        if (captured.Contains("/quit", StringComparison.Ordinal)) restoredDraft.TrySetResult();
                    }
                }
                lock (outputLock) return output.ToString();
            });
            var stderr = process.StandardError.ReadToEndAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(12));
            await inputReady.Task.WaitAsync(timeout.Token);
            await process.StandardInput.WriteAsync("\u0007");
            await process.StandardInput.FlushAsync();
            await editorLaunched.Task.WaitAsync(timeout.Token);
            await restoredDraft.Task.WaitAsync(timeout.Token);
            await process.StandardInput.WriteAsync("\r");
            await process.StandardInput.FlushAsync();
            process.StandardInput.Close();
            try { await process.WaitForExitAsync(timeout.Token); }
            catch (OperationCanceledException) { process.Kill(entireProcessTree: true); throw; }
            var capturedOutput = await stdout;

            Assert.Equal(0, process.ExitCode);
            Assert.Contains("Launching external editor:", capturedOutput);
            Assert.Contains("/quit", capturedOutput);
            Assert.True(capturedOutput.Split("\u001b[?1049l", StringSplitOptions.None).Length >= 3,
                "the alternate screen should be left for the editor and restored before shutdown");
            Assert.DoesNotContain("Shortcut action failed:", capturedOutput);
            Assert.DoesNotContain("Agent error:", capturedOutput);
            Assert.DoesNotContain("Exception:", await stderr);
        }
        finally { Directory.Delete(cwd, recursive: true); }
    }

    [Fact]
    public async Task SessionSearchDeletionRequiresConfirmationAndPreservesActiveSession()
    {
        if (!OperatingSystem.IsLinux() || !File.Exists("/usr/bin/script")) return;
        var cwd = Path.Combine(Path.GetTempPath(), "pisharp-delete-pty-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(cwd);
        try
        {
            var store = new PiSharp.Runtime.Sessions.ConversationStore(cwd, Path.Combine(cwd, "sessions"));
            var victim = new PiSharp.Runtime.Sessions.ConversationSession(cwd, ConnectionSettings.LocalModel,
                new Uri(ConnectionSettings.LocalEndpoint).ToString());
            victim.Rename("Remove Me");
            var victimPath = store.NewPath(victim);
            await store.SaveAsync(victim, victimPath);
            var active = new PiSharp.Runtime.Sessions.ConversationSession(cwd, ConnectionSettings.LocalModel,
                new Uri(ConnectionSettings.LocalEndpoint).ToString());
            active.Rename("Keep Me");
            var activePath = store.NewPath(active);
            await store.SaveAsync(active, activePath);
            var cli = typeof(CliArguments).Assembly.Location;
            var start = new ProcessStartInfo("/usr/bin/script")
            {
                WorkingDirectory = cwd,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                ArgumentList = { "-q", "-e", "-c", $"dotnet '{cli}' --local --session '{activePath}' --session-dir '{store.DirectoryPath}' --no-tools", "/dev/null" }
            };
            using var process = Process.Start(start);
            Assert.NotNull(process);
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            await process.StandardInput.WriteAsync($"/sessions Remove\n/delete-session {victim.Id[..12]}\nno\n/delete-session {victim.Id[..12]}\ndelete {victim.Id[..12]}\n/quit\n");
            process.StandardInput.Close();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(12));
            try { await process.WaitForExitAsync(timeout.Token); }
            catch (OperationCanceledException) { process.Kill(entireProcessTree: true); throw; }
            var output = await stdout;
            Assert.Equal(0, process.ExitCode);
            Assert.Contains("Remove Me", output);
            Assert.Contains("Deletion cancelled.", output);
            Assert.Contains("Deleted " + victim.Id[..12], output);
            Assert.DoesNotContain("Session error:", output);
            Assert.DoesNotContain("Agent error:", await stderr);
            Assert.False(File.Exists(victimPath));
            Assert.True(File.Exists(activePath));
        }
        finally { Directory.Delete(cwd, true); }
    }

    [Fact]
    public async Task StartupModelAndNameArePersistedAndConflictingResumeIsRejected()
    {
        var cwd = Path.Combine(Path.GetTempPath(), "pisharp-startup-flags-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(cwd);
        try
        {
            var cli = typeof(CliArguments).Assembly.Location;
            var folder = Path.Combine(cwd, "sessions");
            async Task<(int ExitCode, string Error)> Run(params string[] arguments)
            {
                var start = new ProcessStartInfo("dotnet")
                {
                    WorkingDirectory = cwd,
                    RedirectStandardInput = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };
                start.ArgumentList.Add(cli);
                foreach (var arg in arguments) start.ArgumentList.Add(arg);
                using var process = Process.Start(start);
                Assert.NotNull(process);
                var stdout = process.StandardOutput.ReadToEndAsync();
                var stderr = process.StandardError.ReadToEndAsync();
                process.StandardInput.Close();
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(12));
                try { await process.WaitForExitAsync(timeout.Token); }
                catch (OperationCanceledException) { process.Kill(entireProcessTree: true); throw; }
                _ = await stdout;
                return (process.ExitCode, await stderr);
            }
            var first = await Run("--local", "--model", "local/test", "--name", "startup", "--session-dir", folder, "--mode", "rpc");
            Assert.Equal(0, first.ExitCode);
            var store = new PiSharp.Runtime.Sessions.ConversationStore(cwd, folder);
            var saved = await store.LoadAsync(Assert.Single(Directory.GetFiles(folder, "*.session.json")));
            Assert.Equal("local/test", saved.Model);
            Assert.Equal("startup", saved.Name);
            var second = await Run("--local", "--model", "other", "--continue", "--session-dir", folder, "--mode", "rpc");
            Assert.Equal(2, second.ExitCode);
            Assert.Contains("conflicts with the saved session model", second.Error);
        }
        finally { Directory.Delete(cwd, recursive: true); }
    }

    [Fact]
    public async Task StartupCanResolveSessionIdAndForkWithoutModifyingSource()
    {
        var cwd = Path.Combine(Path.GetTempPath(), "pisharp-cli-fork-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(cwd);
        try
        {
            var store = new PiSharp.Runtime.Sessions.ConversationStore(cwd, Path.Combine(cwd, "sessions"));
            var source = new PiSharp.Runtime.Sessions.ConversationSession(cwd, ConnectionSettings.LocalModel,
                new Uri(ConnectionSettings.LocalEndpoint).ToString());
            source.Append(new Microsoft.Extensions.AI.ChatMessage(Microsoft.Extensions.AI.ChatRole.User, "original context"));
            var path = store.NewPath(source);
            await store.SaveAsync(source, path);
            async Task<(int ExitCode, string Error)> Run(params string[] options)
            {
                var start = new ProcessStartInfo("dotnet")
                {
                    WorkingDirectory = cwd,
                    RedirectStandardInput = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };
                start.ArgumentList.Add(typeof(CliArguments).Assembly.Location);
                foreach (var arg in options) start.ArgumentList.Add(arg);
                using var process = Process.Start(start);
                Assert.NotNull(process);
                var output = process.StandardOutput.ReadToEndAsync();
                var error = process.StandardError.ReadToEndAsync();
                process.StandardInput.Close();
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(12));
                try { await process.WaitForExitAsync(timeout.Token); }
                catch (OperationCanceledException) { process.Kill(entireProcessTree: true); throw; }
                _ = await output;
                return (process.ExitCode, await error);
            }
            var opened = await Run("--local", "--session-dir", store.DirectoryPath, "--session", source.Id[..12], "--mode", "rpc");
            Assert.Equal(0, opened.ExitCode);
            Assert.Single(Directory.GetFiles(store.DirectoryPath, "*.session.json"));
            var forked = await Run("--local", "--session-dir", store.DirectoryPath, "--fork", source.Id[..12], "--mode", "rpc");
            Assert.Equal(0, forked.ExitCode);
            var paths = Directory.GetFiles(store.DirectoryPath, "*.session.json");
            Assert.Equal(2, paths.Length);
            var copy = await store.LoadAsync(Assert.Single(paths, item => item != path));
            Assert.NotEqual(source.Id, copy.Id);
            Assert.Equal("original context", Assert.Single(copy.ActiveMessages()).Text);
            Assert.Equal(source.ToJson(), (await store.LoadAsync(path)).ToJson());
            var rejected = await Run("--local", "--session-dir", store.DirectoryPath, "--fork", source.Id[..12],
                "--session", path, "--mode", "rpc");
            Assert.Equal(2, rejected.ExitCode);
            Assert.Contains("cannot be combined", rejected.Error);
        }
        finally { Directory.Delete(cwd, recursive: true); }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ImmediateQuitLeavesNoTerminalRepliesAndRestoresTtyThroughLinuxPty(bool reply)
    {
        if (!OperatingSystem.IsLinux()) return;
        var cwd = Path.Combine(Path.GetTempPath(), "pisharp-terminal-shutdown-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(cwd, "agent"));
        try
        {
            var start = new ProcessStartInfo("python3")
            {
                WorkingDirectory = cwd,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            start.ArgumentList.Add("-c");
            start.ArgumentList.Add("""
                import os, pty, select, subprocess, sys, termios, time, fcntl, struct, tty
                master, slave = pty.openpty()
                fcntl.ioctl(slave, termios.TIOCSWINSZ, struct.pack('HHHH', 24, 80, 0, 0))
                original = termios.tcgetattr(slave)
                env = dict(os.environ, TERM='xterm-256color', PISHARP_AGENT_DIR=os.path.join(os.getcwd(), 'agent'))
                env.pop('NO_COLOR', None)
                child = subprocess.Popen(['dotnet', sys.argv[1], '--local', '--no-session', '--no-tools'],
                    stdin=slave, stdout=slave, stderr=slave, env=env, start_new_session=True)
                output = b''
                deadline = time.monotonic() + 12
                def receive():
                    global output
                    assert time.monotonic() < deadline, repr(output[-2000:])
                    ready, _, _ = select.select([master], [], [], min(.2, max(0, deadline-time.monotonic())))
                    if not ready: return
                    output += os.read(master, 65536)
                try:
                    while b'\x1b[c' not in output or b'\x1b[?2026l' not in output:
                        receive()
                    replies = b'\x1b]10;#f8f8f2\x07\x1b]11;#282a36\x07'
                    replies += b''.join(('\x1b]4;%d;#262626\x07' % i).encode() for i in range(16))
                    replies += b'\x1b[?61;1;21;22c'
                    quit_started = time.monotonic()
                    os.write(master, b'/quit\n' + (replies if sys.argv[2] == 'True' else b''))
                    while child.poll() is None:
                        receive()
                    assert child.wait() == 0, repr(output[-2000:])
                    assert time.monotonic()-quit_started < 2, 'shutdown did not stay bounded'
                    assert termios.tcgetattr(slave) == original, 'tty mode was not restored'
                    tty.setraw(slave, when=termios.TCSANOW)
                    os.set_blocking(slave, False)
                    try: remaining = os.read(slave, 65536)
                    except BlockingIOError: remaining = b''
                    assert remaining == b'', 'terminal replies leaked to parent: ' + repr(remaining)
                    for sequence in [b'\x1b[?2004l', b'\x1b[?1006l', b'\x1b[?1002l', b'\x1b[?1000l',
                        b'\x1b[?25h', b'\x1b[?1049l', b'\x1b[?2031l']:
                        assert sequence in output, 'missing cleanup: ' + repr(sequence)
                    print('TTY_AND_QUERY_SHUTDOWN_OK')
                finally:
                    if child.poll() is None: child.kill()
                    child.wait()
                    os.close(master); os.close(slave)
                """);
            start.ArgumentList.Add(typeof(CliArguments).Assembly.Location);
            start.ArgumentList.Add(reply.ToString());
            using var process = Process.Start(start)!;
            var output = process.StandardOutput.ReadToEndAsync();
            var error = process.StandardError.ReadToEndAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            await process.WaitForExitAsync(timeout.Token);
            Assert.True(process.ExitCode == 0, await error);
            Assert.Contains("TTY_AND_QUERY_SHUTDOWN_OK", await output);
        }
        finally { Directory.Delete(cwd, recursive: true); }
    }

    [Fact]
    public async Task TerminalColorQueriesWorkThroughLinuxPty()
    {
        if (!OperatingSystem.IsLinux() || !File.Exists("/usr/bin/script")) return;
        var cwd = Path.Combine(Path.GetTempPath(), "pisharp-terminal-colors-pty-" + Guid.NewGuid().ToString("N"));
        var agent = Path.Combine(cwd, "agent");
        Directory.CreateDirectory(agent);
        try
        {
            var start = new ProcessStartInfo("/usr/bin/script")
            {
                WorkingDirectory = cwd,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            start.ArgumentList.Add("-q");
            start.ArgumentList.Add("-e");
            start.ArgumentList.Add("-E");
            start.ArgumentList.Add("never");
            start.ArgumentList.Add("-c");
            start.ArgumentList.Add($"stty rows 24 cols 80; exec dotnet {ShellQuote(typeof(CliArguments).Assembly.Location)} --local --no-session --no-tools");
            start.ArgumentList.Add("/dev/null");
            start.Environment["PISHARP_AGENT_DIR"] = agent;
            start.Environment["TERM"] = "xterm-256color";
            start.Environment.Remove("NO_COLOR");
            using var process = Process.Start(start);
            Assert.NotNull(process);

            var output = new StringBuilder();
            var outputLock = new object();
            var firstQuery = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var idleReady = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var firstReplyAlive = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var sessionResponse = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var drain = Task.Run(async () =>
            {
                var buffer = new char[1024];
                int count;
                while ((count = await process.StandardOutput.ReadAsync(buffer)) > 0)
                {
                    lock (outputLock)
                    {
                        output.Append(buffer, 0, count);
                        var captured = output.ToString();
                        if (captured.Contains("FIRST_REPLY_ALIVE", StringComparison.Ordinal)) firstReplyAlive.TrySetResult();
                        if (captured.Contains("(ephemeral) ·", StringComparison.Ordinal)) sessionResponse.TrySetResult();
                        if (CountOccurrences(captured, "\u001b]10;?") >= 1) firstQuery.TrySetResult();
                        if (captured.Contains("\u001b[?2026l", StringComparison.Ordinal)) idleReady.TrySetResult();
                    }
                }
                lock (outputLock) return output.ToString();
            });
            var stderr = process.StandardError.ReadToEndAsync();

            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(12));
            await Task.WhenAll(firstQuery.Task, idleReady.Task).WaitAsync(timeout.Token);
            await process.StandardInput.WriteAsync("\u001b]10;#f8f8f2\aFIRST_REPLY_ALIVE");
            await process.StandardInput.FlushAsync();
            Assert.Same(firstReplyAlive.Task, await Task.WhenAny(firstReplyAlive.Task, process.WaitForExitAsync(timeout.Token)).WaitAsync(timeout.Token));
            Assert.False(process.HasExited);
            var dark = new StringBuilder("\u001b]11;#282a36\a");
            for (var index = 0; index < 16; index++)
                dark.Append("\u001b]4;").Append(index).Append(";#").Append(index.ToString("x2")).Append(index.ToString("x2")).Append(index.ToString("x2")).Append('\a');
            dark.Append("\u001b[?62;22c");
            await process.StandardInput.WriteAsync(dark.ToString());
            await process.StandardInput.FlushAsync();

            await process.StandardInput.WriteAsync("\u0015/session\n");
            await process.StandardInput.FlushAsync();
            Assert.Same(sessionResponse.Task, await Task.WhenAny(sessionResponse.Task, process.WaitForExitAsync(timeout.Token)).WaitAsync(timeout.Token));
            Assert.False(process.HasExited);
            await process.StandardInput.WriteAsync("/quit\n");
            await process.StandardInput.FlushAsync();
            process.StandardInput.Close();
            await process.WaitForExitAsync(timeout.Token);

            var capturedOutput = await drain;
            Assert.Equal(0, process.ExitCode);
            Assert.Contains("\u001b[?2031h", capturedOutput);
            Assert.Contains("\u001b[?2031l", capturedOutput);
            Assert.Contains("\u001b]4;15;?", capturedOutput);
            Assert.Contains("\u001b[c", capturedOutput);
            Assert.Equal(1, CountOccurrences(capturedOutput, "\u001b]10;?"));
            Assert.DoesNotContain("Agent error:", await stderr);
        }
        finally { Directory.Delete(cwd, recursive: true); }
    }

    [Fact]
    public async Task InteractiveCommandsRenderAndExitThroughLinuxPty()
    {
        if (!OperatingSystem.IsLinux() || !File.Exists("/usr/bin/script")) return;
        var cwd = Path.Combine(Path.GetTempPath(), "pisharp-pty-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(cwd);
        try
        {
            var assembly = typeof(CliArguments).Assembly.Location;
            var start = new ProcessStartInfo("/usr/bin/script")
            {
                WorkingDirectory = cwd,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                ArgumentList = { "-q", "-e", "-c", $"dotnet '{assembly}' --local --no-session --no-tools", "/dev/null" }
            };
            using var process = Process.Start(start);
            Assert.NotNull(process);
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            await process.StandardInput.WriteAsync("/session\n/name café界🙂\n/name smoke\n/session\n\u001b[200~/name pasted\nsecond\u001b[201~\n/quit\n");
            process.StandardInput.Close();
            using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(12));
            try { await process.WaitForExitAsync(limit.Token); }
            catch (OperationCanceledException) { process.Kill(entireProcessTree: true); throw; }
            var output = await stdout;
            Assert.Equal(0, process.ExitCode);
            Assert.Contains("(ephemeral)", output);
            Assert.Contains("Name: smoke", output);
            Assert.Contains("Name: café界🙂", output);
            Assert.Contains("Name: pasted second", output);
            Assert.Contains("PiSharp", output);
            Assert.DoesNotContain("Agent error", await stderr);
        }
        finally { Directory.Delete(cwd, recursive: true); }
    }

    [Fact]
    public async Task FileCompletionPickerAppliesTheSelectedAtPathThroughLinuxPty()
    {
        if (!OperatingSystem.IsLinux() || !File.Exists("/usr/bin/script")) return;
        var cwd = Path.Combine(Path.GetTempPath(), "pisharp-completion-pty-" + Guid.NewGuid().ToString("N"));
        var agentDirectory = Path.Combine(cwd, "agent");
        Directory.CreateDirectory(agentDirectory);
        Directory.CreateDirectory(Path.Combine(cwd, "docs"));
        await File.WriteAllTextAsync(Path.Combine(cwd, "notes.txt"), "fixture");
        try
        {
            var assembly = typeof(CliArguments).Assembly.Location;
            var start = new ProcessStartInfo("/usr/bin/script")
            {
                WorkingDirectory = cwd,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                ArgumentList = { "-q", "-e", "-c", $"stty rows 24 cols 80; dotnet '{assembly}' --provider openai --model gpt-4o-mini --no-tools --no-session --offline", "/dev/null" }
            };
            foreach (var name in new[] { "OPENAI_API_KEY", "PISHARP_API_KEY", "PISHARP_BASE_URL", "PISHARP_AUTH_PATH", "PISHARP_MODELS_PATH" })
                start.Environment.Remove(name);
            start.Environment["PISHARP_AGENT_DIR"] = agentDirectory;
            using var process = Process.Start(start);
            Assert.NotNull(process);

            var outputBuilder = new System.Text.StringBuilder();
            var outputLock = new object();
            var inputReady = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var completionPicker = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var selectedPath = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var blankPrompt = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var stdout = Task.Run(async () =>
            {
                var buffer = new char[1024];
                while (true)
                {
                    var count = await process.StandardOutput.ReadAsync(buffer);
                    if (count == 0) break;
                    lock (outputLock)
                    {
                        outputBuilder.Append(buffer, 0, count);
                        var output = outputBuilder.ToString();
                        if (output.Contains("\u001b[?2004h", StringComparison.Ordinal)) inputReady.TrySetResult();
                        if (output.Contains("Complete input", StringComparison.Ordinal)) completionPicker.TrySetResult();
                        var frames = TerminalOutputFrameReader.Read(output, rows: 24, columns: 80);
                        if (frames.Count > 0)
                        {
                            var frame = frames[^1].Screen;
                            if (frame.Contains("@notes.txt", StringComparison.Ordinal) &&
                                !frame.Contains("Complete input", StringComparison.Ordinal))
                                selectedPath.TrySetResult();
                            if (selectedPath.Task.IsCompleted &&
                                !frame.Contains("Complete input", StringComparison.Ordinal) &&
                                !frame.Contains("@notes.txt", StringComparison.Ordinal))
                                blankPrompt.TrySetResult();
                        }
                    }
                }
                lock (outputLock) return outputBuilder.ToString();
            });
            var stderr = process.StandardError.ReadToEndAsync();
            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(25));
                await inputReady.Task.WaitAsync(timeout.Token);
                await process.StandardInput.WriteAsync("@\t");
                await process.StandardInput.FlushAsync();
                await completionPicker.Task.WaitAsync(timeout.Token);
                await process.StandardInput.WriteAsync("\u001b[B\u001b[B\r");
                await process.StandardInput.FlushAsync();
                try { await selectedPath.Task.WaitAsync(TimeSpan.FromSeconds(12)); }
                catch (TimeoutException error)
                {
                    lock (outputLock) throw new TimeoutException("The editor did not render the selected @ path.\n" + outputBuilder, error);
                }
                await process.StandardInput.WriteAsync("\u001b");
                await process.StandardInput.FlushAsync();
                await blankPrompt.Task.WaitAsync(timeout.Token);
                await process.StandardInput.WriteAsync("/quit\r");
                process.StandardInput.Close();
                await process.WaitForExitAsync(timeout.Token);
                var output = await stdout;

                Assert.Equal(0, process.ExitCode);
                Assert.Contains("Complete input", output);
                Assert.Contains("@notes.txt", output);
                Assert.DoesNotContain("Completion unavailable", output);
                Assert.DoesNotContain("Error:", await stderr);
            }
            catch
            {
                if (!process.HasExited) process.Kill(entireProcessTree: true);
                throw;
            }
        }
        finally { Directory.Delete(cwd, recursive: true); }
    }

    [Fact]
    public async Task ConfiguredSubmitBindingAndHotkeysCommandWorkThroughLinuxPty()
    {
        if (!OperatingSystem.IsLinux() || !File.Exists("/usr/bin/script")) return;
        var cwd = Path.Combine(Path.GetTempPath(), "pisharp-hotkeys-pty-" + Guid.NewGuid().ToString("N"));
        var agentDirectory = Path.Combine(cwd, "agent");
        Directory.CreateDirectory(agentDirectory);
        try
        {
            await File.WriteAllTextAsync(Path.Combine(agentDirectory, "keybindings.json"), """
                { "tui.input.submit": "ctrl+x" }
                """);
            var assembly = typeof(CliArguments).Assembly.Location;
            var start = new ProcessStartInfo("/usr/bin/script")
            {
                WorkingDirectory = cwd,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                ArgumentList = { "-q", "-e", "-c", $"dotnet '{assembly}' --local --no-session --no-tools", "/dev/null" }
            };
            start.Environment["PISHARP_AGENT_DIR"] = agentDirectory;
            using var process = Process.Start(start);
            Assert.NotNull(process);
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            await process.StandardInput.WriteAsync("/hotkeys\u0018/quit\u0018");
            process.StandardInput.Close();
            using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(12));
            try { await process.WaitForExitAsync(limit.Token); }
            catch (OperationCanceledException) { process.Kill(entireProcessTree: true); throw; }

            var output = await stdout;
            Assert.Equal(0, process.ExitCode);
            Assert.Contains("ctrl+x", output);
            Assert.Contains("Submit input (tui.input.submit)", output);
            Assert.DoesNotContain("Agent error", await stderr);
        }
        finally { Directory.Delete(cwd, recursive: true); }
    }

    private static HttpListener StartLoopbackListener(out int port)
    {
        for (var attempt = 0; attempt < 10; attempt++)
        {
            using var reservation = new TcpListener(IPAddress.Loopback, 0);
            reservation.Start();
            var candidatePort = ((IPEndPoint)reservation.LocalEndpoint).Port;
            reservation.Stop();
            var listener = new HttpListener();
            listener.Prefixes.Add($"http://127.0.0.1:{candidatePort}/");
            try
            {
                listener.Start();
                port = candidatePort;
                return listener;
            }
            catch (HttpListenerException)
            {
                listener.Close();
                if (attempt == 9) throw;
            }
        }
        throw new InvalidOperationException("Could not reserve loopback port for active TUI fixture.");
    }

    private static string ShellQuote(string value) => "'" + value.Replace("'", "'\\''", StringComparison.Ordinal) + "'";

    private static int CountOccurrences(string text, string value)
    {
        var count = 0;
        for (var index = 0; (index = text.IndexOf(value, index, StringComparison.Ordinal)) >= 0; index += value.Length)
            count++;
        return count;
    }

    private static byte[] CreatePng()
    {
        using var bitmap = new SKBitmap(new SKImageInfo(2, 2, SKColorType.Rgba8888, SKAlphaType.Premul));
        bitmap.Erase(SKColors.CornflowerBlue);
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }
}
