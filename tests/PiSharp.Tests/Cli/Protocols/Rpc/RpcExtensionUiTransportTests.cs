using PiSharp.Cli.Protocols;

namespace PiSharp.Tests;

public sealed class RpcExtensionUiTransportTests
{
    [Fact]
    public async Task DisposeWaitsForInFlightPersistentEventWritesAndRejectsLaterEvents()
    {
        using var output = new BlockingTextWriter();
        var transport = new RpcExtensionUiTransport(new JsonLineWriter(output));

        transport.SetStatus("ready", "Ready");
        await output.WriteStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        var firstDispose = transport.DisposeAsync().AsTask();
        var secondDispose = transport.DisposeAsync().AsTask();
        Assert.False(firstDispose.IsCompleted);
        Assert.False(secondDispose.IsCompleted);

        output.ReleaseWrite();
        await Task.WhenAll(firstDispose, secondDispose).WaitAsync(TimeSpan.FromSeconds(2));

        transport.SetStatus("late", "Ignored");

        Assert.Single(output.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries));
        Assert.Contains("\"statusText\":\"Ready\"", output.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("Ignored", output.ToString(), StringComparison.Ordinal);
    }

    private sealed class BlockingTextWriter : StringWriter
    {
        private readonly TaskCompletionSource _releaseWrite = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _blockNextWrite = 1;

        internal TaskCompletionSource WriteStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal void ReleaseWrite() => _releaseWrite.TrySetResult();

        public override async Task WriteAsync(string? value)
        {
            if (Interlocked.Exchange(ref _blockNextWrite, 0) != 0)
            {
                WriteStarted.TrySetResult();
                await _releaseWrite.Task.ConfigureAwait(false);
            }

            await base.WriteAsync(value).ConfigureAwait(false);
        }
    }
}
