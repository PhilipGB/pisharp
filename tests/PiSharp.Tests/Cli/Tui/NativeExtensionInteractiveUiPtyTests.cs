using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using PiSharp.Cli;
using PiSharp.Runtime.Resources;
using PiSharp.Tests.TestInfrastructure;

namespace PiSharp.Tests;

[Collection("Managed npm subprocesses")]
public sealed class NativeExtensionInteractiveUiPtyTests
{
    private sealed class PtyScreenCapture(Process process, int rows, int columns)
    {
        private readonly object _gate = new();
        private readonly StringBuilder _output = new();
        private readonly List<(Func<string, bool> Predicate, TaskCompletionSource<string> Completion)> _waiters = [];
        private readonly TaskCompletionSource _inputReady = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _rows = rows;
        private int _columns = columns;
        private string _lastFrame = string.Empty;

        public Task InputReady => _inputReady.Task;
        public string LastFrame { get { lock (_gate) return _lastFrame; } }
        public string CapturedOutput { get { lock (_gate) return _output.ToString(); } }

        public void SetSize(int rows, int columns)
        {
            lock (_gate)
            {
                _rows = rows;
                _columns = columns;
                var frames = TerminalOutputFrameReader.Read(_output.ToString(), _rows, _columns);
                if (frames.Count > 0) _lastFrame = frames[^1].Screen;
            }
        }

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

        public Task<string> StartReading() => ReadAsync();

        private async Task<string> ReadAsync()
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
                    var frames = TerminalOutputFrameReader.Read(captured, _rows, _columns);
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
            lock (_gate)
            {
                var captured = _output.ToString();
                if (!_inputReady.Task.IsCompleted)
                    _inputReady.TrySetException(new EndOfStreamException(
                        "The CLI exited before entering terminal input mode. Captured output:\n" + Tail(captured)));
                return captured;
            }
        }

