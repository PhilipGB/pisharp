using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using PiSharp.Cli;

namespace PiSharp.Tests;

[CollectionDefinition("App control PTY", DisableParallelization = true)]
public sealed class AppControlPtyCollection;

[Collection("App control PTY")]
public sealed class AppControlPtyTests
{
    [Fact]
    public async Task EscapeCtrlCAndCtrlDFollowIdleEditorSemanticsThroughLinuxPty()
    {
        if (!OperatingSystem.IsLinux() || !File.Exists("/usr/bin/script")) return;
        var root = TemporaryDirectory("app-control-idle");
        var settingsBefore = Path.Combine(root, "stty-before");
        var settingsAfter = Path.Combine(root, "stty-after");
        Process? process = null;
        try
        {
            process = Start(root, settingsBefore, settingsAfter);
            var screen = new PtyScreenCapture(process, 24, 80);
            var stdout = screen.ReadCompletion;
            var stderr = process.StandardError.ReadToEndAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            await screen.InputReady.WaitAsync(timeout.Token);

            async Task SendAsync(string input)
            {
                await process.StandardInput.WriteAsync(input);
                await process.StandardInput.FlushAsync();
            }

            async Task WaitForAsync(Func<string, bool> predicate, string description)
            {
                try { await screen.WaitForFrameAsync(predicate).WaitAsync(timeout.Token); }
                catch (OperationCanceledException)
                {
                    throw new InvalidOperationException($"{description} timed out. Current frame:\n{screen.LastFrame}");
                }
            }

            await WaitForAsync(frame => frame.Contains("❯", StringComparison.Ordinal) ||
                frame.Split('\n').Count(line => line.TrimStart().StartsWith('─')) >= 2, "idle prompt");
            await SendAsync("draft text");
            await WaitForAsync(frame => frame.Contains("draft text", StringComparison.Ordinal), "draft typed");

            await SendAsync("\u001b");
            await Task.Delay(250);
            await WaitForAsync(frame => frame.Contains("draft text", StringComparison.Ordinal),
                "idle Escape preserves nonempty draft");

            await SendAsync("\u0001\u0004");
            await WaitForAsync(frame => frame.Contains("raft text", StringComparison.Ordinal) &&
                !frame.Contains("draft text", StringComparison.Ordinal), "Ctrl+D forward-deletes a character");

            await SendAsync("\u001b");
            await Task.Delay(250);
            await WaitForAsync(frame => frame.Contains("raft text", StringComparison.Ordinal),
                "idle Escape still preserves the edited draft");

            await SendAsync("\u0003");
            await WaitForAsync(frame => !frame.Contains("raft text", StringComparison.Ordinal), "Ctrl+C clears the editor");
            await SendAsync("after clear");
            await WaitForAsync(frame => frame.Contains("after clear", StringComparison.Ordinal),
                "typing works after Ctrl+C");

            await SendAsync("\u0004");
            await WaitForAsync(frame => frame.Contains("after clear", StringComparison.Ordinal),
                "Ctrl+D in a nonempty editor does not exit");
            await SendAsync("x");
            await WaitForAsync(frame => frame.Contains("after clearx", StringComparison.Ordinal),
                "editor remains usable after nonempty Ctrl+D");

            await SendAsync("\u0015/hot\t");
            await WaitForAsync(frame => frame.Contains("/hotkeys", StringComparison.Ordinal),
                "completion is visible");
            await SendAsync("\u001b");
            await Task.Delay(250);
            await WaitForAsync(frame => frame.Contains("/hotkeys", StringComparison.Ordinal),
                "Escape dismisses completion without clearing the editor");
            await SendAsync("\u0015");

            await SendAsync("\u0003\u0003");
            await process.WaitForExitAsync(timeout.Token);
            var output = await stdout;

            Assert.Equal(0, process.ExitCode);
            Assert.Equal(await File.ReadAllTextAsync(settingsBefore), await File.ReadAllTextAsync(settingsAfter));
            Assert.DoesNotContain("Agent error", output);
            Assert.DoesNotContain("Exception:", await stderr);
            RecordEvidence("pty-app-idle-controls", new
            {
                exitCode = process.ExitCode,
                termiosRestored = true,
                escapePreservedDraft = true,
                ctrlDForwardDeleted = true,
                ctrlCCleared = true
            });
        }
        finally { Cleanup(process, root); }
    }

