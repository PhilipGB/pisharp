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
            await Task.Delay(700);
            await process.StandardInput.WriteAsync("\u0015/name forked\n/quit\n");
            process.StandardInput.Close();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(12));
            try { await process.WaitForExitAsync(timeout.Token); }
            catch (OperationCanceledException) { process.Kill(entireProcessTree: true); throw; }
            var output = await stdout;
            Assert.Equal(0, process.ExitCode);
            Assert.Contains("draft to change", output);
            Assert.Contains("Forked " + userId[..12], output);
            Assert.Contains("Name: forked", output);
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
}