        private static string Tail(string text) => text.Length <= 4000 ? text : text[^4000..];
    }

    [Fact]
    public async Task CompiledExtensionDashboardWorksThroughTuiAndCleansUpAfterReloadRemoval()
    {
        if (!OperatingSystem.IsLinux() || !File.Exists("/usr/bin/script")) return;

        var root = Path.Combine(Path.GetTempPath(), "pisharp-native-ui-pty-" + Guid.NewGuid().ToString("N"));
        var agentDirectory = Path.Combine(root, "agent");
        Directory.CreateDirectory(agentDirectory);
        var repositoryRoot = FindRepositoryRoot();
        var fixtureDirectory = Path.Combine(repositoryRoot, "tests", "fixtures",
            "NativeExtensionInteractiveUiFixture", "bin", BuildConfiguration(), "net10.0");
        var fixturePackagePath = CreateFixturePackage(root, fixtureDirectory);
        var packageStore = new NativeExtensionPackageStore(agentDirectory);
        var installedPackage = await packageStore.InstallAsync(root, fixturePackagePath, "user");
        await File.WriteAllTextAsync(Path.Combine(agentDirectory, "settings.json"), JsonSerializer.Serialize(new
        {
            nativeExtensions = new[]
            {
                new { id = installedPackage.Id, version = installedPackage.Version, source = installedPackage.Source }
            }
        }));

        var (listener, port) = TestLoopbackListener.Start();
        using var listenerLease = listener;
        using var serverTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        var requests = new List<string>();
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
                await writer.WriteAsync($"data: {{\"type\":\"response.created\",\"response\":{{\"id\":\"native_ui_{index}\",\"object\":\"response\",\"created_at\":1,\"model\":\"fixture-model\",\"status\":\"in_progress\",\"output\":[]}}}}\n\n");
                if (index == 0)
                {
                    await writer.WriteAsync("data: {\"type\":\"response.output_item.added\",\"output_index\":0,\"item\":{\"id\":\"fc_native_ui\",\"type\":\"function_call\",\"call_id\":\"call_native_ui\",\"name\":\"native_ui_echo\",\"arguments\":\"\"}}\n\n");
                    await writer.WriteAsync("data: {\"type\":\"response.function_call_arguments.done\",\"output_index\":0,\"item_id\":\"fc_native_ui\",\"arguments\":\"{\\\"value\\\":\\\"pty\\\"}\"}\n\n");
                    await writer.WriteAsync("data: {\"type\":\"response.output_item.done\",\"output_index\":0,\"item\":{\"id\":\"fc_native_ui\",\"type\":\"function_call\",\"call_id\":\"call_native_ui\",\"name\":\"native_ui_echo\",\"arguments\":\"{\\\"value\\\":\\\"pty\\\"}\"}}\n\n");
                }
                else
                    await writer.WriteAsync("data: {\"type\":\"response.output_text.delta\",\"item_id\":\"msg_native_ui\",\"output_index\":0,\"content_index\":0,\"delta\":\"NATIVE_UI_AGENT_OK\"}\n\n");
                await writer.WriteAsync($"data: {{\"type\":\"response.completed\",\"response\":{{\"id\":\"native_ui_{index}\",\"object\":\"response\",\"created_at\":1,\"model\":\"fixture-model\",\"status\":\"completed\",\"output\":[]}}}}\n\n");
                await writer.WriteAsync("data: [DONE]\n\n");
                await writer.FlushAsync();
                request.Response.Close();
            }
        });

        await File.WriteAllTextAsync(Path.Combine(agentDirectory, "models.json"),
            """{"providers":{"fixture":{"baseUrl":"http://127.0.0.1:PORT/v1","apiKeyEnv":"PISHARP_FIXTURE_KEY","models":[{"id":"fixture-model","api":"openai-responses","reasoning":true}]}}}"""
                .Replace("PORT", port.ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal));

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
            start.ArgumentList.Add($"stty rows 24 cols 100; exec dotnet {ShellQuote(typeof(CliArguments).Assembly.Location)} --provider fixture --model fixture-model --offline --no-session");
            start.ArgumentList.Add("/dev/null");
            foreach (var name in start.Environment.Keys.Where(name =>
                         name.StartsWith("PISHARP_", StringComparison.Ordinal)).ToArray())
                start.Environment.Remove(name);
            start.Environment.Remove("OPENAI_API_KEY");
            start.Environment.Remove("ANTHROPIC_API_KEY");
            start.Environment["PISHARP_AGENT_DIR"] = agentDirectory;
            start.Environment["PISHARP_SESSION_DIR"] = Path.Combine(root, "sessions");
            start.Environment["PISHARP_FIXTURE_KEY"] = "local-fixture-key";
            process = Process.Start(start) ?? throw new InvalidOperationException("Could not start the native UI PTY fixture.");
            var screen = new PtyScreenCapture(process, rows: 24, columns: 100);
            var outputTask = screen.StartReading();
            var errorTask = process.StandardError.ReadToEndAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));
            await screen.InputReady.WaitAsync(timeout.Token);
            await screen.WaitForFrameAsync(frame => frame.Contains("native-ui-widget:0:idle", StringComparison.Ordinal) &&
                frame.Contains("native-ui:header", StringComparison.Ordinal) &&
                frame.Contains("native-ui:footer", StringComparison.Ordinal) &&
                frame.Contains("native-ui:ready", StringComparison.Ordinal)).WaitAsync(timeout.Token);
            await WaitForOutputAsync(screen, "\u001b]0;Native UI Dashboard\u0007", 0, timeout.Token);

            var shortcutStart = screen.CapturedOutput.Length;
            await process.StandardInput.WriteAsync("\u001b[122;6u");
            await process.StandardInput.FlushAsync();
            await WaitForOutputAsync(screen, "native-ui-shortcut", shortcutStart, timeout.Token);
            await screen.WaitForFrameAsync(frame => frame.Contains("native-ui-shortcut", StringComparison.Ordinal))
                .WaitAsync(timeout.Token);

            await process.StandardInput.WriteAsync("!@");
            await process.StandardInput.FlushAsync();
            await WaitForFrameWithDiagnosticsAsync(screen,
                frame => frame.Contains("native-ui-widget:1:idle", StringComparison.Ordinal) &&
                    frame.Contains("native-ui:1:updated", StringComparison.Ordinal) &&
                    frame.Split('\n').Any(line => line.Trim().Equals("ab", StringComparison.Ordinal)),
                "consumed and rewritten terminal input", timeout.Token, process, errorTask);
            await process.StandardInput.WriteAsync("xy");
            await process.StandardInput.FlushAsync();
            await WaitForFrameWithDiagnosticsAsync(screen,
                frame => frame.Contains("native-ui:burst-consumed", StringComparison.Ordinal) &&
                    frame.Split('\n').Any(line => line.Trim().Equals("abx", StringComparison.Ordinal)),
                "extension interception of adjacent buffered characters", timeout.Token, process, errorTask);
            await process.StandardInput.WriteAsync("\u0015nui\t");
            await process.StandardInput.FlushAsync();
            await WaitForFrameWithDiagnosticsAsync(screen,
                frame => frame.Contains("native-ui-autocomplete", StringComparison.Ordinal) &&
                    frame.Contains("Native UI completion", StringComparison.Ordinal),
                "autocomplete after clearing rewritten input", timeout.Token, process, errorTask);
            await process.StandardInput.WriteAsync("\u0015\u001b[200~paste-value\u001b[201~");
            await process.StandardInput.FlushAsync();
            await WaitForFrameWithDiagnosticsAsync(screen,
                frame => frame.Split('\n').Any(line => line.Trim().Equals("paste-value", StringComparison.Ordinal)),
                "bracketed editor paste", timeout.Token, process, errorTask);
            await process.StandardInput.WriteAsync("\u0015");
            await process.StandardInput.FlushAsync();

            ResizePseudoTerminal(process, rows: 18, columns: 80);
            screen.SetSize(rows: 18, columns: 80);
            await WaitForFrameWithDiagnosticsAsync(screen,
                frame => frame.Contains("native-ui-size:80x18", StringComparison.Ordinal),
                "widget resize invalidation", timeout.Token, process, errorTask);

            await process.StandardInput.WriteAsync("\u0015/ui-dashboard\r");
            await process.StandardInput.FlushAsync();
            await WaitForFrameWithDiagnosticsAsync(screen,
                frame => frame.Contains("native-ui-panel", StringComparison.Ordinal) &&
                    frame.Contains("native-ui-panel-size:80x18", StringComparison.Ordinal),
                "extension dashboard panel", timeout.Token, process, errorTask);
            await process.StandardInput.WriteAsync("accepted\r");
            await process.StandardInput.FlushAsync();
            var dashboardResponse = "native-ui-panel:accepted:theme=True/light/3/light:editor=True:tools=True";
            // The dashboard response wraps at 80 columns, so terminal controls interrupt its raw byte sequence.
            await WaitForFrameWithDiagnosticsAsync(screen,
                frame => string.Concat(frame.Where(character => !char.IsWhiteSpace(character)))
                        .Contains(dashboardResponse, StringComparison.Ordinal) &&
                    frame.Contains("native-ui accepted: accepted", StringComparison.Ordinal) &&
                    frame.Contains("native-ui-widget:1:accepted", StringComparison.Ordinal) &&
                    frame.Contains("native-ui-theme:light", StringComparison.Ordinal),
                "dashboard command response and presentation state", timeout.Token, process, errorTask);

            await process.StandardInput.WriteAsync("\u0015/ui-modal\r");
            await process.StandardInput.FlushAsync();
            await WaitForFrameWithDiagnosticsAsync(screen,
                frame => frame.Contains("native-ui-panel", StringComparison.Ordinal) &&
                    frame.Contains("native-ui-panel-size:80x18", StringComparison.Ordinal),
                "extension overlay modal", timeout.Token, process, errorTask);
            ResizePseudoTerminal(process, rows: 20, columns: 90);
            screen.SetSize(rows: 20, columns: 90);
            await WaitForFrameWithDiagnosticsAsync(screen,
                frame => frame.Contains("native-ui-panel-size:90x20", StringComparison.Ordinal),
                "focused overlay resize", timeout.Token, process, errorTask);
            await process.StandardInput.WriteAsync("overlay-value\r");
            await process.StandardInput.FlushAsync();
            await WaitForOutputWithDiagnosticsAsync(screen, "native-ui-modal:overlay-value:draft=True", 0,
                "overlay completion and editor focus restoration", timeout.Token, process, errorTask);

            await process.StandardInput.WriteAsync("\u0015/ui-modal\r");
            await process.StandardInput.FlushAsync();
            await WaitForFrameWithDiagnosticsAsync(screen,
                frame => frame.Contains("native-ui-panel", StringComparison.Ordinal),
                "extension overlay cancellation", timeout.Token, process, errorTask);
            await process.StandardInput.WriteAsync("\u001b");
            await process.StandardInput.FlushAsync();
            await WaitForOutputWithDiagnosticsAsync(screen, "native-ui-modal-cancelled:draft=True", 0,
                "overlay cancellation and editor focus restoration", timeout.Token, process, errorTask);

            await process.StandardInput.WriteAsync("\u0015/ui-dialogs\r");
            await process.StandardInput.FlushAsync();
            await WaitForFrameWithDiagnosticsAsync(screen,
                frame => frame.Contains("Choose a dashboard region", StringComparison.Ordinal) &&
                    frame.Contains("North", StringComparison.Ordinal) && frame.Contains("South", StringComparison.Ordinal),
                "extension selector dialog", timeout.Token, process, errorTask);
            await process.StandardInput.WriteAsync("\u001b[B\r");
            await process.StandardInput.FlushAsync();
            await WaitForFrameWithDiagnosticsAsync(screen,
                frame => frame.Contains("Confirm dashboard action", StringComparison.Ordinal),
                "extension confirmation dialog", timeout.Token, process, errorTask);
            await process.StandardInput.WriteAsync("\r");
            await process.StandardInput.FlushAsync();
            await WaitForFrameWithDiagnosticsAsync(screen,
                frame => frame.Contains("Dashboard label", StringComparison.Ordinal),
                "extension text input dialog", timeout.Token, process, errorTask);
            await process.StandardInput.WriteAsync("typed label\r");
            await process.StandardInput.FlushAsync();
            await WaitForOutputWithDiagnosticsAsync(screen, "native-ui-dialog:South:True:typed label", 0,
                "extension dialog command result", timeout.Token, process, errorTask);

            await process.StandardInput.WriteAsync("\u0015/ui-editor\r");
            await process.StandardInput.FlushAsync();
            await WaitForFrameWithDiagnosticsAsync(screen,
                frame => frame.Contains("Extension multiline editor", StringComparison.Ordinal) &&
                    frame.Contains("prefilled text", StringComparison.Ordinal),
                "extension multiline editor", timeout.Token, process, errorTask);
            await process.StandardInput.WriteAsync("\rsecond line\u001b[13;5u");
            await process.StandardInput.FlushAsync();
            await WaitForFrameWithDiagnosticsAsync(screen,
                frame => frame.Contains("native-ui-editor-result:prefilled text", StringComparison.Ordinal) &&
                    frame.Contains("second line", StringComparison.Ordinal),
                "extension multiline editor result", timeout.Token, process, errorTask);

            await process.StandardInput.WriteAsync("\u0015run native tool\r");
            await process.StandardInput.FlushAsync();
            await WaitForFrameWithDiagnosticsAsync(screen,
                frame => frame.Contains("NATIVE_UI_AGENT_OK", StringComparison.Ordinal) &&
                    !frame.Contains("Native UI dashboard", StringComparison.Ordinal),
                "agent response after the run returns to idle", timeout.Token, process, errorTask);
            await server.WaitAsync(timeout.Token);
            Assert.Contains("native_ui_echo", requests[0]);
            Assert.Contains("native-ui-tool:pty", requests[1]);

            await process.StandardInput.WriteAsync("\u0015/ui-dashboard-cleanup\r");
            await process.StandardInput.FlushAsync();
            await WaitForOutputWithDiagnosticsAsync(screen, "native-ui-cleanup-complete", 0,
                "extension-owned UI removal", timeout.Token, process, errorTask);
            var extensionCleared = await WaitForFrameWithDiagnosticsAsync(screen, frame =>
                !frame.Contains("native-ui-widget:", StringComparison.Ordinal) &&
                !frame.Contains("native-ui:header", StringComparison.Ordinal) &&
                !frame.Contains("native-ui:footer", StringComparison.Ordinal) &&
                !frame.Contains("native-ui-editor:pass-through", StringComparison.Ordinal) &&
                !frame.Contains("dashboard:", StringComparison.Ordinal), "extension-owned UI cleanup frame",
                timeout.Token, process, errorTask);
            Assert.DoesNotContain("native-ui-widget:", extensionCleared, StringComparison.Ordinal);

            using (var savedSettings = JsonDocument.Parse(await File.ReadAllTextAsync(
                       Path.Combine(agentDirectory, "settings.json"))))
                Assert.Equal("light", savedSettings.RootElement.GetProperty("theme").GetString());

            await File.WriteAllTextAsync(Path.Combine(agentDirectory, "settings.json"), "{\"nativeExtensions\":[]}");
            var reloadStart = screen.CapturedOutput.Length;
            await process.StandardInput.WriteAsync("/reload\r");
            await process.StandardInput.FlushAsync();
            await WaitForOutputAsync(screen,
                "Reloaded keybindings, extensions, skills, prompts, themes, and context files", reloadStart,
                timeout.Token);
            var cleanedFrame = await WaitForFrameWithDiagnosticsAsync(screen, frame =>
                !frame.Contains("native-ui-widget:", StringComparison.Ordinal) &&
                !frame.Contains("native-ui:header", StringComparison.Ordinal) &&
                !frame.Contains("native-ui:footer", StringComparison.Ordinal) &&
                !frame.Contains("native-ui-editor:pass-through", StringComparison.Ordinal),
                "built-in editor restoration", timeout.Token);
            Assert.DoesNotContain("native-ui-widget:", cleanedFrame, StringComparison.Ordinal);
            Assert.DoesNotContain("native-ui:header", cleanedFrame, StringComparison.Ordinal);
            Assert.DoesNotContain("native-ui:footer", cleanedFrame, StringComparison.Ordinal);
            Assert.DoesNotContain("native-ui-editor", cleanedFrame, StringComparison.Ordinal);

            var rawInputStart = screen.CapturedOutput.Length;
            await process.StandardInput.WriteAsync("!@");
            await process.StandardInput.FlushAsync();
            await WaitForOutputWithDiagnosticsAsync(screen, "!@", rawInputStart,
                "input listener removal after package reload", timeout.Token, process, errorTask);
            await process.StandardInput.WriteAsync("\u0015");
            await process.StandardInput.FlushAsync();
            var editorStart = screen.CapturedOutput.Length;
            await process.StandardInput.WriteAsync("editor-after-removal");
            await process.StandardInput.FlushAsync();
            await WaitForOutputAsync(screen, "editor-after-removal", editorStart, timeout.Token);
            await process.StandardInput.WriteAsync("\u0015/quit\r");
            await process.StandardInput.FlushAsync();
            process.StandardInput.Close();
            await process.WaitForExitAsync(timeout.Token);
            var capturedOutput = await outputTask;
            Assert.Equal(0, process.ExitCode);
            Assert.Contains("\u001b]0;\u0007", capturedOutput, StringComparison.Ordinal);
            Assert.DoesNotContain("Agent error:", capturedOutput, StringComparison.Ordinal);
            Assert.DoesNotContain("Exception:", await errorTask, StringComparison.Ordinal);

            await File.WriteAllTextAsync(Path.Combine(agentDirectory, "settings.json"), JsonSerializer.Serialize(new
            {
                nativeExtensions = new[]
                {
                    new { id = installedPackage.Id, version = installedPackage.Version, source = installedPackage.Source }
                }
            }));
            await VerifyExitRestoresLoadedExtensionAndShellInputAsync(root, agentDirectory);
        }
        finally
        {
            if (process is { HasExited: false }) process.Kill(entireProcessTree: true);
            process?.Dispose();
            try { Directory.Delete(root, recursive: true); }
            catch (IOException) { }
        }
    }

    private static async Task VerifyExitRestoresLoadedExtensionAndShellInputAsync(string root, string agentDirectory)
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
        start.ArgumentList.Add($"stty rows 24 cols 100; printf 'PTY_PREEXISTING_SCROLLBACK\\n'; dotnet {ShellQuote(typeof(CliArguments).Assembly.Location)} --provider fixture --model fixture-model --offline --no-session; printf '\\nPISHARP_CLI_RETURNED\\n'; IFS= read -r next_line; printf 'SHELL_INPUT:%s\\n' \"$next_line\"");
        start.ArgumentList.Add("/dev/null");
        foreach (var name in start.Environment.Keys.Where(name => name.StartsWith("PISHARP_", StringComparison.Ordinal)).ToArray())
            start.Environment.Remove(name);
        start.Environment.Remove("OPENAI_API_KEY");
        start.Environment.Remove("ANTHROPIC_API_KEY");
        start.Environment["PISHARP_AGENT_DIR"] = agentDirectory;
        start.Environment["PISHARP_SESSION_DIR"] = Path.Combine(root, "shutdown-sessions");
        start.Environment["PISHARP_FIXTURE_KEY"] = "local-fixture-key";

        using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start the shutdown PTY fixture.");
        var screen = new PtyScreenCapture(process, rows: 24, columns: 100);
        var outputTask = screen.StartReading();
        var errorTask = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        await screen.InputReady.WaitAsync(timeout.Token);
        await WaitForFrameWithDiagnosticsAsync(screen,
            frame => frame.Contains("native-ui:header", StringComparison.Ordinal) &&
                frame.Contains("native-ui:footer", StringComparison.Ordinal) &&
                frame.Contains("native-ui-widget:0:idle", StringComparison.Ordinal),
            "extension dashboard before terminal shutdown", timeout.Token, process, errorTask);

        var exitStart = screen.CapturedOutput.Length;
        await process.StandardInput.WriteAsync("/quit\r");
        await process.StandardInput.FlushAsync();
        await WaitForOutputWithDiagnosticsAsync(screen, "PISHARP_CLI_RETURNED", exitStart,
            "CLI return to the shell", timeout.Token, process, errorTask);
        var exitOutput = screen.CapturedOutput[exitStart..];
        var restoredFrame = screen.LastFrame;
        Assert.DoesNotContain("\u001b[2J", exitOutput, StringComparison.Ordinal);
        Assert.DoesNotContain("\u001b[3J", exitOutput, StringComparison.Ordinal);
        Assert.Contains("native-ui:header", exitOutput, StringComparison.Ordinal);
        Assert.Contains("native-ui:footer", exitOutput, StringComparison.Ordinal);
        Assert.Contains("native-ui-widget:0:idle", exitOutput, StringComparison.Ordinal);
        Assert.DoesNotContain("native-ui-panel", restoredFrame, StringComparison.Ordinal);

        await process.StandardInput.WriteLineAsync("ordinary-shell-input");
        await process.StandardInput.FlushAsync();
        await WaitForOutputWithDiagnosticsAsync(screen, "SHELL_INPUT:ordinary-shell-input", exitStart,
            "ordinary shell input after CLI shutdown", timeout.Token, process, errorTask);
        process.StandardInput.Close();
        await process.WaitForExitAsync(timeout.Token);
        var capturedOutput = await outputTask;
        Assert.Equal(0, process.ExitCode);
        Assert.Contains("PTY_PREEXISTING_SCROLLBACK", capturedOutput, StringComparison.Ordinal);
        Assert.Contains("SHELL_INPUT:ordinary-shell-input", capturedOutput, StringComparison.Ordinal);
        Assert.DoesNotContain("Exception:", await errorTask, StringComparison.Ordinal);
    }

    private static async Task WaitForOutputAsync(PtyScreenCapture screen, string value, int startIndex,
        CancellationToken cancellationToken)
    {
        while (screen.CapturedOutput.IndexOf(value, Math.Min(startIndex, screen.CapturedOutput.Length),
                   StringComparison.Ordinal) < 0)
        {
            await Task.Delay(20, cancellationToken);
        }
    }

    private static async Task WaitForOutputWithDiagnosticsAsync(PtyScreenCapture screen, string value,
        int startIndex, string stage, CancellationToken cancellationToken, Process process,
        Task<string> errorOutput)
    {
        try
        {
            await WaitForOutputAsync(screen, value, startIndex, cancellationToken)
                .WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);
        }
        catch (TimeoutException error)
        {
            if (process is { HasExited: false }) process.Kill(entireProcessTree: true);
            var stderr = await errorOutput.WaitAsync(TimeSpan.FromSeconds(2));
            throw new InvalidOperationException($"Timed out waiting for {stage} '{value}'. Captured output tail:\n" +
                $"{Tail(screen.CapturedOutput)}\nCLI stderr:\n{Tail(stderr)}", error);
        }
    }

    private static async Task<string> WaitForFrameWithDiagnosticsAsync(PtyScreenCapture screen,
        Func<string, bool> predicate, string stage, CancellationToken cancellationToken,
        Process? process = null, Task<string>? errorOutput = null)
    {
        try
        {
            return await screen.WaitForFrameAsync(predicate).WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);
        }
        catch (TimeoutException error)
        {
            if (process is { HasExited: false }) process.Kill(entireProcessTree: true);
            var stderr = errorOutput is null ? "" : await errorOutput.WaitAsync(TimeSpan.FromSeconds(2));
            throw new InvalidOperationException($"Timed out waiting for {stage}. Last frame:\n{screen.LastFrame}\n" +
                $"Captured output tail:\n{Tail(screen.CapturedOutput)}\nCLI stderr:\n{Tail(stderr)}", error);
        }
    }

    private static string Tail(string value) => value.Length <= 4000 ? value : value[^4000..];

    private static void ResizePseudoTerminal(Process scriptProcess, int rows, int columns)
    {
        if (!OperatingSystem.IsLinux()) throw new PlatformNotSupportedException();
        var pending = new Queue<int>();
        pending.Enqueue(scriptProcess.Id);
        while (pending.TryDequeue(out var parentId))
        {
            var childrenPath = $"/proc/{parentId}/task/{parentId}/children";
            if (!File.Exists(childrenPath)) continue;
            foreach (var childText in File.ReadAllText(childrenPath).Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                if (!int.TryParse(childText, out var childId)) continue;
                pending.Enqueue(childId);
                var inputLink = $"/proc/{childId}/fd/0";
                try
                {
                    var terminalPath = File.ResolveLinkTarget(inputLink, returnFinalTarget: true)?.FullName;
                    if (terminalPath is null || !terminalPath.StartsWith("/dev/pts/", StringComparison.Ordinal) ||
                        terminalPath.EndsWith("/ptmx", StringComparison.Ordinal)) continue;
                    var descriptor = Open(terminalPath, 2 | 0x100);
                    if (descriptor < 0) continue;
                    try
                    {
                        var size = new PtyWindowSize((ushort)rows, (ushort)columns, 0, 0);
                        if (Ioctl(descriptor, 0x5414, ref size) == 0) return;
                    }
                    finally { _ = Close(descriptor); }
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
        throw new InvalidOperationException("Could not resize the CLI's controlling pseudo-terminal.");
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PtyWindowSize(ushort rows, ushort columns, ushort pixelWidth, ushort pixelHeight)
    {
        public ushort Rows = rows;
        public ushort Columns = columns;
        public ushort PixelWidth = pixelWidth;
        public ushort PixelHeight = pixelHeight;
    }

    [DllImport("libc", EntryPoint = "open", SetLastError = true)]
    private static extern int Open([MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags);

    [DllImport("libc", EntryPoint = "ioctl", SetLastError = true)]
    private static extern int Ioctl(int descriptor, uint request, ref PtyWindowSize size);

    [DllImport("libc", EntryPoint = "close", SetLastError = true)]
    private static extern int Close(int descriptor);

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "PiSharp.slnx")))
            directory = directory.Parent;
        return directory?.FullName ?? throw new DirectoryNotFoundException("Could not find PiSharp.slnx.");
    }

    private static string BuildConfiguration() =>
        new DirectoryInfo(AppContext.BaseDirectory).Parent?.Name ?? "Debug";

    private static string CreateFixturePackage(string root, string fixtureDirectory)
    {
        var packagePath = Path.Combine(root, "native-ui-fixture.nupkg");
        using var archive = ZipFile.Open(packagePath, ZipArchiveMode.Create);
        using (var writer = new StreamWriter(archive.CreateEntry("pisharp-extension.json").Open()))
            writer.Write(JsonSerializer.Serialize(new
            {
                id = "PiSharp.NativeExtensionInteractiveUiFixture",
                version = "1.0.0",
                apiVersion = 1,
                extensions = new[]
                {
                    new
                    {
                        assembly = "lib/net10.0/PiSharp.NativeExtensionInteractiveUiFixture.dll",
                        type = "PiSharp.NativeExtensionInteractiveUiFixture.InteractiveUiExtension"
                    }
                }
            }));

        foreach (var fileName in new[]
                 {
                     "PiSharp.NativeExtensionInteractiveUiFixture.dll",
                     "PiSharp.NativeExtensionInteractiveUiFixture.deps.json"
                 })
            archive.CreateEntryFromFile(Path.Combine(fixtureDirectory, fileName), "lib/net10.0/" + fileName);
        return packagePath;
    }

    private static string ShellQuote(string value) => "'" + value.Replace("'", "'\\''", StringComparison.Ordinal) + "'";
}
