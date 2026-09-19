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
    public void SupportedLevels_Reasoning_NoMap_BaseLevelsPlusOff()
    {
        // Pinned Pi (models.ts getSupportedThinkingLevels): for a reasoning model
        // every canonical level stays, EXCEPT xhigh/max, which require an explicit
        // non-null thinkingLevelMap entry to be advertised at all:
        //   if (level === "xhigh" || level === "max") return mapped !== undefined;
        var levels = ThinkingLevelSupport.GetSupportedLevels(Model("m", true));
        Assert.Equal(new[] { "off", "minimal", "low", "medium", "high" }, levels);
    }

    [Fact]
    public void SupportedLevels_Reasoning_FullMap_AdvertisesXhighAndMax()
    {
        var model = Model("m", true, new Dictionary<string, string?>
        {
            ["xhigh"] = "xhigh",
            ["max"] = "max",
        });
        Assert.Equal(ThinkingLevel.All, ThinkingLevelSupport.GetSupportedLevels(model));
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
    public void Clamp_UpwardFirstThenDownward()
    {
        // Pinned Pi (clampThinkingLevel): return the requested level when supported;
        // otherwise scan UPWARD from the requested index through the canonical order,
        // then downward. This is not nearest-distance semantics.

        // requested minimal, minimal removed -> scan up -> low
        var minimalGone = Model("m", true, new Dictionary<string, string?> { ["minimal"] = null });
        Assert.Equal("low", ThinkingLevelSupport.Clamp(minimalGone, "minimal"));

        // requested high, only low available -> scan up (none), then down -> low
        var onlyLow = Model("m", true, new Dictionary<string, string?>
        {
            ["off"] = null,
            ["minimal"] = null,
            ["medium"] = null,
            ["high"] = null,
        });
        Assert.Equal("low", ThinkingLevelSupport.Clamp(onlyLow, "high"));

        // requested xhigh, only max mapped -> scan up -> max
        var onlyMax = Model("m", true, new Dictionary<string, string?> { ["max"] = "max" });
        Assert.Equal("max", ThinkingLevelSupport.Clamp(onlyMax, "xhigh"));

        // requested level already supported -> unchanged
        Assert.Equal("medium", ThinkingLevelSupport.Clamp(Model("m", true), "medium"));

        // non-reasoning model -> only off is available
        Assert.Equal("off", ThinkingLevelSupport.Clamp(Model("m", false), "high"));
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
    public void ConfigValue_Command_CachedPathExecutesOnce()
    {
        // The command body after '!' runs through /bin/sh -c; 'echo a >> "path"' works in sh.
        using var temp = TempDirectory.Create();
        var countPath = Path.Combine(temp.Path, "count");
        ConfigValue.ClearCache();
        // Append to the file (execution marker) and echo to stdout (the value).
        var command = BuildMarkerEchoCommand(countPath);
        Assert.Equal("a", ConfigValue.Resolve(command, null));
        // Pinned Pi caches command results (including the value) for process lifetime:
        // the second resolve must not re-execute.
        Assert.Equal("a", ConfigValue.Resolve(command, null));
        Assert.Single(File.ReadAllLines(countPath));
    }

    [Fact]
    public void ConfigValue_Command_UncachedPathExecutesEachTime()
    {
        using var temp = TempDirectory.Create();
        var countPath = Path.Combine(temp.Path, "count");
        ConfigValue.ClearCache();
        var command = BuildMarkerEchoCommand(countPath);
        Assert.Equal("a", ConfigValue.ResolveUncached(command, null));
        Assert.Equal("a", ConfigValue.ResolveUncached(command, null));
        Assert.Equal(2, File.ReadAllLines(countPath).Length);
    }

    [Fact]
    public void ConfigValue_Command_FailureIsNullAndCached()
    {
        using var temp = TempDirectory.Create();
        var countPath = Path.Combine(temp.Path, "count");
        ConfigValue.ClearCache();
        // ';' separates sh commands; the marker append runs before the failure.
        const string separator = ";";
        var command = $"!echo a >> \"{countPath}\" {separator} exit 3";
        // Non-zero exit -> null (pinned Pi: status !== 0 -> undefined).
        Assert.Null(ConfigValue.Resolve(command, null));
        // Pinned Pi caches failures too: the second resolve must not re-execute.
        Assert.Null(ConfigValue.Resolve(command, null));
        Assert.Single(File.ReadAllLines(countPath));
    }

    [Fact]
    public void ConfigValue_Command_BlankStdoutIsNull()
    {
        // sh no-op with empty stdout.
        ConfigValue.ClearCache();
        var command = "!true";
        Assert.Null(ConfigValue.Resolve(command, null));
    }

    /// <summary>
    /// "Append marker line, then echo the value" command: 'echo a &gt;&gt; "path"; echo a' (sh).
    /// </summary>
    private static string BuildMarkerEchoCommand(string countPath)
    {
        const string separator = ";";
        return $"!echo a >> \"{countPath}\" {separator} echo a";
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
