using PiSharp.Runtime.Resources;
using YamlDotNet.Core;

namespace PiSharp.Tests;

public sealed class ResourceYamlMappingTests
{
    [Theory]
    [InlineData("0x1: one\n1: two")]
    [InlineData("1: one\n1.0: two")]
    [InlineData("~: one\nnull: two")]
    [InlineData("true: one\nTrue: two")]
    public void ScalarKeysAreUniqueAfterYamlCoreTypeResolution(string keys)
    {
        var error = Assert.ThrowsAny<YamlException>(() =>
            ResourceFrontmatter.Parse($"---\nname: guide\ndescription: Guide\n{keys}\n---\nbody"));

        Assert.StartsWith("Map keys must be unique at line 4, column 1:", error.Message);
    }

    [Theory]
    [InlineData("name: guide\ndescription: Guide\n\"1\": one\n1: two", "Guide")]
    [InlineData("key: &key description\nname: guide\n*key : Guide\n*key : Other", "Other")]
    [InlineData("name: guide\ndescription: Guide\n? [one]\n: one\n? [one]\n: two", "Guide")]
    [InlineData("name: guide\ndescription: Guide\n.nan: one\n.nan: two", "Guide")]
    [InlineData("key: &key description\nname: guide\n*key : Guide", "Guide")]
    [InlineData("name: guide\n? [description]\n: Guide", null)]
    public void DistinctTypedAndAliasOrComplexKeysPreserveSourceObjectSemantics(string yaml, string? expected)
    {
        var metadata = ResourceFrontmatter.Parse($"---\n{yaml}\n---\nbody");

        Assert.Equal(expected, metadata.Description);
        Assert.Equal("guide", metadata.Name);
        Assert.Equal("body", metadata.Body);
    }
}
