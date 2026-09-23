using PiSharp.Cli;
using PiSharp.Runtime;

namespace PiSharp.Tests;

public sealed class ToolSelectionTests
{
    [Fact]
    public async Task OptInLsMatchesPinnedAsciiListingAndLimit()
    {
        var directory = Path.Combine(Path.GetTempPath(), "pisharp-ls-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            Directory.CreateDirectory(Path.Combine(directory, "Beta"));
            foreach (var file in new[] { "z.txt", ".env", "alpha.txt" })
                await File.WriteAllTextAsync(Path.Combine(directory, file), "");
            var tool = new DirectoryListingTool(directory);
            Assert.Equal(".env\nalpha.txt\nBeta/\nz.txt", await tool.List());
            Assert.Equal(".env\nalpha.txt\n\n[2 entries limit reached. Use limit=4 for more]", await tool.List(limit: 2));
            Assert.Equal("(empty directory)", await tool.List(limit: 0));
            Assert.Equal($"Not a directory: {Path.Combine(directory, "z.txt")}", await tool.List("z.txt"));
            var registry = new CodingTools(directory);
            Assert.Equal(["read", "bash", "edit", "write"], registry.Create().Select(t => t.Name));
            Assert.Equal(["ls"], registry.Create(["ls"]).Select(t => t.Name));
            Assert.Empty(registry.Create(noTools: true));
            Assert.Equal(["ls"], registry.Create(["ls"], noTools: true).Select(t => t.Name));
            Assert.Equal(["read"], registry.Create(["read", "bash"], ["bash"]).Select(t => t.Name));
            Assert.Throws<ArgumentException>(() => registry.Create(["not-a-tool"]));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public void CliToolFiltersAreParsedWithoutConsumingPromptAfterSeparator()
    {
        var args = CliArguments.Parse(["--tools", "read, ls", "--exclude-tools", "ls", "--", "--no-tools"]);
        Assert.Equal(["read", "ls"], args.Tools);
        Assert.Equal(["ls"], args.ExcludeTools);
        Assert.Equal("--no-tools", args.Prompt);
        Assert.True(CliArguments.Parse(["--no-tools"]).NoTools);
        Assert.Throws<ArgumentException>(() => CliArguments.Parse(["--tools"]));
    }
}
