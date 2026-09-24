using PiSharp.Cli;

namespace PiSharp.Tests;

public sealed class ModelScopeGlobTests
{
    [Theory]
    [InlineData("openrouter/*", "openrouter/openai/gpt-4o-mini", false)]
    [InlineData("openrouter/**", "openrouter/openai/gpt-4o-mini", true)]
    [InlineData("custom/r[ea]asoner", "custom/reasoner", true)]
    [InlineData("custom/other-[0-5]", "custom/other-3", true)]
    [InlineData("custom/other-[!0-5]", "custom/other-a", true)]
    [InlineData("custom/r?asoner", "custom/reasoner", true)]
    [InlineData("custom/**/bar", "custom/foo/bar", true)]
    [InlineData("custom/**/bar", "custom/bar", true)]
    [InlineData("custom/*", "custom/.private", false)]
    [InlineData("custom/**", "custom/.private", false)]
    [InlineData("custom/?private", "custom/.private", false)]
    [InlineData("custom/[.]private", "custom/.private", true)]
    [InlineData("CUSTOM/r*o?er", "custom/reasoner", true)]
    public void ScopedGlobMatchesPinnedMinimatchSubset(string pattern, string value, bool expected) =>
        Assert.Equal(expected, ModelScopeGlob.Matches(pattern, value));

    [Fact]
    public void ManyStarsHaveBoundedWork()
    {
        Assert.False(ModelScopeGlob.Matches(string.Concat(Enumerable.Repeat("r*", 200)) + "z", "reasoner"));
        Assert.False(ModelScopeGlob.Matches(new string('*', 513), "reasoner"));
    }
}
