using System.Text.Json;
using PiSharp.Runtime;

namespace PiSharp.Tests;

public sealed class LsCancellationTests
{
    [Fact]
    public async Task AbortSettlesBeforePendingDirectoryWorkWithoutBlockingAnotherListing()
    {
        using var source = SearchProcessProtocolTests.SourceFixture("ls", "cancellation");
        var expected = source.RootElement;
        var root = Path.Combine(Path.GetTempPath(), "pisharp-ls-cancel-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var drained = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        using var cancellation = new CancellationTokenSource();
        try
        {
            foreach (var file in expected.GetProperty("filesAfter").EnumerateObject())
                await File.WriteAllTextAsync(Path.Combine(root, file.Name), file.Value.GetString());
            var tool = new DirectoryListingTool(root, () =>
            {
                ready.TrySetResult();
                try { release.Wait(); }
                finally { drained.TrySetResult(); }
            });
            var execution = tool.List(cancellationToken: cancellation.Token);
            await ready.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(expected.GetProperty("independent").GetProperty("content")[0].GetProperty("text").GetString(),
                await new DirectoryListingTool(root).List().WaitAsync(TimeSpan.FromSeconds(10)));
            cancellation.Cancel();
            var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => execution.WaitAsync(TimeSpan.FromSeconds(10)));
            Assert.Equal(expected.GetProperty("outcome").GetProperty("error").GetString(), error.Message);
            Assert.False(drained.Task.IsCompleted);
            release.Set();
            await drained.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(expected.GetProperty("continuation").GetProperty("content")[0].GetProperty("text").GetString(),
                await new DirectoryListingTool(root).List());
            foreach (var file in expected.GetProperty("filesAfter").EnumerateObject())
                Assert.Equal(file.Value.GetString(), await File.ReadAllTextAsync(Path.Combine(root, file.Name)));
        }
        finally
        {
            cancellation.Cancel();
            release.Set();
            if (ready.Task.IsCompleted) await drained.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Directory.Delete(root, recursive: true);
        }
    }
}
