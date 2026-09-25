using System.Diagnostics;
using PiSharp.Cli;

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
            var assembly = typeof(CliArguments).Assembly.Location;
            var start = new ProcessStartInfo("/usr/bin/script")
            {
                WorkingDirectory = cwd,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                ArgumentList = { "-q", "-e", "-c", $"dotnet '{assembly}' --local --session-dir '{cwd}/sessions' --no-tools", "/dev/null" }
            };
            using var process = Process.Start(start);
            Assert.NotNull(process);
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            await process.StandardInput.WriteAsync($"/name first\n/new\n/name second\n/sessions\n/resume first\n/session\n/export {cwd}/export.html\n/clone\n/session\n/quit\n");
            process.StandardInput.Close();
            using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(12));
            try { await process.WaitForExitAsync(limit.Token); }
            catch (OperationCanceledException) { process.Kill(entireProcessTree: true); throw; }
            var output = await stdout;
            Assert.Equal(0, process.ExitCode);
            Assert.Contains("Resumed", output);
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
            await process.StandardInput.WriteAsync($"/fork\n/fork {userId[..12]}\n");
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
}
