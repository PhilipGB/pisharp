using PiSharp.Cli.Tui;

namespace PiSharp.Tests;

public sealed class TerminalEditorProgressTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FastProgressSettlesWithoutRenderingOrInvokingBackendCancellation(bool fail)
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
