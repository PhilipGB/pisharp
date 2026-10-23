using System.Diagnostics;
using PiSharp.Runtime;
using PiSharp.Runtime.Tools;

namespace PiSharp.Tests.Runtime.Tools;

public sealed class BashProcessLifetimeTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancellationKillsChildAndDetachedGrandchildBeforeSettling(bool abortRunner)
    {
        if (!OperatingSystem.IsLinux()) return;
        var root = Path.Combine(Path.GetTempPath(), "pisharp-bash-descendants-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        using var cancellation = new CancellationTokenSource();
        var tools = new CodingTools(root);
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var childPath = Path.Combine(root, "child.pid");
        var grandchildPath = Path.Combine(root, "grandchild.pid");
        var grandchild = $"printf '%s\\n' \"$$\" > {ProcessTestHelpers.ShellQuote(grandchildPath)}; printf 'detached-ready\\n'; exec sleep 60";
        var child = $"printf '%s\\n' \"$$\" > {ProcessTestHelpers.ShellQuote(childPath)}; setsid /bin/sh -c {ProcessTestHelpers.ShellQuote(grandchild)} & wait";
        var command = $"/bin/sh -c {ProcessTestHelpers.ShellQuote(child)} & wait";
        var execution = tools.ExecuteBashAsync(command, text =>
        {
            if (text.Contains("detached-ready", StringComparison.Ordinal)) ready.TrySetResult();
        }, cancellation.Token);
        try
        {
            await ready.Task.WaitAsync(TimeSpan.FromSeconds(10));
            var childId = ProcessTestHelpers.ReadLinuxProcessId(childPath);
            var grandchildId = ProcessTestHelpers.ReadLinuxProcessId(grandchildPath);
            if (abortRunner) tools.AbortBash();
            else cancellation.Cancel();
            var result = await execution.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.True(result.Cancelled);
            Assert.Null(result.ExitCode);
            Assert.Contains("detached-ready", result.Output);
            ProcessTestHelpers.WaitForLinuxProcessExit(childId);
            ProcessTestHelpers.WaitForLinuxProcessExit(grandchildId);
            Assert.Equal("continued", (await tools.ExecuteBashAsync("printf continued")).Output);
        }
        finally
        {
            cancellation.Cancel();
            try { await execution.WaitAsync(TimeSpan.FromSeconds(10)); }
            finally
            {
                foreach (var path in new[] { grandchildPath, childPath })
                {
                    if (!File.Exists(path)) continue;
                    try { using var process = Process.GetProcessById(ProcessTestHelpers.ReadLinuxProcessId(path)); process.Kill(entireProcessTree: true); }
                    catch (ArgumentException) { }
                    catch (InvalidOperationException) { }
                }
                Directory.Delete(root, recursive: true);
            }
        }
    }
}
