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

    private static JsonElement Map(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }
}
