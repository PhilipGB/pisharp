using System.Diagnostics;
using PiSharp.Cli;

namespace PiSharp.Tests;

public sealed class TerminalSuspendPtyTests
{
    [Theory]
    [InlineData("fullscreen")]
    [InlineData("regular")]
    public async Task CtrlZSuspendRestoresTerminalSettingsAndResumePreservesTheDraft(string mode)
    {
        if (!OperatingSystem.IsLinux() || !File.Exists("/usr/bin/script")) return;
        DirectoryInfo? root = new(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "PiSharp.slnx"))) root = root.Parent;
        Assert.NotNull(root);
        var supervisor = Path.Combine(root.FullName, "tools", "parity", "terminal", "job_control.py");
        var cwd = Path.Combine(Path.GetTempPath(), "pisharp-suspend-pty-" + Guid.NewGuid().ToString("N"));
        var agent = Path.Combine(cwd, "agent");
        Directory.CreateDirectory(agent);
        try
        {
            await File.WriteAllTextAsync(Path.Combine(agent, "settings.json"),
                $"{{\"quietStartup\":true,\"tuiMode\":\"{mode}\"}}");
            var arguments = new[] { "python3", supervisor, "dotnet", typeof(CliArguments).Assembly.Location,
                "--local", "--offline", "--no-session", "--no-tools", "--no-extensions" };
            var command = "stty rows 24 cols 80; exec " + string.Join(' ', arguments.Select(ProcessTestHelpers.ShellQuote));
            var start = new ProcessStartInfo("/usr/bin/script")
            {
                WorkingDirectory = cwd,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                ArgumentList = { "-q", "-e", "-c", command, "/dev/null" }
            };
            start.Environment["PISHARP_AGENT_DIR"] = agent;
            using var process = Process.Start(start);
            Assert.NotNull(process);
            var draftVisible = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var suspended = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var resumed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var edited = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var resumeStart = -1;
            const string draft = "draft survives suspension";
            var capture = new TerminalOutputCapture(process, output =>
            {
                if (output.Contains(draft, StringComparison.Ordinal)) draftVisible.TrySetResult();
                if (output.Contains("suspended:tty-restored", StringComparison.Ordinal)) suspended.TrySetResult();
                var offset = Volatile.Read(ref resumeStart);
                if (offset < 0 || offset > output.Length) return;
                var following = output[offset..];
                if ((mode == "regular" || following.Contains("\u001b[?1049h", StringComparison.Ordinal)) &&
                    following.Contains(draft, StringComparison.Ordinal)) resumed.TrySetResult();
                if (following.Contains(draft + "x", StringComparison.Ordinal)) edited.TrySetResult();
            });
            var stderr = process.StandardError.ReadToEndAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            try
            {
                await capture.WaitForRawInputReadyAsync(TimeSpan.FromSeconds(10));
                await SendAsync("\u001b[200~" + draft + "\u001b[201~");
                await draftVisible.Task.WaitAsync(timeout.Token);
                await SendAsync("\u001a");
                await suspended.Task.WaitAsync(timeout.Token);
                Volatile.Write(ref resumeStart, capture.Output.Length);
                await SendAsync("resume\n");
                await resumed.Task.WaitAsync(timeout.Token);
                await SendAsync("x");
                await edited.Task.WaitAsync(timeout.Token);
                await SendAsync("\u0003\u0003");
                await process.WaitForExitAsync(timeout.Token);
                Assert.Equal(0, process.ExitCode);
                Assert.DoesNotContain("Shortcut action failed:", await capture.Completion);
                Assert.Empty(await stderr);
            }
            finally
            {
                if (!process.HasExited) process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
                await capture.Completion.WaitAsync(TimeSpan.FromSeconds(5));
            }

            async Task SendAsync(string value)
            {
                await process.StandardInput.WriteAsync(value);
                await process.StandardInput.FlushAsync();
            }
        }
        finally { Directory.Delete(cwd, recursive: true); }
    }
}
