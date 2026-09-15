using PiSharp.Core;

namespace PiSharp.Core.Tests;

public sealed class CodingToolsTests
{
    [Fact]
    public async Task EditAsync_ReplacesUniqueExactText()
    {
        using var temp = TempDirectory.Create();
        var file = Path.Combine(temp.Path, "sample.txt");
        await File.WriteAllTextAsync(file, "alpha\nbeta\ngamma\n");
        var tools = new CodingTools(temp.Path);

        await tools.EditAsync("sample.txt", [new EditOperation("beta", "changed")]);

        Assert.Equal("alpha\nchanged\ngamma\n", await File.ReadAllTextAsync(file));
    }

    [Fact]
    public async Task EditAsync_RejectsAmbiguousText()
    {
        using var temp = TempDirectory.Create();
        var file = Path.Combine(temp.Path, "sample.txt");
        await File.WriteAllTextAsync(file, "same\nsame\n");
        var tools = new CodingTools(temp.Path);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            tools.EditAsync("sample.txt", [new EditOperation("same", "changed")]));

        Assert.Contains("not unique", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReadAsync_AllowsConfiguredReadOnlyResourceRoot()
    {
        using var temp = TempDirectory.Create();
        using var resource = TempDirectory.Create();
        var file = Path.Combine(resource.Path, "SKILL.md");
        await File.WriteAllTextAsync(file, "skill body");
        var tools = new CodingTools(temp.Path);
        tools.AddReadOnlyRoot(resource.Path);

        var result = await tools.ReadAsync(file);

        Assert.Contains("skill body", result, StringComparison.Ordinal);
        await Assert.ThrowsAsync<InvalidOperationException>(() => tools.WriteAsync(file, "changed"));
    }

    [Fact]
    public async Task SearchToolsProvideDeterministicWorkspaceResults()
    {
        using var temp = TempDirectory.Create();
        Directory.CreateDirectory(Path.Combine(temp.Path, "src"));
        await File.WriteAllTextAsync(Path.Combine(temp.Path, "root.cs"), "alpha\nneedle\nomega\n");
        await File.WriteAllTextAsync(Path.Combine(temp.Path, "src", "nested.cs"), "needle nested\n");
        await File.WriteAllTextAsync(Path.Combine(temp.Path, ".hidden"), "hidden\n");
        var tools = new CodingTools(temp.Path);

        var listing = await tools.LsAsync();
        var files = await tools.FindAsync("**/*.cs");
        var matches = await tools.GrepAsync("needle", context: 1);

        Assert.Contains("src/", listing, StringComparison.Ordinal);
        Assert.Contains(".hidden", listing, StringComparison.Ordinal);
        Assert.Equal("root.cs\nsrc/nested.cs", files.ReplaceLineEndings("\n"));
        Assert.Contains("root.cs:2: needle", matches, StringComparison.Ordinal);
        Assert.Contains("root.cs-1- alpha", matches, StringComparison.Ordinal);
        Assert.Contains("src/nested.cs:1: needle nested", matches, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WriteAndReadAsync_RoundTrip()
    {
        using var temp = TempDirectory.Create();
        var tools = new CodingTools(temp.Path);

        await tools.WriteAsync("nested/file.txt", "one\ntwo\n");
        var result = await tools.ReadAsync("nested/file.txt");

        Assert.Contains("1\tone", result, StringComparison.Ordinal);
        Assert.Contains("2\ttwo", result, StringComparison.Ordinal);
    }
}
