using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using PiSharp.Cli;
using PiSharp.Cli.Tui;

namespace PiSharp.Tests;

[CollectionDefinition("Editor mutation PTY", DisableParallelization = true)]
public sealed class EditorMutationPtyCollection;

[Collection("Editor mutation PTY")]
public sealed class EditorMutationPtyTests
{
    private sealed class PtyScreenCapture(Process process, int rows = 24, int columns = 100)
    {
        private readonly object _gate = new();
        private readonly StringBuilder _output = new();
        private readonly List<(Func<string, bool> Predicate, TaskCompletionSource<string> Completion)> _waiters = [];
        private string _lastFrame = "";
        private readonly TaskCompletionSource _inputReady = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task InputReady => _inputReady.Task;
        public Task<string> ReadCompletion => ReadAsync(process);
        public string LastFrame { get { lock (_gate) return _lastFrame; } }
        public string CapturedOutput { get { lock (_gate) return _output.ToString(); } }

        public Task<string> WaitForFrameAsync(Func<string, bool> predicate)
        {
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
    public async Task MutationKeysReachPiEditorStatesThroughLinuxPty()
    {
        if (!OperatingSystem.IsLinux() || !File.Exists("/usr/bin/script")) return;
        var root = TemporaryDirectory("mutation-keys");
        Process? process = null;
        try
        {
            process = Start(root);
            var screen = new PtyScreenCapture(process);
            var stdout = screen.ReadCompletion;
            var stderr = process.StandardError.ReadToEndAsync();
            using var timeout = Timeout(30);
            await screen.InputReady.WaitAsync(timeout.Token);

            async Task SendAsync(string input)
            {
                await process.StandardInput.WriteAsync(input);
                await process.StandardInput.FlushAsync();
            }

            async Task ExpectAsync(string present, string absent, string description)
            {
                try
                {
                    await screen.WaitForFrameAsync(frame => frame.Contains(present, StringComparison.Ordinal) &&
                        !frame.Contains(absent, StringComparison.Ordinal)).WaitAsync(timeout.Token);
                }
                catch (OperationCanceledException)
                {
                    throw new InvalidOperationException($"{description} timed out. Last frame:\n{screen.LastFrame}");
                }
            }

            async Task ExpectAbsentAsync(string absent, string description)
            {
                try
                {
                    await screen.WaitForFrameAsync(frame => !frame.Contains(absent, StringComparison.Ordinal))
                        .WaitAsync(timeout.Token);
                }
                catch (OperationCanceledException)
                {
                    throw new InvalidOperationException($"{description} timed out. Last frame:\n{screen.LastFrame}");
                }
            }

            await SendAsync("one two three");
            await ExpectAsync("one two three", "\u0000", "typed draft");

            await SendAsync("\u0017");
            await ExpectAsync("one two ", "one two three", "ctrl+w deletes previous word");
            await SendAsync("\u0017");
            await ExpectAsync("one ", "one two ", "ctrl+w repeats");
            await SendAsync("\u0019");
            await ExpectAsync("one two three", "one two \n", "ctrl+y restores accumulated kill");

            await SendAsync("\u0001\u001bd");
            await ExpectAsync(" two three", "one two three", "alt+d deletes next word");
            await SendAsync("\u000b");
            await ExpectAbsentAsync("two three", "ctrl+k deletes to line end");
            await SendAsync("\u0019");
            await ExpectAsync(" two three", "Operation aborted", "ctrl+y restores the line kill");
            await SendAsync("\u001f");
            await ExpectAbsentAsync("two three", "ctrl-minus undoes the yank");
            await SendAsync("\u0019\u0015");
            await ExpectAbsentAsync("two three", "ctrl+u deletes to line start");
            await SendAsync("\u0019");
            await ExpectAsync(" two three", "Operation aborted", "ctrl+y restores the ctrl+u kill");

            await SendAsync("\u0001\u000b");
            await ExpectAbsentAsync("two three", "ctrl+a ctrl+k clear before backspace checks");
            await SendAsync("abc ghi\u007f");
            await ExpectAsync("abc gh", "abc ghi", "backspace deletes previous grapheme");
            await SendAsync("\u0001\u001b[3~");
            await ExpectAsync("bc gh", "abc gh", "delete removes next grapheme");

            await SendAsync("\u0001\u000b");
            await ExpectAbsentAsync("bc gh", "ctrl+a ctrl+k clear the draft");
            await SendAsync("undo words\u001f");
            await ExpectAsync("undo", "undo words", "ctrl-minus undoes a typed word group");
            await SendAsync("\u0005\u0004");
            await ExpectAsync("undo", "Operation aborted", "ctrl+d keeps a non-empty editor");
            await SendAsync("\u0001\u000b");
            await ExpectAbsentAsync("undo", "clear before quit");

            await SendAsync("/quit\r");
            process.StandardInput.Close();
            await process.WaitForExitAsync(timeout.Token);
            _ = await stdout;
            Assert.Equal(0, process.ExitCode);
            Assert.DoesNotContain("Agent error", await stdout);
            Assert.DoesNotContain("Exception:", await stderr);
            RecordEvidence("pty-mutation-keys", new { exitCode = process.ExitCode });
        }
        finally
        {
            if (process is { HasExited: false }) process.Kill(entireProcessTree: true);
            process?.Dispose();
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task KillRingYankAndYankPopMatchPiThroughLinuxPty()
    {
        if (!OperatingSystem.IsLinux() || !File.Exists("/usr/bin/script")) return;
        var root = TemporaryDirectory("mutation-kill-ring");
        Process? process = null;
        try
        {
            process = Start(root);
            var screen = new PtyScreenCapture(process);
            var stdout = screen.ReadCompletion;
            var stderr = process.StandardError.ReadToEndAsync();
            using var timeout = Timeout(30);
            await screen.InputReady.WaitAsync(timeout.Token);

            async Task SendAsync(string input)
            {
                await process.StandardInput.WriteAsync(input);
                await process.StandardInput.FlushAsync();
            }

            async Task ExpectAsync(string present, string absent, string description)
            {
                try
                {
                    await screen.WaitForFrameAsync(frame => frame.Contains(present, StringComparison.Ordinal) &&
                        !frame.Contains(absent, StringComparison.Ordinal)).WaitAsync(timeout.Token);
                }
                catch (OperationCanceledException)
                {
                    throw new InvalidOperationException($"{description} timed out. Last frame:\n{screen.LastFrame}");
                }
            }

            async Task ExpectAbsentAsync(string absent, string description)
            {
                try
                {
                    await screen.WaitForFrameAsync(frame => !frame.Contains(absent, StringComparison.Ordinal))
                        .WaitAsync(timeout.Token);
                }
                catch (OperationCanceledException)
                {
                    throw new InvalidOperationException($"{description} timed out. Last frame:\n{screen.LastFrame}");
                }
            }

            await SendAsync("one alpha");
            await ExpectAsync("one alpha", "\u0000", "typed draft");
            await SendAsync("\u0017");
            await ExpectAsync("one ", "one alpha", "first backward kill");
            await SendAsync("\u0001\u000b");
            await ExpectAbsentAsync("one alpha", "second kill clears the editor");
            await SendAsync("\u0019");
            await ExpectAbsentAsync("one alpha", "yank restores the most recent kill");
            await SendAsync("\u001by");
            await ExpectAsync("alpha", "one alpha", "alt+y pops to the older kill");
            await SendAsync("\u001by");
            await ExpectAsync("one ", "alpha", "alt+y cycles back to the newest kill");
            await SendAsync("x\u001by");
            await ExpectAsync("one x", "alpha", "alt+y is inert after unrelated typing");

            await SendAsync("\u0003/quit\r");
            process.StandardInput.Close();
            await process.WaitForExitAsync(timeout.Token);
            _ = await stdout;
            Assert.Equal(0, process.ExitCode);
            Assert.DoesNotContain("Agent error", await stdout);
            Assert.DoesNotContain("Exception:", await stderr);
            RecordEvidence("pty-kill-ring", new { exitCode = process.ExitCode });
        }
        finally
        {
            if (process is { HasExited: false }) process.Kill(entireProcessTree: true);
            process?.Dispose();
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ConfiguredMutationBindingsAndReloadApplyThroughLinuxPty()
    {
        if (!OperatingSystem.IsLinux() || !File.Exists("/usr/bin/script")) return;
        var root = TemporaryDirectory("mutation-bindings");
        Process? process = null;
        try
        {
            var agent = Path.Combine(root, "agent");
            var bindings = Path.Combine(agent, "keybindings.json");
            await File.WriteAllTextAsync(bindings, """
                {
                  "tui.editor.deleteWordBackward": "ctrl+n",
                  "tui.editor.yank": "alt+u",
                  "tui.editor.undo": "alt+o"
                }
                """);
            process = Start(root);
            var screen = new PtyScreenCapture(process);
            var stdout = screen.ReadCompletion;
            var stderr = process.StandardError.ReadToEndAsync();
            using var timeout = Timeout(30);
            await screen.InputReady.WaitAsync(timeout.Token);

            async Task SendAsync(string input)
            {
                await process.StandardInput.WriteAsync(input);
                await process.StandardInput.FlushAsync();
            }

            async Task ExpectAsync(string present, string absent, string description)
            {
                try
                {
                    await screen.WaitForFrameAsync(frame => frame.Contains(present, StringComparison.Ordinal) &&
                        !frame.Contains(absent, StringComparison.Ordinal)).WaitAsync(timeout.Token);
                }
                catch (OperationCanceledException)
                {
                    throw new InvalidOperationException($"{description} timed out. Last frame:\n{screen.LastFrame}");
                }
            }

            async Task ExpectAbsentAsync(string absent, string description)
            {
                try
                {
                    await screen.WaitForFrameAsync(frame => !frame.Contains(absent, StringComparison.Ordinal))
                        .WaitAsync(timeout.Token);
                }
                catch (OperationCanceledException)
                {
                    throw new InvalidOperationException($"{description} timed out. Last frame:\n{screen.LastFrame}");
                }
            }

            await SendAsync("gamma delta");
            await ExpectAsync("gamma delta", "\u0000", "typed draft");
            await SendAsync("\u000e");
            await ExpectAsync("gamma ", "gamma delta", "configured ctrl+n deletes the previous word");
            await SendAsync("\u001bu");
            await ExpectAsync("gamma delta", "gamma \n", "configured alt+u yanks the killed word");
            await SendAsync("\u001bo");
            await ExpectAsync("gamma ", "gamma delta", "configured alt+o undoes the yank");
            await SendAsync("\u0001\u000b");
            await ExpectAbsentAsync("gamma", "clear before reload");

            await File.WriteAllTextAsync(bindings, """
                {
                  "tui.editor.deleteWordBackward": "ctrl+q",
                  "tui.editor.yank": "alt+u",
                  "tui.editor.undo": "alt+o"
                }
                """);
            await SendAsync("/reload\r");
            await screen.WaitForFrameAsync(frame => frame.Contains("Reloaded keybindings", StringComparison.Ordinal))
                .WaitAsync(timeout.Token);

            await SendAsync("epsilon zeta");
            await ExpectAsync("epsilon zeta", "gamma", "draft after reload");
            await SendAsync("\u000e");
            await ExpectAsync("epsilon zeta", "gamma", "old binding no longer deletes");
            await SendAsync("\u0011");
            await ExpectAsync("epsilon ", "epsilon zeta", "new binding deletes the previous word");
            await SendAsync("\u001bo");
            await ExpectAsync("epsilon zeta", "epsilon \n", "configured undo restores after reload");

            await SendAsync("\u0003/quit\r");
            process.StandardInput.Close();
            await process.WaitForExitAsync(timeout.Token);
            _ = await stdout;
            Assert.Equal(0, process.ExitCode);
            Assert.DoesNotContain("Agent error", await stdout);
            Assert.DoesNotContain("Exception:", await stderr);
            RecordEvidence("pty-configured-reload", new { exitCode = process.ExitCode });
        }
        finally
        {
            if (process is { HasExited: false }) process.Kill(entireProcessTree: true);
            process?.Dispose();
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task WidgetAndApplicationShortcutsTakePrecedenceOverMutationKeysThroughLinuxPty()
    {
        if (!OperatingSystem.IsLinux() || !File.Exists("/usr/bin/script")) return;
        var root = TemporaryDirectory("mutation-precedence");
        Process? process = null;
        try
        {
            var agent = Path.Combine(root, "agent");
            await File.WriteAllTextAsync(Path.Combine(agent, "keybindings.json"),
                """
                {
                  "app.tools.expand": "ctrl+w"
                }
                """);
            process = Start(root);
            var screen = new PtyScreenCapture(process);
            var stdout = screen.ReadCompletion;
            var stderr = process.StandardError.ReadToEndAsync();
            using var timeout = Timeout(30);
            await screen.InputReady.WaitAsync(timeout.Token);

            async Task SendAsync(string input)
            {
                await process.StandardInput.WriteAsync(input);
                await process.StandardInput.FlushAsync();
            }

            await SendAsync("keepdraft words");
            await screen.WaitForFrameAsync(frame => frame.Contains("keepdraft words", StringComparison.Ordinal))
                .WaitAsync(timeout.Token);

            await SendAsync("\u0017");
            await screen.WaitForFrameAsync(frame => frame.Contains("keepdraft words", StringComparison.Ordinal) &&
                frame.Contains("Tool output:", StringComparison.Ordinal)).WaitAsync(timeout.Token);

            await SendAsync("\u001b[102;6u");
            await screen.WaitForFrameAsync(frame => frame.Contains("Find in transcript", StringComparison.Ordinal))
                .WaitAsync(timeout.Token);

            await SendAsync("find words");
            await screen.WaitForFrameAsync(frame => frame.Contains("find words", StringComparison.Ordinal))
                .WaitAsync(timeout.Token);
            await SendAsync("\u0017");
            await screen.WaitForFrameAsync(frame => frame.Contains("find ", StringComparison.Ordinal) &&
                !frame.Contains("find words", StringComparison.Ordinal)).WaitAsync(timeout.Token);
            await SendAsync("\u001b");
            await screen.WaitForFrameAsync(frame => frame.Contains("keepdraft words", StringComparison.Ordinal) &&
                !frame.Contains("find ", StringComparison.Ordinal)).WaitAsync(timeout.Token);

            await SendAsync("\u0003/quit\r");
            process.StandardInput.Close();
            await process.WaitForExitAsync(timeout.Token);
            _ = await stdout;
            Assert.Equal(0, process.ExitCode);
            Assert.DoesNotContain("Agent error", await stdout);
            Assert.DoesNotContain("Exception:", await stderr);
            RecordEvidence("pty-widget-precedence", new { exitCode = process.ExitCode });
        }
        finally
        {
            if (process is { HasExited: false }) process.Kill(entireProcessTree: true);
            process?.Dispose();
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task MutationSubmissionReachesTheProviderAndRestoresTheTerminalThroughLinuxPty()
    {
        if (!OperatingSystem.IsLinux() || !File.Exists("/usr/bin/script")) return;
        var root = TemporaryDirectory("mutation-submit");
        var agent = Path.Combine(root, "agent");
        var before = Path.Combine(root, "stty-before");
        var after = Path.Combine(root, "stty-after");
        using var listener = StartLoopbackListener(out var port);
        using var serverTimeout = Timeout(30);
        var requests = new List<string>();
        var requestReceived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelRequested = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var server = Task.Run(async () =>
        {
            for (var index = 0; index < 2; index++)
            {
                var request = await listener.GetContextAsync().WaitAsync(serverTimeout.Token);
                Assert.Equal("/v1/responses", request.Request.Url?.AbsolutePath);
                using var reader = new StreamReader(request.Request.InputStream);
                requests.Add(await reader.ReadToEndAsync(serverTimeout.Token));
                request.Response.ContentType = "text/event-stream";
                request.Response.KeepAlive = false;
                await using var writer = new StreamWriter(request.Response.OutputStream);
                if (index == 0)
                {
                    await writer.WriteAsync("data: {\"type\":\"response.created\",\"response\":{\"id\":\"resp_hold\",\"object\":\"response\",\"created_at\":1,\"model\":\"fixture-model\",\"status\":\"in_progress\",\"output\":[]}}\n\n");
                    await writer.FlushAsync();
                    requestReceived.TrySetResult();
                    try { await cancelRequested.Task.WaitAsync(serverTimeout.Token); }
                    catch (OperationCanceledException) { }
                    try { request.Response.Abort(); }
                    catch (ObjectDisposedException) { }
                    continue;
                }

                await writer.WriteAsync("data: {\"type\":\"response.created\",\"response\":{\"id\":\"resp_mutation\",\"object\":\"response\",\"created_at\":1,\"model\":\"fixture-model\",\"status\":\"in_progress\",\"output\":[]}}\n\n");
                await writer.WriteAsync("data: {\"type\":\"response.output_text.delta\",\"item_id\":\"msg_mutation\",\"output_index\":0,\"content_index\":0,\"delta\":\"MUTATION_SUBMIT_OK\"}\n\n");
                await writer.WriteAsync("data: {\"type\":\"response.completed\",\"response\":{\"id\":\"resp_mutation\",\"object\":\"response\",\"created_at\":1,\"model\":\"fixture-model\",\"status\":\"completed\",\"output\":[]}}\n\n");
                await writer.WriteAsync("data: [DONE]\n\n");
                await writer.FlushAsync();
                request.Response.Close();
                release.TrySetResult();
            }
        });
        Process? process = null;
        try
        {
            await File.WriteAllTextAsync(Path.Combine(agent, "models.json"), """
                {"providers":{"fixture":{"baseUrl":"http://127.0.0.1:PORT/v1","apiKey":"fixture-key",
                  "models":[{"id":"fixture-model","api":"openai-responses"}]}}}
                """.Replace("PORT", port.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    StringComparison.Ordinal));
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
            start.ArgumentList.Add($"stty rows 24 cols 100; stty -g > {ShellQuote(before)}; " +
                $"dotnet {ShellQuote(typeof(CliArguments).Assembly.Location)} --provider fixture --model fixture-model " +
                $"--no-session --offline --no-tools; status=$?; stty -g > {ShellQuote(after)}; exit $status");
            start.ArgumentList.Add("/dev/null");
            start.Environment["PISHARP_AGENT_DIR"] = agent;
            process = Process.Start(start);
            Assert.NotNull(process);

            var screen = new PtyScreenCapture(process, 24, 100);
            var stdout = screen.ReadCompletion;
            var stderr = process.StandardError.ReadToEndAsync();
            using var timeout = Timeout(45);
            await screen.InputReady.WaitAsync(timeout.Token);

            async Task SendAsync(string input)
            {
                await process.StandardInput.WriteAsync(input);
                await process.StandardInput.FlushAsync();
            }

            async Task WaitAsync(Func<string, bool> predicate, string description)
            {
                try { await screen.WaitForFrameAsync(predicate).WaitAsync(timeout.Token); }
                catch (OperationCanceledException)
                {
                    throw new InvalidOperationException($"{description} timed out. Last frame:\n{screen.LastFrame}");
                }
            }

            await SendAsync("cancel this\r");
            await requestReceived.Task.WaitAsync(timeout.Token);
            await WaitAsync(frame => frame.Contains("Working", StringComparison.Ordinal), "active run");
            await SendAsync("\u001b");
            cancelRequested.TrySetResult();
            await WaitAsync(frame => frame.Contains("Operation aborted", StringComparison.Ordinal) &&
                !frame.Contains("Enter steers", StringComparison.Ordinal), "cancelled run");

            await SendAsync("draft prompt");
            await SendAsync("\u0017");
            await WaitAsync(frame => frame.Contains("draft ", StringComparison.Ordinal) &&
                !frame.Contains("draft prompt", StringComparison.Ordinal), "mutation submission checkpoint");
            await SendAsync("\u0019");
            await WaitAsync(frame => frame.Contains("draft prompt", StringComparison.Ordinal), "yanked draft");
            await SendAsync("\u0001\u001bd");
            await WaitAsync(frame => frame.Contains(" prompt", StringComparison.Ordinal) &&
                !frame.Contains("draft prompt", StringComparison.Ordinal), "alt+d mutation");
            await SendAsync("\u0005\u0015");
            await WaitAsync(frame => !frame.Contains(" prompt", StringComparison.Ordinal), "ctrl+u mutation");
            await SendAsync("provider visible input");
            await WaitAsync(frame => frame.Contains("provider visible input", StringComparison.Ordinal),
                "final mutation draft");
            await SendAsync("\r");
            await release.Task.WaitAsync(timeout.Token);
            await WaitAsync(frame => frame.Contains("MUTATION_SUBMIT_OK", StringComparison.Ordinal),
                "provider response");

            await SendAsync("\u0003\u0003");
            process.StandardInput.Close();
            await process.WaitForExitAsync(timeout.Token);
            _ = await stdout;
            await server.WaitAsync(timeout.Token);

            Assert.Equal(0, process.ExitCode);
            Assert.Equal(2, requests.Count);
            Assert.Contains("cancel this", requests[0], StringComparison.Ordinal);
            Assert.Contains("provider visible input", requests[1], StringComparison.Ordinal);
            Assert.DoesNotContain("draft prompt", requests[1], StringComparison.Ordinal);
            Assert.Equal(await File.ReadAllTextAsync(before), await File.ReadAllTextAsync(after));
            Assert.DoesNotContain("Agent error", await stdout);
            Assert.DoesNotContain("Exception:", await stderr);
            RecordEvidence("pty-provider-lifecycle", new
            {
                exitCode = process.ExitCode,
                requests = requests.Count,
                providerText = "provider visible input",
                termiosRestored = true
            });
        }
        finally
        {
            release.TrySetResult();
            cancelRequested.TrySetResult();
            serverTimeout.Cancel();
            if (process is { HasExited: false }) process.Kill(entireProcessTree: true);
            process?.Dispose();
            try { await server; }
            catch (OperationCanceledException) { }
            catch (HttpListenerException) { }
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task CtrlDExitsAnEmptyEditorAndRestoresTheTerminalThroughLinuxPty()
    {
        if (!OperatingSystem.IsLinux() || !File.Exists("/usr/bin/script")) return;
        var root = TemporaryDirectory("mutation-ctrl-d");
        var before = Path.Combine(root, "stty-before");
        var after = Path.Combine(root, "stty-after");
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
            start.ArgumentList.Add($"stty rows 24 cols 100; stty -g > {ShellQuote(before)}; " +
                $"dotnet {ShellQuote(typeof(CliArguments).Assembly.Location)} --local --offline --no-session --no-tools; " +
                $"status=$?; stty -g > {ShellQuote(after)}; exit $status");
            start.ArgumentList.Add("/dev/null");
            start.Environment["PISHARP_AGENT_DIR"] = Path.Combine(root, "agent");
            process = Process.Start(start);
            Assert.NotNull(process);

            var screen = new PtyScreenCapture(process);
            var stdout = screen.ReadCompletion;
            var stderr = process.StandardError.ReadToEndAsync();
            using var timeout = Timeout(30);
            await screen.InputReady.WaitAsync(timeout.Token);

            await process.StandardInput.WriteAsync("\u0004");
            await process.StandardInput.FlushAsync();
            process.StandardInput.Close();
            await process.WaitForExitAsync(timeout.Token);
            _ = await stdout;

            Assert.Equal(0, process.ExitCode);
            Assert.Equal(await File.ReadAllTextAsync(before), await File.ReadAllTextAsync(after));
            Assert.DoesNotContain("Agent error", await stdout);
            Assert.DoesNotContain("Exception:", await stderr);
            RecordEvidence("pty-ctrl-d-exit", new { exitCode = process.ExitCode, termiosRestored = true });
        }
        finally
        {
            if (process is { HasExited: false }) process.Kill(entireProcessTree: true);
            process?.Dispose();
            Directory.Delete(root, recursive: true);
        }
    }


    private static readonly object EvidenceGate = new();

    private static void RecordEvidence(string id, object details)
    {
        var path = Environment.GetEnvironmentVariable("PISHARP_EDITOR_MUTATION_EVIDENCE");
        if (string.IsNullOrWhiteSpace(path)) return;
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
        var record = JsonSerializer.Serialize(new
        {
            id,
            implementation = Environment.GetEnvironmentVariable("PISHARP_IMPLEMENTATION_SHA") ?? "unknown",
            platform = OperatingSystem.IsLinux() ? "linux" : "other",
            details
        });
        lock (EvidenceGate) File.AppendAllText(path, record + "\n");
    }

    private static Process Start(string root)
    {
        var agent = Path.Combine(root, "agent");
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
        start.ArgumentList.Add($"stty rows 24 cols 100; exec dotnet {ShellQuote(typeof(CliArguments).Assembly.Location)} " +
            "--local --offline --no-session --no-tools");
        start.ArgumentList.Add("/dev/null");
        foreach (var name in new[] { "OPENAI_API_KEY", "PISHARP_API_KEY", "PISHARP_BASE_URL", "PISHARP_AUTH_PATH", "PISHARP_MODELS_PATH" })
            start.Environment.Remove(name);
        start.Environment["PISHARP_AGENT_DIR"] = agent;
        return Process.Start(start) ?? throw new InvalidOperationException("Could not start the mutation PTY fixture.");
    }

    private static string TemporaryDirectory(string name)
    {
        var root = Path.Combine(Path.GetTempPath(), $"pisharp-{name}-pty-" + Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(Path.Combine(root, "agent"));
        return root;
    }

    private static CancellationTokenSource Timeout(int seconds) => new(TimeSpan.FromSeconds(seconds));

    private static string ShellQuote(string value) => "'" + value.Replace("'", "'\"'\"'", StringComparison.Ordinal) + "'";

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
            catch (HttpListenerException)
            {
                listener.Close();
                if (attempt == 9) throw;
            }
        }
        throw new InvalidOperationException("Could not reserve a loopback port.");
    }
}
