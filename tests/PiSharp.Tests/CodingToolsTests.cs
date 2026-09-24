using System.Text;
using PiSharp.Cli;
using PiSharp.Runtime;
using PiSharp.Runtime.Tools;

namespace PiSharp.Tests;

public sealed class CodingToolsTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "pisharp-test-" + Guid.NewGuid());
    public CodingToolsTests() => Directory.CreateDirectory(_dir);
    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public async Task WriteReadAndEditResolveAgainstWorkingDirectory()
    {
        var tools = new CodingTools(_dir);
        Assert.Equal("Successfully wrote to nested/file.txt", await tools.Write("nested/file.txt", "one\r\ntwo\r\n"));
        Assert.Contains("one\r\ntwo", await tools.Read("nested/file.txt"));
        Assert.Equal("Successfully replaced 1 block(s) in nested/file.txt.", await tools.Edit("nested/file.txt", "one\ntwo", "three\nfour"));
        Assert.Equal("three\r\nfour\r\n", await File.ReadAllTextAsync(Path.Combine(_dir, "nested/file.txt")));
    }

    [Fact]
    public async Task EditFailsWithoutMutatingWhenAmbiguousOrAbsent()
    {
        var tools = new CodingTools(_dir);
        await tools.Write("file", "x x");
        Assert.Contains("2 occurrences", (await Assert.ThrowsAsync<ToolFailureException>(() => tools.Edit("file", "x", "y"))).Message);
        Assert.Contains("Could not find", (await Assert.ThrowsAsync<ToolFailureException>(() => tools.Edit("file", "z", "y"))).Message);
        Assert.Equal("x x", await tools.Read("file"));
    }

    [Fact]
    public async Task AtomicWritePreservesTargetOnCancellationAndFollowsSymlinks()
    {
        var tools = new CodingTools(_dir);
        await tools.Write("actual.txt", "before");
        var actual = Path.Combine(_dir, "actual.txt");
        if (OperatingSystem.IsLinux())
        {
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(actual));
            File.SetUnixFileMode(actual, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
        using (var cancelled = new CancellationTokenSource())
        {
            cancelled.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => tools.Write("actual.txt", "after", cancelled.Token));
        }
        Assert.Equal("before", await File.ReadAllTextAsync(actual));
        if (OperatingSystem.IsLinux())
        {
            File.CreateSymbolicLink(Path.Combine(_dir, "alias.txt"), actual);
            await tools.Write("alias.txt", "after");
            Assert.Equal("after", await File.ReadAllTextAsync(actual));
            Assert.True(new FileInfo(Path.Combine(_dir, "alias.txt")).LinkTarget is not null);
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, File.GetUnixFileMode(actual));
        }
        Assert.Empty(Directory.EnumerateFiles(_dir, ".pisharp-*.tmp"));
    }

    [Fact]
    public async Task BashReportsExitAndCanTimeOut()
    {
        var tools = new CodingTools(_dir);
        if (!OperatingSystem.IsWindows())
        {
            Assert.Equal(_dir + "\n", await tools.Bash("pwd"));
            if (Environment.GetEnvironmentVariable("HOME") is { } home)
                Assert.Equal(home, await tools.Bash("printf '%s' \"$HOME\""));
            var inheritedPath = await tools.Bash("printf '%s' \"$PATH\"");
            Assert.Contains(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                inheritedPath.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries));
        }
        Assert.Equal("line\n", await tools.Bash("printf 'line\\n'"));
        var combined = await tools.Bash("printf stdout-marker; printf stderr-marker >&2");
        Assert.Contains("stdout-marker", combined);
        Assert.Contains("stderr-marker", combined);
        Assert.Equal(7, (await Assert.ThrowsAsync<ToolFailureException>(() => tools.Bash("exit 7"))).ExitCode);
        Assert.Contains("timed out after 1 seconds", (await Assert.ThrowsAsync<ToolFailureException>(() => tools.Bash("sleep 10", timeout: 1))).Message);
    }

    [Fact]
    public async Task BashCancellationAndTimeoutKillTheProcessGroup()
    {
        if (!OperatingSystem.IsLinux()) return;
        var tools = new CodingTools(_dir);
        var cancelledPid = Path.Combine(_dir, "cancelled-child.pid");
        var cancelledStart = Path.Combine(_dir, "cancelled-child.started");
        using var abort = new CancellationTokenSource();
        var cancelled = tools.Bash($"sleep 10 & child=$!; printf '%s\\n' \"$child\" > {ProcessTestHelpers.ShellQuote(cancelledPid + ".tmp")}; mv {ProcessTestHelpers.ShellQuote(cancelledPid + ".tmp")} {ProcessTestHelpers.ShellQuote(cancelledPid)}; touch {ProcessTestHelpers.ShellQuote(cancelledStart)}; wait \"$child\"",
            cancellationToken: abort.Token);
        await ProcessTestHelpers.WaitForFileAsync(cancelledStart);
        var cancelledChild = ProcessTestHelpers.ReadLinuxProcessId(cancelledPid);
        abort.Cancel();
        Assert.Contains("Command aborted", (await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled)).Message);
        ProcessTestHelpers.WaitForLinuxProcessExit(cancelledChild);

        var timeoutPid = Path.Combine(_dir, "timeout-child.pid");
        var timeoutStart = Path.Combine(_dir, "timeout-child.started");
        var timedOut = tools.Bash($"sleep 10 & child=$!; printf '%s\\n' \"$child\" > {ProcessTestHelpers.ShellQuote(timeoutPid + ".tmp")}; mv {ProcessTestHelpers.ShellQuote(timeoutPid + ".tmp")} {ProcessTestHelpers.ShellQuote(timeoutPid)}; touch {ProcessTestHelpers.ShellQuote(timeoutStart)}; wait \"$child\"",
            timeout: 0.5);
        var result = await Assert.ThrowsAsync<ToolFailureException>(() => timedOut);
        Assert.Contains("timed out after 0.5 seconds", result.Message);
        Assert.True(File.Exists(timeoutStart));
        ProcessTestHelpers.WaitForLinuxProcessExit(ProcessTestHelpers.ReadLinuxProcessId(timeoutPid));
    }

    [Fact]
    public async Task BashHonorsShellPathAndRejectsInvalidTimeoutsAndWorkingDirectories()
    {
        if (OperatingSystem.IsWindows() || !File.Exists("/bin/sh")) return;
        var tools = new CodingTools(_dir, "/bin/sh");
        Assert.Equal("not-bash", await tools.Bash("printf '%s' \"${BASH_VERSION:-not-bash}\""));
        Assert.Contains("Custom shell path not found", (await Assert.ThrowsAsync<ToolFailureException>(
            () => new CodingTools(_dir, Path.Combine(_dir, "missing-shell")).Bash("true"))).Message);
        Assert.Contains("finite number", (await Assert.ThrowsAsync<ToolFailureException>(
            () => tools.Bash("true", timeout: double.NaN))).Message);
        Assert.Contains("finite number", (await Assert.ThrowsAsync<ToolFailureException>(
            () => tools.Bash("true", timeout: double.PositiveInfinity))).Message);
        Assert.Contains("maximum", (await Assert.ThrowsAsync<ToolFailureException>(
            () => tools.Bash("true", timeout: int.MaxValue))).Message);
        Assert.Contains("finite number", (await Assert.ThrowsAsync<ToolFailureException>(
            () => tools.Bash("true", timeout: 0))).Message);

        var missingPath = Path.Combine(_dir, "deleted-cwd");
        Directory.CreateDirectory(missingPath);
        var missingDirectory = new CodingTools(missingPath);
        Directory.Delete(missingPath, recursive: true);
        var missing = await Assert.ThrowsAsync<ToolFailureException>(() => missingDirectory.Bash("true"));
        Assert.Contains($"Working directory does not exist: {missingPath}", missing.Message);
    }

    [Fact]
    public async Task BashCapturesOutputFromAChildThatWritesAfterTheShellExits()
    {
        if (!OperatingSystem.IsLinux()) return;
        var output = await new CodingTools(_dir).Bash(
            "parent=$$; (while kill -0 \"$parent\" 2>/dev/null; do :; done; printf 'late-after-parent-exit\\n') & printf 'parent-finished\\n'");
        Assert.True(output.IndexOf("parent-finished", StringComparison.Ordinal) <
            output.IndexOf("late-after-parent-exit", StringComparison.Ordinal), output);
    }

    [Fact]
    public async Task ShellOutputBufferStreamsUtf8AndFlushesAnIncompleteFinalSequence()
    {
        await using var output = new ShellOutputBuffer();
        Assert.Equal("A", await output.AppendAsync(new byte[] { 0x41, 0xE2 }));
        Assert.Equal("", await output.AppendAsync(new byte[] { 0x82 }));
        Assert.Equal("€", await output.AppendAsync(new byte[] { 0xAC }));
        Assert.Equal("", await output.AppendAsync(new byte[] { 0xF0, 0x9F }));
        Assert.Equal("A€�", await output.FinishAsync());
    }
    [Fact]
    public async Task BashBoundsOutputAndRetainsPrivateCompleteLog()
    {
        var tools = new CodingTools(_dir);
        var output = await tools.Bash("seq 1 3000");
        Assert.Contains("3000", output);
        Assert.Contains("Full output: ", output);
        var notice = output.IndexOf("\n\n[Showing lines ", StringComparison.Ordinal);
        Assert.True(notice > 0, output);
        Assert.StartsWith("1001\n1002\n", output[..notice]);
        Assert.Equal(2000, output[..notice].Split('\n').Length);
        var path = output.Split("Full output: ")[1].Split(']')[0];
        try
        {
            Assert.Contains("1\n2\n", await File.ReadAllTextAsync(path));
            Assert.Contains("2999\n3000", await File.ReadAllTextAsync(path));
            if (OperatingSystem.IsLinux())
                Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task BashTruncatesAnOversizedLineAndRetainsItsCompleteLog()
    {
        if (!OperatingSystem.IsLinux()) return;
        var output = await new CodingTools(_dir).Bash("printf '%100000s' | tr ' ' x");
        var notice = output.IndexOf("\n\n[Showing last 50.0KB of line 1 (line is 97.7KB). Full output: ", StringComparison.Ordinal);
        Assert.True(notice > 0, output);
        Assert.Equal(50 * 1024, Encoding.UTF8.GetByteCount(output[..notice]));
        var path = output[(notice + "\n\n[Showing last 50.0KB of line 1 (line is 97.7KB). Full output: ".Length)..].Split(']')[0];
        try { Assert.Equal(new string('x', 100_000), await File.ReadAllTextAsync(path)); }
        finally { File.Delete(path); }
    }
}
