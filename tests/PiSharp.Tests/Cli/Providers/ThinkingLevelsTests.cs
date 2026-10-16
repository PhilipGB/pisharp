using System.Text.Json;
using Microsoft.Extensions.AI;
using PiSharp.Cli;

namespace PiSharp.Tests;

public sealed class ThinkingLevelsTests
{
    [Fact]
    public void OrdinaryReasoningModelsDoNotExposeExtendedLevelsWithoutMapEntries()
    {
        Assert.Equal("medium", ThinkingLevels.Default);
        Assert.Equal("off", ThinkingLevels.ValidateForModel(ThinkingLevels.Default, supportsReasoning: false));
        Assert.Equal(["off", "minimal", "low", "medium", "high"],
            ThinkingLevels.AvailableForModel(true));
        Assert.Equal("high", ThinkingLevels.ValidateForModel("max", true));
        Assert.Equal(["off"], ThinkingLevels.AvailableForModel(false,
            Map("{\"xhigh\":\"xhigh\",\"max\":\"max\"}")));
    }

    [Fact]
    public void ExplicitNullsRemoveLevelsAndSparseExtendedMapsClampUpThenDown()
    {
        var map = Map("{\"high\":null,\"xhigh\":null,\"max\":\"max\"}");

        Assert.Equal(["off", "minimal", "low", "medium", "max"], ThinkingLevels.AvailableForModel(true, map));
        Assert.Equal("max", ThinkingLevels.ValidateForModel("high", true, map));
        Assert.Equal("max", ThinkingLevels.ValidateForModel("xhigh", true, map));
        Assert.Equal("medium", ThinkingLevels.ValidateForModel("high", true,
            Map("{\"high\":null,\"xhigh\":null,\"max\":null}")));
    }

    [Fact]
    public void CanonicalThinkingMapValuesUseMicrosoftExtensionsAiEfforts()
    {
        var map = Map("{\"high\":\"low\",\"max\":\"medium\"}");

        Assert.Equal(ReasoningEffort.Low, ThinkingLevels.ToOptions("high", map)?.Effort);
        Assert.Equal(ReasoningEffort.Medium, ThinkingLevels.ToOptions("max", map)?.Effort);
        Assert.Equal(ReasoningEffort.High, ThinkingLevels.ToOptions("high",
            Map("{\"high\":\"extended\"}"))?.Effort);
    }

    [Fact]
    public void DescriptionsAndStrictModelValidationFollowTheCanonicalLevelList()
    {
        Assert.Equal("No reasoning", ThinkingLevels.Description("off"));
        Assert.Equal("Very brief reasoning (~1k tokens)", ThinkingLevels.Description("minimal"));
        Assert.Equal("Light reasoning (~2k tokens)", ThinkingLevels.Description("low"));
        Assert.Equal("Moderate reasoning (~8k tokens)", ThinkingLevels.Description("medium"));
        Assert.Equal("Deep reasoning (~16k tokens)", ThinkingLevels.Description("high"));
        Assert.Equal("Extra-high reasoning (~32k tokens)", ThinkingLevels.Description("xhigh"));
        Assert.Equal("Maximum reasoning", ThinkingLevels.Description("max"));
        Assert.Equal("high", ThinkingLevels.RequireAvailableForModel("HIGH", true));

        var unsupported = Assert.Throws<ArgumentException>(() =>
            ThinkingLevels.RequireAvailableForModel("high", supportsReasoning: false));
        Assert.Contains("Available levels: off", unsupported.Message);
        Assert.Throws<ArgumentException>(() => ThinkingLevels.RequireAvailableForModel("unknown", true));
    }

    private static JsonElement Map(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }
}
