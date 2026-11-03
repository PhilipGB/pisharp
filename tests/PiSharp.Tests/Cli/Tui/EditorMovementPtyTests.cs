using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using PiSharp.Cli;

namespace PiSharp.Tests;

[CollectionDefinition("Editor movement PTY", DisableParallelization = true)]
public sealed class EditorMovementPtyCollection;

[Collection("Editor movement PTY")]
public sealed class EditorMovementPtyTests
{
    [Fact]
    public async Task CharacterWordLineAndJumpMovementReachEditorStatesThroughLinuxPty()
    {
        if (!OperatingSystem.IsLinux() || !File.Exists("/usr/bin/script")) return;
        var root = TemporaryDirectory("movement-keys");
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

            async Task WaitTextAsync(string present, string? absent, string description)
            {
                try
                {
                    await screen.WaitForFrameAsync(frame => frame.Contains(present, StringComparison.Ordinal) &&
                        (absent is null || !frame.Contains(absent, StringComparison.Ordinal))).WaitAsync(timeout.Token);
                }
                catch (OperationCanceledException)
                {
                    throw new InvalidOperationException($"{description} timed out. Last frame:\n{screen.LastFrame}");
                }
            }

            async Task WaitCursorAsync(Func<int, int, bool> predicate, string description)
            {
                try { await screen.WaitForCursorAsync(predicate).WaitAsync(timeout.Token); }
                catch (OperationCanceledException)
                {
                    throw new InvalidOperationException($"{description} timed out. Cursor " +
                        $"({screen.LastCursorRow},{screen.LastCursorColumn}). Last frame:\n{screen.LastFrame}");
                }
            }

            await SendAsync("alpha beta gamma");
            await WaitTextAsync("alpha beta gamma", null, "typed draft");
            var endColumn = screen.LastCursorColumn;
            Assert.Equal(16, endColumn);

            await SendAsync("\u001b[D");
            await WaitCursorAsync((_, column) => column == endColumn - 1, "left moves one grapheme");
            await SendAsync("\u001b[C");
            await WaitCursorAsync((_, column) => column == endColumn, "right returns one grapheme");
            await SendAsync("\u0002");
            await WaitCursorAsync((_, column) => column == endColumn - 1, "ctrl+b moves left");
            await SendAsync("\u0006");
            await WaitCursorAsync((_, column) => column == endColumn, "ctrl+f moves right");
            await SendAsync("\u001b[1;3D");
            await WaitCursorAsync((_, column) => column < endColumn, "alt+left moves to a previous word boundary");
            var altLeftColumn = screen.LastCursorColumn;
            await SendAsync("\u001b[1;5D");
            await WaitCursorAsync((_, column) => column < altLeftColumn, "ctrl+left moves to a previous word boundary"); var ctrlLeftColumn = screen.LastCursorColumn;
            await SendAsync("\u001b[1;5C");
            await WaitCursorAsync((_, column) => column > ctrlLeftColumn, "ctrl+right moves to a later word boundary");
            await SendAsync("\u001b[1;3C");
            await WaitCursorAsync((_, column) => column == endColumn, "alt+right returns to the line end");
            await SendAsync("\u0001");
            await WaitCursorAsync((_, column) => column == 0, "ctrl+a moves to line start");
            await SendAsync("\u0005");
            await WaitCursorAsync((_, column) => column == endColumn, "ctrl+e moves to line end");

            await SendAsync("\u0001\u0015\u000b");
            await screen.WaitForFrameAsync(frame => !frame.Contains("alpha beta gamma", StringComparison.Ordinal))
                .WaitAsync(timeout.Token);
            await SendAsync("a,b,c\u0001");
            await WaitTextAsync("a,b,c", "alpha beta gamma", "jump draft typed");
            await WaitCursorAsync((_, column) => column == 0, "jump draft start");
            await SendAsync("\u001d,");
            await WaitCursorAsync((_, column) => column == 1, "ctrl+] jumps to the next comma");
            await WaitTextAsync("a,b,c", null, "jump does not insert the target character");
            await SendAsync("\u001b[1;3D");
            await WaitCursorAsync((_, column) => column == 0, "alt+left moves back over the comma");

            await SendAsync("\u0003/quit\r");
            process.StandardInput.Close();
            await process.WaitForExitAsync(timeout.Token);
            _ = await stdout;
            Assert.Equal(0, process.ExitCode);
            Assert.DoesNotContain("Agent error", await stdout);
            Assert.DoesNotContain("Exception:", await stderr);
            RecordEvidence("pty-movement-keys", new { exitCode = process.ExitCode, finalCursorColumn = endColumn });
        }
        finally
        {
            if (process is { HasExited: false }) process.Kill(entireProcessTree: true);
            process?.Dispose();
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task BufferedJumpConsumesOnlyTheNextGraphemeThroughLinuxPty()
    {
        if (!OperatingSystem.IsLinux() || !File.Exists("/usr/bin/script")) return;
        var root = TemporaryDirectory("movement-jump-buffered");
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

            await SendAsync("a,b,c");
            await screen.WaitForFrameAsync(frame => EditorText(frame) == "a,b,c").WaitAsync(timeout.Token);
            await SendAsync("\u0001");
            await screen.WaitForCursorAsync((_, column) => column == 0).WaitAsync(timeout.Token);
            await SendAsync("\u001d,XY");
            await screen.WaitForFrameAsync(frame => EditorText(frame) == "aXY,b,c").WaitAsync(timeout.Token);
            await screen.WaitForCursorAsync((_, column) => column == 3).WaitAsync(timeout.Token);
            await SendAsync("Z");
            await screen.WaitForFrameAsync(frame => EditorText(frame) == "aXYZ,b,c").WaitAsync(timeout.Token);

            await SendAsync("\u0001\u0015\u000b");
            await screen.WaitForFrameAsync(frame => !frame.Contains("aXYZ,b,c", StringComparison.Ordinal))
                .WaitAsync(timeout.Token);
            await SendAsync("\u0003/quit\r");
            process.StandardInput.Close();
            await process.WaitForExitAsync(timeout.Token);
            _ = await stdout;
            Assert.Equal(0, process.ExitCode);
            Assert.DoesNotContain("Agent error", await stdout);
            Assert.DoesNotContain("Exception:", await stderr);
            RecordEvidence("pty-jump-buffered", new
            {
                exitCode = process.ExitCode,
                bufferedTarget = ",",
                bufferedRemainder = "XY",
                editorAfterJump = "aXY,b,c",
                cursorAfterJump = 3
            });
        }
        finally
        {
            if (process is { HasExited: false }) process.Kill(entireProcessTree: true);
            process?.Dispose();
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task VerticalVisualLinePageAndFullscreenPrecedenceMatchThroughLinuxPty()
    {
        if (!OperatingSystem.IsLinux() || !File.Exists("/usr/bin/script")) return;
        var root = TemporaryDirectory("movement-visual");
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

            async Task WaitAsync(Func<string, bool> predicate, string description)
            {
                try { await screen.WaitForFrameAsync(predicate).WaitAsync(timeout.Token); }
                catch (OperationCanceledException)
                {
                    throw new InvalidOperationException($"{description} timed out. Last frame:\n{screen.LastFrame}");
                }
            }

            async Task WaitCursorAsync(Func<int, int, bool> predicate, string description)
            {
                try { await screen.WaitForCursorAsync(predicate).WaitAsync(timeout.Token); }
                catch (OperationCanceledException)
                {
                    throw new InvalidOperationException($"{description} timed out. Cursor " +
                        $"({screen.LastCursorRow},{screen.LastCursorColumn}). Last frame:\n{screen.LastFrame}");
                }
            }

            await SendAsync("one\ntwo\nthree");
            await WaitAsync(frame => frame.Contains("one", StringComparison.Ordinal) &&
                frame.Contains("three", StringComparison.Ordinal), "multiline draft");
            await WaitCursorAsync((_, column) => column == 5, "multiline draft cursor at line end");
            var startRow = screen.LastCursorRow;
            var startColumn = screen.LastCursorColumn;

            await SendAsync("\u001b[A");
            await WaitCursorAsync((row, column) => row == startRow - 1 && column == 3,
                "up moves one visual row and clamps to the shorter line");
            await SendAsync("\u001b[B");
            await WaitCursorAsync((row, column) => row == startRow && column == startColumn,
                "down recovers the preferred column");
            await SendAsync("\u0001");
            await WaitCursorAsync((_, column) => column == 0, "home moves to logical line start");
            await SendAsync("\u0005");
            await WaitCursorAsync((_, column) => column == startColumn, "end moves to logical line end");

            var longText = string.Join(" ", Enumerable.Range(0, 60).Select(index => $"word{index:00}"));
            await SendAsync("\u0001\u0015\u000b\u0001\u0015\u000b\u0001\u0015\u000b");
            await WaitAsync(frame => !frame.Contains("three", StringComparison.Ordinal), "clear multiline draft");
            await SendAsync(longText + "\u0001");
            await WaitAsync(frame => frame.Contains("word59", StringComparison.Ordinal), "long wrapped draft");
            await WaitCursorAsync((_, column) => column == 0, "long draft cursor at first visual row");
            var topRow = screen.LastCursorRow;
            var topColumn = screen.LastCursorColumn;

            await SendAsync("\u001b[6~");
            await Task.Delay(250);
            Assert.Contains("word00", screen.LastFrame, StringComparison.Ordinal);
            Assert.Equal((topRow, topColumn), (screen.LastCursorRow, screen.LastCursorColumn));
            Assert.Contains("word59", screen.LastFrame, StringComparison.Ordinal);

            await SendAsync("\u001b[6;5~");
            await WaitCursorAsync((row, column) => row != topRow || column != topColumn,
                "ctrl+pageDown moves the editor cursor");
            var pageDownRow = screen.LastCursorRow;
            await SendAsync("\u001b[5;5~");
            await WaitCursorAsync((row, column) => row == topRow && column == topColumn,
                "ctrl+pageUp returns to the first visual row");
            Assert.True(pageDownRow >= topRow);

            await SendAsync("\u0003/quit\r");
            process.StandardInput.Close();
            await process.WaitForExitAsync(timeout.Token);
            _ = await stdout;
            Assert.Equal(0, process.ExitCode);
            Assert.DoesNotContain("Agent error", await stdout);
            Assert.DoesNotContain("Exception:", await stderr);
            RecordEvidence("pty-visual-page", new
            {
                exitCode = process.ExitCode,
                firstVisualRow = topRow,
                pageDownRow,
                pageUpRow = topRow
            });
        }
        finally
        {
            if (process is { HasExited: false }) process.Kill(entireProcessTree: true);
            process?.Dispose();
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ConfiguredMovementBindingsAndReloadApplyThroughLinuxPty()
    {
        if (!OperatingSystem.IsLinux() || !File.Exists("/usr/bin/script")) return;
        var root = TemporaryDirectory("movement-bindings");
        Process? process = null;
        try
        {
            var agent = Path.Combine(root, "agent");
            var bindings = Path.Combine(agent, "keybindings.json");
            await File.WriteAllTextAsync(bindings, """
                {
                  "tui.editor.cursorWordLeft": "f9",
                  "tui.editor.jumpForward": "alt+j",
                  "tui.editor.pageDown": "f10"
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

            async Task WaitAsync(Func<string, bool> predicate, string description)
            {
                try { await screen.WaitForFrameAsync(predicate).WaitAsync(timeout.Token); }
                catch (OperationCanceledException)
                {
                    throw new InvalidOperationException($"{description} timed out. Last frame:\n{screen.LastFrame}");
                }
            }

            async Task WaitCursorAsync(Func<int, int, bool> predicate, string description)
            {
                try { await screen.WaitForCursorAsync(predicate).WaitAsync(timeout.Token); }
                catch (OperationCanceledException)
                {
                    throw new InvalidOperationException($"{description} timed out. Cursor " +
                        $"({screen.LastCursorRow},{screen.LastCursorColumn}). Last frame:\n{screen.LastFrame}");
                }
            }

            await SendAsync("gamma delta epsilon");
            await WaitAsync(frame => frame.Contains("gamma delta epsilon", StringComparison.Ordinal), "configured draft");
            await SendAsync("\u001b[20~");
            await WaitCursorAsync((_, column) => column == 12, "configured f9 moves a word left");
            await SendAsync("\u001b[106;3up");
            await WaitCursorAsync((_, column) => column == 13, "configured alt+j jumps to p");
            await WaitAsync(frame => frame.Contains("gamma delta epsilon", StringComparison.Ordinal), "jump keeps text");
            await SendAsync("\u0001\u0015\u000b");
            await WaitAsync(frame => !frame.Contains("gamma delta epsilon", StringComparison.Ordinal), "clear before reload");

            await File.WriteAllTextAsync(bindings, """
                {
                  "tui.editor.cursorWordLeft": "f8",
                  "tui.editor.jumpForward": "alt+j",
                  "tui.editor.pageDown": "f10"
                }
                """);
            await SendAsync("/reload\r");
            await WaitAsync(frame => frame.Contains("Reloaded keybindings", StringComparison.Ordinal), "reload");

            await SendAsync("one two");
            await WaitAsync(frame => frame.Contains("one two", StringComparison.Ordinal), "post-reload draft");
            var endColumn = screen.LastCursorColumn;
            await SendAsync("\u001b[20~");
            await Task.Delay(200);
            Assert.Equal(endColumn, screen.LastCursorColumn);
            await SendAsync("\u001b[19~");
            await WaitCursorAsync((_, column) => column == 4, "new f8 binding moves a word left");

            await SendAsync("\u0003/quit\r");
            process.StandardInput.Close();
            await process.WaitForExitAsync(timeout.Token);
            _ = await stdout;
            Assert.Equal(0, process.ExitCode);
            Assert.DoesNotContain("Agent error", await stdout);
            Assert.DoesNotContain("Exception:", await stderr);
            RecordEvidence("pty-configured-reload", new { exitCode = process.ExitCode, reloaded = true });
        }
        finally
        {
            if (process is { HasExited: false }) process.Kill(entireProcessTree: true);
            process?.Dispose();
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task WidgetAndApplicationShortcutsTakePrecedenceOverMovementKeysThroughLinuxPty()
    {
        if (!OperatingSystem.IsLinux() || !File.Exists("/usr/bin/script")) return;
        var root = TemporaryDirectory("movement-precedence");
        Process? process = null;
        try
        {
            var agent = Path.Combine(root, "agent");
            await File.WriteAllTextAsync(Path.Combine(agent, "keybindings.json"),
                """
                {
                  "app.tools.expand": "ctrl+w",
                  "tui.editor.cursorWordLeft": "ctrl+w"
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
            var cursor = (screen.LastCursorRow, screen.LastCursorColumn);

            await SendAsync("\u0017");
            await screen.WaitForFrameAsync(frame => frame.Contains("keepdraft words", StringComparison.Ordinal) &&
                frame.Contains("Tool output:", StringComparison.Ordinal)).WaitAsync(timeout.Token);
            Assert.Equal(cursor, (screen.LastCursorRow, screen.LastCursorColumn));

            await SendAsync("\u001b[102;6u");
            await screen.WaitForFrameAsync(frame => frame.Contains("Find in transcript", StringComparison.Ordinal))
                .WaitAsync(timeout.Token);
            await SendAsync("find words");
            await screen.WaitForFrameAsync(frame => frame.Contains("find words", StringComparison.Ordinal))
                .WaitAsync(timeout.Token);
            await SendAsync("\u0015");
            await screen.WaitForFrameAsync(frame => !frame.Contains("find words", StringComparison.Ordinal))
                .WaitAsync(timeout.Token);
            await SendAsync("\u001b");
            await screen.WaitForFrameAsync(frame => !frame.Contains("Find in transcript", StringComparison.Ordinal))
                .WaitAsync(timeout.Token);
            await SendAsync("\u0001\u000b");
            await screen.WaitForFrameAsync(frame => !frame.Contains("keepdraft words", StringComparison.Ordinal))
                .WaitAsync(timeout.Token);
            await SendAsync("/quit\r");
            process.StandardInput.Close();
            await process.WaitForExitAsync(timeout.Token);
            _ = await stdout;
            Assert.Equal(0, process.ExitCode);
            Assert.DoesNotContain("Agent error", await stdout);
            Assert.DoesNotContain("Exception:", await stderr);
            RecordEvidence("pty-widget-precedence", new { exitCode = process.ExitCode, applicationWon = true });
        }
        finally
        {
            if (process is { HasExited: false }) process.Kill(entireProcessTree: true);
            process?.Dispose();
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task HistoryDraftRestorationAndSubmissionReachTheProviderThroughLinuxPty()
    {
        if (!OperatingSystem.IsLinux() || !File.Exists("/usr/bin/script")) return;
        var root = TemporaryDirectory("movement-history-provider");
        var agent = Path.Combine(root, "agent");
        var before = Path.Combine(root, "stty-before");
        var after = Path.Combine(root, "stty-after");
        using var listener = StartLoopbackListener(out var port);
        using var serverTimeout = Timeout(45);
        var requests = new List<string>();
        var server = Task.Run(async () =>
        {
            for (var index = 0; index < 3; index++)
            {
                var request = await listener.GetContextAsync().WaitAsync(serverTimeout.Token);
                using var reader = new StreamReader(request.Request.InputStream);
                requests.Add(await reader.ReadToEndAsync(serverTimeout.Token));
                request.Response.ContentType = "text/event-stream";
                request.Response.KeepAlive = false;
                await using var writer = new StreamWriter(request.Response.OutputStream);
                var text = index switch
                {
                    0 => "HISTORY_SEED_ONE",
                    1 => "HISTORY_SEED_TWO",
                    _ => "MOVEMENT_SUBMIT_OK"
                };
                await writer.WriteAsync("data: {\"type\":\"response.created\",\"response\":{\"id\":\"resp_" + index +
                    "\",\"object\":\"response\",\"created_at\":1,\"model\":\"fixture-model\",\"status\":\"in_progress\",\"output\":[]}}\n\n");
                await writer.WriteAsync("data: {\"type\":\"response.output_text.delta\",\"item_id\":\"msg_" + index +
                    "\",\"output_index\":0,\"content_index\":0,\"delta\":\"" + text + "\"}\n\n");
                await writer.WriteAsync("data: {\"type\":\"response.completed\",\"response\":{\"id\":\"resp_" + index +
                    "\",\"object\":\"response\",\"created_at\":1,\"model\":\"fixture-model\",\"status\":\"completed\",\"output\":[]}}\n\n");
                await writer.WriteAsync("data: [DONE]\n\n");
                await writer.FlushAsync();
                request.Response.Close();
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
            await File.WriteAllTextAsync(Path.Combine(agent, "keybindings.json"), """
                {
                  "tui.editor.historyPrevious": "f6",
                  "tui.editor.historyNext": "f5"
                }
                """);
            process = Start(root, provider: true, before, after);
            var screen = new PtyScreenCapture(process);
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

            await SendAsync("first history prompt\r");
            await WaitAsync(frame => frame.Contains("HISTORY_SEED_ONE", StringComparison.Ordinal), "first seed response");
            await SendAsync("second history prompt\r");
            await WaitAsync(frame => frame.Contains("HISTORY_SEED_TWO", StringComparison.Ordinal), "second seed response");

            await SendAsync("draft line one\n");
            await SendAsync("draft line two");
            await WaitAsync(frame => frame.Contains("draft line one", StringComparison.Ordinal) &&
                frame.Contains("draft line two", StringComparison.Ordinal), "multiline draft");
            await SendAsync("\u001b[17~");
            await WaitAsync(frame => EditorText(frame) == "second history prompt", "explicit history previous newest");
            await SendAsync("\u001b[17~");
            await WaitAsync(frame => EditorText(frame) == "first history prompt", "explicit history previous older");
            await SendAsync("\u001b[15~");
            await WaitAsync(frame => EditorText(frame) == "second history prompt", "explicit history next newer");
            await SendAsync("\u001b[15~");
            await WaitAsync(frame => EditorText(frame) == "draft line one\ndraft line two",
                "explicit history restores the unsent draft");
            await WaitAsync(frame => frame.Contains("fixture-model", StringComparison.Ordinal),
                "history binding keeps the model");

            await SendAsync("\u0001\u0015\u000b\u0001\u0015\u000b\u0001\u0015\u000b");
            await WaitAsync(frame => !frame.Contains("draft line one", StringComparison.Ordinal), "clear restored draft");
            await SendAsync("provider movement input\r");
            await WaitAsync(frame => frame.Contains("MOVEMENT_SUBMIT_OK", StringComparison.Ordinal), "provider response");
            await server.WaitAsync(timeout.Token);

            await SendAsync("\u0003\u0003");
            process.StandardInput.Close();
            await process.WaitForExitAsync(timeout.Token);
            _ = await stdout;

            Assert.Equal(0, process.ExitCode);
            Assert.Equal(3, requests.Count);
            Assert.Contains("first history prompt", requests[0], StringComparison.Ordinal);
            Assert.Contains("second history prompt", requests[1], StringComparison.Ordinal);
            Assert.Contains("provider movement input", requests[2], StringComparison.Ordinal);
            Assert.DoesNotContain("draft line one", requests[2], StringComparison.Ordinal);
            Assert.Equal(await File.ReadAllTextAsync(before), await File.ReadAllTextAsync(after));
            Assert.DoesNotContain("Agent error", await stdout);
            Assert.DoesNotContain("Exception:", await stderr);
            RecordEvidence("pty-provider-lifecycle", new
            {
                exitCode = process.ExitCode,
                requests = requests.Count,
                providerText = "provider movement input",
                historyEntries = 2,
                restoredDraft = "draft line one\ndraft line two",
                termiosRestored = true
            });
        }
        finally
        {
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
    public async Task ExternalEditorShortcutWinsOverCollidingMovementBindingAndRestoresTerminalThroughLinuxPty()
    {
        if (!OperatingSystem.IsLinux() || !File.Exists("/usr/bin/script")) return;
        var root = TemporaryDirectory("movement-external-editor");
        var agent = Path.Combine(root, "agent");
        var before = Path.Combine(root, "stty-before");
        var after = Path.Combine(root, "stty-after");
        var editorStty = Path.Combine(root, "editor-stty");
        var editorDraft = Path.Combine(root, "editor-draft");
        Process? process = null;
        try
        {
            await File.WriteAllTextAsync(Path.Combine(agent, "keybindings.json"),
                """
                { "tui.editor.cursorWordLeft": "ctrl+g" }
                """);
            var editorScript = Path.Combine(root, "fake external editor.sh");
            await File.WriteAllTextAsync(editorScript, """
                #!/bin/sh
                stty -g > "$PISHARP_EDITOR_STTY"
                cp "$1" "$PISHARP_EDITOR_DRAFT"
                printf '%s edited' "$(cat "$1")" > "$1"
                """);
            File.SetUnixFileMode(editorScript,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            process = Start(root, provider: false, before, after, editorStty, editorDraft);
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

            await SendAsync("draft words");
            await screen.WaitForFrameAsync(frame => frame.Contains("draft words", StringComparison.Ordinal))
                .WaitAsync(timeout.Token);
            await SendAsync("\u0007");
            await screen.WaitForFrameAsync(frame => frame.Contains("draft words edited", StringComparison.Ordinal))
                .WaitAsync(timeout.Token);

            Assert.Contains("Launching external editor:", screen.CapturedOutput);
            Assert.True(File.Exists(editorStty), "The external editor did not run.");
            Assert.True(File.Exists(editorDraft), "The external editor did not receive the draft file.");
            Assert.Equal("draft words", await File.ReadAllTextAsync(editorDraft));
            Assert.Equal((await File.ReadAllTextAsync(before)).Trim(),
                (await File.ReadAllTextAsync(editorStty)).Trim());

            await SendAsync("\u0001\u000b");
            await screen.WaitForFrameAsync(frame => !frame.Contains("draft words edited", StringComparison.Ordinal))
                .WaitAsync(timeout.Token);
            await SendAsync("\u0003\u0003");
            process.StandardInput.Close();
            await process.WaitForExitAsync(timeout.Token);
            _ = await stdout;

            Assert.Equal(0, process.ExitCode);
            Assert.Equal((await File.ReadAllTextAsync(before)).Trim(),
                (await File.ReadAllTextAsync(after)).Trim());
            Assert.DoesNotContain("Agent error", await stdout);
            Assert.DoesNotContain("Exception:", await stderr);
            RecordEvidence("pty-external-editor", new
            {
                exitCode = process.ExitCode,
                draft = "draft words",
                editedDraft = "draft words edited",
                termiosRestored = true
            });
        }
        finally
        {
            if (process is { HasExited: false }) process.Kill(entireProcessTree: true);
            process?.Dispose();
            Directory.Delete(root, recursive: true);
        }
    }


    private static string EditorText(string frame)
    {
        var lines = frame.Split('\n');
        var separators = lines.Select((line, index) => (line, index))
            .Where(pair => pair.line.TrimStart().StartsWith('─')).Select(pair => pair.index).ToArray();
        if (separators.Length < 2) return "";
        var top = separators[^2];
        var bottom = separators[^1];
        return string.Join("\n", lines[(top + 1)..bottom]
            .Select(line => line.TrimEnd()).Where(line => line.Length > 0));
    }

    private static readonly object EvidenceGate = new();

    private static void RecordEvidence(string id, object details)
    {
        var path = Environment.GetEnvironmentVariable("PISHARP_EDITOR_MOVEMENT_EVIDENCE");
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

    private static Process Start(string root, bool provider = false, string? before = null, string? after = null,
        string? editorStty = null, string? editorDraft = null)
    {
        var agent = Path.Combine(root, "agent");
        before ??= Path.Combine(root, "stty-before");
        after ??= Path.Combine(root, "stty-after");
        var command = provider
            ? $"stty rows 24 cols 100; stty -g > {ShellQuote(before)}; " +
              $"dotnet {ShellQuote(typeof(CliArguments).Assembly.Location)} --provider fixture --model fixture-model " +
              $"--no-session --offline --no-tools; status=$?; stty -g > {ShellQuote(after)}; exit $status"
            : $"stty rows 24 cols 100; stty -g > {ShellQuote(before)}; " +
              $"dotnet {ShellQuote(typeof(CliArguments).Assembly.Location)} --local --offline --no-session --no-tools; " +
              $"status=$?; stty -g > {ShellQuote(after)}; exit $status";
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
        start.ArgumentList.Add(command);
        start.ArgumentList.Add("/dev/null");
        foreach (var name in new[] { "OPENAI_API_KEY", "PISHARP_API_KEY", "PISHARP_BASE_URL", "PISHARP_AUTH_PATH", "PISHARP_MODELS_PATH" })
            start.Environment.Remove(name);
        start.Environment["PISHARP_AGENT_DIR"] = agent;
        if (editorStty is not null) start.Environment["PISHARP_EDITOR_STTY"] = editorStty;
        if (editorDraft is not null) start.Environment["PISHARP_EDITOR_DRAFT"] = editorDraft;
        if (editorStty is not null)
            start.Environment["VISUAL"] = $"/bin/sh {ShellQuote(Path.Combine(root, "fake external editor.sh"))}";
        return Process.Start(start) ?? throw new InvalidOperationException("Could not start the movement PTY fixture.");
    }

    private static string TemporaryDirectory(string name)
    {
        var root = Path.Combine(Path.GetTempPath(), $"pisharp-{name}-pty-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "agent"));
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
