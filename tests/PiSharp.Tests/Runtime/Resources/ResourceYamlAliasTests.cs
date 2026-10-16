using PiSharp.Runtime.Resources;
using YamlDotNet.Core;

namespace PiSharp.Tests;

public sealed class ResourceYamlAliasTests
{
    [Theory]
    [InlineData(99, false)]
    [InlineData(100, true)]
    [InlineData(101, true)]
    public void ScalarAliasesUseTheSourceAnchorReferenceLimit(int count, bool rejected)
    {
        var yaml = "name: guide\ndescription: &guide Guide\nunused: [" +
            string.Join(", ", Enumerable.Repeat("*guide", count)) + "]";

        Verify(yaml, rejected);
    }

    [Theory]
    [InlineData(2, 2, false)]
    [InlineData(2, 4, false)]
    [InlineData(3, 3, false)]
    [InlineData(4, 3, true)]
    public void NestedAliasExpansionUsesPerAnchorWeights(int fanout, int levels, bool rejected)
    {
        var yaml = "name: guide\ndescription: &a0 Guide";
        for (var level = 1; level <= levels; level++)
            yaml += $"\nlevel{level}: &a{level} [" + string.Join(", ", Enumerable.Repeat($"*a{level - 1}", fanout)) + "]";

        Verify(yaml, rejected);
    }

    [Fact]
    public void ACollectionMayReferenceItselfWithoutExpandingMetadata()
    {
        Verify("name: guide\ndescription: Guide\nunused: &self [*self]", rejected: false);
    }

    [Fact]
    public void SyntaxErrorsPrecedeUnresolvedAliasConversionErrors()
    {
        var error = Assert.ThrowsAny<YamlException>(() =>
            ResourceFrontmatter.Parse("---\nname: guide\ndescription: *missing\nunused: [bad\n---\nbody"));

        Assert.StartsWith("Flow sequence in block collection", error.Message);
    }

    private static void Verify(string yaml, bool rejected)
    {
        if (rejected)
        {
            var error = Assert.ThrowsAny<YamlException>(() => ResourceFrontmatter.Parse($"---\n{yaml}\n---\nbody"));
            Assert.Equal("Excessive alias count indicates a resource exhaustion attack", error.Message);
        }
        else
        {
            var metadata = ResourceFrontmatter.Parse($"---\n{yaml}\n---\nbody");
            Assert.Equal("Guide", metadata.Description);
            Assert.Equal("body", metadata.Body);
        }
    }
}
