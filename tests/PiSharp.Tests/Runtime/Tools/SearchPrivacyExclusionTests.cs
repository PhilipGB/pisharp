using PiSharp.Runtime.Tools;

namespace PiSharp.Tests;

public sealed class SearchPrivacyExclusionTests
{
    [Fact]
    public async Task RepositoryPrivateDirectoryAndFilesStayExcludedWhileApplicationRescuesRemainVisible()
    {
        if (!OperatingSystem.IsLinux()) return;
        var root = Path.Combine(Path.GetTempPath(), "pisharp-search-private-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "private"));
        try
        {
            Assert.Equal(0, (await DirectProcessExecution.ExecuteAsync("git", ["init", "-q"], root, null, default)).ExitCode);
            var excludes = Path.Combine(root, "global-excludes");
            await File.WriteAllTextAsync(excludes, "private/\nprivate-link/\nsecret.txt\nrescued.txt\n");
            Assert.Equal(0, (await DirectProcessExecution.ExecuteAsync("git", ["config", "--local", "core.excludesFile", excludes], root, null, default)).ExitCode);
            await File.WriteAllTextAsync(Path.Combine(root, ".ignore"), "!rescued.txt\n");
            foreach (var path in new[] { "private/hidden.txt", "secret.txt", "rescued.txt", "visible.txt" })
                await File.WriteAllTextAsync(Path.Combine(root, path), "needle\n");
            Directory.CreateSymbolicLink(Path.Combine(root, "private-link"), "private");
            var result = await new SearchTools(root).Find("*");
            Assert.DoesNotContain("private", result, StringComparison.Ordinal);
            Assert.DoesNotContain("secret.txt", result, StringComparison.Ordinal);
            Assert.Contains("rescued.txt", result, StringComparison.Ordinal);
            Assert.Contains("visible.txt", result, StringComparison.Ordinal);
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}
