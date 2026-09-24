using System.Diagnostics;
using PiSharp.Runtime;
using PiSharp.Runtime.Tools;

namespace PiSharp.Tests;

public sealed class SearchToolsTests
{
    [Fact]
    public async Task FindAndGrepUseOptionalLoadoutAndBoundMatches()
    {
        var root = Path.Combine(Path.GetTempPath(), "pisharp-search-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "sub"));
            Directory.CreateDirectory(Path.Combine(root, "node_modules"));
            await File.WriteAllTextAsync(Path.Combine(root, "alpha.cs"), "before\nmatch-one\nafter\n");
            await File.WriteAllTextAsync(Path.Combine(root, "sub", "test.cs"), "match-two\n");
            await File.WriteAllTextAsync(Path.Combine(root, "node_modules", "ignored.cs"), "match-three\n");
            var tools = new SearchTools(root);
            Assert.Equal("alpha.cs\nsub/test.cs", await tools.Find("**/*.cs"));
            Assert.Equal("alpha.cs:2: match-one\nsub/test.cs:1: match-two", await tools.Grep("match", glob: "*.cs"));
            Assert.Contains("alpha.cs-1- before\nalpha.cs:2: match-one\nalpha.cs-3- after",
                await tools.Grep("match-one", glob: "*.cs", context: 1));
            Assert.Contains("1 matches limit reached", await tools.Grep("match", glob: "*.cs", limit: 1));
            Assert.Equal(["grep", "find"], new CodingTools(root).Create(["grep", "find"]).Select(tool => tool.Name));
            Assert.Equal("No matches found", await tools.Grep("absent"));
            Assert.Equal("No files found matching pattern", await tools.Find("*.unknown"));
            await Assert.ThrowsAsync<ToolFailureException>(() => tools.Grep("("));
            await Assert.ThrowsAsync<ToolFailureException>(() => tools.Find("*.cs", "missing"));
            await Assert.ThrowsAsync<ToolFailureException>(() => tools.Find("*.cs", "alpha.cs"));
            Assert.Equal("alpha.cs:2: match-one", await tools.Grep("match-one", "alpha.cs"));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task GitIgnoreFiltersUntrackedFilesWhenGitIsAvailable()
    {
        if (!OperatingSystem.IsLinux()) return;
        var root = Path.Combine(Path.GetTempPath(), "pisharp-git-search-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            using var git = Process.Start(new ProcessStartInfo("git") { WorkingDirectory = root, ArgumentList = { "init", "-q" } });
            Assert.NotNull(git);
            await git.WaitForExitAsync();
            Assert.Equal(0, git.ExitCode);
            await File.WriteAllTextAsync(Path.Combine(root, ".gitignore"), "*.txt\n!visible.txt\n");
            await File.WriteAllTextAsync(Path.Combine(root, "visible.txt"), "secret-value\n");
            await File.WriteAllTextAsync(Path.Combine(root, "ignored.txt"), "secret-value\n");
            var tools = new SearchTools(root);
            Assert.Equal("visible.txt", await tools.Find("*.txt"));
            Assert.Equal("visible.txt:1: secret-value", await tools.Grep("secret-value"));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task GitIgnoreFiltersSearchResultsOutsideGitRepositories()
    {
        var root = Path.Combine(Path.GetTempPath(), "pisharp-ignore-search-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            await File.WriteAllTextAsync(Path.Combine(root, ".gitignore"), "*.txt\n!visible.txt\n");
            await File.WriteAllTextAsync(Path.Combine(root, "visible.txt"), "match-value\n");
            await File.WriteAllTextAsync(Path.Combine(root, "ignored.txt"), "match-value\n");
            var nested = Path.Combine(root, "sub");
            Directory.CreateDirectory(nested);
            await File.WriteAllTextAsync(Path.Combine(nested, ".gitignore"), "!kept.txt\n");
            await File.WriteAllTextAsync(Path.Combine(nested, "kept.txt"), "match-value\n");
            await File.WriteAllTextAsync(Path.Combine(nested, "ignored.txt"), "match-value\n");
            var tools = new SearchTools(root);
            var found = await tools.Find("**/*.txt");
            Assert.Contains("visible.txt", found);
            Assert.Contains("sub/kept.txt", found);
            Assert.DoesNotContain("ignored.txt", found);
            var matches = await tools.Grep("match-value");
            Assert.Contains("visible.txt:1: match-value", matches);
            Assert.Contains("sub/kept.txt:1: match-value", matches);
            Assert.DoesNotContain("ignored.txt", matches);
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}