    [Theory]
    [InlineData("tree", "Conversation tree", "app.session.tree")]
    [InlineData("fork", "Fork from message", "app.session.fork")]
    [InlineData("none", null, null)]
    public async Task DoubleEscapeHonoursTheConfiguredActionAndReturnsToTheEditor(
        string setting, string? overlayTitle, string? expectedAction)
    {
        if (!OperatingSystem.IsLinux() || !File.Exists("/usr/bin/script")) return;
        var root = TemporaryDirectory("app-control-escape-" + setting);
        var agent = Path.Combine(root, "agent");
        Directory.CreateDirectory(agent);
        var sessionPath = Path.Combine(root, "app-control.jsonl");
        await File.WriteAllTextAsync(sessionPath, SessionJsonl(root));
        await File.WriteAllTextAsync(Path.Combine(agent, "settings.json"),
            JsonSerializer.Serialize(new Dictionary<string, object>
            {
                ["quietStartup"] = true,
                ["doubleEscapeAction"] = setting
            }));
        await File.WriteAllTextAsync(Path.Combine(agent, "models.json"), """
            {"providers":{"fixture":{"baseUrl":"http://127.0.0.1:1/v1","apiKey":"fixture-key",
              "models":[{"id":"fixture-model","api":"openai-responses"}]}}}
            """);
        Process? process = null;
        try
        {
            process = StartWithArguments(root, agent,
                $"--offline --no-tools --session {ShellQuote(sessionPath)}");
            var screen = new PtyScreenCapture(process, 24, 80);
            var stdout = screen.ReadCompletion;
            var stderr = process.StandardError.ReadToEndAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            await screen.InputReady.WaitAsync(timeout.Token);

            async Task SendAsync(string input)
            {
                await process.StandardInput.WriteAsync(input);
                await process.StandardInput.FlushAsync();
            }

            async Task WaitForAsync(Func<string, bool> predicate, string description)
            {
                try { await screen.WaitForFrameAsync(predicate).WaitAsync(timeout.Token); }
                catch (OperationCanceledException)
                {
                    throw new InvalidOperationException($"{description} timed out. Current frame:\n{screen.LastFrame}");
                }
            }

            await WaitForAsync(frame => frame.Contains("ASSISTANT_2", StringComparison.Ordinal),
                "restored session transcript");

            if (overlayTitle is not null)
            {
                await SendAsync("\u001b");
                await Task.Delay(700);
                await SendAsync("\u001b");
                await Task.Delay(250);
                await WaitForAsync(frame => !frame.Contains(overlayTitle, StringComparison.Ordinal),
                    "Escape outside the double-press window does not open the selector");
                await Task.Delay(600);
                await SendAsync("\u001b");
                await SendAsync("\u001b");
                await WaitForAsync(frame => frame.Contains(overlayTitle, StringComparison.Ordinal),
                    "double Escape opens the configured selector");
                await SendAsync("\u001b");
                await WaitForAsync(frame => !frame.Contains(overlayTitle, StringComparison.Ordinal),
                    "Escape dismisses the selector");
            }
            else
            {
                await SendAsync("\u001b\u001b");
                await Task.Delay(300);
                await WaitForAsync(frame => !frame.Contains("Conversation tree", StringComparison.Ordinal) &&
                    !frame.Contains("Fork from message", StringComparison.Ordinal),
                    "double Escape with none opens no selector");
            }

            await SendAsync("editor ready");
            await WaitForAsync(frame => frame.Contains("editor ready", StringComparison.Ordinal),
                "editor is usable after double Escape");
            await SendAsync("\u0015");
            await SendAsync("\u0003\u0003");
            await process.WaitForExitAsync(timeout.Token);
            var output = await stdout;
            Assert.Equal(0, process.ExitCode);
            Assert.DoesNotContain("Agent error", output);
            Assert.DoesNotContain("Exception:", await stderr);
            RecordEvidence("pty-app-double-escape-" + setting, new
            {
                exitCode = process.ExitCode,
                setting,
                expectedAction,
                singleEscapeOpened = false,
                selectorOpened = overlayTitle is not null,
                editorRestored = true
            });
        }
        finally { Cleanup(process, root); }
    }

