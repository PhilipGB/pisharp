using PiSharp.Core.Models;

namespace PiSharp.Core.Tests;

/// <summary>Thinking level support, clamping, cost calculation, and config value resolution.</summary>
public class ModelsDomainTests
{
    private static ModelInfo Model(string id, bool reasoning, IReadOnlyDictionary<string, string?>? map = null) => new()
    {
        Id = id,
        Name = id,
        Api = "openai-completions",
        Provider = "test",
        BaseUrl = "http://localhost",
        Reasoning = reasoning,
        Input = ["text"],
        Cost = new ModelCost { Input = 1, Output = 2, CacheRead = 0.1, CacheWrite = 1.25 },
        ContextWindow = 128_000,
        MaxTokens = 16_384,
        ThinkingLevelMap = map,
    };

    [Fact]
    public void SupportedLevels_NonReasoning_OnlyOff()
    {
        var levels = ThinkingLevelSupport.GetSupportedLevels(Model("m", false));
        Assert.Equal(new[] { "off" }, levels);
    }

    [Fact]
    public void SupportedLevels_Reasoning_DefaultSet()
    {
        var levels = ThinkingLevelSupport.GetSupportedLevels(Model("m", true));
        Assert.Equal(new[] { "minimal", "low", "medium", "high" }, levels);
    }

    [Fact]
    public void SupportedLevels_NullMapEntry_Unsupported()
    {
        var model = Model("m", true, new Dictionary<string, string?> { ["high"] = null });
        var levels = ThinkingLevelSupport.GetSupportedLevels(model);
        Assert.DoesNotContain("high", levels);
        Assert.Contains("low", levels);
    }

    [Fact]
    public void SupportedLevels_ExplicitMapEntry_Honored()
    {
        var model = Model("m", true, new Dictionary<string, string?> { ["xhigh"] = "xhigh" });
        var levels = ThinkingLevelSupport.GetSupportedLevels(model);
        Assert.Contains("xhigh", levels);
        Assert.DoesNotContain("max", levels);
    }

    [Fact]
    public void Clamp_PicksNearestSupported()
    {
        var model = Model("m", true, new Dictionary<string, string?> { ["high"] = null, ["medium"] = "med" });
        // high unsupported -> nearest lower is medium
        Assert.Equal("medium", ThinkingLevelSupport.Clamp(model, "high"));
        // minimal unsupported (only off/minimal? no: off+low+medium supported) -> low
        Assert.Equal("low", ThinkingLevelSupport.Clamp(model, "minimal"));
    }

    [Fact]
    public void CostCalculator_BasicRates()
    {
        var model = Model("m", false);
        var usage = new ModelUsage { Input = 1_000_000, Output = 1_000_000, CacheRead = 0, CacheWrite = 0 };
        var withCost = ModelCostCalculator.WithCost(usage, model);
        Assert.Equal(1.0, withCost.Cost.Input, 10);
        Assert.Equal(2.0, withCost.Cost.Output, 10);
        Assert.Equal(3.0, withCost.Cost.Total, 10);
    }

    [Fact]
    public void CostCalculator_HigherInputTierWins()
    {
        var model = new ModelInfo
        {
            Id = "m",
            Name = "m",
            Api = "openai-completions",
            Provider = "test",
            BaseUrl = "http://localhost",
            Input = ["text"],
            Cost = new ModelCost
            {
                Input = 1,
                Output = 2,
                CacheRead = 0.1,
                CacheWrite = 1.25,
                Tiers =
                [
                    new ModelCostTier { InputTokensAbove = 279_000, Input = 5, Output = 10, CacheRead = 0.5, CacheWrite = 6.25 },
                ],
            },
            ContextWindow = 1_000_000,
            MaxTokens = 16_384,
        };
        var usage = new ModelUsage { Input = 300_000, Output = 0, CacheRead = 0, CacheWrite = 0 };
        var withCost = ModelCostCalculator.WithCost(usage, model);
        Assert.Equal(1.5, withCost.Cost.Input, 10);
    }

