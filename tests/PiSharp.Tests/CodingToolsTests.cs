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
    public async Task WriteToDirectoryUsesNodeEisdirError()
    {
        if (!OperatingSystem.IsLinux()) return;
        var directory = Path.Combine(_dir, "target-directory");
        Directory.CreateDirectory(directory);

        var error = await Assert.ThrowsAsync<ToolFailureException>(() => new CodingTools(_dir).Write("target-directory", "content"));
        Assert.Equal($"EISDIR: illegal operation on a directory, open '{directory}'", error.Message);
    }

    [Fact]
    public async Task WriteUnderFileParentsUsesNodeEexistAndEnotdirErrors()
    {
        if (!OperatingSystem.IsLinux()) return;
        var file = Path.Combine(_dir, "parent-file");
        await File.WriteAllTextAsync(file, "existing");

        var directError = await Assert.ThrowsAsync<ToolFailureException>(() =>
            new CodingTools(_dir).Write("parent-file/child.txt", "content"));
        Assert.Equal($"EEXIST: file already exists, mkdir '{file}'", directError.Message);

        var nestedParent = Path.Combine(file, "child");
        var nestedError = await Assert.ThrowsAsync<ToolFailureException>(() =>
            new CodingTools(_dir).Write("parent-file/child/grandchild.txt", "content"));
        Assert.Equal($"ENOTDIR: not a directory, mkdir '{nestedParent}'", nestedError.Message);
        Assert.Equal("existing", await File.ReadAllTextAsync(file));
    }

    [Fact]
    public async Task WritePermissionDeniedUsesNodeEaccesError()
    {
        if (!OperatingSystem.IsLinux()) return;
        var directory = Path.Combine(_dir, "private-write-dir");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "file.txt");
        try
        {
            File.SetUnixFileMode(directory, UnixFileMode.None);
            try
            {
                await using var probe = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                return;
            }
            catch (UnauthorizedAccessException) { }

            var error = await Assert.ThrowsAsync<ToolFailureException>(() => new CodingTools(_dir).Write("private-write-dir/file.txt", "content"));
            Assert.Equal($"EACCES: permission denied, open '{path}'", error.Message);
        }
        finally
        {
            File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
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
    public async Task FileMutationQueueSerializesExistingTargetsThroughDirectorySymlinks()
    {
        if (!OperatingSystem.IsLinux()) return;

        var actualDirectory = Path.Combine(_dir, "actual");
        var aliasDirectory = Path.Combine(_dir, "alias");
        Directory.CreateDirectory(actualDirectory);
        Directory.CreateSymbolicLink(aliasDirectory, actualDirectory);
        var actualPath = Path.Combine(actualDirectory, "shared.txt");
        var aliasPath = Path.Combine(aliasDirectory, "shared.txt");
        await File.WriteAllTextAsync(actualPath, "before");

        var queue = new FileMutationQueue();
        var firstStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = queue.RunAsync(aliasPath, async () =>
        {
            firstStarted.SetResult();
            await releaseFirst.Task;
            return true;
        }, CancellationToken.None);

        try
        {
            await firstStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
            var second = queue.RunAsync(actualPath, () =>
            {
                secondStarted.SetResult();
                return Task.FromResult(true);
            }, CancellationToken.None);

            Assert.False(secondStarted.Task.IsCompleted);
            releaseFirst.SetResult();
            await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(2));
            Assert.True(secondStarted.Task.IsCompleted);
        }
        finally
        {
            releaseFirst.TrySetResult();
            await first;
        }
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
    public async Task BashSignalTerminationUsesShellExitCodesAndPreservesPartialOutput()
    {
        if (OperatingSystem.IsWindows() || !File.Exists("/bin/bash")) return;
        var tools = new CodingTools(_dir, "/bin/bash");
        foreach (var (signal, exitCode) in new[] { ("KILL", 137), ("TERM", 143) })
        {
            var error = await Assert.ThrowsAsync<ToolFailureException>(() =>
                tools.Bash($"printf 'before-kill\\n'; kill -{signal} $$"));
            Assert.Equal(exitCode, error.ExitCode);
            Assert.Contains("before-kill", error.Message);
            Assert.EndsWith($"Command exited with code {exitCode}", error.Message);
        }
    }

    [Fact]
    public async Task BashTimeoutRetainsFullTruncatedOutputAndReportsItsPath()
    {
        if (OperatingSystem.IsWindows()) return;
        var outputReady = Path.Combine(_dir, "timeout-output-ready");
        var command = $"seq 1 3000; touch {ProcessTestHelpers.ShellQuote(outputReady)}; while :; do :; done";
        var execution = new CodingTools(_dir).Bash(command, timeout: 1.5);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var outputWritten = ProcessTestHelpers.WaitForFileAsync(outputReady, deadline.Token);
        var timedOut = Assert.ThrowsAsync<ToolFailureException>(() => execution);
        await Task.WhenAll(outputWritten, timedOut);

        var error = await timedOut;
        Assert.Contains("Command timed out after 1.5 seconds", error.Message);
        Assert.Contains("[Showing lines 1001-3000 of 3000. Full output: ", error.Message);
        var pathMatch = System.Text.RegularExpressions.Regex.Match(error.Message, @"Full output: ([^\]\n]+)");
        Assert.True(pathMatch.Success, error.Message);
        var fullOutputPath = pathMatch.Groups[1].Value;
        Assert.Contains("1\n2\n3\n", await File.ReadAllTextAsync(fullOutputPath));
        Assert.EndsWith("2998\n2999\n3000\n", await File.ReadAllTextAsync(fullOutputPath));
    }

    [Fact]
    public void DirectBashUpdatesArePublishedImmediatelyWithoutCoalescing()
    {
        var updates = new List<string>();
        using var publisher = new BashOutputUpdates(updates.Add, throttle: false);

        publisher.Append("first");
        Assert.Equal(["first"], updates);
        publisher.Append("second");
        Assert.Equal(["first", "second"], updates);
        publisher.Complete();
        Assert.Equal(["first", "second"], updates);
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
    public async Task AbortBashCancelsToolAndDirectExecutionsOnTheSharedRunner()
    {
        if (!OperatingSystem.IsLinux()) return;
        var tools = new CodingTools(_dir);
        var directStarted = Path.Combine(_dir, "direct-started");
        var toolStarted = Path.Combine(_dir, "tool-started");
        var release = Path.Combine(_dir, "release");
        var direct = tools.ExecuteBashAsync($"touch {ProcessTestHelpers.ShellQuote(directStarted)}; while [ ! -e {ProcessTestHelpers.ShellQuote(release)} ]; do :; done");
        var tool = tools.Bash($"touch {ProcessTestHelpers.ShellQuote(toolStarted)}; while [ ! -e {ProcessTestHelpers.ShellQuote(release)} ]; do :; done");
        await Task.WhenAll(ProcessTestHelpers.WaitForFileAsync(directStarted), ProcessTestHelpers.WaitForFileAsync(toolStarted));

        tools.AbortBash();

        Assert.True((await direct).Cancelled);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => tool);
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
    public async Task DirectBashOutputNormalizesAnsiBinaryControlsAndCarriageReturnsAcrossChunks()
    {
        const string expected = "red\nlinkbyte�";
        var result = await new CodingTools(_dir).ExecuteBashAsync(
            "printf '\\033[31mred\\033[0m\\r\\n\\033]8;;https://example.invalid\\alink\\033]8;;\\a\\001byte\\377'");
        Assert.Equal(expected, result.Output);

        await using var output = new ShellOutputBuffer(normalizeOutput: true);
        await output.AppendAsync(Encoding.UTF8.GetBytes("\u001b[31"));
        await output.AppendAsync(Encoding.UTF8.GetBytes("mred\u001b[0m\r\n\u001b]8;;https://example.invalid"));
        await output.AppendAsync(Encoding.UTF8.GetBytes("\alink\u001b]8;;\a\u0001byte"));
        await output.AppendAsync(new byte[] { 0xff });
        Assert.Equal(expected, await output.FinishAsync());

        string? fullOutputPath = null;
        try
        {
            await using var spill = new ShellOutputBuffer(normalizeOutput: true);
            var ansiOnly = string.Concat(Enumerable.Repeat("\u001b[31m", 11_000));
            await spill.AppendAsync(Encoding.UTF8.GetBytes(ansiOnly));
            await spill.AppendAsync(Encoding.UTF8.GetBytes("red"));
            await spill.AppendAsync(new byte[] { 0xe2 });
            var spilled = await spill.FinishWithMetadataAsync();
            var path = spilled.FullOutputPath ?? throw new InvalidOperationException("Expected a spill file.");
            fullOutputPath = path;
            Assert.False(spilled.Truncated);
            Assert.Equal("red�", spilled.Output);
            Assert.Equal("red�", await File.ReadAllTextAsync(path));
        }
        finally
        {
            if (fullOutputPath is not null && File.Exists(fullOutputPath)) File.Delete(fullOutputPath);
        }
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
