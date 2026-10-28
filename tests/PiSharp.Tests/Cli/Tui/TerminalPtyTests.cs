using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using PiSharp.Cli;
using PiSharp.Cli.Tui;
using PiSharp.ResourceDiscoveryExtension;
using SkiaSharp;

namespace PiSharp.Tests;

public sealed class TerminalPtyTests
{
    private sealed class PtyOutputCapture
    {
        private readonly StringBuilder _output = new();
        private readonly TaskCompletionSource _inputReady = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public PtyOutputCapture(Process process) => Completion = ReadAsync(process);

        public Task InputReady => _inputReady.Task;
        public Task<string> Completion { get; }

        private async Task<string> ReadAsync(Process process)
        {
            var buffer = new char[1024];
            int count;
            while ((count = await process.StandardOutput.ReadAsync(buffer)) > 0)
            {
                _output.Append(buffer, 0, count);
                if (_output.ToString().Contains("\u001b[?2004h", StringComparison.Ordinal))
                    _inputReady.TrySetResult();
            }
            return _output.ToString();
        }
    }

    private sealed class PtyScreenCapture(Process process, int rows, int columns)
    {
        private readonly object _gate = new();
        private readonly StringBuilder _output = new();
        private readonly List<(Func<string, bool> Predicate, TaskCompletionSource<string> Completion)> _waiters = [];
        private string _lastFrame = "";
        private readonly TaskCompletionSource _inputReady = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public PtyScreenCapture(Process process) : this(process, 30, 100)
        {
        }

        public Task InputReady => _inputReady.Task;
        public Task<string> ReadCompletion => ReadAsync(process);
        public string LastFrame { get { lock (_gate) return _lastFrame; } }
        public string CapturedOutput { get { lock (_gate) return _output.ToString(); } }

        public Task<string> WaitForFrameAsync(Func<string, bool> predicate)
        {
            ArgumentNullException.ThrowIfNull(predicate);
            lock (_gate)
            {
                if (_lastFrame.Length > 0 && predicate(_lastFrame)) return Task.FromResult(_lastFrame);
                var completion = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
                _waiters.Add((predicate, completion));
                return completion.Task;
            }
        }

        private async Task<string> ReadAsync(Process process)
        {
            var buffer = new char[2048];
            int count;
            while ((count = await process.StandardOutput.ReadAsync(buffer)) > 0)
            {
                lock (_gate)
                {
                    _output.Append(buffer, 0, count);
                    var captured = _output.ToString();
                    if (captured.Contains("\u001b[?2004h", StringComparison.Ordinal)) _inputReady.TrySetResult();
                    var frames = TerminalOutputFrameReader.Read(captured, rows, columns);
                    if (frames.Count == 0) continue;
                    _lastFrame = frames[^1].Screen;
                    for (var index = _waiters.Count - 1; index >= 0; index--)
                    {
                        var waiter = _waiters[index];
                        if (!waiter.Predicate(_lastFrame)) continue;
                        _waiters.RemoveAt(index);
                        waiter.Completion.TrySetResult(_lastFrame);
                    }
                }
            }
            lock (_gate) return _output.ToString();
        }
    }

