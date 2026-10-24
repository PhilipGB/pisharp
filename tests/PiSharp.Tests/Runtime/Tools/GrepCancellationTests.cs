using System.Runtime.InteropServices;
using Microsoft.Extensions.AI;
using PiSharp.Runtime;
using PiSharp.Runtime.Tools;

namespace PiSharp.Tests;

public sealed class GrepCancellationTests
{
    [Fact]
    public async Task RealRipgrepReadCanBeCancelledWithoutBlockingIndependentSearchOrRecovery()
    {
        if (!OperatingSystem.IsLinux()) return;
        using var source = SearchProcessProtocolTests.SourceFixture();
        var expected = source.RootElement.GetProperty("realRipgrepCancellation");
        var root = Path.Combine(Path.GetTempPath(), "pisharp-grep-cancel-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        File.SetUnixFileMode(root, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var fifo = Path.Combine(root, "blocked-input");
        Assert.Equal(0, mkfifo(fifo, 0x180));
        using var cancellation = new CancellationTokenSource();
        Task<object?>? blocked = null;
        Task<int>? writerPending = null;
        var writer = -1;
        var children = new List<int>();
        try
        {
            await File.WriteAllTextAsync(Path.Combine(root, "file.txt"), "needle\n");
            var function = new CodingTools(root).CreateAll().OfType<AIFunction>().Single(tool => tool.Name == "grep");
            blocked = function.InvokeAsync(new AIFunctionArguments { ["pattern"] = "needle", ["path"] = "blocked-input" }, cancellation.Token).AsTask();
            writerPending = Task.Run(() => open(fifo, 1 | 0x80000));
            writer = await writerPending.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.True(writer >= 0);
            var parallel = await function.InvokeAsync(new AIFunctionArguments { ["pattern"] = "needle", ["path"] = "file.txt" }).AsTask().WaitAsync(TimeSpan.FromSeconds(10));
            Assert.True(ToolResultOutput.TryRead(parallel, out var parallelText, out _));
            Assert.Equal(expected.GetProperty("parallel").GetProperty("content")[0].GetProperty("text").GetString(), parallelText);
            Assert.True(expected.GetProperty("blockedBeforeCancellation").GetBoolean());
            Assert.False(blocked.IsCompleted);
            children.AddRange(ProcessTestHelpers.FindChildrenWithArgument(fifo));
            Assert.Equal(expected.GetProperty("processCount").GetInt32(), children.Count);
            Assert.NotEmpty(children);
            cancellation.Cancel();
            var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => blocked.WaitAsync(TimeSpan.FromSeconds(10)));
            Assert.Equal(expected.GetProperty("outcome").GetProperty("error").GetString(), error.Message);
            foreach (var child in children) ProcessTestHelpers.WaitForLinuxProcessExit(child);
            var continuation = await function.InvokeAsync(new AIFunctionArguments { ["pattern"] = "needle", ["path"] = "file.txt" });
            Assert.True(ToolResultOutput.TryRead(continuation, out var text, out _));
            Assert.Equal(expected.GetProperty("continuation").GetProperty("content")[0].GetProperty("text").GetString(), text);
            Assert.Equal("needle\n", await File.ReadAllTextAsync(Path.Combine(root, "file.txt")));
        }
        finally
        {
            cancellation.Cancel();
            foreach (var child in ProcessTestHelpers.FindChildrenWithArgument(fifo))
            {
                try { using var process = System.Diagnostics.Process.GetProcessById(child); process.Kill(entireProcessTree: true); }
                catch (ArgumentException) { }
                catch (InvalidOperationException) { }
            }
            if (writerPending is not null && writer < 0)
            {
                var unblock = open(fifo, 0x800 | 0x80000);
                try { writer = await writerPending.WaitAsync(TimeSpan.FromSeconds(5)); }
                finally { if (unblock >= 0) close(unblock); }
            }
            if (writer >= 0) close(writer);
            if (blocked is not null)
                try { await blocked.WaitAsync(TimeSpan.FromSeconds(5)); } catch (OperationCanceledException) { }
            Directory.Delete(root, recursive: true);
        }
    }

    [DllImport("libc", SetLastError = true)]
    private static extern int mkfifo(string path, uint mode);
    [DllImport("libc", SetLastError = true)]
    private static extern int open(string path, int flags);
    [DllImport("libc", SetLastError = true)]
    private static extern int close(int descriptor);
}
