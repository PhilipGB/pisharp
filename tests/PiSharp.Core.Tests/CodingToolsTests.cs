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
