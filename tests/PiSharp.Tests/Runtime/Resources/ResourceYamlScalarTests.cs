using PiSharp.Runtime.Resources;

namespace PiSharp.Tests;

public sealed class ResourceYamlScalarTests
{
    [Theory]
    [InlineData("!!int word", "word")]
    [InlineData("!!int 1.2", "1.2")]
    [InlineData("!!int 1e2", "1e2")]
    [InlineData("!!bool no", "no")]
    [InlineData("!!bool yes", "yes")]
    [InlineData("!!float word", "word")]
    [InlineData("!!float 1", "1")]
    [InlineData("!!null text", "text")]
    [InlineData("!!null 0", "0")]
    [InlineData("!!int 0x10", null)]
    [InlineData("!!float 1.2", null)]
    [InlineData("!!null null", null)]
    [InlineData("!!bool true", null)]
    public void ExplicitTagsApplyTheirOwnScalarGrammarAndRetainInvalidValuesAsStrings(string scalar, string? expected)
    {
        var metadata = ResourceFrontmatter.Parse($"---\nname: guide\ndescription: {scalar}\n---\nbody");

        Assert.Equal(expected, metadata.Description);
        Assert.Equal("body", metadata.Body);
    }
}