    [Fact]
    public async Task EscapeCancelsInteractiveBashAndLeavesThePromptUsableThroughLinuxPty()
    {
        if (!OperatingSystem.IsLinux() || !File.Exists("/usr/bin/script")) return;
        var root = TemporaryDirectory("app-control-bash");
        var agent = Path.Combine(root, "agent");
        Directory.CreateDirectory(agent);
        await File.WriteAllTextAsync(Path.Combine(agent, "settings.json"), "{\"quietStartup\":true}");
        var marker = Path.Combine(root, "bash-finished");
        Process? process = null;
        try
        {
            process = StartWithArguments(root, agent, "--local --offline --no-session --no-tools");
            var screen = new PtyScreenCapture(process, 24, 80);
            var stdout = screen.ReadCompletion;
            var stderr = process.StandardError.ReadToEndAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(40));
            await screen.InputReady.WaitAsync(timeout.Token);

            async Task SendAsync(string input)
            {
                await process.StandardInput.WriteAsync(input);
                await process.StandardInput.FlushAsync();
            }

            async Task WaitForAsync(Func<string, bool> predicate, string description)
            {
                try { await screen.WaitForFrameAsync(predicate).WaitAsync(timeout.Token); }
                catch (OperationCanceledException)
                {
                    throw new InvalidOperationException($"{description} timed out. Current frame:\n{screen.LastFrame}");
                }
            }

            await SendAsync("!bash -c 'exec -a pisharp-app-control-child sleep 4; " +
                $"touch {ShellQuote(marker)}'\r");
            await WaitForAsync(frame => frame.Contains("pisharp-app-control-child", StringComparison.Ordinal),
                "bash command started");
            await Task.Delay(400);
            await SendAsync("\u001b");
            await Task.Delay(500);
            await SendAsync("after bash cancel");
            await WaitForAsync(frame => frame.Contains("after bash cancel", StringComparison.Ordinal),
                "prompt accepts input after Bash cancellation");
            await Task.Delay(4500);
            Assert.False(File.Exists(marker), "The cancelled Bash command continued to completion.");

            await SendAsync("\u0015!echo BASH_AFTER_OK\r");
            await WaitForAsync(frame => frame.Contains("BASH_AFTER_OK", StringComparison.Ordinal),
                "subsequent Bash command executes");
            await SendAsync("\u0015");
            await SendAsync("\u0003\u0003");
            await process.WaitForExitAsync(timeout.Token);
            var output = await stdout;
            Assert.Equal(0, process.ExitCode);
            Assert.DoesNotContain("Agent error", output);
            Assert.DoesNotContain("Exception:", await stderr);
            Assert.Empty(FindProcesses("pisharp-app-control-child"));
            RecordEvidence("pty-app-bash-cancel", new
            {
                exitCode = process.ExitCode,
                cancelledBeforeCompletion = !File.Exists(marker),
                survivingChildren = 0,
                promptResponsive = true,
                subsequentBashExecuted = true
            });
        }
        finally { Cleanup(process, root); }
    }

    [Theory]
    [InlineData("quit")]
    [InlineData("ctrl-d")]
    [InlineData("ctrl-c")]
    [InlineData("bash-cancel")]
    public async Task ShutdownPathsRestoreTheTerminalAndExitCleanlyThroughLinuxPty(string path)
    {
        if (!OperatingSystem.IsLinux() || !File.Exists("/usr/bin/script")) return;
        var root = TemporaryDirectory("app-control-shutdown-" + path);
        var settingsBefore = Path.Combine(root, "stty-before");
        var settingsAfter = Path.Combine(root, "stty-after");
        Process? process = null;
        try
        {
            process = Start(root, settingsBefore, settingsAfter);
            var screen = new PtyScreenCapture(process, 24, 80);
            var stdout = screen.ReadCompletion;
            var stderr = process.StandardError.ReadToEndAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(40));
            await screen.InputReady.WaitAsync(timeout.Token);

            async Task SendAsync(string input)
            {
                await process.StandardInput.WriteAsync(input);
                await process.StandardInput.FlushAsync();
            }

            async Task WaitForAsync(Func<string, bool> predicate, string description)
            {
                try { await screen.WaitForFrameAsync(predicate).WaitAsync(timeout.Token); }
                catch (OperationCanceledException)
                {
                    throw new InvalidOperationException($"{description} timed out. Current frame:\n{screen.LastFrame}");
                }
            }

            switch (path)
            {
                case "quit":
                    await SendAsync("/quit\r");
                    break;
                case "ctrl-d":
                    await SendAsync("\u0004");
                    break;
                case "ctrl-c":
                    await SendAsync("\u0003\u0003");
                    break;
                case "bash-cancel":
                    await SendAsync("!bash -c 'exec -a pisharp-app-control-child sleep 4'\r");
                    await WaitForAsync(frame => frame.Contains("pisharp-app-control-child", StringComparison.Ordinal),
                        "bash command started");
                    await Task.Delay(300);
                    await SendAsync("\u001b");
                    await Task.Delay(300);
                    await SendAsync("\u0003\u0003");
                    break;
            }

            await process.WaitForExitAsync(timeout.Token);
            var output = await stdout;

            Assert.Equal(0, process.ExitCode);
            Assert.Equal(await File.ReadAllTextAsync(settingsBefore), await File.ReadAllTextAsync(settingsAfter));
            Assert.DoesNotContain("Agent error", output);
            Assert.DoesNotContain("Exception:", await stderr);
            var activated = CountOccurrences(output, "\u001b[?2004h");
            var restored = CountOccurrences(output, "\u001b[?2004l");
            Assert.True(activated >= 1 && restored >= 1);
            Assert.True(output.LastIndexOf("\u001b[?2004l", StringComparison.Ordinal) >
                output.LastIndexOf("\u001b[?2004h", StringComparison.Ordinal));
            RecordEvidence("pty-app-shutdown-" + path, new
            {
                exitCode = process.ExitCode,
                path,
                termiosRestored = true,
                bracketedPasteActivated = activated,
                bracketedPasteRestored = restored
            });
        }
        finally { Cleanup(process, root); }
    }

    [Fact]
    public async Task EscapeDuringStreamingRestoresQueuedMessagesAndStartsNoExtraRequestThroughLinuxPty()
    {
        if (!OperatingSystem.IsLinux() || !File.Exists("/usr/bin/script")) return;
        var root = TemporaryDirectory("app-control-stream");
        var agent = Path.Combine(root, "agent");
        Directory.CreateDirectory(agent);
        await File.WriteAllTextAsync(Path.Combine(agent, "settings.json"), "{\"quietStartup\":true}");
        using var listener = StartLoopbackListener(out var port);
        await File.WriteAllTextAsync(Path.Combine(agent, "models.json"), """
            {"providers":{"fixture":{"baseUrl":"http://127.0.0.1:PORT/v1","apiKey":"fixture-key",
              "models":[{"id":"fixture-model","api":"openai-responses"}]}}}
            """.Replace("PORT", port.ToString(System.Globalization.CultureInfo.InvariantCulture),
                StringComparison.Ordinal));
        var requests = new List<string>();
        var releaseFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var serverTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(40));
        var server = Task.Run(async () =>
        {
            for (var index = 0; index < 2; index++)
            {
                HttpListenerContext request;
                try { request = await listener.GetContextAsync().WaitAsync(serverTimeout.Token); }
                catch (Exception error) when (error is OperationCanceledException or HttpListenerException or ObjectDisposedException)
                {
                    return;
                }
                using var reader = new StreamReader(request.Request.InputStream);
                var body = await reader.ReadToEndAsync(serverTimeout.Token);
                lock (requests) requests.Add(body);
                request.Response.ContentType = "text/event-stream";
                request.Response.KeepAlive = false;
                await using var writer = new StreamWriter(request.Response.OutputStream);
                try
                {
                    await writer.WriteAsync(
                        "data: {\"type\":\"response.created\",\"response\":{\"id\":\"resp_1\",\"object\":\"response\"," +
                        "\"created_at\":1,\"model\":\"fixture-model\",\"status\":\"in_progress\",\"output\":[]}}\n\n");
                    if (index == 0)
                    {
                        await writer.WriteAsync(
                            "data: {\"type\":\"response.output_text.delta\",\"item_id\":\"msg_0\",\"output_index\":0," +
                            "\"content_index\":0,\"delta\":\"STREAM_ONE\"}\n\n");
                        await writer.FlushAsync(serverTimeout.Token);
                        await releaseFirst.Task.WaitAsync(serverTimeout.Token);
                    }
                    else
                    {
                        await writer.WriteAsync(
                            "data: {\"type\":\"response.output_text.delta\",\"item_id\":\"msg_1\",\"output_index\":0," +
                            "\"content_index\":0,\"delta\":\"SECOND_REPLY\"}\n\n");
                        await writer.WriteAsync(
                            "data: {\"type\":\"response.completed\",\"response\":{\"id\":\"resp_1\",\"object\":\"response\"," +
                            "\"created_at\":1,\"model\":\"fixture-model\",\"status\":\"completed\"," +
                            "\"output\":[],\"usage\":{\"input_tokens\":1,\"output_tokens\":1,\"total_tokens\":2}}}\n\n");
                        await writer.FlushAsync(serverTimeout.Token);
                    }
                }
                catch (Exception error) when (error is IOException or ObjectDisposedException or OperationCanceledException)
                {
                    // The first response is abandoned by the cancellation under test.
                }
                try { request.Response.Close(); }
                catch (ObjectDisposedException) { }
            }
        });
        Process? process = null;
        try
        {
            process = StartWithArguments(root, agent, "--provider fixture --model fixture-model --offline --no-session --no-tools");
            var screen = new PtyScreenCapture(process, 24, 80);
            var stdout = screen.ReadCompletion;
            var stderr = process.StandardError.ReadToEndAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(40));
            await screen.InputReady.WaitAsync(timeout.Token);

            async Task SendAsync(string input)
            {
                await process.StandardInput.WriteAsync(input);
                await process.StandardInput.FlushAsync();
            }

            async Task WaitForAsync(Func<string, bool> predicate, string description)
            {
                try { await screen.WaitForFrameAsync(predicate).WaitAsync(timeout.Token); }
                catch (OperationCanceledException)
                {
                    throw new InvalidOperationException($"{description} timed out. Current frame:\n{screen.LastFrame}");
                }
            }

            await SendAsync("stream one\r");
            await WaitForAsync(frame => frame.Contains("STREAM_ONE", StringComparison.Ordinal) &&
                frame.Contains("Working", StringComparison.Ordinal), "streaming started");
            await SendAsync("steer text\r");
            await SendAsync("\u001b[13;3u");
            await SendAsync("follow text");
            await SendAsync("\u001b[13;3u");
            await SendAsync("\u001b[200~current draft\u001b[201~");
            await WaitForAsync(frame => frame.Contains("current draft", StringComparison.Ordinal), "draft during stream");
            await SendAsync("\u001b");
            await WaitForAsync(frame => frame.Contains("Operation aborted", StringComparison.Ordinal) &&
                !frame.Contains("Enter steers", StringComparison.Ordinal), "stream aborted");
            await WaitForAsync(frame => frame.Contains("steer text", StringComparison.Ordinal) &&
                frame.Contains("follow text", StringComparison.Ordinal) &&
                frame.Contains("current draft", StringComparison.Ordinal), "queued messages restored");

            releaseFirst.TrySetResult();
            await SendAsync("\r");
            await WaitForAsync(frame => frame.Contains("SECOND_REPLY", StringComparison.Ordinal) &&
                !frame.Contains("Working", StringComparison.Ordinal), "subsequent prompt completed");
            await SendAsync("\u0003\u0003");
            await process.WaitForExitAsync(timeout.Token);
            var output = await stdout;
            serverTimeout.Cancel();
            try { await server.WaitAsync(timeout.Token); } catch (OperationCanceledException) { }

            Assert.Equal(0, process.ExitCode);
            string[] captured;
            lock (requests) captured = requests.ToArray();
            Assert.Equal(2, captured.Length);
            var second = JsonDocument.Parse(captured[1]);
            var payload = second.RootElement;
            var messages = payload.TryGetProperty("input", out var input) ? input : payload.GetProperty("messages");
            var user = messages.EnumerateArray()
                .Last(message => message.GetProperty("role").GetString() == "user")
                .GetProperty("content");
            var text = user.ValueKind == JsonValueKind.String
                ? user.GetString()!
                : string.Join("\n", user.EnumerateArray()
                    .Select(part => part.TryGetProperty("text", out var value) ? value.GetString() : null)
                    .Where(value => value is not null));
            Assert.Equal("steer text\n\nfollow text\n\ncurrent draft", text);
            Assert.DoesNotContain("Agent error", output);
            Assert.DoesNotContain("Exception:", await stderr);
            RecordEvidence("pty-app-stream-cancel", new
            {
                exitCode = process.ExitCode,
                requests = captured.Length,
                restoredDraft = text,
                aborted = true,
                lateStreamCorruptedDraft = false
            });
        }
        finally
        {
            releaseFirst.TrySetResult();
            if (process is { HasExited: false }) process.Kill(entireProcessTree: true);
            process?.Dispose();
            listener.Stop();
            Directory.Delete(root, recursive: true);
        }
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
            catch (HttpListenerException) { }
        }
        throw new InvalidOperationException("Could not start the app-control provider listener.");
    }

    [Fact]
    public async Task RegularModeDoubleEscapeAfterBashEntryOpensTheTreeSelectorThroughLinuxPty()
    {
        if (!OperatingSystem.IsLinux() || !File.Exists("/usr/bin/script")) return;
        var root = TemporaryDirectory("app-control-escape-regular");
        var agent = Path.Combine(root, "agent");
        Directory.CreateDirectory(agent);
        var sessionPath = Path.Combine(root, "app-control.jsonl");
        await File.WriteAllTextAsync(sessionPath, SessionJsonl(root));
        await File.WriteAllTextAsync(Path.Combine(agent, "settings.json"),
            "{\"quietStartup\":true,\"tuiMode\":\"regular\",\"doubleEscapeAction\":\"tree\"}");
        await File.WriteAllTextAsync(Path.Combine(agent, "models.json"), """
            {"providers":{"fixture":{"baseUrl":"http://127.0.0.1:1/v1","apiKey":"fixture-key",
              "models":[{"id":"fixture-model","api":"openai-responses"}]}}}
            """);
        Process? process = null;
        try
        {
            process = StartWithArguments(root, agent,
                $"--offline --no-tools --session {ShellQuote(sessionPath)}");
            var screen = new PtyScreenCapture(process, 24, 80);
            var stdout = screen.ReadCompletion;
            var stderr = process.StandardError.ReadToEndAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            await screen.InputReady.WaitAsync(timeout.Token);

            async Task SendAsync(string input)
            {
                await process.StandardInput.WriteAsync(input);
                await process.StandardInput.FlushAsync();
            }

            async Task WaitForAsync(Func<string, bool> predicate, string description)
            {
                try { await screen.WaitForFrameAsync(predicate).WaitAsync(timeout.Token); }
                catch (OperationCanceledException)
                {
                    throw new InvalidOperationException($"{description} timed out. Current frame:\n{screen.LastFrame}");
                }
            }

            await WaitForAsync(frame => frame.Contains("ASSISTANT_2", StringComparison.Ordinal),
                "restored session transcript");
            await SendAsync("!echo BASH_ENTRY_SHOULD_NOT_RUN");
            await WaitForAsync(frame => frame.Contains("BASH_ENTRY_SHOULD_NOT_RUN", StringComparison.Ordinal),
                "bash-entry draft");
            await SendAsync("\u001b");
            await WaitForAsync(frame => !frame.Contains("BASH_ENTRY_SHOULD_NOT_RUN", StringComparison.Ordinal),
                "bash-entry clear");
            await SendAsync("\u001b[27u\u001b[27u");
            await WaitForAsync(frame => frame.Contains("Conversation tree", StringComparison.Ordinal),
                "regular-mode double Escape tree");
            await SendAsync("\u001b");
            await WaitForAsync(frame => !frame.Contains("Conversation tree", StringComparison.Ordinal),
                "tree dismissal");
            await SendAsync("\u0003\u0003");
            await process.WaitForExitAsync(timeout.Token);
            _ = await stdout;
            Assert.Equal(0, process.ExitCode);
            Assert.DoesNotContain("Exception:", await stderr);
        }
        finally { Cleanup(process, root); }
    }

    private static Process Start(string root, string settingsBefore, string settingsAfter)
    {
        var agent = Path.Combine(root, "agent");
        Directory.CreateDirectory(agent);
        File.WriteAllText(Path.Combine(agent, "settings.json"), "{\"quietStartup\":true}");
        return StartWithArguments(root, agent, "--local --offline --no-session --no-tools",
            $"stty rows 24 cols 80; stty -g > {ShellQuote(settingsBefore)}; " +
            $"dotnet {ShellQuote(typeof(CliArguments).Assembly.Location)} {{arguments}}; status=$?; " +
            $"stty -g > {ShellQuote(settingsAfter)}; exit $status");
    }

    private static Process StartWithArguments(string root, string agent, string arguments, string? shellCommand = null)
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
        start.ArgumentList.Add((shellCommand ?? $"stty rows 24 cols 80; exec dotnet {ShellQuote(typeof(CliArguments).Assembly.Location)} {{arguments}}")
            .Replace("{arguments}", arguments, StringComparison.Ordinal));
        start.ArgumentList.Add("/dev/null");
        foreach (var name in new[] { "OPENAI_API_KEY", "PISHARP_API_KEY", "PISHARP_BASE_URL", "PISHARP_MODEL", "PISHARP_SETTINGS_PATH" })
            start.Environment.Remove(name);
        start.Environment["PISHARP_AGENT_DIR"] = agent;
        return Process.Start(start) ?? throw new InvalidOperationException("Could not start the app-control PTY fixture.");
    }

    private static string SessionJsonl(string workingDirectory)
    {
        var lines = new List<string>
        {
            JsonSerializer.Serialize(new
            {
                type = "session",
                version = 3,
                id = "app-control",
                timestamp = "2025-01-01T00:00:00Z",
                cwd = workingDirectory
            })
        };
        string? parentId = null;
        for (var turn = 1; turn <= 2; turn++)
        {
            var userId = $"user-{turn}";
            lines.Add(JsonSerializer.Serialize(new
            {
                type = "message",
                id = userId,
                parentId,
                timestamp = "2025-01-01T00:00:01Z",
                message = new
                {
                    role = "user",
                    content = new[] { new { type = "text", text = $"USER_{turn}" } },
                    timestamp = 1735689601000L
                }
            }));
            var assistantId = $"assistant-{turn}";
            lines.Add(JsonSerializer.Serialize(new
            {
                type = "message",
                id = assistantId,
                parentId = userId,
                timestamp = "2025-01-01T00:00:02Z",
                message = new
                {
                    role = "assistant",
                    content = new[] { new { type = "text", text = $"ASSISTANT_{turn}" } },
                    api = "openai-responses",
                    provider = "fixture",
                    model = "fixture-model",
                    usage = new { input = 1, output = 1, cacheRead = 0, cacheWrite = 0, totalTokens = 2 },
                    stopReason = "stop",
                    timestamp = 1735689602000L
                }
            }));
            parentId = assistantId;
        }
        return string.Join('\n', lines) + "\n";
    }

    private static IReadOnlyList<string> FindProcesses(string marker)
    {
        try
        {
            var start = new ProcessStartInfo("pgrep")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            start.ArgumentList.Add("-f");
            start.ArgumentList.Add(marker);
            using var process = Process.Start(start);
            if (process is null) return [];
            var output = process.StandardOutput.ReadToEnd();
            process.WaitForExit(2000);
            return output.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        }
        catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return [];
        }
    }

    private static string TemporaryDirectory(string name)
    {
        var root = Path.Combine(Path.GetTempPath(), $"pisharp-{name}-pty-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static void Cleanup(Process? process, string root)
    {
        if (process is { HasExited: false }) process.Kill(entireProcessTree: true);
        process?.Dispose();
        Directory.Delete(root, recursive: true);
    }

    private static string ShellQuote(string value) => "'" + value.Replace("'", "'\\''", StringComparison.Ordinal) + "'";

    private static int CountOccurrences(string text, string value)
    {
        var count = 0;
        for (var index = 0; (index = text.IndexOf(value, index, StringComparison.Ordinal)) >= 0; index += value.Length)
            count++;
        return count;
    }

    private static void RecordEvidence(string id, object details)
    {
        var path = Environment.GetEnvironmentVariable("PISHARP_APP_CONTROL_EVIDENCE");
        if (string.IsNullOrWhiteSpace(path)) return;
        var implementation = Environment.GetEnvironmentVariable("PISHARP_IMPLEMENTATION_SHA") ?? "";
        var record = JsonSerializer.Serialize(new
        {
            id,
            implementation,
            platform = "linux",
            details
        });
        File.AppendAllText(path, record + Environment.NewLine);
    }
}