    [Fact]
    public void CostCalculator_AnthropicLongCacheWrite_BilledAtDoubleInput()
    {
        var model = Model("m", false);
        var usage = new ModelUsage { Input = 0, Output = 0, CacheRead = 0, CacheWrite = 1_000_000, CacheWrite1h = 1_000_000 };
        var withCost = ModelCostCalculator.WithCost(usage, model);
        // 2x base input rate for the 1h cache write
        Assert.Equal(2.0, withCost.Cost.CacheWrite, 10);
    }

    // ── ConfigValue ──────────────────────────────────────────────────────

    [Fact]
    public void ConfigValue_Template_ResolvesEnvVars()
    {
        var env = new Dictionary<string, string> { ["FOO"] = "abc" };
        Assert.Equal("abc", ConfigValue.Resolve("$FOO", env));
        Assert.Equal("x-abc-y", ConfigValue.Resolve("x-${FOO}-y", env));
        Assert.Null(ConfigValue.Resolve("$MISSING_VAR_XYZ", env));
    }

    [Fact]
    public void ConfigValue_Escapes()
    {
        var env = new Dictionary<string, string>();
        Assert.Equal("$FOO", ConfigValue.Resolve("$$FOO", env));
        Assert.Equal("!cmd", ConfigValue.Resolve("$!cmd", env));
        Assert.False(ConfigValue.IsCommand("$!cmd"));
        Assert.True(ConfigValue.IsCommand("!cmd"));
    }

    [Fact]
    public void ConfigValue_EnvVarNames()
    {
        Assert.Equal(new[] { "FOO", "BAR" }, ConfigValue.GetEnvVarNames("$FOO-${BAR}-$FOO"));
        Assert.Equal(new[] { "FOO" }, ConfigValue.GetMissingEnvVarNames("$FOO-$BAR",
            new Dictionary<string, string> { ["BAR"] = "x" }));
        Assert.True(ConfigValue.IsConfigured("literal", null));
        Assert.False(ConfigValue.IsConfigured("$NOPE", null));
    }

    [Fact]
    public void ConfigValue_ResolveOrThrow_ReportsMissingVars()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            ConfigValue.ResolveOrThrow("$MISSING_ONE_XYZ", "apiKey", null));
        Assert.Contains("environment variable: MISSING_ONE_XYZ", ex.Message);

        var ex2 = Assert.Throws<InvalidOperationException>(() =>
            ConfigValue.ResolveOrThrow("$MISSING_ONE_XYZ-$MISSING_TWO_XYZ", "apiKey", null));
        Assert.Contains("environment variables: MISSING_ONE_XYZ, MISSING_TWO_XYZ", ex2.Message);
    }

    [Fact]
    public void ConfigValue_Command_ExecutesAndCaches()
    {
        ConfigValue.ClearCache();
        var result = ConfigValue.Resolve("!echo hello-cache-test", null);
        Assert.Equal("hello-cache-test", result);
        Assert.Equal("hello-cache-test", ConfigValue.Resolve("!echo hello-cache-test", null));
    }

    [Fact]
    public void ConfigValue_Command_FailureIsUncached()
    {
        ConfigValue.ClearCache();
        Assert.Null(ConfigValue.Resolve("!exit 3", null));
    }

    [Fact]
    public void ConfigValue_Headers_DropUnresolved_OrThrow()
    {
        var env = new Dictionary<string, string> { ["H_KEY"] = "k" };
        var headers = new Dictionary<string, string>
        {
            ["X-One"] = "$H_KEY",
            ["X-Missing"] = "$H_NOPE_XYZ",
        };
        var resolved = ConfigValue.ResolveHeaders(headers, env);
        Assert.Single(resolved!);
        Assert.Equal("k", resolved!["X-One"]);

        var ex = Assert.Throws<InvalidOperationException>(() => ConfigValue.ResolveHeadersOrThrow(headers, "provider p", env));
        Assert.Contains("X-Missing", ex.Message);
    }
}
