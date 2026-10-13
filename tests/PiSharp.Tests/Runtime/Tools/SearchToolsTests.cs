using System.Diagnostics;
using System.Text;
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
            await File.WriteAllTextAsync(Path.Combine(root, ".ignore"), "!node_modules/\n!node_modules/**\n");
            await File.WriteAllTextAsync(Path.Combine(root, ".fdignore"), "!node_modules/\n!node_modules/**\n");
            var tools = new SearchTools(root);
            Assert.Equal("alpha.cs\nnode_modules/ignored.cs\nsub/test.cs", await tools.Find("**/*.cs"));
            var grepLines = (await tools.Grep("match", glob: "*.cs")).Split('\n').Order(StringComparer.Ordinal);
            Assert.Equal([
                "alpha.cs:2: match-one", "node_modules/ignored.cs:1: match-three", "sub/test.cs:1: match-two"
            ], grepLines);
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
    public async Task GitSearchTraversesNestedRepositoriesWithTheirOwnIgnoreRules()
    {
        if (!OperatingSystem.IsLinux()) return;
        var root = Path.Combine(Path.GetTempPath(), "pisharp-nested-git-search-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var nested = Path.Combine(root, "nested");
        var deeper = Path.Combine(nested, "deeper");
        Directory.CreateDirectory(deeper);
        try
        {
            foreach (var directory in new[] { root, nested, deeper })
            {
                using var git = Process.Start(new ProcessStartInfo("git")
                {
                    WorkingDirectory = directory,
                    ArgumentList = { "init", "-q" }
                });
                Assert.NotNull(git);
                await git.WaitForExitAsync();
                Assert.Equal(0, git.ExitCode);
            }

            await File.WriteAllTextAsync(Path.Combine(root, ".gitignore"), "nested/outer-ignored.txt\n");
            await File.WriteAllTextAsync(Path.Combine(root, ".fdignore"), "nested/**/find-ignored.txt\n");
            await File.WriteAllTextAsync(Path.Combine(root, ".rgignore"), "nested/**/grep-ignored.txt\n");
            await File.WriteAllTextAsync(Path.Combine(nested, ".gitignore"), "inner-ignored.txt\ndeeper/deeper-parent-ignored.txt\n");
            await File.WriteAllTextAsync(Path.Combine(deeper, ".gitignore"), "deep-ignored.txt\n");
            await File.WriteAllTextAsync(Path.Combine(nested, "visible.txt"), "needle\n");
            await File.WriteAllTextAsync(Path.Combine(nested, "outer-ignored.txt"), "needle\n");
            await File.WriteAllTextAsync(Path.Combine(nested, "inner-ignored.txt"), "needle\n");
            await File.WriteAllTextAsync(Path.Combine(nested, "find-ignored.txt"), "needle\n");
            await File.WriteAllTextAsync(Path.Combine(nested, "grep-ignored.txt"), "needle\n");
            await File.WriteAllTextAsync(Path.Combine(deeper, "visible.txt"), "needle\n");
            await File.WriteAllTextAsync(Path.Combine(deeper, "deeper-parent-ignored.txt"), "needle\n");
            await File.WriteAllTextAsync(Path.Combine(deeper, "deep-ignored.txt"), "needle\n");
            await File.WriteAllTextAsync(Path.Combine(deeper, "find-ignored.txt"), "needle\n");
            await File.WriteAllTextAsync(Path.Combine(deeper, "grep-ignored.txt"), "needle\n");

            var tools = new SearchTools(root);
            Assert.Equal("nested/deeper/deeper-parent-ignored.txt\nnested/deeper/grep-ignored.txt\nnested/deeper/visible.txt\nnested/grep-ignored.txt\nnested/outer-ignored.txt\nnested/visible.txt",
                await tools.Find("*.txt"));
            var grepPaths = (await tools.Grep("needle")).Split('\n')
                .Select(line => line[..line.IndexOf(':')]).Order(StringComparer.Ordinal);
            Assert.Equal([
                "nested/deeper/deeper-parent-ignored.txt", "nested/deeper/find-ignored.txt", "nested/deeper/visible.txt",
                "nested/find-ignored.txt", "nested/outer-ignored.txt", "nested/visible.txt"
            ], grepPaths);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FindListsSymlinkEntriesButGrepDoesNotFollowThem(bool initializeGitRepository)
    {
        if (!OperatingSystem.IsLinux()) return;
        var root = Path.Combine(Path.GetTempPath(), "pisharp-search-symlinks-" + Guid.NewGuid().ToString("N"));
        var targetDirectory = Path.Combine(root, "target");
        Directory.CreateDirectory(targetDirectory);
        try
        {
            if (initializeGitRepository)
            {
                using var git = Process.Start(new ProcessStartInfo("git")
                {
                    WorkingDirectory = root,
                    ArgumentList = { "init", "-q" }
                });
                Assert.NotNull(git);
                await git.WaitForExitAsync();
                Assert.Equal(0, git.ExitCode);
            }

            await File.WriteAllTextAsync(Path.Combine(root, "real.txt"), "needle\n");
            await File.WriteAllTextAsync(Path.Combine(targetDirectory, "inside.txt"), "needle\n");
            File.CreateSymbolicLink(Path.Combine(root, "file-link.txt"), "real.txt");
            Directory.CreateSymbolicLink(Path.Combine(root, "dir-link"), "target");

            var tools = new SearchTools(root);
            Assert.Equal("file-link.txt\nreal.txt\ntarget/inside.txt", await tools.Find("*.txt"));
            Assert.Equal("dir-link/", await tools.Find("dir-link"));
            var grepLines = (await tools.Grep("needle")).Split('\n').Order(StringComparer.Ordinal);
            Assert.Equal(["real.txt:1: needle", "target/inside.txt:1: needle"], grepLines);
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

    [Fact]
    public async Task GrepByteLimitKeepsCompleteHeadLinesAndUsesByteNotice()
    {
        var root = Path.Combine(Path.GetTempPath(), "pisharp-grep-limit-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var lines = Enumerable.Range(1, 120).Select(index => $"hit-{index:D3} {new string('x', 480)}");
            await File.WriteAllLinesAsync(Path.Combine(root, "large.txt"), lines);
            var output = await new SearchTools(root).Grep("hit", limit: 200);
            Assert.Contains("large.txt:1: hit-001", output);
            Assert.Contains("50.0KB limit reached", output);
            Assert.DoesNotContain("200 matches limit reached", output);
            Assert.DoesNotContain("hit-120", output);
            var body = output.Split("\n\n[", 2)[0];
            Assert.True(Encoding.UTF8.GetByteCount(body) <= 50 * 1024);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task GrepLongLinesUsePinnedTruncationMarker()
    {
        var root = Path.Combine(Path.GetTempPath(), "pisharp-grep-line-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            await File.WriteAllTextAsync(Path.Combine(root, "large.txt"), "long-match" + new string('x', 600));
            var output = await new SearchTools(root).Grep("long-match");
            Assert.Contains("... [truncated]", output);
            Assert.Contains("Some lines truncated to 500 chars. Use read tool to see full lines", output);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task GrepSkipsNulBinaryFilesLikeRipgrep()
    {
        var root = Path.Combine(Path.GetTempPath(), "pisharp-grep-binary-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            await File.WriteAllTextAsync(Path.Combine(root, "same-line.bin"), "needle\0tail\nneedle after binary\n");
            await File.WriteAllTextAsync(Path.Combine(root, "later-line.bin"), "needle before binary\nbinary\0line\nneedle after binary\n");
            await File.WriteAllTextAsync(Path.Combine(root, "plain.txt"), "needle plain text\n");

            var output = await new SearchTools(root).Grep("needle");
            Assert.Equal(["plain.txt:1: needle plain text"],
                output.Split('\n').Order(StringComparer.Ordinal));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FindIncludesMatchingEmptyDirectories(bool initializeGitRepository)
    {
        if (initializeGitRepository && !OperatingSystem.IsLinux()) return;
        var root = Path.Combine(Path.GetTempPath(), "pisharp-find-empty-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            if (initializeGitRepository)
            {
                using var git = Process.Start(new ProcessStartInfo("git")
                {
                    WorkingDirectory = root,
                    ArgumentList = { "init", "-q" }
                });
                Assert.NotNull(git);
                await git.WaitForExitAsync();
                Assert.Equal(0, git.ExitCode);
            }
            Directory.CreateDirectory(Path.Combine(root, "empty.txt"));
            Assert.Equal("empty.txt/", await new SearchTools(root).Find("*.txt"));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task GrepAndFindRespectTheirSpecificIgnoreFilesOutsideGit()
    {
        var root = Path.Combine(Path.GetTempPath(), "pisharp-ignore-specific-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            await File.WriteAllTextAsync(Path.Combine(root, ".gitignore"), "*.txt\n");
            await File.WriteAllTextAsync(Path.Combine(root, ".ignore"), "!shared.txt\n");
            await File.WriteAllTextAsync(Path.Combine(root, ".fdignore"), "!find-only.txt\n");
            await File.WriteAllTextAsync(Path.Combine(root, ".rgignore"), "!grep-only.txt\n");
            await File.WriteAllTextAsync(Path.Combine(root, "shared.txt"), "match-value\n");
            await File.WriteAllTextAsync(Path.Combine(root, "find-only.txt"), "match-value\n");
            await File.WriteAllTextAsync(Path.Combine(root, "grep-only.txt"), "match-value\n");
            await File.WriteAllTextAsync(Path.Combine(root, "ignored.txt"), "match-value\n");
            var tools = new SearchTools(root);
            Assert.Equal("find-only.txt\nshared.txt", await tools.Find("*.txt"));
            var grepPaths = (await tools.Grep("match-value")).Split('\n')
                .Select(line => line[..line.IndexOf(':')]).Order(StringComparer.Ordinal);
            Assert.Equal(["grep-only.txt", "shared.txt"], grepPaths);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task GrepAndFindRespectTheirSpecificIgnoreFilesInsideGit()
    {
        if (!OperatingSystem.IsLinux()) return;
        var root = Path.Combine(Path.GetTempPath(), "pisharp-ignore-git-specific-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            using var init = Process.Start(new ProcessStartInfo("git")
            {
                WorkingDirectory = root,
                ArgumentList = { "init", "-q" }
            });
            Assert.NotNull(init);
            await init.WaitForExitAsync();
            Assert.Equal(0, init.ExitCode);

            await File.WriteAllTextAsync(Path.Combine(root, ".gitignore"), "git-ignored.txt\nstaged-git.txt\n");
            await File.WriteAllTextAsync(Path.Combine(root, ".ignore"), "ignore-only.txt\nstaged-ignore.txt\n");
            await File.WriteAllTextAsync(Path.Combine(root, ".rgignore"), "rg-only.txt\nstaged-rg.txt\n");
            await File.WriteAllTextAsync(Path.Combine(root, ".fdignore"), "fd-only.txt\nstaged-fd.txt\n");
            foreach (var file in new[]
                     {
                         "git-ignored.txt", "ignore-only.txt", "rg-only.txt", "fd-only.txt", "visible.txt",
                         "staged-git.txt", "staged-ignore.txt", "staged-rg.txt", "staged-fd.txt"
                     })
                await File.WriteAllTextAsync(Path.Combine(root, file), "match-value\n");

            using var add = Process.Start(new ProcessStartInfo("git")
            {
                WorkingDirectory = root,
                ArgumentList = { "add", "-f", "staged-git.txt", "staged-ignore.txt", "staged-rg.txt", "staged-fd.txt" }
            });
            Assert.NotNull(add);
            await add.WaitForExitAsync();
            Assert.Equal(0, add.ExitCode);

            var tools = new SearchTools(root);
            Assert.Equal("rg-only.txt\nstaged-rg.txt\nvisible.txt", await tools.Find("*.txt"));
            var grep = await tools.Grep("match-value");
            var grepPaths = grep.Split('\n').Select(line => line[..line.IndexOf(':')]).Order(StringComparer.Ordinal);
            Assert.Equal(["fd-only.txt", "staged-fd.txt", "visible.txt"], grepPaths);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task GitSearchRootInheritsIgnoreFilesFromRepositoryAncestors()
    {
        if (!OperatingSystem.IsLinux()) return;
        var root = Path.Combine(Path.GetTempPath(), "pisharp-ignore-git-parent-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            using var init = Process.Start(new ProcessStartInfo("git")
            {
                WorkingDirectory = root,
                ArgumentList = { "init", "-q" }
            });
            Assert.NotNull(init);
            await init.WaitForExitAsync();
            Assert.Equal(0, init.ExitCode);

            await File.WriteAllTextAsync(Path.Combine(root, ".gitignore"), "sub/git-parent.txt\n");
            await File.WriteAllTextAsync(Path.Combine(root, ".ignore"), "sub/ignore-parent.txt\n");
            await File.WriteAllTextAsync(Path.Combine(root, ".rgignore"), "sub/rg-parent.txt\n");
            await File.WriteAllTextAsync(Path.Combine(root, ".fdignore"), "sub/fd-parent.txt\n");
            var subdirectory = Path.Combine(root, "sub");
            Directory.CreateDirectory(subdirectory);
            foreach (var file in new[] { "git-parent.txt", "ignore-parent.txt", "rg-parent.txt", "fd-parent.txt", "visible.txt" })
                await File.WriteAllTextAsync(Path.Combine(subdirectory, file), "match-value\n");

            var tools = new SearchTools(subdirectory);
            Assert.Equal("rg-parent.txt\nvisible.txt", await tools.Find("*.txt"));
            var grepPaths = (await tools.Grep("match-value")).Split('\n')
                .Select(line => line[..line.IndexOf(':')]).Order(StringComparer.Ordinal);
            Assert.Equal(["fd-parent.txt", "visible.txt"], grepPaths);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task HigherPriorityIgnoreFilesCanRestoreGitIgnoredFiles()
    {
        if (!OperatingSystem.IsLinux()) return;
        var root = Path.Combine(Path.GetTempPath(), "pisharp-ignore-git-restore-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            using var init = Process.Start(new ProcessStartInfo("git")
            {
                WorkingDirectory = root,
                ArgumentList = { "init", "-q" }
            });
            Assert.NotNull(init);
            await init.WaitForExitAsync();
            Assert.Equal(0, init.ExitCode);

            await File.WriteAllTextAsync(Path.Combine(root, ".gitignore"), "rescued.txt\n");
            await File.WriteAllTextAsync(Path.Combine(root, ".ignore"), "!rescued.txt\n");
            await File.WriteAllTextAsync(Path.Combine(root, "rescued.txt"), "match-value\n");
            await File.WriteAllTextAsync(Path.Combine(root, "visible.txt"), "match-value\n");

            var tools = new SearchTools(root);
            Assert.Equal("rescued.txt\nvisible.txt", await tools.Find("*.txt"));
            Assert.Contains("rescued.txt:1: match-value", await tools.Grep("match-value"));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task GitLocalAndGlobalExcludesFilterUntrackedAndStagedFiles()
    {
        if (!OperatingSystem.IsLinux()) return;
        var root = Path.Combine(Path.GetTempPath(), "pisharp-search-git-excludes-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            using var init = Process.Start(new ProcessStartInfo("git")
            {
                WorkingDirectory = root,
                ArgumentList = { "init", "-q" }
            });
            Assert.NotNull(init);
            await init.WaitForExitAsync();
            Assert.Equal(0, init.ExitCode);

            var globalIgnore = Path.Combine(root, "global-excludes");
            await File.WriteAllTextAsync(globalIgnore,
                "global-untracked.txt\nstaged-global.txt\nglobal-rescued.txt\n");
            using var configure = Process.Start(new ProcessStartInfo("git")
            {
                WorkingDirectory = root,
                ArgumentList = { "config", "--local", "core.excludesFile", globalIgnore }
            });
            Assert.NotNull(configure);
            await configure.WaitForExitAsync();
            Assert.Equal(0, configure.ExitCode);

            var infoExclude = Path.Combine(root, ".git", "info", "exclude");
            await File.AppendAllTextAsync(infoExclude,
                "local-untracked.txt\nstaged-local.txt\nlocal-rescued.txt\n");
            await File.WriteAllTextAsync(Path.Combine(root, ".ignore"), "!global-rescued.txt\n!local-rescued.txt\n");
            foreach (var file in new[]
                     {
                         "global-untracked.txt", "staged-global.txt", "global-rescued.txt", "local-untracked.txt",
                         "staged-local.txt", "local-rescued.txt", "visible.txt"
                     })
                await File.WriteAllTextAsync(Path.Combine(root, file), "match-value\n");

            using var add = Process.Start(new ProcessStartInfo("git")
            {
                WorkingDirectory = root,
                ArgumentList = { "add", "-f", "staged-global.txt", "staged-local.txt" }
            });
            Assert.NotNull(add);
            await add.WaitForExitAsync();
            Assert.Equal(0, add.ExitCode);

            var tools = new SearchTools(root);
            Assert.Equal("global-rescued.txt\nlocal-rescued.txt\nvisible.txt", await tools.Find("*.txt"));
            var grepPaths = (await tools.Grep("match-value")).Split('\n')
                .Select(line => line[..line.IndexOf(':')]).Order(StringComparer.Ordinal);
            Assert.Equal(["global-rescued.txt", "local-rescued.txt", "visible.txt"], grepPaths);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task GlobalFdIgnoreHasLowerPrecedenceThanProjectFdIgnore()
    {
        var root = Path.Combine(Path.GetTempPath(), "pisharp-search-global-fdignore-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var globalFdIgnore = Path.Combine(root, "global-fd-ignore");
            await File.WriteAllTextAsync(globalFdIgnore, "*.tmp\n");
            await File.WriteAllTextAsync(Path.Combine(root, ".fdignore"), "!keep.tmp\n");
            await File.WriteAllTextAsync(Path.Combine(root, "ignored.tmp"), "ignore\n");
            await File.WriteAllTextAsync(Path.Combine(root, "keep.tmp"), "keep\n");
            await File.WriteAllTextAsync(Path.Combine(root, "also-ignored.tmp"), "ignore\n");

            var files = await SearchInventory.EnumerateAsync(root, CancellationToken.None,
                includeFdIgnore: true, ignoreBaseDirectory: root, fdGlobalIgnorePath: globalFdIgnore);
            var temporaryFiles = files.Where(file => Path.GetExtension(file) == ".tmp")
                .Select(Path.GetFileName).Order(StringComparer.Ordinal);
            Assert.Equal(["keep.tmp"], temporaryFiles);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task IgnorePatternsSupportBraceAlternatives()
    {
        var root = Path.Combine(Path.GetTempPath(), "pisharp-search-ignore-braces-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            await File.WriteAllTextAsync(Path.Combine(root, ".gitignore"), "*.{tmp,cache}\n");
            await File.WriteAllTextAsync(Path.Combine(root, "ignored.tmp"), "match-value\n");
            await File.WriteAllTextAsync(Path.Combine(root, "ignored.cache"), "match-value\n");
            await File.WriteAllTextAsync(Path.Combine(root, "visible.log"), "match-value\n");
            await File.WriteAllTextAsync(Path.Combine(root, "kept.txt"), "match-value\n");

            var tools = new SearchTools(root);
            Assert.Equal("visible.log", await tools.Find("*.log"));
            var grepPaths = (await tools.Grep("match-value")).Split('\n')
                .Select(line => line[..line.IndexOf(':')]).Order(StringComparer.Ordinal);
            Assert.Equal(["kept.txt", "visible.log"], grepPaths);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task IgnorePatternsTrimUnescapedTrailingSpacesAndKeepEscapedSpaces()
    {
        var root = Path.Combine(Path.GetTempPath(), "pisharp-search-ignore-spaces-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            await File.WriteAllTextAsync(Path.Combine(root, ".gitignore"), "ignored.txt   \nliteral-space.txt\\ \n");
            await File.WriteAllTextAsync(Path.Combine(root, "ignored.txt"), "match-value\n");
            await File.WriteAllTextAsync(Path.Combine(root, "literal-space.txt "), "match-value\n");
            await File.WriteAllTextAsync(Path.Combine(root, "visible.txt"), "match-value\n");

            var tools = new SearchTools(root);
            Assert.Equal("visible.txt", await tools.Find("*.txt*"));
            Assert.Equal("visible.txt:1: match-value", await tools.Grep("match-value"));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task FindAndGrepGlobsSupportBraceAlternativesAndCharacterClasses()
    {
        var root = Path.Combine(Path.GetTempPath(), "pisharp-search-query-globs-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            await File.WriteAllTextAsync(Path.Combine(root, "file-a.txt"), "match-value\n");
            await File.WriteAllTextAsync(Path.Combine(root, "file-b.log"), "match-value\n");
            await File.WriteAllTextAsync(Path.Combine(root, "file-b.txt"), "match-value\n");
            await File.WriteAllTextAsync(Path.Combine(root, "file-c.txt"), "match-value\n");
            await File.WriteAllTextAsync(Path.Combine(root, "file-,.txt"), "match-value\n");
            await File.WriteAllTextAsync(Path.Combine(root, "file-].txt"), "match-value\n");

            var tools = new SearchTools(root);
            Assert.Equal("file-a.txt\nfile-b.log\nfile-b.txt", await tools.Find("file-[ab].{txt,log}"));
            var braceMatches = (await tools.Grep("match-value", glob: "file-[ab].{txt,log}"))
                .Split('\n').Order(StringComparer.Ordinal);
            Assert.Equal([
                "file-a.txt:1: match-value", "file-b.log:1: match-value", "file-b.txt:1: match-value"
            ], braceMatches);
            Assert.Equal("file-].txt", await tools.Find("file-[]].txt"));
            Assert.Equal("file-].txt:1: match-value", await tools.Grep("match-value", glob: "file-[]].txt"));
            var classInsideAlternative = "file-,.txt\nfile-a.txt\nfile-b.txt\nfile-c.txt";
            Assert.Equal(classInsideAlternative, await tools.Find("file-{[a,b],c}.txt"));
            var classMatches = (await tools.Grep("match-value", glob: "file-{[a,b],c}.txt"))
                .Split('\n').Order(StringComparer.Ordinal);
            var expectedClassMatches = classInsideAlternative.Split('\n')
                .Select(path => $"{path}:1: match-value").Order(StringComparer.Ordinal);
            Assert.Equal(expectedClassMatches, classMatches);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task GitSearchPreservesNewlinesInFileNames()
    {
        if (!OperatingSystem.IsLinux()) return;
        var root = Path.Combine(Path.GetTempPath(), "pisharp-search-newline-path-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            using var git = Process.Start(new ProcessStartInfo("git")
            {
                WorkingDirectory = root,
                ArgumentList = { "init", "-q" }
            });
            Assert.NotNull(git);
            await git.WaitForExitAsync();
            Assert.Equal(0, git.ExitCode);

            var fileName = "line\nbreak.txt";
            await File.WriteAllTextAsync(Path.Combine(root, fileName), "match-value\n");
            var tools = new SearchTools(root);
            Assert.Equal(fileName, await tools.Find("*.txt"));
            Assert.Equal($"{fileName}:1: match-value", await tools.Grep("match-value"));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task GrepReportsUnreadableFilesLikeRipgrep()
    {
        if (!OperatingSystem.IsLinux()) return;
        var root = Path.Combine(Path.GetTempPath(), "pisharp-search-permission-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var file = Path.Combine(root, "private.txt");
        try
        {
            await File.WriteAllTextAsync(file, "needle\n");
            File.SetUnixFileMode(file, UnixFileMode.None);

            try { _ = File.ReadAllText(file); }
            catch (UnauthorizedAccessException)
            {
                var error = await Assert.ThrowsAsync<ToolFailureException>(() => new SearchTools(root).Grep("needle"));
                Assert.Equal($"rg: {file}: Permission denied (os error 13)", error.Message);
                return;
            }
        }
        finally
        {
            if (File.Exists(file)) File.SetUnixFileMode(file, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            Directory.Delete(root, recursive: true);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GrepReportsUnreadableDirectoriesLikeRipgrep(bool initializeGitRepository)
    {
        if (!OperatingSystem.IsLinux()) return;
        var root = Path.Combine(Path.GetTempPath(), "pisharp-search-private-dir-" + Guid.NewGuid().ToString("N"));
        var privateDirectory = Path.Combine(root, "private");
        Directory.CreateDirectory(privateDirectory);
        try
        {
            if (initializeGitRepository)
            {
                using var git = Process.Start(new ProcessStartInfo("git")
                {
                    WorkingDirectory = root,
                    ArgumentList = { "init", "-q" }
                });
                Assert.NotNull(git);
                await git.WaitForExitAsync();
                Assert.Equal(0, git.ExitCode);
            }

            var file = Path.Combine(privateDirectory, "secret.txt");
            await File.WriteAllTextAsync(file, "needle\n");
            if (initializeGitRepository)
            {
                using var add = Process.Start(new ProcessStartInfo("git")
                {
                    WorkingDirectory = root,
                    ArgumentList = { "add", "-f", "private/secret.txt" }
                });
                Assert.NotNull(add);
                await add.WaitForExitAsync();
                Assert.Equal(0, add.ExitCode);
            }

            File.SetUnixFileMode(privateDirectory, UnixFileMode.None);
            try
            {
                _ = Directory.EnumerateFileSystemEntries(privateDirectory).ToArray();
                return;
            }
            catch (UnauthorizedAccessException) { }

            var error = await Assert.ThrowsAsync<ToolFailureException>(() => new SearchTools(root).Grep("needle"));
            Assert.Equal($"rg: {privateDirectory}: Permission denied (os error 13)", error.Message);
        }
        finally
        {
            if (Directory.Exists(privateDirectory))
                File.SetUnixFileMode(privateDirectory, UnixFileMode.UserRead | UnixFileMode.UserWrite |
                    UnixFileMode.UserExecute);
            Directory.Delete(root, recursive: true);
        }
    }
}
