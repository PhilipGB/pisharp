using PiSharp.Cli;

namespace PiSharp.Tests;

public sealed class SessionPathArgumentTests
{
    [Theory]
    [InlineData("folder/file.jsonl", "folder/file.jsonl")]
    [InlineData("'folder with spaces/file.jsonl'", "folder with spaces/file.jsonl")]
    [InlineData("\"folder with spaces/file.jsonl\"", "folder with spaces/file.jsonl")]
    [InlineData("  \" spaced.jsonl \"  ", " spaced.jsonl ")]
    public void ParsesOnePathAndRemovesOptionalQuotes(string input, string expected) =>
        Assert.Equal(expected, SessionPathArgument.Parse(input));

    [Theory]
    [InlineData("")]
    [InlineData("''")]
    [InlineData("\"\"")]
    [InlineData("\"missing close")]
    [InlineData("'two paths' other")]
    public void RejectsEmptyOrMultiplePaths(string input) =>
        Assert.Throws<ArgumentException>(() => SessionPathArgument.Parse(input));
}
