using PiSharp.Cli;
using PiSharp.Runtime.Providers;

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
    [InlineData("custom/{reasoner,other}-*", "custom/reasoner-v1", true)]
    [InlineData("custom/{reasoner,other}-*", "custom/other-v2", true)]
    [InlineData("custom/{reasoner,other}-*", "custom/third-v1", false)]
    [InlineData("custom/{reasoner,{other,model}}-*", "custom/model-v3", true)]
    [InlineData("custom/@(reasoner|other)-*", "custom/reasoner-v1", true)]
    [InlineData("custom/@(reasoner|other)-*", "custom/third-v1", false)]
    [InlineData("custom/other-{01..03}", "custom/other-02", true)]
    [InlineData("custom/other-{01..03}", "custom/other-2", false)]
    [InlineData("custom/!(reasoner)-*", "custom/other-v1", true)]
    [InlineData("custom/!(reasoner)-*", "custom/reasoner-v1", false)]
    [InlineData("!custom/blocked-*", "custom/allowed-v1", true)]
    [InlineData("!custom/blocked-*", "custom/blocked-v1", false)]
    [InlineData(@"custom/literal\*", "custom/literal*", true)]
    [InlineData("custom/{reasoner,other}-[0-9]", "CUSTOM/OTHER-7", true)]
    [InlineData(@"custom/model\[1m\]", "custom/model[1m]", true)]
    [InlineData("CUSTOM/r*o?er", "custom/reasoner", true)]
    public void ScopedGlobMatchesPinnedMinimatchSubset(string pattern, string value, bool expected) =>
        Assert.Equal(expected, ModelScopeGlob.Matches(pattern, value));

    [Fact]
    public void ManyStarsHaveBoundedWork()
    {
        Assert.False(ModelScopeGlob.Matches(string.Concat(Enumerable.Repeat("r*", 200)) + "z", "reasoner"));
        Assert.False(ModelScopeGlob.Matches(new string('*', 513), "reasoner"));
    }

    [Fact]
    public void ScopeResolverKeepsPatternOrderSuffixesAliasesAndDiagnostics()
    {
        ModelDescriptor[] models =
        [
            new("claude-sonnet-4-5-20250101", null, null, null, Provider: "anthropic"),
            new("claude-sonnet-4-5", null, null, null, Provider: "anthropic"),
            new("reasoner", null, null, null, true, Provider: "fixture"),
            new("literal:high", null, null, null, Provider: "fixture"),
            new("shared", null, null, null, Provider: "fixture-a"),
            new("shared", null, null, null, Provider: "fixture-b")
        ];

        var result = ModelScopeResolver.Resolve(
            ["sonnet", "fixture/reasoner:high", "fixture/literal:high", "shared", "missing"], models);

        Assert.Equal(["claude-sonnet-4-5", "reasoner", "literal:high", "shared", "shared"],
            result.Models.Select(item => item.Model.Id));
        Assert.Equal("high", result.Models[1].ThinkingLevel);
        Assert.Null(result.Models[2].ThinkingLevel);
        Assert.Contains(result.Diagnostics, item => item.Code == "ambiguous" && item.Pattern == "shared");
        Assert.Contains(result.Diagnostics, item => item.Code == "no-match" && item.Pattern == "missing");
    }

    [Fact]
    public void ScopeResolverMatchesBraceAndEscapedPatternsAgainstProviderAndBareIds()
    {
        ModelDescriptor[] models =
        [
            new("model[1m]", null, null, null, Provider: "fixture"),
            new("reasoner-a", null, null, null, Provider: "fixture-a"),
            new("reasoner-b", null, null, null, Provider: "fixture-b")
        ];

        var result = ModelScopeResolver.Resolve(["reasoner-{a,b}*", @"fixture/model\[1m\]"], models);

        Assert.Equal(["reasoner-a", "reasoner-b", "model[1m]"], result.Models.Select(item => item.Model.Id));
        Assert.Empty(result.Diagnostics);
    }

    [Fact]
    public void ScopeResolverHandlesLargeCatalogAndPatternSetInConfiguredOrder()
    {
        const int modelCount = 2048;
        const int patternCount = 96;
        var models = Enumerable.Range(0, modelCount)
            .Select(index => new ModelDescriptor($"model-{index:D4}", null, null, null,
                Provider: "fixture"))
            .ToArray();
        var patterns = Enumerable.Range(0, patternCount)
            .Select(index => $"fixture/model-{index * 16:D4}*")
            .ToArray();

        var result = ModelScopeResolver.Resolve(patterns, models);

        Assert.Equal(patternCount, result.Models.Count);
        Assert.Equal(Enumerable.Range(0, patternCount).Select(index => $"model-{index * 16:D4}"),
            result.Models.Select(item => item.Model.Id));
        Assert.Empty(result.Diagnostics);
    }
}
