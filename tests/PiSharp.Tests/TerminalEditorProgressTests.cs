using System.Diagnostics;
using PiSharp.Cli.Tui;

namespace PiSharp.Tests;

public sealed class TerminalEditorProgressTests
{
    [Fact]
    public async Task FastProgressSettlesWithoutRenderingOrInvokingBackendCancellation()
    {
        if (!OperatingSystem.IsLinux()) return;
        const string childMarker = "PISHARP_PROGRESS_PTY_TEST";
        if (Environment.GetEnvironmentVariable(childMarker) == "1")
        {
            await AssertFastProgressAsync(false);
            await AssertFastProgressAsync(true);
            return;
        }

        // Progress owns terminal mode even when the operation finishes before its first frame.
        var start = new ProcessStartInfo("/usr/bin/script")
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        start.ArgumentList.Add("-q");
        start.ArgumentList.Add("-e");
        start.ArgumentList.Add("-c");
        var assembly = typeof(TerminalEditorProgressTests).Assembly.Location;
        var quotedAssembly = "'" + assembly.Replace("'", "'\\''", StringComparison.Ordinal) + "'";
        start.ArgumentList.Add($"dotnet vstest {quotedAssembly} --TestCaseFilter:FullyQualifiedName~TerminalEditorProgressTests.FastProgressSettlesWithoutRenderingOrInvokingBackendCancellation");
        start.ArgumentList.Add("/dev/null");
        start.Environment[childMarker] = "1";
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try { await process.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException) { process.Kill(entireProcessTree: true); throw; }
        var captured = await output;
        Assert.True(process.ExitCode == 0, captured + await error);
        Assert.Contains("Passed:     1", captured);
    }

    private static async Task AssertFastProgressAsync(bool fail)
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        using var screen = new TerminalScreen(output, error, () => 80, () => 24);
        var editor = new TerminalEditor();
        editor.Prefill("draft to keep");
        editor.AttachScreen(screen);
        output.GetStringBuilder().Clear();
        var failure = new InvalidOperationException("operation failed");
        var cancellationCalled = false;
        var afterCancellationCalled = false;
        CancellationToken operationToken = default;

        var operation = editor.RunProgressAsync<int>("Operation", "subject", "Stop?", "confirm",
            (token, update) =>
            {
                operationToken = token;
                return fail ? Task.FromException<int>(failure) : Task.FromResult(42);
            },
            () =>
            {
                cancellationCalled = true;
                return Task.CompletedTask;
            },
            () =>
            {
                afterCancellationCalled = true;
                return Task.CompletedTask;
            });

        if (fail)
        {
            var actual = await Assert.ThrowsAsync<InvalidOperationException>(() => operation);
            Assert.Same(failure, actual);
        }
        else
        {
            var result = await operation;
            Assert.False(result.Cancelled);
            Assert.Equal(42, result.Value);
        }
        Assert.Equal("draft to keep", editor.Draft);
        Assert.DoesNotContain("Starting…", output.ToString());
        Assert.DoesNotContain("Operation", output.ToString());
        Assert.False(cancellationCalled);
        Assert.False(afterCancellationCalled);
        Assert.True(operationToken.IsCancellationRequested);
    }
}
