using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using PiSharp.Cli;

namespace PiSharp.Tests;

public sealed class BashShutdownProcessTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(15)]
    [InlineData(1)]
    public async Task RpcShutdownTerminatesActiveBashAndDetachedDescendants(int signal)
    {
        if (!OperatingSystem.IsLinux()) return;
        var root = Path.Combine(Path.GetTempPath(), "pisharp-bash-shutdown-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        Process? process = null;
        var childPath = Path.Combine(root, "child.pid");
        var grandchildPath = Path.Combine(root, "grandchild.pid");
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        try
        {
            var start = new ProcessStartInfo("dotnet")
            {
                WorkingDirectory = root,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            start.ArgumentList.Add(typeof(CliArguments).Assembly.Location);
            foreach (var argument in new[] { "--mode", "rpc", "--local", "--offline", "--no-session", "--no-extensions", "--no-skills" })
                start.ArgumentList.Add(argument);
            start.Environment["PISHARP_AGENT_DIR"] = Path.Combine(root, "agent");
            process = Process.Start(start)!;
            var stdout = process.StandardOutput.ReadToEndAsync(deadline.Token);
            var stderr = process.StandardError.ReadToEndAsync(deadline.Token);
            var ready = Path.Combine(root, "ready");
            var grandchild = $"printf '%s\\n' \"$$\" > {ProcessTestHelpers.ShellQuote(grandchildPath)}; touch {ProcessTestHelpers.ShellQuote(ready)}; exec sleep 60";
            var child = $"printf '%s\\n' \"$$\" > {ProcessTestHelpers.ShellQuote(childPath)}; setsid /bin/sh -c {ProcessTestHelpers.ShellQuote(grandchild)} & wait";
            var command = $"/bin/sh -c {ProcessTestHelpers.ShellQuote(child)} & wait";
            await process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(new { id = "shutdown-bash", type = "bash", command }));
            await process.StandardInput.FlushAsync(deadline.Token);
            await ProcessTestHelpers.WaitForFileAsync(ready, deadline.Token);
            var childId = ProcessTestHelpers.ReadLinuxProcessId(childPath);
            var grandchildId = ProcessTestHelpers.ReadLinuxProcessId(grandchildPath);
            if (signal == 0) process.StandardInput.Close();
            else Assert.Equal(0, kill(process.Id, signal));
            await process.WaitForExitAsync(deadline.Token);
            ProcessTestHelpers.WaitForLinuxProcessExit(childId);
            ProcessTestHelpers.WaitForLinuxProcessExit(grandchildId);
            Assert.Equal(signal == 0 ? 0 : 128 + signal, process.ExitCode);
            Assert.DoesNotContain("Unhandled exception", await stderr);
            await stdout;
        }
        finally
        {
            if (process is not null)
            {
                if (!process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(); }
                process.Dispose();
            }
            foreach (var file in new[] { grandchildPath, childPath })
            {
                if (!File.Exists(file)) continue;
                try { using var descendant = Process.GetProcessById(ProcessTestHelpers.ReadLinuxProcessId(file)); descendant.Kill(entireProcessTree: true); }
                catch (ArgumentException) { }
                catch (InvalidOperationException) { }
            }
            Directory.Delete(root, recursive: true);
        }
    }

    [DllImport("libc", SetLastError = true)]
    private static extern int kill(int processId, int signal);
}