    [Fact]
    public async Task InteractiveReloadRunsNativeExtensionResourceDiscoveryAgain()
    {
        if (!OperatingSystem.IsLinux() || !File.Exists("/usr/bin/script")) return;
        var root = Path.Combine(Path.GetTempPath(), "pisharp-extension-reload-pty-" + Guid.NewGuid().ToString("N"));
        var agent = Path.Combine(root, "agent");
        var tracePath = Path.Combine(root, "extension-trace.log");
        Directory.CreateDirectory(agent);
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
            start.ArgumentList.Add($"stty rows 24 cols 100; exec dotnet {ShellQuote(typeof(CliArguments).Assembly.Location)} --local --offline --no-session --no-tools --extension {ShellQuote(typeof(ExtensionContextFixture).Assembly.Location)}");
            start.ArgumentList.Add("/dev/null");
            start.Environment["PISHARP_AGENT_DIR"] = agent;
            start.Environment["PISHARP_TEST_EXTENSION_CONTEXT"] = "true";
            start.Environment["PISHARP_TEST_EXTENSION_CONTEXT_TRACE"] = tracePath;
            process = Process.Start(start);
            Assert.NotNull(process);

            var output = new StringBuilder();
            var inputReady = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var reloadCompleted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var stdout = Task.Run(async () =>
            {
                var buffer = new char[1024];
                while (await process.StandardOutput.ReadAsync(buffer) is var count && count > 0)
                {
                    lock (output)
                    {
                        output.Append(buffer, 0, count);
                        var captured = output.ToString();
                        if (captured.Contains("\u001b[?2004h", StringComparison.Ordinal)) inputReady.TrySetResult();
                        if (captured.Contains("Reloaded keybindings, extensions, skills, prompts, themes, and context files", StringComparison.Ordinal))
                            reloadCompleted.TrySetResult();
                    }
                }
                lock (output) return output.ToString();
            });
            var stderr = process.StandardError.ReadToEndAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            await inputReady.Task.WaitAsync(timeout.Token);
            await process.StandardInput.WriteAsync("/reload\r");
            await process.StandardInput.FlushAsync();
            await reloadCompleted.Task.WaitAsync(timeout.Token);
            await process.StandardInput.WriteAsync("/quit\r");
            await process.StandardInput.FlushAsync();
            process.StandardInput.Close();
            await process.WaitForExitAsync(timeout.Token);
            var capturedOutput = await stdout;

            Assert.Equal(0, process.ExitCode);
            Assert.DoesNotContain("Agent error:", capturedOutput);
            Assert.DoesNotContain("Exception:", await stderr);
            var trace = await File.ReadAllLinesAsync(tracePath);
            Assert.Contains("resource:startup", trace);
            Assert.Contains("resource:reload", trace);
        }
        finally
        {
            if (process is { HasExited: false }) process.Kill(entireProcessTree: true);
            process?.Dispose();
            Directory.Delete(root, recursive: true);
        }
    }

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
            await process.StandardInput.WriteAsync("/trust\r");
            await process.StandardInput.FlushAsync();
            try { await initialSelector.Task.WaitAsync(timeout.Token); }
            catch (OperationCanceledException)
            {
                string tail;
                lock (outputLock) tail = new string(output.ToString().TakeLast(5000).ToArray());
                throw new InvalidOperationException($"Trust selector did not open. exited={process.HasExited}; stdout tail: {tail}");
            }
            await process.StandardInput.WriteAsync("\u001b[A\r");
            await process.StandardInput.FlushAsync();
            await decisionSaved.Task.WaitAsync(timeout.Token);
            await process.StandardInput.WriteAsync("/trust\r");
            await process.StandardInput.FlushAsync();
            await selectorAfterSave.Task.WaitAsync(timeout.Token);
            await process.StandardInput.WriteAsync("\r");
            await process.StandardInput.FlushAsync();
            await decisionSavedAgain.Task.WaitAsync(timeout.Token);
            await process.StandardInput.WriteAsync("/quit\r");
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
            Assert.Contains("PiSharp can explain its own features and look up its docs. Ask it how to use or extend PiSharp.", capturedOutput);
            Assert.DoesNotContain("Use /hotkeys for shortcut help and / for commands.", capturedOutput);
            Assert.True(capturedOutput.IndexOf("PiSharp can explain its own features and look up its docs. Ask it how to use or extend PiSharp.", StringComparison.Ordinal) <
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
    public async Task DoubleCtrlCExitsInteractiveProcess()
    {
        if (!OperatingSystem.IsLinux() || !File.Exists("/usr/bin/script")) return;
        var root = Path.Combine(Path.GetTempPath(), "pisharp-double-ctrl-c-pty-" + Guid.NewGuid().ToString("N"));
        var agent = Path.Combine(root, "agent");
        Directory.CreateDirectory(agent);
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
            start.ArgumentList.Add($"stty rows 24 cols 80; exec dotnet {ShellQuote(typeof(CliArguments).Assembly.Location)} --local --offline --no-session --no-tools");
            start.ArgumentList.Add("/dev/null");
            start.Environment["PISHARP_AGENT_DIR"] = agent;
            process = Process.Start(start);
            Assert.NotNull(process);

            var inputReady = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var output = new StringBuilder();
            var stdout = Task.Run(async () =>
            {
                var buffer = new char[1024];
                while (true)
                {
                    var count = await process.StandardOutput.ReadAsync(buffer);
                    if (count == 0) break;
                    output.Append(buffer, 0, count);
                    if (output.ToString().Contains("\u001b[?2004h", StringComparison.Ordinal))
                        inputReady.TrySetResult();
                }
            });
            var stderr = process.StandardError.ReadToEndAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await inputReady.Task.WaitAsync(timeout.Token);
            await process.StandardInput.WriteAsync("\u0003\u0003");
            await process.StandardInput.FlushAsync();
            await process.WaitForExitAsync(timeout.Token);
            await stdout;

            Assert.Equal(0, process.ExitCode);
            Assert.DoesNotContain("Exception:", await stderr);
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
            await process.StandardInput.WriteAsync("!printf '%s\\n' \"bang-result:$PISHARP_BANG_PREFIX\"\r");
            await process.StandardInput.FlushAsync();
            await normalResult.Task.WaitAsync(timeout.Token);
            await process.StandardInput.WriteAsync("!!printf '%s\\n' \"excluded-result:$PISHARP_BANG_PREFIX\"\r");
            await process.StandardInput.FlushAsync();
            await excludedResult.Task.WaitAsync(timeout.Token);
            await process.StandardInput.WriteAsync("/quit\r");
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
        var terminalSettingsBefore = Path.Combine(root, "stty-before");
        var terminalSettingsAfter = Path.Combine(root, "stty-after");
        using var serverTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var firstRequestReceived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var followUpRequestReceived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirstResponse = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var server = Task.Run(async () =>
        {
            for (var index = 0; index < 3; index++)
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
                if (index == 2) followUpRequestReceived.TrySetResult();
                request.Response.ContentType = "text/event-stream";
                request.Response.KeepAlive = false;
                await using var writer = new StreamWriter(request.Response.OutputStream);
                await writer.WriteAsync($"data: {{\"type\":\"response.created\",\"response\":{{\"id\":\"resp_{index}\",\"object\":\"response\",\"created_at\":1,\"model\":\"fixture-model\",\"status\":\"in_progress\",\"output\":[]}}}}\n\n");
                if (index == 0)
                {
                    await writer.WriteAsync("data: {\"type\":\"response.output_text.delta\",\"item_id\":\"msg_0\",\"output_index\":0,\"content_index\":0,\"delta\":\"Text before tool call.\\n\"}\n\n");
                    await writer.WriteAsync("data: {\"type\":\"response.output_item.added\",\"output_index\":0,\"item\":{\"id\":\"fc_1\",\"type\":\"function_call\",\"call_id\":\"call_1\",\"name\":\"echo_ext\",\"arguments\":\"\"}}\n\n");
                    await writer.WriteAsync("data: {\"type\":\"response.function_call_arguments.done\",\"output_index\":0,\"item_id\":\"fc_1\",\"arguments\":\"{\\\"value\\\":\\\"pty\\\"}\"}\n\n");
                    await writer.WriteAsync("data: {\"type\":\"response.output_item.done\",\"output_index\":0,\"item\":{\"id\":\"fc_1\",\"type\":\"function_call\",\"call_id\":\"call_1\",\"name\":\"echo_ext\",\"arguments\":\"{\\\"value\\\":\\\"pty\\\"}\"}}\n\n");
                }
                else
                    await writer.WriteAsync($"data: {{\"type\":\"response.output_text.delta\",\"item_id\":\"msg_{index}\",\"output_index\":0,\"content_index\":0,\"delta\":\"{(index == 1 ? "PTY_REPLY_OK" : "FOLLOWUP_AFTER_TOOL_OK")}\"}}\n\n");
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
            start.ArgumentList.Add($"stty rows 24 cols 80; stty -g > {ShellQuote(terminalSettingsBefore)}; dotnet {ShellQuote(typeof(CliArguments).Assembly.Location)} --provider fixture --model fixture-model --no-session --offline --extension {ShellQuote(typeof(FixtureExtension).Assembly.Location)}; command_status=$?; stty -g > {ShellQuote(terminalSettingsAfter)}; exit $command_status");
            start.ArgumentList.Add("/dev/null");
            start.Environment["PISHARP_AGENT_DIR"] = agent;
            start.Environment["PISHARP_FIXTURE_KEY"] = "pty-fixture-key";
            process = Process.Start(start);
            Assert.NotNull(process);

            var output = new StringBuilder();
            var idleReady = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var idleAfterReply = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var workingVisible = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var preToolTextVisible = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var toolCallVisible = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var toolResultVisible = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var idleAfterFollowUp = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var injectedErrorVisible = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
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
                        if (captured.Contains("Session error: fixture command failed", StringComparison.Ordinal)) injectedErrorVisible.TrySetResult();
                        if (captured.Contains("extension call: pty", StringComparison.Ordinal)) toolCallVisible.TrySetResult();
                        if (captured.Contains("extension result: pty / extension: pty", StringComparison.Ordinal)) toolResultVisible.TrySetResult();
                        if (captured.Contains("Settings · User settings", StringComparison.Ordinal)) settingsScopeShown.TrySetResult();
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
                            if (frame.Contains("Working", StringComparison.Ordinal)) workingVisible.TrySetResult();
                            if (frame.Contains("Text before tool call.", StringComparison.Ordinal)) preToolTextVisible.TrySetResult();
                            var queuedCount = 0;
                            for (var position = 0;
                                (position = frame.IndexOf(queuedMessage, position, StringComparison.Ordinal)) >= 0;
                                position += queuedMessage.Length)
                                queuedCount++;
                            if (queuedCount >= 1) firstSteeringQueued.TrySetResult();
                            if (queuedCount >= 2) bothSteeringQueued.TrySetResult();

                            const string activeFooter = "Enter steers · follow-up queues · Escape aborts";
                            if (frame.Contains("PTY_REPLY_OK", StringComparison.Ordinal) &&
                                !frame.Contains(activeFooter, StringComparison.Ordinal) &&
                                !frame.Contains("Working", StringComparison.Ordinal))
                                idleAfterReply.TrySetResult();
                            if (frame.Contains("FOLLOWUP_AFTER_TOOL_OK", StringComparison.Ordinal) &&
                                !frame.Contains(activeFooter, StringComparison.Ordinal) &&
                                !frame.Contains("Working", StringComparison.Ordinal))
                                idleAfterFollowUp.TrySetResult();
                        }
                    }
                }
            });
            var stderr = process.StandardError.ReadToEndAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            await idleReady.Task.WaitAsync(timeout.Token);
            try
            {
                await process.StandardInput.WriteAsync("/settings\r");
                await process.StandardInput.FlushAsync();
                await settingsScopeShown.Task.WaitAsync(timeout.Token);
                await process.StandardInput.WriteAsync("Steering mode\r");
                await process.StandardInput.FlushAsync();
                await steeringOptionsShown.Task.WaitAsync(timeout.Token);
                await process.StandardInput.WriteAsync("All together\r");
                await process.StandardInput.FlushAsync();
                await steeringSaved.Task.WaitAsync(timeout.Token);
                await process.StandardInput.WriteAsync("\u001b");
                await process.StandardInput.FlushAsync();
                await settingsClosed.Task.WaitAsync(timeout.Token);
                await process.StandardInput.WriteAsync("Use echo_ext with value pty.\r");
                await process.StandardInput.FlushAsync();
                await firstRequestReceived.Task.WaitAsync(timeout.Token);
                await workingVisible.Task.WaitAsync(timeout.Token);
                await process.StandardInput.WriteAsync("steering one\r");
                await process.StandardInput.FlushAsync();
                await firstSteeringQueued.Task.WaitAsync(timeout.Token);
                await process.StandardInput.WriteAsync("steering two\r");
                await process.StandardInput.FlushAsync();
                await bothSteeringQueued.Task.WaitAsync(timeout.Token);
                releaseFirstResponse.TrySetResult();
                await preToolTextVisible.Task.WaitAsync(timeout.Token);
                await toolCallVisible.Task.WaitAsync(timeout.Token);
                await toolResultVisible.Task.WaitAsync(timeout.Token);
                await idleAfterReply.Task.WaitAsync(timeout.Token);
                await process.StandardInput.WriteAsync("follow up after tool\r");
                await process.StandardInput.FlushAsync();
                await followUpRequestReceived.Task.WaitAsync(timeout.Token);
                await idleAfterFollowUp.Task.WaitAsync(timeout.Token);
                await process.StandardInput.WriteAsync("/explode\r");
                await process.StandardInput.FlushAsync();
                await injectedErrorVisible.Task.WaitAsync(timeout.Token);
                await process.StandardInput.WriteAsync("/quit\r");
                await process.StandardInput.FlushAsync();
                await process.WaitForExitAsync(timeout.Token);
            }
            catch (OperationCanceledException error)
            {
                process.Kill(entireProcessTree: true);
                await drain;
                string diagnostic;
                lock (output) diagnostic = output.ToString();
                var frames = TerminalOutputFrameReader.Read(diagnostic, rows: 24, columns: 80);
                var currentFrame = frames.Count == 0 ? "<no frame>" : frames[^1].Screen;
                var shortcutDiagnostics = string.Join(" | ", new[]
                    { "Thinking level:", "Shortcut action failed", "Session error:" }
                    .Where(marker => diagnostic.Contains(marker, StringComparison.Ordinal)));
                throw new TimeoutException($"Active PTY failed to exit. Latest screen:\n{currentFrame}\nShortcut output: {shortcutDiagnostics}\nstderr:\n{await stderr}", error);
            }
            await drain;
            await server.WaitAsync(timeout.Token);

            string terminalOutput;
            lock (output) terminalOutput = output.ToString();
            Assert.Equal(1, process.ExitCode);
            Assert.Contains("Text before tool call.", terminalOutput);
            Assert.Contains("extension call: pty", terminalOutput);
            Assert.Contains("extension result: pty / extension: pty", terminalOutput);
            Assert.Contains("PTY_REPLY_OK", terminalOutput);
            Assert.Contains("FOLLOWUP_AFTER_TOOL_OK", terminalOutput);
            Assert.Contains("Session error: fixture command failed", terminalOutput);
            Assert.Contains("\u001b[?1049l", terminalOutput);
            Assert.Equal(await File.ReadAllTextAsync(terminalSettingsBefore), await File.ReadAllTextAsync(terminalSettingsAfter));
            Assert.Contains("function_call_output", requests[1]);
            Assert.Contains("extension: pty", requests[1]);
            Assert.Contains("follow up after tool", requests[2]);
            Assert.Contains("extension: pty", requests[2]);
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
    public async Task McpManagerSelectsDisablesBacksOutAndKeepsTheConversationUsableThroughLinuxPty()
    {
        if (!OperatingSystem.IsLinux() || !File.Exists("/usr/bin/script")) return;
        var root = Path.Combine(Path.GetTempPath(), "pisharp-mcp-manager-pty-" + Guid.NewGuid().ToString("N"));
        var agent = Path.Combine(root, "agent");
        Directory.CreateDirectory(agent);
        using var listener = StartLoopbackListener(out var port);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var providerRequestReceived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var provider = Task.Run(async () =>
        {
            var request = await listener.GetContextAsync().WaitAsync(timeout.Token);
            Assert.Equal("/v1/responses", request.Request.Url?.AbsolutePath);
            using var reader = new StreamReader(request.Request.InputStream);
            var body = await reader.ReadToEndAsync(timeout.Token);
            Assert.Contains("conversation remains usable", body, StringComparison.Ordinal);
            providerRequestReceived.TrySetResult();
            request.Response.ContentType = "text/event-stream";
            request.Response.KeepAlive = false;
            await using var writer = new StreamWriter(request.Response.OutputStream);
            await writer.WriteAsync("data: {\"type\":\"response.created\",\"response\":{\"id\":\"mcp_followup\",\"object\":\"response\",\"created_at\":1,\"model\":\"fixture-model\",\"status\":\"in_progress\",\"output\":[]}}\n\n");
            await writer.WriteAsync("data: {\"type\":\"response.output_text.delta\",\"item_id\":\"mcp_followup_text\",\"output_index\":0,\"content_index\":0,\"delta\":\"MCP_MANAGER_FOLLOWUP_OK\"}\n\n");
            await writer.WriteAsync("data: {\"type\":\"response.completed\",\"response\":{\"id\":\"mcp_followup\",\"object\":\"response\",\"created_at\":1,\"model\":\"fixture-model\",\"status\":\"completed\",\"output\":[]}}\n\n");
            await writer.WriteAsync("data: [DONE]\n\n");
            await writer.FlushAsync();
            request.Response.Close();
        });
        Process? process = null;
        try
        {
            var fixture = Path.Combine(AppContext.BaseDirectory, "TestInfrastructure", "Fixtures", "mcp_fixture.py");
            var configPath = Path.Combine(agent, "mcp.json");
            await File.WriteAllTextAsync(configPath, JsonSerializer.Serialize(new
            {
                mcpServers = new Dictionary<string, object>
                {
                    ["manager"] = new { command = "python3", args = new[] { fixture }, exposure = "direct" }
                }
            }));
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
            start.ArgumentList.Add($"stty rows 24 cols 80; exec dotnet {ShellQuote(typeof(CliArguments).Assembly.Location)} --provider fixture --model fixture-model --no-session --offline");
            start.ArgumentList.Add("/dev/null");
            start.Environment["PISHARP_AGENT_DIR"] = agent;
            start.Environment["PISHARP_FIXTURE_KEY"] = "pty-fixture-key";
            foreach (var name in new[] { "OPENAI_API_KEY", "PISHARP_API_KEY", "PISHARP_BASE_URL", "PISHARP_MODEL", "PISHARP_AUTH_PATH", "PISHARP_MODELS_PATH" })
                start.Environment.Remove(name);
            foreach (var name in start.Environment.Keys.Where(name => name.StartsWith("MCP_FIXTURE_", StringComparison.Ordinal)).ToArray())
                start.Environment.Remove(name);
            process = Process.Start(start);
            Assert.NotNull(process);

            var output = new StringBuilder();
            var outputGate = new object();
            var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var managerList = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var actionMenu = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var disabledDetails = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var disabledList = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var conversationInputVisible = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var followUpSettled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var stdout = Task.Run(async () =>
            {
                var buffer = new char[4096];
                while (true)
                {
                    var count = await process.StandardOutput.ReadAsync(buffer);
                    if (count == 0) break;
                    lock (outputGate)
                    {
                        output.Append(buffer, 0, count);
                        var captured = output.ToString();
                        var frames = TerminalOutputFrameReader.Read(captured, rows: 24, columns: 80);
                        var frame = frames.Count == 0 ? string.Empty : frames[^1].Screen;
                        if (captured.Contains("\u001b[?2026l", StringComparison.Ordinal)) ready.TrySetResult();
                        if (frame.Contains("MCP servers", StringComparison.Ordinal) && frame.Contains("manager", StringComparison.Ordinal))
                        {
                            managerList.TrySetResult();
                        }
                        if (frame.Contains("MCP server manager", StringComparison.Ordinal) && frame.Contains("Disable", StringComparison.Ordinal))
                            actionMenu.TrySetResult();
                        if (frame.Contains("State: disabled", StringComparison.Ordinal) && frame.Contains("Enable", StringComparison.Ordinal))
                            disabledDetails.TrySetResult();
                        if (frame.Contains("MCP servers", StringComparison.Ordinal) && frame.Contains("manager", StringComparison.Ordinal) &&
                            frame.Contains("disabled", StringComparison.Ordinal) &&
                            !frame.Contains("State: disabled", StringComparison.Ordinal)) disabledList.TrySetResult();
                        if (frame.Contains("conversation remains usable", StringComparison.Ordinal) &&
                            !frame.Contains("Search: conversation remains usable", StringComparison.Ordinal))
                            conversationInputVisible.TrySetResult();
                        const string activeFooter = "Enter steers · follow-up queues · Escape aborts";
                        if (frame.Contains("MCP_MANAGER_FOLLOWUP_OK", StringComparison.Ordinal) &&
                            !frame.Contains(activeFooter, StringComparison.Ordinal) &&
                            !frame.Contains("Working", StringComparison.Ordinal)) followUpSettled.TrySetResult();
                    }
                }
                lock (outputGate) return output.ToString();
            });
            var stderr = new StringBuilder();
            var stderrDrain = Task.Run(async () => { var buffer = new char[1024]; int count; while ((count = await process.StandardError.ReadAsync(buffer)) > 0) lock (outputGate) stderr.Append(buffer, 0, count); });
            try { await ready.Task.WaitAsync(timeout.Token); }
            catch { string partial; string errors; lock (outputGate) { partial = output.ToString(); errors = stderr.ToString(); } Assert.Fail("TUI startup timeout. stdout: " + partial + " stderr: " + errors); }
            await process.StandardInput.WriteAsync("/mcp\r");
            await process.StandardInput.FlushAsync();
            try { await managerList.Task.WaitAsync(timeout.Token); }
            catch { string partial; string errors; lock (outputGate) { partial = output.ToString(); errors = stderr.ToString(); } Assert.Fail("Manager list timeout. stdout: " + partial + " stderr: " + errors); }
            await process.StandardInput.WriteAsync("\r");
            await process.StandardInput.FlushAsync();
            await actionMenu.Task.WaitAsync(timeout.Token);
            await process.StandardInput.WriteAsync("\u001b[B\u001b[B\u001b[B\r");
            await process.StandardInput.FlushAsync();
            await disabledDetails.Task.WaitAsync(timeout.Token);
            await process.StandardInput.WriteAsync("\u001b");
            await process.StandardInput.FlushAsync();
            await disabledList.Task.WaitAsync(timeout.Token);
            await process.StandardInput.WriteAsync("\u001b");
            await process.StandardInput.FlushAsync();
            await Task.Delay(100, timeout.Token);
            await process.StandardInput.WriteAsync("conversation remains usable");
            await process.StandardInput.FlushAsync();
            await conversationInputVisible.Task.WaitAsync(TimeSpan.FromSeconds(5), timeout.Token);
            await process.StandardInput.WriteAsync("\r");
            await process.StandardInput.FlushAsync();
            try { await providerRequestReceived.Task.WaitAsync(TimeSpan.FromSeconds(5), timeout.Token); }
            catch
            {
                string partial;
                lock (outputGate) partial = output.ToString();
                var frames = TerminalOutputFrameReader.Read(partial, rows: 24, columns: 80);
                Assert.Fail("Conversation prompt did not submit a provider request. Last frame: " +
                    (frames.Count == 0 ? partial : frames[^1].Screen) +
                    (provider.IsFaulted ? " Provider fixture: " + provider.Exception : ""));
            }
            await followUpSettled.Task.WaitAsync(timeout.Token);
            await process.StandardInput.WriteAsync("/quit\r");
            await process.StandardInput.FlushAsync();
            process.StandardInput.Close();
            await process.WaitForExitAsync(timeout.Token);
            await stdout;
            await provider.WaitAsync(timeout.Token);

            string terminalOutput;
            lock (outputGate) terminalOutput = output.ToString();
            Assert.Equal(0, process.ExitCode);
            Assert.Contains("Disabled MCP server manager.", terminalOutput, StringComparison.Ordinal);
            Assert.Contains("MCP_MANAGER_FOLLOWUP_OK", terminalOutput, StringComparison.Ordinal);
            using var saved = JsonDocument.Parse(await File.ReadAllTextAsync(configPath));
            Assert.False(saved.RootElement.GetProperty("mcpServers").GetProperty("manager").GetProperty("enabled").GetBoolean());
            Assert.DoesNotContain("Session error:", terminalOutput, StringComparison.Ordinal);
            Assert.DoesNotContain("Error:", stderr.ToString(), StringComparison.Ordinal);
        }
        finally
        {
            timeout.Cancel();
            if (process is { HasExited: false }) process.Kill(entireProcessTree: true);
            process?.Dispose();
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task SessionDiscoverySortResumeAndCloneWorkThroughLinuxPty()
    {
        if (!OperatingSystem.IsLinux() || !File.Exists("/usr/bin/script")) return;
        var cwd = Path.Combine(Path.GetTempPath(), "pisharp-session-pty-" + Guid.NewGuid().ToString("N"));
        var agentDirectory = Path.Combine(cwd, "agent");
        Directory.CreateDirectory(cwd);
        Directory.CreateDirectory(agentDirectory);
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
                ArgumentList = { "-q", "-e", "-c", $"stty ixon rows 24 cols 80; dotnet '{assembly}' --local --session-dir '{cwd}/sessions' --no-tools; stty -a", "/dev/null" }
            };
            start.Environment["PISHARP_AGENT_DIR"] = agentDirectory;
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
            int OutputLength()
            {
                lock (outputLock) return output.Length;
            }
            var stderr = process.StandardError.ReadToEndAsync();
            using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            await inputReady.Task.WaitAsync(limit.Token);
            await process.StandardInput.WriteAsync("/name first\r/new\r/name second\r/sessions\r/resume first\r/resume second\r/resume\r");
            await process.StandardInput.FlushAsync();
            await pickerOpened.Task.WaitAsync(limit.Token);
            var firstId = (await PiSharp.Runtime.Sessions.SessionCatalog.ListAsync(store))
                .Single(session => session.Name == "first").Id[..12];
            var secondId = (await PiSharp.Runtime.Sessions.SessionCatalog.ListAsync(store))
                .Single(session => session.Name == "second").Id[..12];
            var secondIdPrefix = secondId[..8];
            await process.StandardInput.WriteAsync(secondIdPrefix);
            await process.StandardInput.FlushAsync();
            await WaitForOutputAsync(captured => captured.Contains($"Search: {secondIdPrefix}", StringComparison.Ordinal), limit.Token);
            await process.StandardInput.WriteAsync("\u0012"); // Ctrl+R renames the selected session.
            await process.StandardInput.FlushAsync();
            await WaitForOutputAsync(captured => captured.Contains("Rename Session", StringComparison.Ordinal), limit.Token);
            await process.StandardInput.WriteAsync("\u0001renamed \r"); // Keep the prefilled "second" suffix.
            await process.StandardInput.FlushAsync();
            await WaitForOutputAsync(captured => captured.Contains($"Search: {secondIdPrefix}", StringComparison.Ordinal) &&
                captured.Contains($"renamed second · {secondId}", StringComparison.Ordinal), limit.Token);
            Assert.Equal("renamed second", (await PiSharp.Runtime.Sessions.SessionCatalog.ListAsync(store))
                .Single(session => session.Id.StartsWith(secondId, StringComparison.Ordinal)).Name);
            await process.StandardInput.WriteAsync("\r"); // The selected session remains selected after rename.
            await process.StandardInput.FlushAsync();
            await WaitForOutputAsync(captured => captured.Contains("Already in this session.", StringComparison.Ordinal), limit.Token);
            var beforeReopen = OutputLength();
            await process.StandardInput.WriteAsync("/resume\r");
            await process.StandardInput.FlushAsync();
            await WaitForOutputAsync(captured => captured.Length > beforeReopen &&
                captured[beforeReopen..].Contains("Resume session · Recent", StringComparison.Ordinal), limit.Token);
            await process.StandardInput.WriteAsync("\u0015"); // Clear search before checking name sort.
            await process.StandardInput.FlushAsync();
            await process.StandardInput.WriteAsync("\u0013"); // Ctrl+S switches the picker to name order.
            await process.StandardInput.FlushAsync();
            await WaitForOutputAsync(captured => captured.Contains("Resume session · Name", StringComparison.Ordinal), limit.Token);
            await process.StandardInput.WriteAsync("\u001b[H\r"); // Home, then resume the first name-sorted session.
            await process.StandardInput.FlushAsync();
            await WaitForOutputAsync(captured => captured.Contains($"Resumed {firstId}", StringComparison.Ordinal), limit.Token);
            await process.StandardInput.WriteAsync($"/session\r/export {cwd}/export.html\r/clone\r/session\r/quit\r");
            await process.StandardInput.FlushAsync();
            process.StandardInput.Close();
            try { await process.WaitForExitAsync(limit.Token); }
            catch (OperationCanceledException) { process.Kill(entireProcessTree: true); throw; }
            var capturedOutput = await stdout;
            var secondIds = (await PiSharp.Runtime.Sessions.SessionCatalog.ListAsync(store))
                .Where(session => session.Name == "renamed second").Select(session => session.Id[..12]).ToHashSet();
            var resumedIds = System.Text.RegularExpressions.Regex.Matches(capturedOutput, @"Resumed ([a-f0-9]{12})")
                .Select(match => match.Groups[1].Value).ToArray();
            Assert.Equal(0, process.ExitCode);
            Assert.Contains("Resumed", capturedOutput);
            Assert.Contains("Resume session · Name", capturedOutput);
            Assert.Contains("Search: " + secondIdPrefix, capturedOutput);
            Assert.Contains("renamed second · " + secondId, capturedOutput);
            Assert.Contains(firstId, resumedIds);
            Assert.Contains(resumedIds, id => secondIds.Contains(id));
            Assert.Matches(@"(?<!-)ixon\b", capturedOutput);
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
            var inputReady = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
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
                        if (captured.Contains("\u001b[?2004h", StringComparison.Ordinal)) inputReady.TrySetResult();
                        var visibleFrames = TerminalOutputFrameReader.Read(captured, rows: 24, columns: 180)
                            .Select(frame => string.Join(' ', frame.Screen.Split('\n')
                                .Select(line => line.Trim())));
                        var visibleOutput = string.Join('\n', visibleFrames);
                        var promptIndex = visibleOutput.LastIndexOf(confirmationPrompt, StringComparison.Ordinal);
                        var cancelledIndex = visibleOutput.LastIndexOf("Import cancelled.", StringComparison.Ordinal);
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
            await inputReady.Task.WaitAsync(timeout.Token);
            await process.StandardInput.WriteAsync($"/import \"{sourcePath}\"\r");
            await process.StandardInput.FlushAsync();
            try
            {
                await promptReady.Task.WaitAsync(timeout.Token);
                await process.StandardInput.WriteAsync("cancel\r");
                await process.StandardInput.FlushAsync();
                await cancelledReady.Task.WaitAsync(timeout.Token);
                Assert.DoesNotContain(await PiSharp.Runtime.Sessions.SessionCatalog.ListAsync(store),
                    session => session.Id == importedId);
                await process.StandardInput.WriteAsync($"/import \"{sourcePath}\"\r");
                await retryPromptReady.Task.WaitAsync(timeout.Token);
                await process.StandardInput.WriteAsync("import\r");
                await process.StandardInput.FlushAsync();
                await importReady.Task.WaitAsync(timeout.Token);
                await process.StandardInput.WriteAsync($"/export-jsonl \"{exportPath}\"\r");
                await process.StandardInput.FlushAsync();
                await exportReady.Task.WaitAsync(timeout.Token);
                await process.StandardInput.WriteAsync("/session\r/quit\r");
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
            var visibleTerminalOutput = string.Join('\n', TerminalOutputFrameReader.Read(terminalOutput, rows: 24, columns: 180)
                .Select(frame => string.Join(' ', frame.Screen.Split('\n').Select(line => line.Trim()))));
            Assert.Contains("Type import to confirm:", visibleTerminalOutput);
            Assert.Contains("Import cancelled.", terminalOutput);
            Assert.Contains("Exported private Pi JSONL to", terminalOutput);
            Assert.Contains($"Imported Pi session {importedId}", terminalOutput);
            Assert.Contains("imported context from Pi", terminalOutput);
            Assert.DoesNotContain("› imported context from Pi", terminalOutput);
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
            var startupOutput = new PtyOutputCapture(process);
            var startupError = process.StandardError.ReadToEndAsync();
            await startupOutput.InputReady.WaitAsync(timeout.Token);
            await process.StandardInput.WriteAsync("/session\r/quit\r");
            process.StandardInput.Close();
            await process.WaitForExitAsync(timeout.Token);
            var startupText = await startupOutput.Completion;
            var startupSession = Assert.Single(await PiSharp.Runtime.Sessions.SessionCatalog.ListAsync(startupStore),
                session => session.Id == importedId);
            Assert.Equal(sourceCwd, (await startupStore.LoadAsync(startupSession.Path)).WorkingDirectory);
            Assert.Equal(0, process.ExitCode);
            Assert.Contains(importedId, startupText);
            Assert.Contains("imported context from Pi", startupText);
            Assert.DoesNotContain("› imported context from Pi", startupText);
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
            var stdout = new PtyOutputCapture(process);
            var stderr = process.StandardError.ReadToEndAsync();
            await stdout.InputReady.WaitAsync(TimeSpan.FromSeconds(12));
            await process.StandardInput.WriteAsync($"/fork {userId[..12]}\r");
            await process.StandardInput.FlushAsync();
            using (var forkReady = new CancellationTokenSource(TimeSpan.FromSeconds(12)))
            {
                while (Directory.EnumerateFiles(store.DirectoryPath, "*.session.json").Count() < 2)
                    await Task.Delay(30, forkReady.Token);
            }
            await Task.Delay(150);
            await process.StandardInput.WriteAsync("\u0015/name forked\r/quit\r");
            process.StandardInput.Close();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(12));
            try { await process.WaitForExitAsync(timeout.Token); }
            catch (OperationCanceledException) { process.Kill(entireProcessTree: true); throw; }
            var output = await stdout.Completion;
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
            var inputReady = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
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
                        if (current.Contains("\u001b[?2004h", StringComparison.Ordinal)) inputReady.TrySetResult();
                        var forkStatus = current.LastIndexOf($"Forked {selectedIdPrefix}", StringComparison.Ordinal);
                        if (forkStatus >= 0 && current.IndexOf("draft to change", forkStatus, StringComparison.Ordinal) >= 0)
                            promptReady.TrySetResult();
                    }
                }
                lock (outputLock) return outputBuilder.ToString();
            });
            var stderr = process.StandardError.ReadToEndAsync();
            await inputReady.Task.WaitAsync(TimeSpan.FromSeconds(12));
            await process.StandardInput.WriteAsync("/fork\rdraft to change\r");
            await process.StandardInput.FlushAsync();
            await promptReady.Task.WaitAsync(TimeSpan.FromSeconds(12));
            await process.StandardInput.WriteAsync("\u0015/name forked\r/quit\r");
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
            var inputReady = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
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
                        if (current.Contains("\u001b[?2004h", StringComparison.Ordinal)) inputReady.TrySetResult();
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
            await inputReady.Task.WaitAsync(TimeSpan.FromSeconds(12));
            await process.StandardInput.WriteAsync("/settings\rHide thinking\r");
            await process.StandardInput.FlushAsync();
            await saved.Task.WaitAsync(TimeSpan.FromSeconds(12));
            await process.StandardInput.WriteAsync("External editor\r");
            await process.StandardInput.FlushAsync();
            await editorPrompt.Task.WaitAsync(TimeSpan.FromSeconds(12));
            await process.StandardInput.WriteAsync("code --wait\r");
            await process.StandardInput.FlushAsync();
            await externalEditorSaved.Task.WaitAsync(TimeSpan.FromSeconds(12));
            await process.StandardInput.WriteAsync("Theme\rlight\r");
            await process.StandardInput.FlushAsync();
            await themeSaved.Task.WaitAsync(TimeSpan.FromSeconds(12));
            await process.StandardInput.WriteAsync("Steering mode\rall\r");
            await process.StandardInput.FlushAsync();
            await steeringSaved.Task.WaitAsync(TimeSpan.FromSeconds(12));
            await process.StandardInput.WriteAsync("Follow-up mode\rOne at a time\r");
            await process.StandardInput.FlushAsync();
            await followUpSaved.Task.WaitAsync(TimeSpan.FromSeconds(12));
            await process.StandardInput.WriteAsync("HTTP proxy\r");
            await process.StandardInput.FlushAsync();
            await proxyPrompt.Task.WaitAsync(TimeSpan.FromSeconds(12));
            await process.StandardInput.WriteAsync("https://proxy.example:8443\r");
            await process.StandardInput.FlushAsync();
            await proxySaved.Task.WaitAsync(TimeSpan.FromSeconds(12));
            await process.StandardInput.WriteAsync("HTTP idle timeout\r");
            await process.StandardInput.FlushAsync();
            await process.StandardInput.WriteAsync("Disabled\r");
            await process.StandardInput.FlushAsync();
            await idleTimeoutSaved.Task.WaitAsync(TimeSpan.FromSeconds(12));
            await process.StandardInput.WriteAsync("Provider request timeout\r");
            await process.StandardInput.FlushAsync();
            await providerTimeoutPrompt.Task.WaitAsync(TimeSpan.FromSeconds(12));
            await process.StandardInput.WriteAsync("45000\r");
            await process.StandardInput.FlushAsync();
            await providerTimeoutSaved.Task.WaitAsync(TimeSpan.FromSeconds(12));
            await process.StandardInput.WriteAsync("Provider retry delay limit\rNo limit\r");
            await process.StandardInput.FlushAsync();
            await providerRetryDelaySaved.Task.WaitAsync(TimeSpan.FromSeconds(12));
            await process.StandardInput.WriteAsync("Code block indent\r");
            await process.StandardInput.FlushAsync();
            await codeBlockIndentPrompt.Task.WaitAsync(TimeSpan.FromSeconds(12));
            await process.StandardInput.WriteAsync(" > \r");
            await process.StandardInput.FlushAsync();
            await codeBlockIndentSaved.Task.WaitAsync(TimeSpan.FromSeconds(12));
            await process.StandardInput.WriteAsync("Terminal true color\rEnabled\r");
            await process.StandardInput.FlushAsync();
            await trueColorSaved.Task.WaitAsync(TimeSpan.FromSeconds(12));
            await process.StandardInput.WriteAsync("Skill commands\rDisabled\r");
            await process.StandardInput.FlushAsync();
            await skillCommandsSaved.Task.WaitAsync(TimeSpan.FromSeconds(12));
            await process.StandardInput.WriteAsync("\u001b");
            await process.StandardInput.FlushAsync();
            await closed.Task.WaitAsync(TimeSpan.FromSeconds(12));
            await process.StandardInput.WriteAsync("/quit\r");
            await process.StandardInput.FlushAsync();
            process.StandardInput.Close();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(12));
            try { await process.WaitForExitAsync(timeout.Token); }
            catch (OperationCanceledException) { process.Kill(entireProcessTree: true); throw; }
            var output = await stdout;
            Assert.Equal(0, process.ExitCode);
            Assert.Contains("Settings · User settings", output);
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

    [Theory]
    [InlineData(80)]
    [InlineData(52)]
    public async Task SettingsListRendersFiltersChangesAndPersistsThroughLinuxPty(int columns)
    {
        if (!OperatingSystem.IsLinux() || !File.Exists("/usr/bin/script")) return;
        var cwd = Path.Combine(Path.GetTempPath(), "pisharp-settings-list-pty-" + Guid.NewGuid().ToString("N"));
        var agentDirectory = Path.Combine(cwd, "agent");
        Directory.CreateDirectory(agentDirectory);
        Process? process = null;
        Process? restarted = null;
        try
        {
            process = StartSettingsFixtureProcess(cwd, agentDirectory, columns);
            var screen = new PtyScreenCapture(process, 24, columns);
            var stdout = screen.ReadCompletion;
            var stderr = process.StandardError.ReadToEndAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            await screen.InputReady.WaitAsync(timeout.Token);

            async Task SendAsync(Process target, string input)
            {
                await target.StandardInput.WriteAsync(input);
                await target.StandardInput.FlushAsync();
            }

            await SendAsync(process, "/settings\r");
            var initial = await screen.WaitForFrameAsync(frame =>
                frame.Contains("Settings · User settings", StringComparison.Ordinal) &&
                frame.Contains("Auto-compact", StringComparison.Ordinal) &&
                frame.Contains("(1/28)", StringComparison.Ordinal)).WaitAsync(timeout.Token);
            var sharedOrder = new[]
            {
                "Auto-compact", "Auto-resize images", "Block images", "Skill commands",
                "Show hardware cursor", "Editor padding", "Output padding", "Autocomplete max items",
                "Clear on shrink", "Terminal progress"
            };
            var positions = sharedOrder.Select(label => initial.IndexOf(label, StringComparison.Ordinal)).ToArray();
            Assert.All(positions, position => Assert.True(position >= 0, $"Missing setting in initial view: {sharedOrder[Array.IndexOf(positions, position)]}"));
            Assert.Equal(positions.Order(), positions);
            var compactRow = initial.Split('\n').Single(line => line.Contains("Auto-compact", StringComparison.Ordinal));
            var resizeRow = initial.Split('\n').Single(line => line.Contains("Auto-resize images", StringComparison.Ordinal));
            Assert.Equal(compactRow.IndexOf("true", StringComparison.Ordinal),
                resizeRow.IndexOf("true", StringComparison.Ordinal));
            Assert.Contains("Automatically compact context", initial, StringComparison.Ordinal);
            Assert.Contains("Enter/Space change", initial, StringComparison.Ordinal);
            Assert.All(initial.Split('\n'), line => Assert.True(TerminalTextLayout.Width(line) <= columns));

            await SendAsync(process, "\u001b[B");
            var resizedDescription = await screen.WaitForFrameAsync(frame =>
                frame.Contains("Settings · User settings", StringComparison.Ordinal) &&
                frame.Contains("Resize large images", StringComparison.Ordinal)).WaitAsync(timeout.Token);
            Assert.Contains("Auto-resize images", resizedDescription, StringComparison.Ordinal);

            await SendAsync(process, "\u001b[A ");
            var compactDisabled = await screen.WaitForFrameAsync(frame =>
                frame.Split('\n').Any(line => line.Contains("Auto-compact", StringComparison.Ordinal) &&
                    line.Contains("false", StringComparison.Ordinal))).WaitAsync(timeout.Token);
            Assert.Contains("Settings · User settings", compactDisabled, StringComparison.Ordinal);

            await SendAsync(process, "show hardware cursor");
            var filtered = await screen.WaitForFrameAsync(frame =>
                frame.Contains("Filter: show hardware cursor", StringComparison.Ordinal) &&
                frame.Contains("(1/1)", StringComparison.Ordinal) &&
                frame.Contains("Show hardware cursor", StringComparison.Ordinal)).WaitAsync(timeout.Token);
            Assert.DoesNotContain("Auto-compact", filtered, StringComparison.Ordinal);
            await SendAsync(process, "\r");
            await screen.WaitForFrameAsync(frame =>
                frame.Contains("Settings · User settings", StringComparison.Ordinal) &&
                frame.Split('\n').Any(line => line.Contains("Show hardware cursor", StringComparison.Ordinal) &&
                    line.Contains("true", StringComparison.Ordinal))).WaitAsync(timeout.Token);

            await SendAsync(process, "\u001b");
            await screen.WaitForFrameAsync(frame =>
                !frame.Contains("Settings · User settings", StringComparison.Ordinal) &&
                !frame.Contains("Auto-compact", StringComparison.Ordinal)).WaitAsync(timeout.Token);
            await SendAsync(process, "/quit\r");
            process.StandardInput.Close();
            await process.WaitForExitAsync(timeout.Token);
            Assert.Equal(0, process.ExitCode);
            Assert.DoesNotContain("Agent error", await stdout);
            Assert.DoesNotContain("Exception:", await stderr);

            var settings = await UserSettings.LoadAsync(agentDirectory, _ => null);
            Assert.False(settings.Compaction?.Enabled);
            Assert.True(settings.ShowHardwareCursor);

            restarted = StartSettingsFixtureProcess(cwd, agentDirectory, columns);
            var restartedScreen = new PtyScreenCapture(restarted, 24, columns);
            var restartedOutput = restartedScreen.ReadCompletion;
            var restartedError = restarted.StandardError.ReadToEndAsync();
            await restartedScreen.InputReady.WaitAsync(timeout.Token);
            await restarted.StandardInput.WriteAsync("/settings\rshow hardware cursor");
            await restarted.StandardInput.FlushAsync();
            var restored = await restartedScreen.WaitForFrameAsync(frame =>
                frame.Contains("Filter: show hardware cursor", StringComparison.Ordinal) &&
                frame.Contains("(1/1)", StringComparison.Ordinal) &&
                frame.Split('\n').Any(line => line.Contains("Show hardware cursor", StringComparison.Ordinal) &&
                    line.Contains("true", StringComparison.Ordinal))).WaitAsync(timeout.Token);
            Assert.Contains("Settings · User settings", restored, StringComparison.Ordinal);
            await restarted.StandardInput.WriteAsync("\u001b");
            await restarted.StandardInput.FlushAsync();
            await restartedScreen.WaitForFrameAsync(frame =>
                !frame.Contains("Settings · User settings", StringComparison.Ordinal)).WaitAsync(timeout.Token);
            await restarted.StandardInput.WriteAsync("/quit\r");
            await restarted.StandardInput.FlushAsync();
            restarted.StandardInput.Close();
            await restarted.WaitForExitAsync(timeout.Token);
            Assert.Equal(0, restarted.ExitCode);
            Assert.DoesNotContain("Agent error", await restartedOutput);
            Assert.DoesNotContain("Exception:", await restartedError);
        }
        finally
        {
            if (process is { HasExited: false }) process.Kill(entireProcessTree: true);
            process?.Dispose();
            if (restarted is { HasExited: false }) restarted.Kill(entireProcessTree: true);
            restarted?.Dispose();
            Directory.Delete(cwd, recursive: true);
        }
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
                await process.StandardInput.WriteAsync("/copy\r");
                await process.StandardInput.FlushAsync();
                await slashCopyCompleted.Task.WaitAsync(TimeSpan.FromSeconds(12));
                await process.StandardInput.WriteAsync("/quit\r");
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
            var stdout = new PtyOutputCapture(process);
            var stderr = process.StandardError.ReadToEndAsync();
            await stdout.InputReady.WaitAsync(TimeSpan.FromSeconds(12));
            await process.StandardInput.WriteAsync($"/sessions Remove\r/delete-session {victim.Id[..12]}\rno\r/delete-session {victim.Id[..12]}\rdelete {victim.Id[..12]}\r/quit\r");
            process.StandardInput.Close();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(12));
            try { await process.WaitForExitAsync(timeout.Token); }
            catch (OperationCanceledException) { process.Kill(entireProcessTree: true); throw; }
            var output = await stdout.Completion;
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
    public async Task ConversationTreeSearchSelectsAndPersistsABranchThroughLinuxPty()
    {
        if (!OperatingSystem.IsLinux() || !File.Exists("/usr/bin/script")) return;
        var cwd = Path.Combine(Path.GetTempPath(), "pisharp-tree-picker-pty-" + Guid.NewGuid().ToString("N"));
        var agent = Path.Combine(cwd, "agent");
        Directory.CreateDirectory(agent);
        try
        {
            var store = new PiSharp.Runtime.Sessions.ConversationStore(cwd, Path.Combine(cwd, "sessions"));
            var conversation = new PiSharp.Runtime.Sessions.ConversationSession(cwd, ConnectionSettings.LocalModel,
                new Uri(ConnectionSettings.LocalEndpoint).ToString());
            conversation.Append(new Microsoft.Extensions.AI.ChatMessage(Microsoft.Extensions.AI.ChatRole.User, "first prompt"));
            conversation.Append(new Microsoft.Extensions.AI.ChatMessage(Microsoft.Extensions.AI.ChatRole.Assistant, "first answer"));
            var sharedHead = conversation.Tree.HeadId!;
            conversation.Append(new Microsoft.Extensions.AI.ChatMessage(Microsoft.Extensions.AI.ChatRole.User, "second prompt"));
            conversation.Append(new Microsoft.Extensions.AI.ChatMessage(Microsoft.Extensions.AI.ChatRole.Assistant, "second answer"));
            var selectedHead = conversation.Tree.HeadId!;
            conversation.Tree.Select(sharedHead);
            conversation.Append(new Microsoft.Extensions.AI.ChatMessage(Microsoft.Extensions.AI.ChatRole.User, "alternate prompt"));
            conversation.Append(new Microsoft.Extensions.AI.ChatMessage(Microsoft.Extensions.AI.ChatRole.Assistant, "alternate answer"));
            var alternateHead = conversation.Tree.HeadId!;
            var sessionPath = store.NewPath(conversation);
            await store.SaveAsync(conversation, sessionPath);

            var start = new ProcessStartInfo("/usr/bin/script")
            {
                WorkingDirectory = cwd,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                ArgumentList =
                {
                    "-q", "-e", "-c",
                    $"stty rows 24 cols 100; exec dotnet '{typeof(CliArguments).Assembly.Location}' --local --session '{sessionPath}' --session-dir '{store.DirectoryPath}' --no-tools",
                    "/dev/null"
                }
            };
            start.Environment["PISHARP_AGENT_DIR"] = agent;
            using var process = Process.Start(start);
            Assert.NotNull(process);
            var output = new StringBuilder();
            var outputLock = new object();
            var inputReady = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var pickerOpened = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var branchSummaryPromptOpened = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var customSummaryChoiceOpened = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var branchSelected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
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
                        if (captured.Contains("Conversation tree", StringComparison.Ordinal)) pickerOpened.TrySetResult();
                        if (captured.Contains("Summarize branch?", StringComparison.Ordinal))
                            branchSummaryPromptOpened.TrySetResult();
                        if (captured.Contains("Summarize with custom prompt", StringComparison.Ordinal))
                            customSummaryChoiceOpened.TrySetResult();
                        if (captured.Contains($"Selected {selectedHead[..12]}", StringComparison.Ordinal)) branchSelected.TrySetResult();
                    }
                }
                lock (outputLock) return output.ToString();
            });
            var stderr = process.StandardError.ReadToEndAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(12));
            await inputReady.Task.WaitAsync(timeout.Token);
            await process.StandardInput.WriteAsync("/tree\r");
            await process.StandardInput.FlushAsync();
            await pickerOpened.Task.WaitAsync(timeout.Token);
            await process.StandardInput.WriteAsync("second answer\r");
            await process.StandardInput.FlushAsync();
            await branchSummaryPromptOpened.Task.WaitAsync(timeout.Token);
            await customSummaryChoiceOpened.Task.WaitAsync(timeout.Token);
            await process.StandardInput.WriteAsync("\r");
            await process.StandardInput.FlushAsync();
            await branchSelected.Task.WaitAsync(timeout.Token);
            await process.StandardInput.WriteAsync("/session\r/quit\r");
            await process.StandardInput.FlushAsync();
            process.StandardInput.Close();
            await process.WaitForExitAsync(timeout.Token);

            var capturedOutput = await stdout;
            var saved = await store.LoadAsync(sessionPath);
            Assert.Equal(0, process.ExitCode);
            Assert.Contains("assistant: second answer", capturedOutput);
            Assert.Contains("No summary", capturedOutput);
            Assert.Contains("Summarize", capturedOutput);
            Assert.Contains("Summarize with custom prompt", capturedOutput);
            Assert.Contains($"Selected {selectedHead[..12]}", capturedOutput);
            Assert.Equal(selectedHead, saved.Tree.HeadId);
            Assert.Contains(saved.Tree.Entries, entry => entry.Id == alternateHead);
            Assert.Equal(["first prompt", "first answer", "second prompt", "second answer"],
                saved.ActiveMessages().Select(message => message.Text));
            Assert.DoesNotContain("Session error:", capturedOutput);
            Assert.DoesNotContain("Agent error:", await stderr);
        }
        finally { Directory.Delete(cwd, recursive: true); }
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
            var agent = Path.Combine(cwd, "agent");
            Directory.CreateDirectory(agent);
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
                start.ArgumentList.Add("--offline");
                foreach (var arg in arguments) start.ArgumentList.Add(arg);
                start.Environment["PISHARP_AGENT_DIR"] = agent;
                start.Environment["HOME"] = cwd;
                using var process = Process.Start(start);
                Assert.NotNull(process);
                var stdout = process.StandardOutput.ReadToEndAsync();
                var stderr = process.StandardError.ReadToEndAsync();
                process.StandardInput.Close();
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(12));
                try { await process.WaitForExitAsync(timeout.Token); }
                catch (OperationCanceledException)
                {
                    process.Kill(entireProcessTree: true);
                    await process.WaitForExitAsync();
                    throw new TimeoutException($"Startup flags process timed out for {string.Join(' ', arguments)}. " +
                        $"stdout: {await stdout}; stderr: {await stderr}");
                }
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
    [InlineData(true, false)]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public async Task ExitLeavesNoTerminalRepliesAndRestoresTtyThroughLinuxPty(bool reply, bool cancel)
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
                    active_flags = termios.tcgetattr(slave)[0]
                    assert not active_flags & termios.ICRNL, 'raw mode translated Enter into Ctrl+J'
                    assert not active_flags & termios.IXON, 'raw mode left XON/XOFF enabled'
                    replies = b'\x1b]10;#f8f8f2\x07\x1b]11;#282a36\x07'
                    replies += b''.join(('\x1b]4;%d;#262626\x07' % i).encode() for i in range(16))
                    replies += b'\x1b[?61;1;21;22c'
                    quit_started = time.monotonic()
                    if sys.argv[2] == 'True': os.write(master, replies)
                    os.write(master, b'\x03\x03' if sys.argv[3] == 'True' else b'/quit\r')
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
            start.ArgumentList.Add(cancel.ToString());
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

            await process.StandardInput.WriteAsync("\u0015/session\r");
            await process.StandardInput.FlushAsync();
            Assert.Same(sessionResponse.Task, await Task.WhenAny(sessionResponse.Task, process.WaitForExitAsync(timeout.Token)).WaitAsync(timeout.Token));
            Assert.False(process.HasExited);
            await process.StandardInput.WriteAsync("/quit\r");
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
            var stdout = new PtyOutputCapture(process);
            var stderr = process.StandardError.ReadToEndAsync();
            await stdout.InputReady.WaitAsync(TimeSpan.FromSeconds(12));
            await process.StandardInput.WriteAsync("/session\r/name café界🙂\r/name smoke\r/session\r\u001b[200~/name pasted\nsecond\u001b[201~\r/quit\r");
            process.StandardInput.Close();
            using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(12));
            try { await process.WaitForExitAsync(limit.Token); }
            catch (OperationCanceledException) { process.Kill(entireProcessTree: true); throw; }
            var output = await stdout.Completion;
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

            var screen = new PtyScreenCapture(process, rows: 24, columns: 80);
            var stdout = screen.ReadCompletion;
            var stderr = process.StandardError.ReadToEndAsync();
            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(25));
                await screen.InputReady.WaitAsync(timeout.Token);
                await process.StandardInput.WriteAsync("@n\t");
                await process.StandardInput.FlushAsync();
                var selectedPath = await screen.WaitForFrameAsync(frame => EditorInputLine(frame) == "@notes.txt" &&
                    !frame.Contains("Complete input", StringComparison.Ordinal)).WaitAsync(timeout.Token);
                Assert.Contains("@notes.txt", selectedPath);
                await process.StandardInput.WriteAsync("\u0015");
                await process.StandardInput.FlushAsync();
                await screen.WaitForFrameAsync(frame => EditorInputLine(frame) == "").WaitAsync(timeout.Token);
                await process.StandardInput.WriteAsync("/quit\r");
                process.StandardInput.Close();
                await process.WaitForExitAsync(timeout.Token);
                var output = await stdout;

                Assert.Equal(0, process.ExitCode);
                Assert.Contains("@notes.txt", output);
                Assert.DoesNotContain("Complete input", output);
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
    public async Task SlashCommandTabCyclesAndOneEnterOpensThinkingSelectorThroughLinuxPty()
    {
        if (!OperatingSystem.IsLinux() || !File.Exists("/usr/bin/script")) return;
        var cwd = Path.Combine(Path.GetTempPath(), "pisharp-command-completion-pty-" + Guid.NewGuid().ToString("N"));
        var agentDirectory = Path.Combine(cwd, "agent");
        Directory.CreateDirectory(agentDirectory);
        await WriteThinkingFixtureSettingsAsync(agentDirectory);
        Process? process = null;
        try
        {
            process = StartThinkingFixtureProcess(cwd, agentDirectory);
            var screen = new PtyScreenCapture(process);
            var stdout = screen.ReadCompletion;
            var stderr = process.StandardError.ReadToEndAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(25));
            await screen.InputReady.WaitAsync(timeout.Token);

            async Task SendAsync(string input)
            {
                await process.StandardInput.WriteAsync(input);
                await process.StandardInput.FlushAsync();
            }

            async Task WaitForEditorAsync(string expected)
            {
                try
                {
                    await screen.WaitForFrameAsync(frame =>
                        string.Equals(EditorInputLine(frame), expected, StringComparison.Ordinal)).WaitAsync(timeout.Token);
                }
                catch (OperationCanceledException)
                {
                    throw new InvalidOperationException($"Timed out waiting for editor input '{expected}'. " +
                        $"Decoded input was '{EditorInputLine(screen.LastFrame)}'. Current screen:\n{screen.LastFrame}");
                }
            }

            await SendAsync("/mod");
            await WaitForEditorAsync("/mod");
            await SendAsync("\t");
            await WaitForEditorAsync("/model");
            await SendAsync("\t");
            await WaitForEditorAsync("/models");
            await SendAsync("\u001b[Z");
            await WaitForEditorAsync("/model");

            await SendAsync("\u0015");
            await WaitForEditorAsync("");
            await SendAsync("/thi");
            await WaitForEditorAsync("/thi");
            await SendAsync("\t");
            await WaitForEditorAsync("/thinking");
            await SendAsync("\r");
            var selector = await screen.WaitForFrameAsync(frame =>
                frame.Contains("Thinking Level", StringComparison.Ordinal) &&
                frame.Contains("medium", StringComparison.Ordinal)).WaitAsync(timeout.Token);

            Assert.DoesNotContain("Complete input", selector);
            await SendAsync("\u001b");
            await screen.WaitForFrameAsync(frame => !frame.Contains("Thinking Level", StringComparison.Ordinal) &&
                EditorInputLine(frame) == "").WaitAsync(timeout.Token);
            await SendAsync("/quit\r");
            process.StandardInput.Close();
            await process.WaitForExitAsync(timeout.Token);
            var terminalOutput = await stdout;

            Assert.Equal(0, process.ExitCode);
            Assert.Contains("Thinking Level", terminalOutput);
            Assert.DoesNotContain("Complete input", terminalOutput);
            Assert.DoesNotContain("Agent error", terminalOutput);
            Assert.DoesNotContain("Exception:", await stderr);
        }
        finally
        {
            if (process is { HasExited: false }) process.Kill(entireProcessTree: true);
            process?.Dispose();
            Directory.Delete(cwd, recursive: true);
        }
    }

    [Fact]
    public async Task ShiftTabCyclesThinkingAndNextRequestUsesTheNewLevelThroughLinuxPty()
    {
        if (!OperatingSystem.IsLinux() || !File.Exists("/usr/bin/script")) return;
        var cwd = Path.Combine(Path.GetTempPath(), "pisharp-thinking-cycle-pty-" + Guid.NewGuid().ToString("N"));
        var agentDirectory = Path.Combine(cwd, "agent");
        Directory.CreateDirectory(agentDirectory);
        using var listener = StartLoopbackListener(out var port);
        using var serverTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var requestBody = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var responseReady = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseResponse = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var server = Task.Run(async () =>
        {
            var request = await listener.GetContextAsync().WaitAsync(serverTimeout.Token);
            Assert.Equal("/v1/responses", request.Request.Url?.AbsolutePath);
            using var reader = new StreamReader(request.Request.InputStream);
            requestBody.TrySetResult(await reader.ReadToEndAsync(serverTimeout.Token));
            request.Response.ContentType = "text/event-stream";
            request.Response.KeepAlive = false;
            await using var writer = new StreamWriter(request.Response.OutputStream);
            await writer.WriteAsync("data: {\"type\":\"response.created\",\"response\":{\"id\":\"resp_cycle\",\"object\":\"response\",\"created_at\":1,\"model\":\"fixture-model\",\"status\":\"in_progress\",\"output\":[]}}\n\n");
            await writer.FlushAsync();
            responseReady.TrySetResult();
            await releaseResponse.Task.WaitAsync(serverTimeout.Token);
            await writer.WriteAsync("data: {\"type\":\"response.output_text.delta\",\"item_id\":\"msg_cycle\",\"output_index\":0,\"content_index\":0,\"delta\":\"SHIFT_TAB_HIGH_OK\"}\n\n");
            await writer.WriteAsync("data: {\"type\":\"response.completed\",\"response\":{\"id\":\"resp_cycle\",\"object\":\"response\",\"created_at\":1,\"model\":\"fixture-model\",\"status\":\"completed\",\"output\":[]}}\n\n");
            await writer.WriteAsync("data: [DONE]\n\n");
            await writer.FlushAsync();
            request.Response.Close();
        });
        Process? process = null;
        try
        {
            await WriteThinkingFixtureSettingsAsync(agentDirectory);
            await UserSettingsWriter.SetAsync(Path.Combine(agentDirectory, "settings.json"),
                "terminal.showTerminalProgress", "true", userScope: true);
            var modelsPath = Path.Combine(agentDirectory, "models.json");
            var models = await File.ReadAllTextAsync(modelsPath);
            await File.WriteAllTextAsync(modelsPath,
                models.Replace("http://127.0.0.1:1/v1", $"http://127.0.0.1:{port}/v1", StringComparison.Ordinal));
            process = StartThinkingFixtureProcess(cwd, agentDirectory);
            var screen = new PtyScreenCapture(process);
            var stdout = screen.ReadCompletion;
            var stderr = process.StandardError.ReadToEndAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            await screen.InputReady.WaitAsync(timeout.Token);
            await screen.WaitForFrameAsync(frame => frame.Split('\n').Any(line =>
                line.Contains("fixture-model", StringComparison.Ordinal) &&
                line.Contains("medium", StringComparison.Ordinal))).WaitAsync(timeout.Token);

            await process.StandardInput.WriteAsync("\u001b[Z");
            await process.StandardInput.FlushAsync();
            var highFooter = await screen.WaitForFrameAsync(frame => frame.Split('\n').Any(line =>
                line.Contains("fixture-model • high", StringComparison.Ordinal))).WaitAsync(timeout.Token);
            Assert.Contains("fixture-model • high", highFooter, StringComparison.Ordinal);

            await process.StandardInput.WriteAsync("request after shift tab\r");
            await process.StandardInput.FlushAsync();
            var requestJson = await requestBody.Task.WaitAsync(timeout.Token);
            using var parsedRequest = JsonDocument.Parse(requestJson);
            Assert.Equal("high", parsedRequest.RootElement.GetProperty("reasoning").GetProperty("effort").GetString());
            await responseReady.Task.WaitAsync(timeout.Token);
            const string progressStarted = "\u001b]9;4;3\u0007";
            const string progressCleared = "\u001b]9;4;0\u0007";
            async Task WaitForOutputAsync(string expected)
            {
                while (!screen.CapturedOutput.Contains(expected, StringComparison.Ordinal))
                    await Task.Delay(10, timeout.Token);
            }

            await WaitForOutputAsync(progressStarted);
            Assert.DoesNotContain(progressCleared, screen.CapturedOutput);
            releaseResponse.TrySetResult();
            await screen.WaitForFrameAsync(frame => frame.Contains("SHIFT_TAB_HIGH_OK", StringComparison.Ordinal) &&
                !frame.Contains("Enter steers · follow-up queues · Escape aborts", StringComparison.Ordinal) &&
                !frame.Contains("Working", StringComparison.Ordinal) &&
                frame.Split('\n').Any(line => line.Contains("fixture-model • high", StringComparison.Ordinal)))
                .WaitAsync(timeout.Token);
            await WaitForOutputAsync(progressCleared);

            await process.StandardInput.WriteAsync("/quit\r");
            await process.StandardInput.FlushAsync();
            process.StandardInput.Close();
            await process.WaitForExitAsync(timeout.Token);
            await server.WaitAsync(timeout.Token);
            Assert.Equal(0, process.ExitCode);
            Assert.DoesNotContain("Agent error", await stdout);
            Assert.DoesNotContain("Exception:", await stderr);
        }
        finally
        {
            releaseResponse.TrySetResult();
            serverTimeout.Cancel();
            if (process is { HasExited: false }) process.Kill(entireProcessTree: true);
            process?.Dispose();
            try { await server; }
            catch (OperationCanceledException) { }
            catch (HttpListenerException) { }
            Directory.Delete(cwd, recursive: true);
        }
    }

    [Fact]
    public async Task InvalidDirectThinkingLevelPreservesActiveValueThroughLinuxPty()
    {
        if (!OperatingSystem.IsLinux() || !File.Exists("/usr/bin/script")) return;
        var cwd = Path.Combine(Path.GetTempPath(), "pisharp-thinking-invalid-pty-" + Guid.NewGuid().ToString("N"));
        var agentDirectory = Path.Combine(cwd, "agent");
        Directory.CreateDirectory(agentDirectory);
        await WriteThinkingFixtureSettingsAsync(agentDirectory);
        Process? process = null;
        try
        {
            process = StartThinkingFixtureProcess(cwd, agentDirectory);
            var screen = new PtyScreenCapture(process);
            var stdout = screen.ReadCompletion;
            var stderr = process.StandardError.ReadToEndAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            await screen.InputReady.WaitAsync(timeout.Token);

            async Task SendAsync(string input)
            {
                await process.StandardInput.WriteAsync(input);
                await process.StandardInput.FlushAsync();
            }

            async Task WaitForOutputAsync(string expected)
            {
                while (!screen.CapturedOutput.Contains(expected, StringComparison.Ordinal))
                    await Task.Delay(10, timeout.Token);
            }

            await SendAsync("/thinking xhigh\r");
            await WaitForOutputAsync("Session error: Thinking level 'xhigh' is not supported");
            var invalidThinkingFrame = screen.LastFrame;
            await SendAsync("/thinking\r");
            await screen.WaitForFrameAsync(frame => frame.Contains("Thinking Level", StringComparison.Ordinal) &&
                frame.Split('\n').Any(line => line.Contains('>') && line.Contains("medium", StringComparison.Ordinal)))
                .WaitAsync(timeout.Token);
            await SendAsync("\u001b");
            await screen.WaitForFrameAsync(frame => !frame.Contains("Thinking Level", StringComparison.Ordinal) &&
                frame.Split('\n').Any(line => line.Contains("fixture-model", StringComparison.Ordinal) &&
                    line.Contains("medium", StringComparison.Ordinal))).WaitAsync(timeout.Token);
            await SendAsync("/quit\r");
            process.StandardInput.Close();
            await process.WaitForExitAsync(timeout.Token);

            Assert.Equal(1, process.ExitCode);
            _ = await stdout;
            Assert.Contains("Available levels: off,", invalidThinkingFrame, StringComparison.Ordinal);
            Assert.Contains("minimal, low, medium, high.", invalidThinkingFrame, StringComparison.Ordinal);
            Assert.DoesNotContain("Unhandled exception", await stderr);
        }
        finally
        {
            if (process is { HasExited: false }) process.Kill(entireProcessTree: true);
            process?.Dispose();
            Directory.Delete(cwd, recursive: true);
        }
    }

    [Fact]
    public async Task ThinkingSelectorSelectsCancelsAndPersistsModelDefaultThroughLinuxPty()
    {
        if (!OperatingSystem.IsLinux() || !File.Exists("/usr/bin/script")) return;
        var cwd = Path.Combine(Path.GetTempPath(), "pisharp-thinking-selector-pty-" + Guid.NewGuid().ToString("N"));
        var agentDirectory = Path.Combine(cwd, "agent");
        Directory.CreateDirectory(agentDirectory);
        await WriteThinkingFixtureSettingsAsync(agentDirectory);
        Process? process = null;
        try
        {
            process = StartThinkingFixtureProcess(cwd, agentDirectory);
            var screen = new PtyScreenCapture(process);
            var stdout = screen.ReadCompletion;
            var stderr = process.StandardError.ReadToEndAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(25));
            await screen.InputReady.WaitAsync(timeout.Token);

            async Task SendAsync(string input)
            {
                await process.StandardInput.WriteAsync(input);
                await process.StandardInput.FlushAsync();
            }

            async Task WaitForOutputAsync(string expected)
            {
                while (!screen.CapturedOutput.Contains(expected, StringComparison.Ordinal))
                    await Task.Delay(10, timeout.Token);
            }

            Task<string> WaitForIdleThinkingAsync(string level) => screen.WaitForFrameAsync(frame =>
                !frame.Contains("Thinking Level", StringComparison.Ordinal) &&
                frame.Split('\n').Any(line => line.Contains("fixture-model", StringComparison.Ordinal) &&
                    line.Contains(level, StringComparison.Ordinal)));

            async Task<string> WaitForSelectedThinkingLevelAsync(string level)
            {
                try
                {
                    return await screen.WaitForFrameAsync(frame =>
                        frame.Contains("Thinking Level", StringComparison.Ordinal) &&
                        frame.Split('\n').Any(line => line.Contains('>') &&
                            line.Contains(level, StringComparison.Ordinal)))
                        .WaitAsync(timeout.Token);
                }
                catch (OperationCanceledException)
                {
                    throw new InvalidOperationException($"Timed out waiting for selected thinking level '{level}'. Current screen:\n{screen.LastFrame}");
                }
            }

            await SendAsync("/thinking\r");
            var initialSelector = await WaitForSelectedThinkingLevelAsync("medium");
            Assert.DoesNotContain("xhigh", initialSelector);
            Assert.DoesNotContain("max", initialSelector);
            await SendAsync("\u001b[B");
            await WaitForSelectedThinkingLevelAsync("high");
            await SendAsync("\u0003");
            await WaitForIdleThinkingAsync("medium").WaitAsync(timeout.Token);

            await SendAsync("/thinking\r");
            await WaitForSelectedThinkingLevelAsync("medium");
            await SendAsync("\u001b[B");
            await WaitForSelectedThinkingLevelAsync("high");
            await SendAsync("\r");
            await WaitForIdleThinkingAsync("high").WaitAsync(timeout.Token);

            await SendAsync("/thinking\r");
            await WaitForSelectedThinkingLevelAsync("high");
            await SendAsync("\u001b");
            await WaitForIdleThinkingAsync("high").WaitAsync(timeout.Token);

            await SendAsync("/thinking\r");
            await WaitForSelectedThinkingLevelAsync("high");
            await SendAsync("\u0013");
            await screen.WaitForFrameAsync(frame => !frame.Contains("Thinking Level", StringComparison.Ordinal) &&
                frame.Split('\n').Any(line => line.Contains("fixture-model", StringComparison.Ordinal) &&
                    line.Contains("high", StringComparison.Ordinal))).WaitAsync(timeout.Token);
            await WaitForOutputAsync("Saved thinking default for fixture/fixture-model: high.");
            var savedSettings = await UserSettings.LoadAsync(agentDirectory, _ => null);
            Assert.Equal("high", savedSettings.GetModelThinkingLevel("fixture", "fixture-model"));

            await SendAsync("/quit\r");
            process.StandardInput.Close();
            await process.WaitForExitAsync(timeout.Token);
            Assert.Equal(0, process.ExitCode);
            Assert.DoesNotContain("Agent error", await stdout);
            Assert.DoesNotContain("Exception:", await stderr);
        }
        finally
        {
            if (process is { HasExited: false }) process.Kill(entireProcessTree: true);
            process?.Dispose();
        }

        Process? restarted = null;
        try
        {
            restarted = StartThinkingFixtureProcess(cwd, agentDirectory);
            var screen = new PtyScreenCapture(restarted);
            var stdout = screen.ReadCompletion;
            var stderr = restarted.StandardError.ReadToEndAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            await screen.InputReady.WaitAsync(timeout.Token);
            await screen.WaitForFrameAsync(frame => frame.Split('\n').Any(line =>
                line.Contains("fixture-model", StringComparison.Ordinal) && line.Contains("high", StringComparison.Ordinal)))
                .WaitAsync(timeout.Token);
            await restarted.StandardInput.WriteAsync("/quit\r");
            restarted.StandardInput.Close();
            await restarted.WaitForExitAsync(timeout.Token);
            Assert.Equal(0, restarted.ExitCode);
            Assert.DoesNotContain("Agent error", await stdout);
            Assert.DoesNotContain("Exception:", await stderr);
        }
        finally
        {
            if (restarted is { HasExited: false }) restarted.Kill(entireProcessTree: true);
            restarted?.Dispose();
            Directory.Delete(cwd, recursive: true);
        }
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

    private static async Task WriteThinkingFixtureSettingsAsync(string agentDirectory)
    {
        await File.WriteAllTextAsync(Path.Combine(agentDirectory, "settings.json"), """
            {
              "defaultThinkingLevel": "medium"
            }
            """);
        await File.WriteAllTextAsync(Path.Combine(agentDirectory, "models.json"), """
            {
              "providers": {
                "fixture": {
                  "baseUrl": "http://127.0.0.1:1/v1",
                  "apiKey": "fixture-key",
                  "models": [
                    { "id": "fixture-model", "api": "openai-responses", "reasoning": true }
                  ]
                }
              }
            }
            """);
    }

    [Fact]
    public async Task ScopedModelsSelectorAppliesSessionEditsAndPersistsReorderedScopeThroughLinuxPty()
    {
        if (!OperatingSystem.IsLinux() || !File.Exists("/usr/bin/script")) return;
        var root = Path.Combine(Path.GetTempPath(), "pisharp-scoped-models-pty-" + Guid.NewGuid().ToString("N"));
        var agent = Path.Combine(root, "agent");
        Directory.CreateDirectory(agent);
        await File.WriteAllTextAsync(Path.Combine(agent, "models.json"), """
            {"providers":{"fixture":{"baseUrl":"https://fixture.test/v1","apiKeyEnv":"PISHARP_SCOPED_KEY","models":[
              {"id":"model-a","name":"Model A","reasoning":true},
              {"id":"model-b","name":"Model B","reasoning":true}
            ]}}}
            """);
        await File.WriteAllTextAsync(Path.Combine(agent, "settings.json"),
            "{\"shellCommandPrefix\":\"preserved\",\"enabledModels\":[\"fixture/model-a\",\"fixture/model-b\"]}\n");

        Process? process = null;
        Process? restoredProcess = null;
        try
        {
            process = StartThinkingFixtureProcess(root, agent);
            var screen = new PtyScreenCapture(process, rows: 24, columns: 100);
            var stdout = screen.ReadCompletion;
            var stderr = process.StandardError.ReadToEndAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));
            try { await screen.InputReady.WaitAsync(timeout.Token); }
            catch (OperationCanceledException)
            {
                var stdoutTail = screen.CapturedOutput;
                if (!process.HasExited) process.Kill(entireProcessTree: true);
                var errorText = await stderr;
                throw new InvalidOperationException($"Scoped-model selector PTY did not become interactive. Exit={process.ExitCode}. stdout: {stdoutTail[^Math.Min(2000, stdoutTail.Length)..]} stderr: {errorText}");
            }
            await process.StandardInput.WriteAsync("/scoped-models\r");
            await process.StandardInput.FlushAsync();
            await screen.WaitForFrameAsync(frame => frame.Contains("Scoped Models", StringComparison.Ordinal))
                .WaitAsync(timeout.Token);

            await process.StandardInput.WriteAsync("fixture/model-b");
            await process.StandardInput.FlushAsync();
            await screen.WaitForFrameAsync(frame => frame.Contains("Search: fixture/model-b", StringComparison.Ordinal))
                .WaitAsync(timeout.Token);
            await process.StandardInput.WriteAsync("\r");
            await process.StandardInput.FlushAsync();
            await process.StandardInput.WriteAsync("\u001b");
            await process.StandardInput.FlushAsync();
            await screen.WaitForFrameAsync(frame => !frame.Contains("Scoped Models", StringComparison.Ordinal) &&
                EditorInputLine(frame).Length == 0).WaitAsync(timeout.Token);

            using (var unchanged = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(agent, "settings.json"))))
                Assert.Equal(new[] { "fixture/model-a", "fixture/model-b" }, unchanged.RootElement.GetProperty("enabledModels")
                    .EnumerateArray().Select(item => item.GetString()));

            await process.StandardInput.WriteAsync("/scoped-models\r");
            await process.StandardInput.FlushAsync();
            await screen.WaitForFrameAsync(frame => frame.Contains("Scoped Models", StringComparison.Ordinal))
                .WaitAsync(timeout.Token);
            await process.StandardInput.WriteAsync("fixture/model-a");
            await process.StandardInput.FlushAsync();
            var reopened = await screen.WaitForFrameAsync(frame => frame.Contains("Search: fixture/model-a", StringComparison.Ordinal) &&
                frame.Contains("✓ model-a [fixture]", StringComparison.Ordinal)).WaitAsync(timeout.Token);
            Assert.Contains("✓ model-a [fixture]", reopened, StringComparison.Ordinal);
            await process.StandardInput.WriteAsync("\u0010");
            await process.StandardInput.FlushAsync();
            await process.StandardInput.WriteAsync("\u001b[1;3B");
            await process.StandardInput.FlushAsync();
            await process.StandardInput.WriteAsync("\u0018");
            await process.StandardInput.FlushAsync();
            await process.StandardInput.WriteAsync("\u0001");
            await process.StandardInput.FlushAsync();
            await process.StandardInput.WriteAsync("\u0013");
            await process.StandardInput.FlushAsync();

            var settingsPath = Path.Combine(agent, "settings.json");
            using (var savedWait = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token))
            {
                savedWait.CancelAfter(TimeSpan.FromSeconds(8));
                while (!File.Exists(settingsPath))
                    await Task.Delay(30, savedWait.Token);
            }
            var scopeSaved = false;
            try
            {
                while (!scopeSaved)
                {
                    using var savedDocument = JsonDocument.Parse(await File.ReadAllTextAsync(settingsPath, timeout.Token));
                    scopeSaved = savedDocument.RootElement.TryGetProperty("enabledModels", out var enabledModels) &&
                        enabledModels.ValueKind == JsonValueKind.Array && enabledModels.EnumerateArray()
                            .Select(item => item.GetString()).SequenceEqual(new[] { "fixture/model-b", "fixture/model-a" });
                    if (!scopeSaved) await Task.Delay(30, timeout.Token);
                }
            }
            catch (OperationCanceledException)
            {
                var stdoutTail = screen.CapturedOutput;
                if (!process.HasExited) process.Kill(entireProcessTree: true);
                var errorText = await stderr;
                throw new InvalidOperationException($"Scoped-model selection was not saved. Screen: {screen.LastFrame}. stdout: {stdoutTail[^Math.Min(2000, stdoutTail.Length)..]} stderr: {errorText}");
            }
            var saved = await UserSettings.LoadAsync(agent, _ => null, timeout.Token);
            Assert.Equal("preserved", saved.ShellCommandPrefix);
            Assert.Equal(new[] { "fixture/model-b", "fixture/model-a" }, saved.EnabledModels);

            await process.StandardInput.WriteAsync("\u001b");
            await process.StandardInput.FlushAsync();
            await screen.WaitForFrameAsync(frame => frame.Contains(
                    $"Scoped models: {saved.EnabledModels!.Count} model references", StringComparison.Ordinal))
                .WaitAsync(timeout.Token);
            await process.StandardInput.WriteAsync("/quit\r");
            process.StandardInput.Close();
            await process.WaitForExitAsync(timeout.Token);
            await stdout.WaitAsync(timeout.Token);

            Assert.Equal(0, process.ExitCode);
            Assert.DoesNotContain("Exception:", await stderr, StringComparison.Ordinal);

            restoredProcess = StartThinkingFixtureProcess(root, agent);
            var restoredScreen = new PtyScreenCapture(restoredProcess, rows: 24, columns: 100);
            var restoredStdout = restoredScreen.ReadCompletion;
            var restoredStderr = restoredProcess.StandardError.ReadToEndAsync();
            await restoredScreen.InputReady.WaitAsync(timeout.Token);
            await restoredProcess.StandardInput.WriteAsync("/scoped-models\r");
            await restoredProcess.StandardInput.FlushAsync();
            var restoredOrder = await restoredScreen.WaitForFrameAsync(frame =>
                frame.Contains("Scoped Models", StringComparison.Ordinal) &&
                frame.Contains("model-b [fixture]", StringComparison.Ordinal) &&
                frame.Contains("model-a [fixture]", StringComparison.Ordinal)).WaitAsync(timeout.Token);
            Assert.True(restoredOrder.IndexOf("model-b [fixture]", StringComparison.Ordinal) <
                restoredOrder.IndexOf("model-a [fixture]", StringComparison.Ordinal));
            await restoredProcess.StandardInput.WriteAsync("fixture/model-a");
            await restoredProcess.StandardInput.FlushAsync();
            var restoredSelection = await restoredScreen.WaitForFrameAsync(frame =>
                frame.Contains("Search: fixture/model-a", StringComparison.Ordinal) &&
                frame.Contains("model-a [fixture]", StringComparison.Ordinal)).WaitAsync(timeout.Token);
            Assert.Contains("✓ model-a [fixture]", restoredSelection, StringComparison.Ordinal);
            await restoredProcess.StandardInput.WriteAsync("\u001b");
            await restoredProcess.StandardInput.FlushAsync();
            await restoredScreen.WaitForFrameAsync(frame => !frame.Contains("Scoped Models", StringComparison.Ordinal))
                .WaitAsync(timeout.Token);
            await restoredProcess.StandardInput.WriteAsync("/quit\r");
            restoredProcess.StandardInput.Close();
            await restoredProcess.WaitForExitAsync(timeout.Token);
            await restoredStdout.WaitAsync(timeout.Token);
            Assert.Equal(0, restoredProcess.ExitCode);
            Assert.DoesNotContain("Exception:", await restoredStderr, StringComparison.Ordinal);
        }
        finally
        {
            if (process is { HasExited: false }) process.Kill(entireProcessTree: true);
            if (restoredProcess is { HasExited: false }) restoredProcess.Kill(entireProcessTree: true);
            process?.Dispose();
            restoredProcess?.Dispose();
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task SettingsPickerSetsAndRemovesPerModelThinkingDefaultsThroughLinuxPty()
    {
        // Pinned Pi b06c1f2cf1dea6af8748ddeee18c23587d2cb7c6 exposes per-model levels and clear in settings-selector.ts.
        if (!OperatingSystem.IsLinux() || !File.Exists("/usr/bin/script")) return;
        var root = Path.Combine(Path.GetTempPath(), "pisharp-model-thinking-settings-pty-" + Guid.NewGuid().ToString("N"));
        var agent = Path.Combine(root, "agent");
        Directory.CreateDirectory(agent);
        Process? process = null;
        try
        {
            await WriteThinkingFixtureSettingsAsync(agent);
            await File.WriteAllTextAsync(Path.Combine(agent, "settings.json"), """
                {"defaultThinkingLevel":"medium","modelThinkingLevels":{"fixture/fixture-model":"low"}}
                """);
            process = StartThinkingFixtureProcess(root, agent);
            var screen = new PtyScreenCapture(process, rows: 24, columns: 100);
            var stdout = screen.ReadCompletion;
            var stderr = process.StandardError.ReadToEndAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));
            await screen.InputReady.WaitAsync(timeout.Token);

            async Task SendAsync(string input)
            {
                await process.StandardInput.WriteAsync(input);
                await process.StandardInput.FlushAsync();
            }

            async Task OpenModelThinkingSettingsAsync(bool rootPicker)
            {
                if (rootPicker)
                {
                    await SendAsync("/settings\r");
                    await screen.WaitForFrameAsync(frame => frame.Contains("Settings · User settings", StringComparison.Ordinal))
                        .WaitAsync(timeout.Token);
                    await SendAsync(string.Concat(Enumerable.Repeat("\u001b[B", 17)));
                    await screen.WaitForFrameAsync(frame => frame.Contains("Settings · User settings", StringComparison.Ordinal) &&
                        frame.Contains("Default thinking per model", StringComparison.Ordinal)).WaitAsync(timeout.Token);
                }
                await SendAsync("\r");
                try
                {
                    await screen.WaitForFrameAsync(frame => frame.Contains("Default Thinking · Model", StringComparison.Ordinal) &&
                        frame.Contains("fixture-model [fixture]", StringComparison.Ordinal)).WaitAsync(timeout.Token);
                }
                catch (OperationCanceledException)
                {
                    throw new InvalidOperationException($"Per-model thinking picker did not open. Last frame:\n{screen.LastFrame}");
                }
                await SendAsync("\r");
                await screen.WaitForFrameAsync(frame => frame.Contains("Thinking Default · fixture/fixture-model", StringComparison.Ordinal))
                    .WaitAsync(timeout.Token);
            }

            await OpenModelThinkingSettingsAsync(rootPicker: true);
            await SendAsync("\u001b[B\u001b[B");
            await screen.WaitForFrameAsync(frame => frame.Contains("Thinking Default · fixture/fixture-model", StringComparison.Ordinal) &&
                frame.Split('\n').Any(line => line.Contains("> ", StringComparison.Ordinal) &&
                    line.Contains("high", StringComparison.Ordinal))).WaitAsync(timeout.Token);
            await SendAsync("\r");
            await screen.WaitForFrameAsync(frame => frame.Contains("Settings · User settings", StringComparison.Ordinal) &&
                frame.Contains("Saved thinking default for fixture/fixture-model: high.", StringComparison.Ordinal))
                .WaitAsync(timeout.Token);
            await SendAsync("\u001b");
            await screen.WaitForFrameAsync(frame => frame.Contains("Settings closed.", StringComparison.Ordinal))
                .WaitAsync(timeout.Token);
            await screen.WaitForFrameAsync(frame => frame.Split('\n').Any(line =>
                line.Contains("fixture-model • high", StringComparison.Ordinal))).WaitAsync(timeout.Token);
            Assert.Equal("high", (await UserSettings.LoadAsync(agent, _ => null, timeout.Token))
                .GetModelThinkingLevel("fixture", "fixture-model"));
            await OpenModelThinkingSettingsAsync(rootPicker: true);
            await SendAsync("\u001b[B");
            await screen.WaitForFrameAsync(frame => frame.Contains("Thinking Default · fixture/fixture-model", StringComparison.Ordinal) &&
                frame.Split('\n').Any(line => line.Contains("> ", StringComparison.Ordinal) &&
                    line.Contains("Clear override", StringComparison.Ordinal))).WaitAsync(timeout.Token);
            await SendAsync("\r");
            using (var clearTimeout = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token))
            {
                clearTimeout.CancelAfter(TimeSpan.FromSeconds(10));
                var cleared = false;
                while (!cleared)
                {
                    var currentSettings = await UserSettings.LoadAsync(agent, _ => null, clearTimeout.Token);
                    cleared = currentSettings.ModelThinkingLevels is null;
                    if (!cleared)
                        await Task.Delay(30, clearTimeout.Token);
                }
            }
            await screen.WaitForFrameAsync(frame => frame.Contains("Settings · User settings", StringComparison.Ordinal))
                .WaitAsync(timeout.Token);
            await SendAsync("\u001b");
            await screen.WaitForFrameAsync(frame => frame.Contains("Settings closed.", StringComparison.Ordinal))
                .WaitAsync(timeout.Token);
            await screen.WaitForFrameAsync(frame => frame.Split('\n').Any(line =>
                line.Contains("fixture-model • medium", StringComparison.Ordinal))).WaitAsync(timeout.Token);
            Assert.Null((await UserSettings.LoadAsync(agent, _ => null, timeout.Token)).ModelThinkingLevels);

            await SendAsync("/quit\r");
            process.StandardInput.Close();
            await process.WaitForExitAsync(timeout.Token);
            await stdout.WaitAsync(timeout.Token);
            Assert.Equal(0, process.ExitCode);
            Assert.DoesNotContain("Exception:", await stderr, StringComparison.Ordinal);
        }
        finally
        {
            if (process is { HasExited: false }) process.Kill(entireProcessTree: true);
            process?.Dispose();
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task SavedPerModelThinkingDefaultChangesTheNextProviderRequestThroughLinuxPty()
    {
        // The selected settings value must reach the next physical OpenAI Responses request.
        if (!OperatingSystem.IsLinux() || !File.Exists("/usr/bin/script")) return;
        var root = Path.Combine(Path.GetTempPath(), "pisharp-model-thinking-wire-pty-" + Guid.NewGuid().ToString("N"));
        var agent = Path.Combine(root, "agent");
        Directory.CreateDirectory(agent);
        using var listener = StartLoopbackListener(out var port);
        using var serverTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        var wireRequest = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var server = Task.Run(async () =>
        {
            var request = await listener.GetContextAsync().WaitAsync(serverTimeout.Token);
            Assert.Equal("/v1/responses", request.Request.Url?.AbsolutePath);
            using (var reader = new StreamReader(request.Request.InputStream))
                wireRequest.TrySetResult(await reader.ReadToEndAsync(serverTimeout.Token));
            request.Response.ContentType = "text/event-stream";
            request.Response.KeepAlive = false;
            await using var writer = new StreamWriter(request.Response.OutputStream);
            await writer.WriteAsync("data: {\"type\":\"response.created\",\"response\":{\"id\":\"settings_wire\",\"object\":\"response\",\"created_at\":1,\"model\":\"fixture-model\",\"status\":\"in_progress\",\"output\":[]}}\n\n");
            await writer.WriteAsync("data: {\"type\":\"response.output_text.delta\",\"item_id\":\"settings_wire_msg\",\"output_index\":0,\"content_index\":0,\"delta\":\"SETTINGS_WIRE_OK\"}\n\n");
            await writer.WriteAsync("data: {\"type\":\"response.completed\",\"response\":{\"id\":\"settings_wire\",\"object\":\"response\",\"created_at\":1,\"model\":\"fixture-model\",\"status\":\"completed\",\"output\":[]}}\n\n");
            await writer.WriteAsync("data: [DONE]\n\n");
            await writer.FlushAsync();
            request.Response.Close();
        });
        Process? process = null;
        try
        {
            await WriteThinkingFixtureSettingsAsync(agent);
            var modelsPath = Path.Combine(agent, "models.json");
            var models = await File.ReadAllTextAsync(modelsPath);
            await File.WriteAllTextAsync(modelsPath,
                models.Replace("http://127.0.0.1:1/v1", $"http://127.0.0.1:{port}/v1", StringComparison.Ordinal));
            await File.WriteAllTextAsync(Path.Combine(agent, "settings.json"), """
                {"defaultThinkingLevel":"medium","modelThinkingLevels":{"fixture/fixture-model":"low"}}
                """);
            await UserSettingsWriter.SetAsync(Path.Combine(agent, "settings.json"),
                "terminal.showTerminalProgress", "true", userScope: true);
            process = StartThinkingFixtureProcess(root, agent);
            var screen = new PtyScreenCapture(process, rows: 24, columns: 100);
            var stdout = screen.ReadCompletion;
            var stderr = process.StandardError.ReadToEndAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
            await screen.InputReady.WaitAsync(timeout.Token);

            async Task SendAsync(string input)
            {
                await process.StandardInput.WriteAsync(input);
                await process.StandardInput.FlushAsync();
            }

            await SendAsync("/settings\r");
            await screen.WaitForFrameAsync(frame => frame.Contains("Settings · User settings", StringComparison.Ordinal))
                .WaitAsync(timeout.Token);
            await SendAsync(string.Concat(Enumerable.Repeat("\u001b[B", 17)));
            await screen.WaitForFrameAsync(frame => frame.Contains("Settings · User settings", StringComparison.Ordinal) &&
                frame.Contains("Default thinking per model", StringComparison.Ordinal)).WaitAsync(timeout.Token);
            await SendAsync("\r");
            await screen.WaitForFrameAsync(frame => frame.Contains("Default Thinking · Model", StringComparison.Ordinal) &&
                frame.Contains("fixture-model [fixture]", StringComparison.Ordinal)).WaitAsync(timeout.Token);
            await SendAsync("\r");
            await screen.WaitForFrameAsync(frame => frame.Contains("Thinking Default · fixture/fixture-model", StringComparison.Ordinal))
                .WaitAsync(timeout.Token);
            await SendAsync("\u001b[B\u001b[B");
            await screen.WaitForFrameAsync(frame => frame.Contains("Thinking Default · fixture/fixture-model", StringComparison.Ordinal) &&
                frame.Split('\n').Any(line => line.Contains("> ", StringComparison.Ordinal) &&
                    line.Contains("high", StringComparison.Ordinal))).WaitAsync(timeout.Token);
            await SendAsync("\r");
            await screen.WaitForFrameAsync(frame => frame.Contains("Settings · User settings", StringComparison.Ordinal) &&
                frame.Contains("Saved thinking default for fixture/fixture-model: high.", StringComparison.Ordinal))
                .WaitAsync(timeout.Token);
            await SendAsync("\u001b");
            await screen.WaitForFrameAsync(frame => frame.Contains("Settings closed.", StringComparison.Ordinal))
                .WaitAsync(timeout.Token);
            await screen.WaitForFrameAsync(frame => frame.Split('\n').Any(line =>
                line.Contains("fixture-model • high", StringComparison.Ordinal))).WaitAsync(timeout.Token);

            await SendAsync("request after settings change\r");
            using (var requestBody = JsonDocument.Parse(await wireRequest.Task.WaitAsync(timeout.Token)))
                Assert.Equal("high", requestBody.RootElement.GetProperty("reasoning").GetProperty("effort").GetString());
            const string progressStarted = "\u001b]9;4;3\u0007";
            const string progressCleared = "\u001b]9;4;0\u0007";
            async Task WaitForOutputAsync(string expected)
            {
                while (!screen.CapturedOutput.Contains(expected, StringComparison.Ordinal))
                    await Task.Delay(10, timeout.Token);
            }

            await WaitForOutputAsync(progressStarted);
            await screen.WaitForFrameAsync(frame => frame.Contains("SETTINGS_WIRE_OK", StringComparison.Ordinal) &&
                frame.Split('\n').Any(line => line.Contains("fixture-model • high", StringComparison.Ordinal)))
                .WaitAsync(timeout.Token);
            await WaitForOutputAsync(progressCleared);

            await SendAsync("/quit\r");
            process.StandardInput.Close();
            await process.WaitForExitAsync(timeout.Token);
            await stdout.WaitAsync(timeout.Token);
            await server.WaitAsync(timeout.Token);
            Assert.Equal(0, process.ExitCode);
            Assert.DoesNotContain("Exception:", await stderr, StringComparison.Ordinal);
        }
        finally
        {
            serverTimeout.Cancel();
            listener.Close();
            try { await server; }
            catch (Exception error) when (error is OperationCanceledException or HttpListenerException or ObjectDisposedException) { }
            if (process is { HasExited: false }) process.Kill(entireProcessTree: true);
            process?.Dispose();
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ScopedModelsSelectorAddsModelsDiscoveredWhileOpenThroughLinuxPty()
    {
        // Pinned Pi b06c1f2cf1dea6af8748ddeee18c23587d2cb7c6 refreshes the selector catalog while it remains open.
        if (!OperatingSystem.IsLinux() || !File.Exists("/usr/bin/script")) return;
        var root = Path.Combine(Path.GetTempPath(), "pisharp-scoped-model-refresh-pty-" + Guid.NewGuid().ToString("N"));
        var agent = Path.Combine(root, "agent");
        Directory.CreateDirectory(agent);
        using var listener = StartLoopbackListener(out var port);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var refreshRequest = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseRefresh = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var server = Task.Run(async () =>
        {
            for (var index = 0; index < 2; index++)
            {
                var request = await listener.GetContextAsync().WaitAsync(timeout.Token);
                Assert.Equal("/v1/models", request.Request.Url?.AbsolutePath);
                Assert.Equal("Bearer scoped-key", request.Request.Headers["Authorization"]);
                if (index == 1)
                {
                    refreshRequest.TrySetResult();
                    await releaseRefresh.Task.WaitAsync(timeout.Token);
                }
                var body = index == 0
                    ? """{"data":[{"id":"model-a","name":"Model A","reasoning":true}]}"""
                    : """{"data":[{"id":"model-a","name":"Model A","reasoning":true},{"id":"refresh-probe-zebra-482","name":"Refresh Probe","reasoning":true}]}""";
                var bytes = Encoding.UTF8.GetBytes(body);
                request.Response.ContentType = "application/json";
                request.Response.ContentLength64 = bytes.Length;
                await request.Response.OutputStream.WriteAsync(bytes, timeout.Token);
                request.Response.Close();
            }
        });
        Process? process = null;
        try
        {
            var modelsJson = """
                {"providers":{"fixture":{"baseUrl":"http://127.0.0.1:0/v1","apiKeyEnv":"PISHARP_SCOPED_KEY","models":[{"id":"fixture-model","reasoning":true}]}}}
                """;
            await File.WriteAllTextAsync(Path.Combine(agent, "models.json"),
                modelsJson.Replace("127.0.0.1:0/v1", $"127.0.0.1:{port}/v1", StringComparison.Ordinal));
            process = StartThinkingFixtureProcess(root, agent, offline: false, scopedKey: "scoped-key");
            var screen = new PtyScreenCapture(process, rows: 24, columns: 100);
            var stdout = screen.ReadCompletion;
            var stderr = process.StandardError.ReadToEndAsync();
            await screen.InputReady.WaitAsync(timeout.Token);
            await process.StandardInput.WriteAsync("/scoped-models\r");
            await process.StandardInput.FlushAsync();
            await screen.WaitForFrameAsync(frame => frame.Contains("Scoped Models", StringComparison.Ordinal))
                .WaitAsync(timeout.Token);
            await refreshRequest.Task.WaitAsync(timeout.Token);
            await process.StandardInput.WriteAsync("fixture/refresh-probe-zebra-482");
            await process.StandardInput.FlushAsync();
            await screen.WaitForFrameAsync(frame => frame.Contains("Search: fixture/refresh-probe-zebra-482", StringComparison.Ordinal) &&
                frame.Contains("No matching items", StringComparison.Ordinal)).WaitAsync(timeout.Token);

            releaseRefresh.TrySetResult();
            var refreshed = await screen.WaitForFrameAsync(frame =>
                frame.Contains("Search: fixture/refresh-probe-zebra-482", StringComparison.Ordinal) &&
                frame.Contains("refresh-probe-zebra-482 [fixture]", StringComparison.Ordinal)).WaitAsync(timeout.Token);
            Assert.Contains("refresh-probe-zebra-482 [fixture]", refreshed, StringComparison.Ordinal);

            await process.StandardInput.WriteAsync("\u001b");
            await process.StandardInput.FlushAsync();
            await screen.WaitForFrameAsync(frame => !frame.Contains("Scoped Models", StringComparison.Ordinal))
                .WaitAsync(timeout.Token);
            await process.StandardInput.WriteAsync("/quit\r");
            process.StandardInput.Close();
            await process.WaitForExitAsync(timeout.Token);
            await stdout.WaitAsync(timeout.Token);
            await server.WaitAsync(timeout.Token);
            Assert.Equal(0, process.ExitCode);
            Assert.DoesNotContain("Exception:", await stderr, StringComparison.Ordinal);
        }
        finally
        {
            releaseRefresh.TrySetResult();
            if (process is { HasExited: false }) process.Kill(entireProcessTree: true);
            process?.Dispose();
            Directory.Delete(root, recursive: true);
        }
    }

    private static Process StartThinkingFixtureProcess(string workingDirectory, string agentDirectory,
        bool offline = true, string? scopedKey = null)
    {
        var start = new ProcessStartInfo("/usr/bin/script")
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        start.ArgumentList.Add("-q");
        start.ArgumentList.Add("-e");
        start.ArgumentList.Add("-c");
        start.ArgumentList.Add($"stty rows 30 cols 100; exec dotnet {ShellQuote(typeof(CliArguments).Assembly.Location)} --provider fixture --model fixture-model --no-session{(offline ? " --offline" : "")} --no-tools");
        start.ArgumentList.Add("/dev/null");
        foreach (var name in new[] { "OPENAI_API_KEY", "PISHARP_API_KEY", "PISHARP_BASE_URL", "PISHARP_AUTH_PATH", "PISHARP_MODELS_PATH" })
            start.Environment.Remove(name);
        start.Environment["PISHARP_AGENT_DIR"] = agentDirectory;
        if (scopedKey is not null) start.Environment["PISHARP_SCOPED_KEY"] = scopedKey;
        return Process.Start(start) ?? throw new InvalidOperationException("Could not start the interactive PTY fixture.");
    }

    private static Process StartSettingsFixtureProcess(string workingDirectory, string agentDirectory, int columns)
    {
        var start = new ProcessStartInfo("/usr/bin/script")
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        start.ArgumentList.Add("-q");
        start.ArgumentList.Add("-e");
        start.ArgumentList.Add("-c");
        start.ArgumentList.Add($"stty rows 24 cols {columns}; exec dotnet {ShellQuote(typeof(CliArguments).Assembly.Location)} --local --offline --no-session --no-tools");
        start.ArgumentList.Add("/dev/null");
        foreach (var name in new[] { "OPENAI_API_KEY", "PISHARP_API_KEY", "PISHARP_BASE_URL", "PISHARP_AUTH_PATH", "PISHARP_MODELS_PATH" })
            start.Environment.Remove(name);
        start.Environment["PISHARP_AGENT_DIR"] = agentDirectory;
        return Process.Start(start) ?? throw new InvalidOperationException("Could not start the settings PTY fixture.");
    }

    private static string EditorInputLine(string frame)
    {
        return frame.Split('\n').Select(line => line.Trim())
            .LastOrDefault(line => line.StartsWith('@') ||
                line.StartsWith('/') && line.Count(character => character == '/') == 1 &&
                !line.Contains("   ", StringComparison.Ordinal)) ?? "";
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
