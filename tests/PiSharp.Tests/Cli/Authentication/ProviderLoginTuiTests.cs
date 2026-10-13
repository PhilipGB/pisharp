using System.Text;
using PiSharp.Cli;

namespace PiSharp.Tests;

public sealed class ProviderLoginTuiTests
{
    [Fact]
    public async Task InteractiveLoginAndLogoutNeverSendUnauthenticatedPromptsOrEchoSecret()
    {
        if (!OperatingSystem.IsLinux() || !File.Exists("/usr/bin/script")) return;
        var root = Path.Combine(Path.GetTempPath(), "pisharp-provider-pty-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var agentDirectory = Path.Combine(root, "agent");
            Directory.CreateDirectory(agentDirectory);
            await File.WriteAllTextAsync(Path.Combine(agentDirectory, "models.json"), """
                {"providers":{"fixture":{"baseUrl":"http://127.0.0.1:1/v1","apiKeyEnv":"PISHARP_FIXTURE_KEY",
                 "models":[{"id":"fixture-model","reasoning":false},{"id":"fixture-alt","reasoning":false}]}}}
                """);
            var start = new System.Diagnostics.ProcessStartInfo("/usr/bin/script")
            {
                WorkingDirectory = root,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            start.ArgumentList.Add("-q");
            start.ArgumentList.Add("-e");
            start.ArgumentList.Add("-c");
            start.ArgumentList.Add($"stty rows 24 cols 80; exec dotnet '{typeof(CliArguments).Assembly.Location}' --provider fixture --model fixture-model --no-tools --offline --session-dir '{root}/sessions'");
            start.ArgumentList.Add("/dev/null");
            foreach (var name in new[] { "PISHARP_FIXTURE_KEY", "OPENAI_API_KEY", "PISHARP_API_KEY", "PISHARP_BASE_URL", "PISHARP_AUTH_PATH", "PISHARP_MODELS_PATH" })
                start.Environment.Remove(name);
            start.Environment["PISHARP_AGENT_DIR"] = agentDirectory;
            using var process = System.Diagnostics.Process.Start(start)!;
            var output = new StringBuilder();
            var draining = Task.Run(async () =>
            {
                var buffer = new char[1024];
                int count;
                while ((count = await process.StandardOutput.ReadAsync(buffer)) > 0)
                    lock (output) output.Append(buffer, 0, count);
            });
            var stderr = process.StandardError.ReadToEndAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            async Task WaitFor(string text)
            {
                while (true)
                {
                    lock (output) if (output.ToString().Contains(text, StringComparison.Ordinal)) return;
                    await Task.Delay(20, timeout.Token);
                }
            }
            try
            {
                await process.StandardInput.WriteAsync("before-login\n/login fixture oauth\n/login fixture api-key\n");
                await process.StandardInput.FlushAsync();
                await WaitFor("API key for fixture:");
                await process.StandardInput.WriteAsync("fixture-secret-not-for-transcript\n");
                await process.StandardInput.FlushAsync();
                await WaitFor("Authenticated fixture with api-key");
                await process.StandardInput.WriteAsync("/model fixture/fixture-alt\n/thinking high\n/thinking\n/model\n\u001b[B\u001b[A\r/logout\nafter-logout\n/quit\n");
                process.StandardInput.Close();
                await process.WaitForExitAsync(timeout.Token);
                await draining;
                var transcript = output.ToString();
                Assert.Equal(1, process.ExitCode); // Rejected prompts are failures, not successful inference.
                Assert.Contains("has no configured OAuth adapter", transcript);
                Assert.Contains("Authenticated fixture with api-key", transcript);
                Assert.Contains("Model: fixture-alt", transcript);
                Assert.Contains("Thinking: off", transcript);
                Assert.Contains("Thinking: off; available: off", transcript);
                Assert.Contains("Logged out fixture", transcript);
                var restoreBoundary = transcript.LastIndexOf("\u001b[?1049l", StringComparison.Ordinal);
                Assert.True(restoreBoundary >= 0, "The interactive screen was not restored.");
                var restoredScrollback = transcript[(restoreBoundary + "\u001b[?1049l".Length)..];
                Assert.Equal(2, restoredScrollback.Split("is not authenticated. Use /login", StringSplitOptions.None).Length - 1);
                Assert.DoesNotContain("fixture-secret-not-for-transcript", transcript);
                Assert.DoesNotContain("Connection refused", transcript);
                Assert.DoesNotContain("fixture-secret-not-for-transcript", await stderr);
                var store = new PiSharp.Runtime.Sessions.ConversationStore(root, Path.Combine(root, "sessions"));
                var session = await store.LoadAsync(Assert.Single(Directory.GetFiles(store.DirectoryPath, "*.session.json")));
                Assert.Empty(session.ActiveMessages());
                Assert.Equal("fixture-alt", session.Model);
                Assert.Equal("fixture", session.Provider);
                Assert.Null(await new AuthStorage(Path.Combine(agentDirectory, "auth.json")).ReadAsync("fixture"));
            }
            catch
            {
                if (!process.HasExited) process.Kill(entireProcessTree: true);
                throw;
            }
        }
        finally { Directory.Delete(root, true); }
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task ProviderOAuthUrlUsesConfiguredCopyKeyAndCanBeCancelledInTheTui(
        bool clipboardUnavailable, bool customCopyKey)
    {
        if (!OperatingSystem.IsLinux() || !File.Exists("/usr/bin/script")) return;
        var root = Path.Combine(Path.GetTempPath(), "pisharp-provider-oauth-tui-" + Guid.NewGuid().ToString("N"));
        var agent = Path.Combine(root, "agent");
        var bin = Path.Combine(root, "bin");
        Directory.CreateDirectory(agent);
        Directory.CreateDirectory(bin);
        System.Diagnostics.Process? process = null;
        try
        {
            if (customCopyKey)
                await File.WriteAllTextAsync(Path.Combine(agent, "keybindings.json"),
                    "{ \"app.message.copy\": \"ctrl+shift+c\" }");
            if (clipboardUnavailable)
            {
                var waylandCopy = Path.Combine(bin, "wl-copy");
                await File.WriteAllTextAsync(waylandCopy, "#!/bin/sh\ncat >/dev/null\nexit 1\n");
                File.SetUnixFileMode(waylandCopy,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }
            var openBrowser = Path.Combine(bin, "xdg-open");
            await File.WriteAllTextAsync(openBrowser, "#!/bin/sh\nexit 0\n");
            File.SetUnixFileMode(openBrowser,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

            var start = new System.Diagnostics.ProcessStartInfo("/usr/bin/script")
            {
                WorkingDirectory = root,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            start.ArgumentList.Add("-q");
            start.ArgumentList.Add("-e");
            start.ArgumentList.Add("-c");
            start.ArgumentList.Add($"stty rows 24 cols 48; exec dotnet {ShellQuote(typeof(CliArguments).Assembly.Location)} --local --offline --no-session");
            start.ArgumentList.Add("/dev/null");
            start.Environment["PISHARP_AGENT_DIR"] = agent;
            start.Environment["DOTNET_PROCESSOR_COUNT"] = "2";
            foreach (var name in new[] { "DISPLAY", "WAYLAND_DISPLAY", "TERMUX_VERSION", "SSH_CONNECTION", "SSH_CLIENT", "MOSH_CONNECTION" })
                start.Environment.Remove(name);
            foreach (var name in new[] { "ANTHROPIC_API_KEY", "OPENAI_API_KEY", "PISHARP_API_KEY", "PISHARP_BASE_URL", "PISHARP_SETTINGS_PATH" })
                start.Environment.Remove(name);
            if (clipboardUnavailable) start.Environment["WAYLAND_DISPLAY"] = "wayland-test";
            start.Environment["PATH"] = bin + Path.PathSeparator + Environment.GetEnvironmentVariable("PATH");
            process = System.Diagnostics.Process.Start(start);
            Assert.NotNull(process);

            var output = new StringBuilder();
            var outputGate = new object();
            var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var methodSelectorVisible = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var authorizationUrlVisible = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var copyHintsVisible = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var copyOutcomeVisible = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
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
                        var current = output.ToString();
                        if (current.Contains("Qwen3.8-27B-GGUF", StringComparison.Ordinal)) ready.TrySetResult();
                        if (current.Contains("Select an OAuth login method:", StringComparison.Ordinal))
                            methodSelectorVisible.TrySetResult();
                        if (current.Contains("https://claude.ai/oauth/authorize?", StringComparison.Ordinal))
                        {
                            authorizationUrlVisible.TrySetResult();
                            if (current.Contains(customCopyKey ? "Ctrl+Shift+C to copy" : "Ctrl+X to copy",
                                    StringComparison.Ordinal) && current.Contains("Esc to cancel", StringComparison.Ordinal))
                                copyHintsVisible.TrySetResult();
                        }
                        if (current.Contains(clipboardUnavailable
                                ? "Could not copy sign-in URL to clipboard"
                                : "Copied URL to clipboard", StringComparison.Ordinal))
                            copyOutcomeVisible.TrySetResult();
                        if (current.Contains("Login cancelled.", StringComparison.Ordinal)) cancelled.TrySetResult();
                    }
                }
                lock (outputGate) return output.ToString();
            });
            var stderr = process.StandardError.ReadToEndAsync();
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(35));
            try { await ready.Task.WaitAsync(deadline.Token); }
            catch (OperationCanceledException error)
            {
                string partial;
                lock (outputGate) partial = output.ToString();
                throw new TimeoutException("The provider TUI did not become ready.\n" + partial, error);
            }

            await process.StandardInput.WriteAsync("/login anthropic oauth\n");
            await process.StandardInput.FlushAsync();
            await methodSelectorVisible.Task.WaitAsync(deadline.Token);
            await process.StandardInput.WriteAsync("\u001b[B\r");
            await process.StandardInput.FlushAsync();
            await authorizationUrlVisible.Task.WaitAsync(deadline.Token);
            try { await copyHintsVisible.Task.WaitAsync(TimeSpan.FromSeconds(8), deadline.Token); }
            catch (TimeoutException error)
            {
                string partial;
                lock (outputGate) partial = output.ToString();
                throw new TimeoutException("Provider sign-in screen missed its copy/cancel hints.\n" +
                    partial[^Math.Min(partial.Length, 6000)..], error);
            }

            string captured;
            lock (outputGate) captured = output.ToString();
            var authorizationUrl = FindAuthorizationUrl(captured);
            Assert.NotNull(authorizationUrl);
            Assert.Contains("Ctrl+click to open", captured, StringComparison.Ordinal);

            await process.StandardInput.WriteAsync(customCopyKey ? "\u001b[99;6u" : "\u0018");
            await process.StandardInput.FlushAsync();
            try { await copyOutcomeVisible.Task.WaitAsync(TimeSpan.FromSeconds(8), deadline.Token); }
            catch (TimeoutException error)
            {
                string partial;
                lock (outputGate) partial = output.ToString();
                throw new TimeoutException("Provider sign-in prompt did not process the configured copy key.\n" +
                    partial[^Math.Min(partial.Length, 6000)..], error);
            }
            lock (outputGate) captured = output.ToString();
            var osc52 = "\u001b]52;c;" + Convert.ToBase64String(Encoding.UTF8.GetBytes(authorizationUrl!)) + "\u0007";
            if (clipboardUnavailable)
            {
                Assert.Contains("Could not copy sign-in URL to clipboard", captured, StringComparison.Ordinal);
                Assert.DoesNotContain(osc52, captured, StringComparison.Ordinal);
            }
            else
            {
                Assert.Contains("Copied URL to clipboard", captured, StringComparison.Ordinal);
                Assert.Contains(osc52, captured, StringComparison.Ordinal);
            }

            await process.StandardInput.WriteAsync("\u001b");
            await process.StandardInput.FlushAsync();
            await cancelled.Task.WaitAsync(deadline.Token);
            await process.StandardInput.WriteAsync("/quit\n");
            await process.StandardInput.FlushAsync();
            process.StandardInput.Close();
            await process.WaitForExitAsync(deadline.Token);
            captured = await stdout;
            Assert.Equal(0, process.ExitCode);
            Assert.DoesNotContain("MCP sign-in failed", captured, StringComparison.Ordinal);
            Assert.DoesNotContain("Error:", await stderr, StringComparison.Ordinal);
        }
        finally
        {
            if (process is { HasExited: false }) process.Kill(entireProcessTree: true);
            process?.Dispose();
            Directory.Delete(root, recursive: true);
        }
    }

    private static string? FindAuthorizationUrl(string output)
    {
        const string marker = "\u001b]8;;";
        for (var offset = 0; (offset = output.IndexOf(marker, offset, StringComparison.Ordinal)) >= 0;)
        {
            var start = offset + marker.Length;
            var end = output.IndexOf("\u001b\\", start, StringComparison.Ordinal);
            if (end < 0) return null;
            var target = output[start..end];
            if (target.StartsWith("https://claude.ai/oauth/authorize?", StringComparison.Ordinal)) return target;
            offset = end + 2;
        }
        return null;
    }

    private static string ShellQuote(string value) => "'" + value.Replace("'", "'\\''", StringComparison.Ordinal) + "'";

}
