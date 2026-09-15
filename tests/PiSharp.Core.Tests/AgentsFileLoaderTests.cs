using PiSharp.Core;

namespace PiSharp.Core.Tests;

public sealed class AgentsFileLoaderTests
{
    [Fact]
    public async Task LoadAsync_LoadsGlobalThenParentsThenWorkspace()
    {
        using var temp = TempDirectory.Create();
        var home = Directory.CreateDirectory(Path.Combine(temp.Path, "home")).FullName;
        var project = Directory.CreateDirectory(Path.Combine(temp.Path, "repo", "src")).FullName;

        Directory.CreateDirectory(Path.Combine(home, ".pi", "agent"));
        await File.WriteAllTextAsync(Path.Combine(home, ".pi", "agent", "AGENTS.md"), "global");
        await File.WriteAllTextAsync(Path.Combine(temp.Path, "repo", "AGENTS.md"), "repo");
        await File.WriteAllTextAsync(Path.Combine(project, "AGENTS.md"), "src");

        var result = await new AgentsFileLoader().LoadAsync(project, home);

        Assert.True(result.IndexOf("global", StringComparison.Ordinal) < result.IndexOf("repo", StringComparison.Ordinal));
        Assert.True(result.IndexOf("repo", StringComparison.Ordinal) < result.IndexOf("src", StringComparison.Ordinal));
    }

    [Fact]
    public async Task LoadAsync_OverrideWinsWithinSameDirectory()
    {
        using var temp = TempDirectory.Create();
        await File.WriteAllTextAsync(Path.Combine(temp.Path, "AGENTS.md"), "normal");
        await File.WriteAllTextAsync(Path.Combine(temp.Path, "AGENTS.override.md"), "override");

        var result = await new AgentsFileLoader().LoadAsync(temp.Path, Path.Combine(temp.Path, "no-home"));

        Assert.Contains("override", result, StringComparison.Ordinal);
        Assert.DoesNotContain("normal", result, StringComparison.Ordinal);
    }
    [Fact]
    public async Task LoadAsync_ContextRootStopsParentDiscovery()
    {
        using var temp = TempDirectory.Create();
        var parent = Directory.CreateDirectory(Path.Combine(temp.Path, "parent")).FullName;
        var project = Directory.CreateDirectory(Path.Combine(parent, "repo")).FullName;
        await File.WriteAllTextAsync(Path.Combine(parent, "CLAUDE.md"), "parent-only-instruction");
        await File.WriteAllTextAsync(Path.Combine(project, "AGENTS.md"), "repo-only-instruction");

        var result = await new AgentsFileLoader().LoadAsync(
            project,
            Path.Combine(temp.Path, "no-home"),
            contextRoot: project);

        Assert.Contains("repo-only-instruction", result, StringComparison.Ordinal);
        Assert.DoesNotContain("parent-only-instruction", result, StringComparison.Ordinal);
    }

}
