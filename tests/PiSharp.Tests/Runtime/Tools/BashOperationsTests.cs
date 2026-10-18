using System.Text;
using PiSharp.Runtime.Tools;

namespace PiSharp.Tests.Runtime.Tools;

public sealed class BashOperationsTests : IDisposable
{
    private readonly string _workingDirectory = Path.Combine(Path.GetTempPath(), "pisharp-bash-ops-" + Guid.NewGuid().ToString("N"));

    public BashOperationsTests() => Directory.CreateDirectory(_workingDirectory);

    [Fact]
    public async Task CustomOperationUsesPrefixedCommandAndHostOutputPipeline()
    {
        var shell = new ShellExecution(_workingDirectory, shellPath: null, shellCommandPrefix: "export PREFIX=enabled");
        var updates = new List<string>();
        string? observedCommand = null;
        string? observedWorkingDirectory = null;

        var result = await shell.ExecuteWithOperationsAsync("printf hello", async (command, cwd, outputSink, cancellationToken) =>
        {
            observedCommand = command;
            observedWorkingDirectory = cwd;
            await outputSink(Encoding.UTF8.GetBytes("\u001b[31mhello\u001b[0m\r\n"));
            return 23;
        }, updates.Add, CancellationToken.None);

        Assert.Equal("export PREFIX=enabled\nprintf hello", observedCommand);
        Assert.Equal(_workingDirectory, observedWorkingDirectory);
        Assert.Equal("hello\n", result.Output);
        Assert.Equal("hello\n", result.DisplayOutput);
        Assert.Equal(23, result.ExitCode);
        Assert.False(result.Cancelled);
        Assert.False(result.Truncated);
        Assert.Equal(["hello\n"], updates);
    }

    [Fact]
    public async Task CustomOperationOutputUsesNormalTruncationAndFullOutputCapture()
    {
        var shell = new ShellExecution(_workingDirectory, shellPath: null, shellCommandPrefix: null);
        var bytes = Encoding.UTF8.GetBytes(string.Join('\n', Enumerable.Range(1, 3000)));

        var result = await shell.ExecuteWithOperationsAsync("large", async (_, _, outputSink, _) =>
        {
            await outputSink(bytes);
            return 0;
        }, onUpdate: null, CancellationToken.None);

        try
        {
            Assert.True(result.Truncated);
            Assert.NotNull(result.FullOutputPath);
            Assert.True(File.Exists(result.FullOutputPath));
            Assert.Contains("3000", result.Output, StringComparison.Ordinal);
            Assert.Contains("Full output:", result.DisplayOutput, StringComparison.Ordinal);
        }
        finally
        {
            if (result.FullOutputPath is not null && File.Exists(result.FullOutputPath))
                File.Delete(result.FullOutputPath);
        }
    }

    [Fact]
    public async Task CallerCancellationReachesCustomOperationAndReturnsCapturedCancellationResult()
    {
        var shell = new ShellExecution(_workingDirectory, shellPath: null, shellCommandPrefix: null);
        using var cancellation = new CancellationTokenSource();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var execution = shell.ExecuteWithOperationsAsync("blocked", async (_, _, outputSink, cancellationToken) =>
        {
            await outputSink(Encoding.UTF8.GetBytes("before cancellation"));
            started.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return 0;
        }, onUpdate: null, cancellation.Token);

        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();
        var result = await execution.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(result.Cancelled);
        Assert.Null(result.ExitCode);
        Assert.Equal("before cancellation", result.Output);
    }

    [Fact]
    public async Task AbortBashCancelsCustomOperation()
    {
        var shell = new ShellExecution(_workingDirectory, shellPath: null, shellCommandPrefix: null);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var execution = shell.ExecuteWithOperationsAsync("blocked", async (_, _, _, cancellationToken) =>
        {
            started.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return 0;
        }, onUpdate: null, CancellationToken.None);

        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        shell.AbortBash();
        var result = await execution.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(result.Cancelled);
        Assert.Null(result.ExitCode);
    }

    public void Dispose()
    {
        if (Directory.Exists(_workingDirectory)) Directory.Delete(_workingDirectory, recursive: true);
    }
}
