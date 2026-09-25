using System.Diagnostics;
using PiSharp.Cli;
using SkiaSharp;

namespace PiSharp.Tests;

public sealed class TerminalPtyTests
{
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
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            await process.StandardInput.WriteAsync($"/name first\n/new\n/name second\n/sessions\n/resume first\n/resume\nsecond\n/session\n/export {cwd}/export.html\n/clone\n/session\n/quit\n");
            process.StandardInput.Close();
            using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(12));
            try { await process.WaitForExitAsync(limit.Token); }
            catch (OperationCanceledException) { process.Kill(entireProcessTree: true); throw; }
            var output = await stdout;
            var secondIds = (await PiSharp.Runtime.Sessions.SessionCatalog.ListAsync(store))
                .Where(session => session.Name == "second").Select(session => session.Id[..12]).ToHashSet();
            var resumedIds = System.Text.RegularExpressions.Regex.Matches(output, @"Resumed ([a-f0-9]{12})")
                .Select(match => match.Groups[1].Value).ToArray();
            Assert.Equal(0, process.ExitCode);
            Assert.Contains("Resumed", output);
            Assert.Contains("Resume session", output);
            Assert.Contains(resumedIds, id => secondIds.Contains(id));
            Assert.Contains("clone:", output);
            Assert.Contains("Exported private HTML", output);
            Assert.Contains("PiSharp session", await File.ReadAllTextAsync(Path.Combine(cwd, "export.html")));
            Assert.Equal(3, Directory.EnumerateFiles(Path.Combine(cwd, "sessions"), "*.session.json").Count());
            Assert.DoesNotContain("Session error", output);
            Assert.DoesNotContain("Agent error", await stderr);
        }
        finally { Directory.Delete(cwd, recursive: true); }
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
            Assert.DoesNotContain("Agent error:", output);
            Assert.DoesNotContain("Exception:", await stderr);
            var settings = await PiSharp.Cli.UserSettings.LoadAsync(agent, _ => null);
            Assert.True(settings.HideThinkingBlock);
            Assert.Equal("code --wait", settings.ExternalEditor);
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
                ArgumentList = { "-q", "-e", "-c", $"stty rows 24 cols 80; dotnet '{assembly}' --provider openai --no-tools --no-session --offline", "/dev/null" }
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
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            await process.StandardInput.WriteAsync("\u0007\n");
            await process.StandardInput.FlushAsync();
            process.StandardInput.Close();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(12));
            try { await process.WaitForExitAsync(timeout.Token); }
            catch (OperationCanceledException) { process.Kill(entireProcessTree: true); throw; }
            var output = await stdout;

            Assert.Equal(0, process.ExitCode);
            Assert.Contains("Launching external editor:", output);
            Assert.Contains("❯ /quit", output);
            Assert.True(output.Split("\u001b[?1049l", StringSplitOptions.None).Length >= 3,
                "the alternate screen should be left for the editor and restored before shutdown");
            Assert.DoesNotContain("Shortcut action failed:", output);
            Assert.DoesNotContain("Agent error:", output);
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
            var normalized = System.Text.RegularExpressions.Regex.Replace(output, "\\r+\\n", "\n");
            Assert.True(normalized.Contains("Name: pasted\nsecond", StringComparison.Ordinal), normalized);
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
                ArgumentList = { "-q", "-e", "-c", $"stty rows 24 cols 80; dotnet '{assembly}' --provider openai --no-tools --no-session --offline", "/dev/null" }
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
                        if (output.Contains("❯ @notes.txt", StringComparison.Ordinal)) selectedPath.TrySetResult();
                        var frameStart = output.LastIndexOf("\u001b[?2026h\u001b[2J\u001b[H", StringComparison.Ordinal);
                        if (selectedPath.Task.IsCompleted && frameStart >= 0)
                        {
                            var frame = output[(frameStart + "\u001b[?2026h\u001b[2J\u001b[H".Length)..];
                            if (frame.Contains("❯ ", StringComparison.Ordinal) && !frame.Contains("@notes.txt", StringComparison.Ordinal))
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
                Assert.Contains("❯ @notes.txt", output);
                Assert.Contains("❯ ", output);
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

    private static byte[] CreatePng()
    {
        using var bitmap = new SKBitmap(new SKImageInfo(2, 2, SKColorType.Rgba8888, SKAlphaType.Premul));
        bitmap.Erase(SKColors.CornflowerBlue);
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }
}
