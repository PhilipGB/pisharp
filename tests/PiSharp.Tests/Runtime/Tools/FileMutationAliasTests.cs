using PiSharp.Runtime;

namespace PiSharp.Tests;

public sealed class FileMutationAliasTests
{
    [Theory]
    [InlineData("relative")]
    [InlineData("directory-existing")]
    [InlineData("directory-new")]
    [InlineData("directory-new-parent")]
    [InlineData("file-existing")]
    [InlineData("file-dangling")]
    public async Task AliasesSerializeWhileUnrelatedPathsRemainIndependent(string aliasKind)
    {
        if (!OperatingSystem.IsLinux()) return;
        var root = Path.Combine(Path.GetTempPath(), "pisharp-mutation-alias-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "actual"));
        var actual = Path.Combine(root, "actual", aliasKind == "directory-new-parent" ? "missing/nested/file" : "file");
        var alias = actual;
        var queue = new FileMutationQueue();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<bool>? first = null;
        Task<bool>? second = null;
        try
        {
            if (aliasKind.EndsWith("existing", StringComparison.Ordinal)) await File.WriteAllTextAsync(actual, "original");
            if (aliasKind.StartsWith("directory", StringComparison.Ordinal))
            {
                Directory.CreateSymbolicLink(Path.Combine(root, "alias"), "actual");
                alias = Path.Combine(root, "alias", Path.GetRelativePath(Path.Combine(root, "actual"), actual));
            }
            else if (aliasKind.StartsWith("file", StringComparison.Ordinal))
            {
                alias = Path.Combine(root, "alias");
                File.CreateSymbolicLink(alias, Path.GetRelativePath(root, actual));
            }
            else alias = Path.GetRelativePath(Environment.CurrentDirectory, actual);

            first = queue.RunAsync(alias, async () => { entered.SetResult(); await release.Task; return true; }, CancellationToken.None);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var secondEntered = false;
            second = queue.RunAsync(actual, () => { secondEntered = true; return Task.FromResult(true); }, CancellationToken.None);
            Assert.False(secondEntered);
            Assert.False(second.IsCompleted);
            var unrelated = queue.RunAsync(Path.Combine(root, "other"), () => Task.FromResult(true), CancellationToken.None);
            Assert.True(await unrelated.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.False(secondEntered);
            release.SetResult();
            await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(secondEntered);
        }
        finally
        {
            release.TrySetResult();
            if (first is not null) await first.WaitAsync(TimeSpan.FromSeconds(5));
            if (second is not null) await second.WaitAsync(TimeSpan.FromSeconds(5));
            Directory.Delete(root, recursive: true);
        }
    }
}
