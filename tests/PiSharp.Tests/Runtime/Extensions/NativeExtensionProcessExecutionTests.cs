using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;
using PiSharp.Extensions.Abstractions;
using PiSharp.Runtime.Extensions;
using PiSharp.Runtime.Tools;

namespace PiSharp.Tests;

[Collection("Process timing")]
public sealed class NativeExtensionProcessExecutionTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "pisharp-exec-" + Guid.NewGuid().ToString("N"));
    internal static string FixtureAssembly => Path.Combine(AppContext.BaseDirectory, "NativeExecFixture", "NativeExecExtension.dll");

    public NativeExtensionProcessExecutionTests() => Directory.CreateDirectory(_directory);

    [Fact]
    public async Task ArgumentsAreLiteralAndDefaultDirectoryEnvironmentAndIgnoredInputArePreserved()
    {
        using var executor = new NativeExtensionProcessExecutor(_directory);
        string[] arguments = ["argument with spaces", "quoted \"text\"", "", "漢🙂", "$(touch should-not-exist)",
            "; echo injected", "*", "--flag", "a\\b"];
        var result = await executor.ExecuteAsync("dotnet", [FixtureAssembly, "inspect", .. arguments]);
        using var data = JsonDocument.Parse(result.Stdout);
        Assert.Equal(arguments, data.RootElement.GetProperty("Arguments").EnumerateArray().Select(value => value.GetString()));
        Assert.Equal(_directory, data.RootElement.GetProperty("WorkingDirectory").GetString());
        Assert.Equal(Environment.GetEnvironmentVariable("PATH"), data.RootElement.GetProperty("Environment").GetString());
        Assert.Equal(-1, data.RootElement.GetProperty("Input").GetInt32());
        Assert.Empty(Directory.GetFiles(_directory));
        Assert.Equal(0, result.ExitCode);
        Assert.False(result.Killed);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WorkingDirectoryOverrideResolvesAgainstCapturedHostDirectory(bool absolute)
    {
        using var executor = new NativeExtensionProcessExecutor(_directory);
        var child = Directory.CreateDirectory(Path.Combine(_directory, "child")).FullName;
        var result = await executor.ExecuteAsync("dotnet", [FixtureAssembly, "inspect"],
            new() { WorkingDirectory = absolute ? child : "child" });
        using var data = JsonDocument.Parse(result.Stdout);
        Assert.Equal(child, data.RootElement.GetProperty("WorkingDirectory").GetString());
    }

    [Fact]
    public async Task InvalidDirectoryAndMissingExecutableAreStartFailures()
    {
        using var executor = new NativeExtensionProcessExecutor(_directory);
        await Assert.ThrowsAsync<DirectoryNotFoundException>(() => executor.ExecuteAsync("dotnet", [],
            new() { WorkingDirectory = "missing" }));
        await Assert.ThrowsAsync<Win32Exception>(() => executor.ExecuteAsync("pisharp-missing-" + Guid.NewGuid(), []));
    }

    [Fact]
    public async Task InvalidArgumentsAndTimeoutFailBeforeLaunching()
    {
        using var executor = new NativeExtensionProcessExecutor(_directory);
        await Assert.ThrowsAsync<ArgumentException>(() => executor.ExecuteAsync("dotnet", ["nul\0argument"]));
        await Assert.ThrowsAsync<ArgumentException>(() => executor.ExecuteAsync("dotnet", [null!]));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => executor.ExecuteAsync("dotnet", [],
            new() { Timeout = TimeSpan.MaxValue }));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task NonpositiveTimeoutIsDisabled(int milliseconds)
    {
        using var executor = new NativeExtensionProcessExecutor(_directory);
        var result = await executor.ExecuteAsync("dotnet", [FixtureAssembly, "output", "ok", "", "0"],
            new() { Timeout = TimeSpan.FromMilliseconds(milliseconds) });
        Assert.Equal(new("ok", "", 0, false), result);
    }

    [Theory]
    [InlineData("stdout", "", 0)]
    [InlineData("", "stderr", 0)]
    [InlineData("", "", 0)]
    [InlineData("", "failure", 7)]
    [InlineData("\u001b[31mred\r\n\0BOM:\ufeff🙂", "\r\nerror", 0)]
    public async Task StreamsAndNonzeroExitCodesAreReturnedWithoutFormatting(string stdout, string stderr, int code)
    {
        using var executor = new NativeExtensionProcessExecutor(_directory);
        var result = await executor.ExecuteAsync("dotnet", [FixtureAssembly, "output", stdout.Replace('\0', '␀'), stderr, code.ToString()]);
        Assert.Equal(new PiSharpExtensionExecResult(stdout, stderr, code, false), result);
    }

    [Fact]
    public async Task LargeSimultaneousStreamsPreserveMultibyteSequencesAcrossReads()
    {
        using var executor = new NativeExtensionProcessExecutor(_directory);
        var result = await executor.ExecuteAsync("dotnet", [FixtureAssembly, "large", "256"]);
        var expected = string.Concat(Enumerable.Repeat(new string('a', 8191) + "€漢🙂\u001b[31m\r\n", 256));
        Assert.Equal(expected, result.Stdout);
        Assert.Equal(expected, result.Stderr);
        Assert.False(result.Killed);
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task OversizedCaptureFailsExplicitlyAndExecutorRemainsUsable()
    {
        using var executor = new NativeExtensionProcessExecutor(_directory);
        var error = await Assert.ThrowsAsync<IOException>(() => executor.ExecuteAsync("dotnet",
            [FixtureAssembly, "large", "9000"]));
        Assert.Contains(DirectProcessExecution.MaximumOutputBytes.ToString(), error.InnerException!.Message);
        Assert.Equal("again", (await executor.ExecuteAsync("dotnet", [FixtureAssembly, "output", "again", "", "0"])).Stdout);
    }

    [Fact]
    public async Task CancellationBeforeStartCreatesNoProcess()
    {
        using var executor = new NativeExtensionProcessExecutor(_directory);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        var marker = Path.Combine(_directory, "never-started");
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => executor.ExecuteAsync("dotnet",
            [FixtureAssembly, "wait", marker], cancellationToken: cancelled.Token));
        Assert.False(File.Exists(marker));
        Assert.Equal(0, (await executor.ExecuteAsync("dotnet", [FixtureAssembly, "output", "", "", "0"])).ExitCode);
    }

    [Fact]
    public async Task CancellationKillsDescendantsAndDrainsBothStreams()
    {
        using var executor = new NativeExtensionProcessExecutor(_directory);
        using var cancelled = new CancellationTokenSource();
        var marker = Path.Combine(_directory, "tree");
        var pending = executor.ExecuteAsync("dotnet", [FixtureAssembly, "tree", marker], cancellationToken: cancelled.Token);
        await ProcessTestHelpers.WaitForFileAsync(marker);
        cancelled.Cancel();
        var result = await pending.WaitAsync(TimeSpan.FromSeconds(15));
        Assert.Equal(new PiSharpExtensionExecResult("ready-output", "ready-error", 0, true), result);
        AssertExited(marker);
        AssertExited(marker + ".child");
    }

    [Fact]
    public async Task CleanupWithoutProcessGroupKillsTreeBeforeLosingTheParent()
    {
        var marker = Path.Combine(_directory, "ungrouped-tree");
        var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false };
        foreach (var argument in new[] { FixtureAssembly, "tree", marker }) start.ArgumentList.Add(argument);
        using var child = Process.Start(start)!;
        try
        {
            await ProcessTestHelpers.WaitForFileAsync(marker);
            await ProcessLifetime.TerminateAsync(child, processGroup: false);
            AssertExited(marker);
            AssertExited(marker + ".child");
        }
        finally
        {
            if (!child.HasExited) child.Kill(entireProcessTree: true);
        }
    }

    [Fact]
    public async Task CleanupWithoutProcessGroupAfterRootExitClosesPipesButCannotFindDescendant()
    {
        var marker = Path.Combine(_directory, "ungrouped-exited-root");
        // Launch directly to exercise the Windows / no-setsid ownership path on every host.
        var start = new ProcessStartInfo(Environment.ProcessPath!)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (var argument in new[] { FixtureAssembly, "inherit", marker, "wait", "7" })
            start.ArgumentList.Add(argument);
        using var root = Process.Start(start)!;
        Process? descendant = null;
        using var stop = new CancellationTokenSource();
        async Task ReadAsync(Stream stream)
        {
            try { await stream.CopyToAsync(Stream.Null, stop.Token); }
            catch (Exception error) when (stop.IsCancellationRequested &&
                error is OperationCanceledException or IOException or ObjectDisposedException)
            { }
        }
        var stdout = ReadAsync(root.StandardOutput.BaseStream);
        var stderr = ReadAsync(root.StandardError.BaseStream);
        try
        {
            await ProcessTestHelpers.WaitForFileAsync(marker);
            descendant = Process.GetProcessById(ProcessTestHelpers.ReadLinuxProcessId(marker));
            await root.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(7, root.ExitCode);
            Assert.False(stdout.IsCompleted);
            Assert.False(stderr.IsCompleted);

            await ProcessLifetime.TerminateAsync(root, processGroup: false);
            Assert.False(descendant.HasExited);
            var lastOutput = Stopwatch.GetTimestamp();
            await ProcessLifetime.DrainOutputAfterExitAsync(root, stdout, stderr, CancellationToken.None,
                stop, () => lastOutput).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(stdout.IsCompletedSuccessfully);
            Assert.True(stderr.IsCompletedSuccessfully);
            Assert.False(descendant.HasExited);
        }
        finally
        {
            if (!root.HasExited) root.Kill(entireProcessTree: true);
            if (descendant is not null)
            {
                if (!descendant.HasExited) descendant.Kill(entireProcessTree: true);
                if (OperatingSystem.IsLinux()) AssertExited(marker);
                else await descendant.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
                descendant.Dispose();
            }
            await ProcessLifetime.StopPumpsAsync(root, stdout, stderr, stop);
        }
    }

    [Fact]
    public async Task TimeoutEscalatesForProcessIgnoringTermination()
    {
        using var executor = new NativeExtensionProcessExecutor(_directory);
        var marker = Path.Combine(_directory, "timeout");
        var pending = executor.ExecuteAsync("dotnet", [FixtureAssembly, "wait", marker, "ignore-term"],
            new() { Timeout = TimeSpan.FromSeconds(2) });
        await ProcessTestHelpers.WaitForFileAsync(marker);
        var result = await pending.WaitAsync(TimeSpan.FromSeconds(15));
        Assert.True(result.Killed);
        Assert.Equal(0, result.ExitCode);
        AssertExited(marker);
    }

    [Fact]
    public async Task NaturalExitDisposesEarlierTimeoutBeforeSubsequentExecution()
    {
        using var executor = new NativeExtensionProcessExecutor(_directory);
        var first = await executor.ExecuteAsync("dotnet", [FixtureAssembly, "output", "first", "", "0"],
            new() { Timeout = TimeSpan.FromSeconds(1) });
        Assert.False(first.Killed);
        var marker = Path.Combine(_directory, "later-execution");
        var pending = executor.ExecuteAsync("dotnet", [FixtureAssembly, "release", marker]);
        await ProcessTestHelpers.WaitForFileAsync(marker);
        // Cross the earlier timer's deadline while the second child waits on an explicit release signal.
        await Task.Delay(TimeSpan.FromSeconds(1.2));
        Assert.False(pending.IsCompleted);
        await File.WriteAllTextAsync(marker + ".release", "release");
        var second = await pending;
        Assert.False(second.Killed);
        Assert.Equal(0, second.ExitCode);
    }

    [Theory]
    [InlineData("writer")]
    [InlineData("wait")]
    public async Task InheritedPipesUseSlidingIdleGraceWithoutBlockingForever(string descendant)
    {
        using var executor = new NativeExtensionProcessExecutor(_directory);
        var marker = Path.Combine(_directory, "inherited");
        try
        {
            var result = await executor.ExecuteAsync("dotnet", [FixtureAssembly, "inherit", marker, descendant])
                .WaitAsync(TimeSpan.FromSeconds(15));
            Assert.False(result.Killed);
            Assert.Equal(descendant == "writer" ? string.Concat(Enumerable.Repeat("tail", 100_000)) : "", result.Stdout);
            Assert.Equal(descendant == "writer" ? string.Concat(Enumerable.Repeat("err", 100_000)) : "", result.Stderr);
        }
        finally
        {
            if (File.Exists(marker))
            {
                try
                {
                    using var child = Process.GetProcessById(ProcessTestHelpers.ReadLinuxProcessId(marker));
                    if (!child.HasExited) child.Kill(entireProcessTree: true);
                }
                catch (ArgumentException) { }
            }
        }
    }

    [Fact]
    public async Task DisposalCancelsActiveProcessAndRevokesHandle()
    {
        var executor = new NativeExtensionProcessExecutor(_directory);
        var marker = Path.Combine(_directory, "owned");
        var running = executor.ExecuteAsync("dotnet", [FixtureAssembly, "tree", marker]);
        await ProcessTestHelpers.WaitForFileAsync(marker);
        executor.Dispose();
        Assert.True((await running).Killed);
        AssertExited(marker);
        AssertExited(marker + ".child");
        Assert.Throws<ObjectDisposedException>(() => { _ = executor.ExecuteAsync("dotnet", []); });
    }

    [Fact]
    public async Task BufferedBytesSurviveIdleGraceWhileReadersAwaitAnExplicitRelease()
    {
        if (OperatingSystem.IsWindows()) return;
        var start = new ProcessStartInfo(Environment.ProcessPath!)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (var argument in new[] { FixtureAssembly, "output", "pending-output", "pending-error", "0" })
            start.ArgumentList.Add(argument);
        using var child = Process.Start(start)!;
        await child.WaitForExitAsync();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task<string> ReadAsync(StreamReader reader)
        {
            await release.Task;
            return await reader.ReadToEndAsync();
        }
        var stdout = ReadAsync(child.StandardOutput);
        var stderr = ReadAsync(child.StandardError);
        using var stop = new CancellationTokenSource();
        var lastOutput = Stopwatch.GetTimestamp();
        var draining = ProcessLifetime.DrainOutputAfterExitAsync(child, stdout, stderr, CancellationToken.None,
            stop, () => lastOutput);
        try
        {
            await Task.Delay(TimeSpan.FromMilliseconds(250));
            Assert.False(draining.IsCompleted);
        }
        finally { release.SetResult(); }
        await draining.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("pending-output", await stdout);
        Assert.Equal("pending-error", await stderr);
    }

    [Fact]
    public async Task CancellationDuringDescendantDrainPreservesAlreadyExitedRootsCode()
    {
        if (!OperatingSystem.IsLinux()) return;
        using var executor = new NativeExtensionProcessExecutor(_directory);
        using var cancelled = new CancellationTokenSource();
        var marker = Path.Combine(_directory, "draining-root");
        var pending = executor.ExecuteAsync("dotnet", [FixtureAssembly, "inherit", marker, "stream", "7"],
            cancellationToken: cancelled.Token);
        await ProcessTestHelpers.WaitForFileAsync(marker + ".parent");
        try
        {
            using var root = Process.GetProcessById(ProcessTestHelpers.ReadLinuxProcessId(marker + ".parent"));
            await root.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
        }
        catch (ArgumentException) { }
        cancelled.Cancel();
        var result = await pending.WaitAsync(TimeSpan.FromSeconds(15));
        Assert.True(result.Killed);
        Assert.Equal(7, result.ExitCode);
        AssertExited(marker);
    }

    private static void AssertExited(string marker)
    {
        if (OperatingSystem.IsLinux()) ProcessTestHelpers.WaitForLinuxProcessExit(ProcessTestHelpers.ReadLinuxProcessId(marker));
    }

    public void Dispose() => Directory.Delete(_directory, recursive: true);
}
