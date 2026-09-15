using PiSharp.Core;

namespace PiSharp.Core.Tests;

public sealed class EditEngineTests
{
    [Fact]
    public void Apply_SupportsFuzzyTypographyAndPreservesLineEndings()
    {
        var content = "first\r\nvar message = “hello”;\r\nlast\r\n";
        var result = new EditEngine().Apply(
            "sample.cs",
            content,
            [new EditOperation("var message = \"hello\";", "var message = \"updated\";")]);

        Assert.True(result.UsedFuzzyMatch);
        Assert.Equal("first\r\nvar message = \"updated\";\r\nlast\r\n", result.UpdatedContent);
        Assert.Contains("-2", result.Diff, StringComparison.Ordinal);
        Assert.Contains("+2", result.Diff, StringComparison.Ordinal);
        Assert.Contains("@@", result.Patch, StringComparison.Ordinal);
    }

    [Fact]
    public void Apply_MatchesAllEditsAgainstTheOriginalAndRejectsOverlap()
    {
        var engine = new EditEngine();

        var result = engine.Apply(
            "sample.txt",
            "alpha\nbeta\ngamma\n",
            [
                new EditOperation("alpha", "one"),
                new EditOperation("gamma", "three"),
            ]);

        Assert.Equal("one\nbeta\nthree\n", result.UpdatedContent);
        Assert.Throws<InvalidOperationException>(() => engine.Apply(
            "sample.txt",
            "abcdef",
            [new EditOperation("abc", "x"), new EditOperation("bcd", "y")]));
    }

    [Fact]
    public void Apply_RejectsNoOpReplacement()
    {
        var error = Assert.Throws<InvalidOperationException>(() => new EditEngine().Apply(
            "sample.txt",
            "same",
            [new EditOperation("same", "same")]));

        Assert.Contains("No changes made", error.Message, StringComparison.Ordinal);
    }
}
