using PiSharp.Cli;

namespace PiSharp.Tests;

public sealed class CliModelFilterTests
{
    [Theory]
    [InlineData("", "fixture", "static-only", true)]
    [InlineData("fi sta", "fixture", "static-only", true)]
    [InlineData("FIX/STA", "fixture", "static-only", true)]
    [InlineData("ftc stc", "fixture", "static-only", true)]
    [InlineData("fixture/other", "fixture", "static-only", false)]
    [InlineData("gpt4", "openai", "gpt-4o", true)]
    [InlineData("4gpt", "openai", "gpt-4o", true)]
    [InlineData("gpt5", "openai", "gpt-4o", false)]
    public void MatchesPinnedFuzzyTokenAndSwappedAlphaNumericCases(string pattern, string provider, string id, bool expected) =>
        Assert.Equal(expected, CliModelFilter.Matches(pattern, provider, id));
}
