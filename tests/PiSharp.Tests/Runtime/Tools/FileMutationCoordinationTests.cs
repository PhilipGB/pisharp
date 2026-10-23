using System.Text.Json;
using PiSharp.Core;
using PiSharp.Runtime;
using PiSharp.Runtime.Tools;

namespace PiSharp.Tests;

public sealed class FileMutationCoordinationTests
{
    public static IEnumerable<object[]> OrderedPairs()
    {
        foreach (var row in ReadPinnedResults().EnumerateArray().Where(row => row.GetProperty("caseId").GetString()!.StartsWith("mutation-order-", StringComparison.Ordinal)))
            yield return [row.GetProperty("firstTool").GetString()!, row.GetProperty("secondTool").GetString()!, row.GetProperty("finalContent").GetString()!,
                row.GetProperty("firstResult").GetProperty("content")[0].GetProperty("text").GetString()!,
                row.GetProperty("secondResult").GetProperty("content")[0].GetProperty("text").GetString()!];
    }

    [Theory]
    [MemberData(nameof(OrderedPairs))]
    public async Task RealMutationsRespectRegistrationOrderAndPreserveUnrelatedProgress(string firstTool, string secondTool,
        string expected, string firstExpected, string secondExpected)
    {
        var root = CreateRoot();
        var queue = new FileMutationQueue();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<bool>? blocker = null;
        Task<string>? first = null, second = null;
        try
        {
            await File.WriteAllTextAsync(Path.Combine(root, "shared"), "alpha\nbeta\n");
            var mutations = new FileMutations(root, queue);
            blocker = queue.RunAsync(Path.Combine(root, "shared"), async () => { await release.Task; return true; }, CancellationToken.None);
            first = Invoke(firstTool, true);
            second = Invoke(secondTool, false);
            Assert.False(first.IsCompleted);
            Assert.False(second.IsCompleted);
            await mutations.WriteAsync("unrelated", "independent").WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal("independent", await File.ReadAllTextAsync(Path.Combine(root, "unrelated")));
            Assert.Equal("alpha\nbeta\n", await File.ReadAllTextAsync(Path.Combine(root, "shared")));

            release.SetResult();
            await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(5));

            Assert.Equal(firstExpected, await first);
            Assert.Equal(secondExpected, await second);
            Assert.Equal(expected, await File.ReadAllTextAsync(Path.Combine(root, "shared")));
            Assert.Empty(Directory.EnumerateFiles(root, ".pisharp-*.tmp"));

            async Task<string> Invoke(string tool, bool isFirst) => tool == "write"
                ? await mutations.WriteAsync("shared", isFirst ? "first\n" : "second\n")
                : (await mutations.EditAsync("shared", [new TextEdit(isFirst ? "alpha" : firstTool == "write" ? "first" : "beta",
                    isFirst ? "ALPHA" : firstTool == "write" ? "edited" : "BETA")], CancellationToken.None)).Text;
        }
        finally
        {
            release.TrySetResult();
            if (blocker is not null) await blocker.WaitAsync(TimeSpan.FromSeconds(5));
            if (first is not null) await first.WaitAsync(TimeSpan.FromSeconds(5));
            if (second is not null) await second.WaitAsync(TimeSpan.FromSeconds(5));
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task CancellationWhileWaitingNeverRunsTheMutationOrReleasesAnActiveWriter()
    {
        var root = CreateRoot();
        var queue = new FileMutationQueue();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<bool>? active = null;
        Task<string>? following = null;
        try
        {
            var target = Path.Combine(root, "shared");
            await File.WriteAllTextAsync(target, "original");
            var mutations = new FileMutations(root, queue);
            active = queue.RunAsync(target, async () => { await release.Task; return true; }, CancellationToken.None);
            using var cancellation = new CancellationTokenSource();
            var cancelled = mutations.WriteAsync("shared", "must not commit", cancellation.Token);

            cancellation.Cancel();

            var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await cancelled.WaitAsync(TimeSpan.FromSeconds(5)));
            var pinned = ReadPinnedResults().EnumerateArray().Single(row => row.GetProperty("caseId").GetString() == "mutation-cancelled-waiter");
            Assert.Equal(pinned.GetProperty("cancelled").GetProperty("error").GetString(), error.Message);
            following = mutations.WriteAsync("shared", "following");
            Assert.False(following.IsCompleted);
            Assert.Equal("original", await File.ReadAllTextAsync(target));
            release.SetResult();
            await following.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal("following", await File.ReadAllTextAsync(target));
        }
        finally
        {
            release.TrySetResult();
            if (active is not null) await active.WaitAsync(TimeSpan.FromSeconds(5));
            if (following is not null) await following.WaitAsync(TimeSpan.FromSeconds(5));
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task CancelledPreparedWriteKeepsItsQueueUntilCleanupAndLeavesNoPartialTarget()
    {
        var root = CreateRoot();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var observedMode = (UnixFileMode?)null;
        var invocations = 0;
        Task<string>? first = null, second = null;
        try
        {
            var target = Path.Combine(root, "shared");
            await File.WriteAllTextAsync(target, "original");
            if (OperatingSystem.IsLinux()) File.SetUnixFileMode(target, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead);
            var mutations = new FileMutations(root, beforeCommit: async () =>
            {
                if (Interlocked.Increment(ref invocations) != 1) return;
                if (OperatingSystem.IsLinux()) observedMode = File.GetUnixFileMode(Assert.Single(Directory.EnumerateFiles(root, ".pisharp-*.tmp")));
                entered.SetResult();
                await release.Task;
            });
            using var cancellation = new CancellationTokenSource();
            first = mutations.WriteAsync("shared", "first", cancellation.Token);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));

            cancellation.Cancel();
            second = mutations.WriteAsync("shared", "second");

            Assert.False(first.IsCompleted);
            Assert.False(second.IsCompleted);
            Assert.Equal("original", await File.ReadAllTextAsync(target));
            release.SetResult();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await first.WaitAsync(TimeSpan.FromSeconds(5)));
            await second.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal("second", await File.ReadAllTextAsync(target));
            Assert.Empty(Directory.EnumerateFiles(root, ".pisharp-*.tmp"));
            if (OperatingSystem.IsLinux())
            {
                Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, observedMode);
                Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead, File.GetUnixFileMode(target));
            }
        }
        finally
        {
            release.TrySetResult();
            if (first is not null)
                try { await first.WaitAsync(TimeSpan.FromSeconds(5)); } catch (OperationCanceledException) { }
            if (second is not null) await second.WaitAsync(TimeSpan.FromSeconds(5));
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task PreparedWriteFailureKeepsOriginalBytesAndRemovesItsPrivateCandidate()
    {
        var root = CreateRoot();
        try
        {
            var target = Path.Combine(root, "shared");
            await File.WriteAllTextAsync(target, "original");
            var mutations = new FileMutations(root, beforeCommit: () => Task.FromException(new IOException("controlled failure")));

            await Assert.ThrowsAsync<ToolFailureException>(() => mutations.WriteAsync("shared", "must not commit"));

            Assert.Equal("original", await File.ReadAllTextAsync(target));
            Assert.Equal("shared", Path.GetFileName(Assert.Single(Directory.EnumerateFileSystemEntries(root))));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static string CreateRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "pisharp-mutation-coordination-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static JsonElement ReadPinnedResults()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "PiSharp.slnx"))) directory = directory.Parent;
        using var fixture = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory!.FullName, "docs", "parity", "fixtures",
            "builtin-coding-tool-mutation-coordination-2026-10-22.json")));
        return fixture.RootElement.GetProperty("results").Clone();
    }
}
